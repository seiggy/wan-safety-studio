#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SettingsPath
)
. (Join-Path $PSScriptRoot 'Studio-Recovery.ps1')
$context = Read-StudioRecoveryScope $SettingsPath
$script:RecoveryScope = $context.foundation.scope
$account = Invoke-StudioRecoveryAz @('account','show')
Assert-StudioRecovery ($account.tenantId -ieq $script:RecoveryScope.tenantId -and
    $account.id -ieq $script:RecoveryScope.subscriptionId -and $account.environmentName -ceq 'AzureCloud') 'Azure CLI context differs; no resources changed.'
$errors = [Collections.Generic.List[string]]::new()
$terminal = @('Completed','Failed','Canceled')
$networkApi = '2024-05-01'
$control = $context.foundation.regions[$context.foundation.controlRegion]
$siteId = $null
$configuredSite = $context.siteId
if ($env:GITHUB_ACTIONS -eq 'true') {
    Assert-StudioRecovery ($env:AZURE_CLIENT_ID -and $account.user.type -ceq 'servicePrincipal' -and
        $account.user.name -ieq $env:AZURE_CLIENT_ID) 'Recovery must use its explicit federated CLI identity.'
}

# Until P5 consumes a durable operational fence, stopping and verifying the one
# shared host is the effective submission fence. Never restart it in recovery.
try {
    if ($configuredSite) {
        $siteId = $configuredSite
        $site = Invoke-StudioRecoveryArm $siteId '2023-12-01' -Missing
        if ($site) {
            Assert-StudioRecoveryOwned $site $siteId $control
            Invoke-StudioRecoveryArm "$siteId/stop" '2023-12-01' 'POST' | Out-Null
            Assert-StudioRecovery ((Invoke-StudioRecoveryArm $siteId '2023-12-01').properties.state -ceq 'Stopped') 'Submission host did not stop.'
        }
    }
} catch { $errors.Add("Submission fence: $_") }

foreach ($key in $context.foundation.regions.Keys) {
    $region = $context.foundation.regions[$key]
    $cancelErrors = [Collections.Generic.List[string]]::new()
    try {
        $workspace = Invoke-StudioRecoveryArm $region.workspaceId '2024-10-01' -Missing
        if ($workspace) {
            Assert-StudioRecoveryOwned $workspace $region.workspaceId $region
            try {
                foreach ($job in @(Get-StudioRecoveryJobs $region)) {
                    if ($job.properties.status -notin $terminal) {
                        try { Invoke-StudioRecoveryArm "$($job.id)/cancel" '2024-10-01' 'POST' | Out-Null }
                        catch { $cancelErrors.Add("Cancel $($job.name) failed.") }
                    }
                }
            } catch { $cancelErrors.Add("Job enumeration: $_") }
        }
        $compute = Invoke-StudioRecoveryArm $region.computeId '2024-10-01' -Missing
        if ($compute) {
            Assert-StudioRecoveryOwned $compute $region.computeId $region -Ephemeral
            Invoke-StudioRecoveryArm $region.computeId '2024-10-01&underlyingResourceAction=Delete' 'DELETE' | Out-Null
        }
        Wait-StudioRecoveryAbsent $region.computeId '2024-10-01'
        if ($workspace) {
            $deadline = [DateTime]::UtcNow.AddMinutes(10)
            do {
                $active = @(Get-StudioRecoveryJobs $region | Where-Object { $_.properties.status -notin $terminal })
                if (-not $active.Count) { break }
                Assert-StudioRecovery ([DateTime]::UtcNow -lt $deadline) 'Relevant jobs are not terminal after compute removal.'
                Start-Sleep -Seconds 5
            } while ($true)
            foreach ($problem in $cancelErrors) { Write-Warning "$problem Fresh reads subsequently proved all relevant jobs terminal." }
        }
        $nat = Invoke-StudioRecoveryArm $region.natGatewayId $networkApi -Missing
        $pip = Invoke-StudioRecoveryArm $region.publicIpId $networkApi -Missing
        if (-not $region.policy.manageComputeEgress) {
            Assert-StudioRecovery (-not $nat -and -not $pip) 'Unexpected egress exists with management disabled; explicit ownership reconciliation is required.'
            continue
        }
        if ($nat) {
            Assert-StudioRecoveryOwned $nat $region.natGatewayId $region -Ephemeral
            if ($nat.properties.ContainsKey('subnets')) {
                foreach ($attached in @($nat.properties.subnets)) {
                    Assert-StudioRecovery ($attached.id -ieq $region.landingZone.gpuSubnetId) 'Owned NAT has an unexpected subnet attachment.'
                }
            }
        }
        if ($pip) { Assert-StudioRecoveryOwned $pip $region.publicIpId $region -Ephemeral }
        $subnetId = $region.landingZone.gpuSubnetId
        $subnet = Invoke-StudioRecoveryArm $subnetId $networkApi
        Assert-StudioRecovery ($subnet.id -ieq $subnetId -and $subnet.properties.addressPrefix -ceq $region.landingZone.gpuSubnetCidr -and
            $subnet.properties.networkSecurityGroup.id -ieq $region.networkSecurityGroupId -and
            @($subnet.properties.delegations).Count -eq 0 -and $subnet.properties.defaultOutboundAccess -eq $false) 'Dedicated GPU subnet contract changed.'
        if ($subnet.properties.ContainsKey('natGateway') -and $subnet.properties.natGateway) {
            Assert-StudioRecovery ($nat -and $subnet.properties.natGateway.id -ieq $region.natGatewayId -and $subnet.etag) 'Foreign or unverified NAT association; refusing detach.'
            $properties = $subnet.properties
            foreach ($field in @('natGateway','provisioningState','ipConfigurations','resourceNavigationLinks','serviceAssociationLinks','privateEndpoints','purpose')) { $properties.Remove($field) }
            Invoke-StudioRecoveryArm $subnetId $networkApi 'PUT' @{properties=$properties} -Headers @{'If-Match'=$subnet.etag} | Out-Null
            $deadline = [DateTime]::UtcNow.AddMinutes(10)
            do {
                $fresh = Invoke-StudioRecoveryArm $subnetId $networkApi
                if (-not $fresh.properties.ContainsKey('natGateway') -or -not $fresh.properties.natGateway) { break }
                Assert-StudioRecovery ($fresh.properties.natGateway.id -ieq $region.natGatewayId -and [DateTime]::UtcNow -lt $deadline) 'NAT detach not verified.'
                Start-Sleep -Seconds 5
            } while ($true)
        }
        foreach ($id in @($region.natGatewayId, $region.publicIpId)) {
            $value = Invoke-StudioRecoveryArm $id $networkApi -Missing
            if ($value) {
                Assert-StudioRecoveryOwned $value $id $region -Ephemeral
                Invoke-StudioRecoveryArm $id $networkApi 'DELETE' | Out-Null
            }
            Wait-StudioRecoveryAbsent $id $networkApi
        }
    } catch { $errors.Add("${key}: $_") }
}
foreach ($region in $context.foundation.regions.Values) {
    try {
        foreach ($entry in @(@($region.computeId,'2024-10-01'), @($region.natGatewayId,$networkApi), @($region.publicIpId,$networkApi))) {
            Assert-StudioRecovery ($null -eq (Invoke-StudioRecoveryArm $entry[0] $entry[1] -Missing)) 'Final resource-absence audit failed.'
        }
        if (Invoke-StudioRecoveryArm $region.workspaceId '2024-10-01' -Missing) {
            Assert-StudioRecovery (@(Get-StudioRecoveryJobs $region | Where-Object { $_.properties.status -notin $terminal }).Count -eq 0) 'Final job-termination audit failed.'
        }
    } catch { $errors.Add("Final audit $($region.workspaceId): $_") }
}
if ($siteId) {
    try {
        $freshSite = Invoke-StudioRecoveryArm $siteId '2023-12-01' -Missing
        Assert-StudioRecovery (-not $freshSite -or $freshSite.properties.state -ceq 'Stopped') 'Shared submission host restarted during release.'
    } catch { $errors.Add("Final submission fence: $_") }
}
if ($errors.Count) { throw ("Release NOT verified; keep submissions fenced.`n" + ($errors -join "`n")) }
Write-Host 'VERIFIED OFF: shared submission host stopped/absent; all selected owned compute/egress absent and relevant jobs terminal. No Terraform state mutation.'
