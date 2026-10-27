#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-StudioRecovery($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Read-StudioRecoveryJson([string]$Path) {
    $text = Get-Content -LiteralPath $Path -Raw
    $document = [System.Text.Json.JsonDocument]::Parse($text)
    function Test-Unique($Element) {
        if ($Element.ValueKind -eq 'Object') {
            $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                Assert-StudioRecovery ($keys.Add($property.Name)) 'Duplicate JSON property.'
                Test-Unique $property.Value
            }
        } elseif ($Element.ValueKind -eq 'Array') {
            foreach ($child in $Element.EnumerateArray()) { Test-Unique $child }
        }
    }
    try { Test-Unique $document.RootElement } finally { $document.Dispose() }
    ConvertFrom-Json -InputObject $text -AsHashtable -Depth 100
}
function Get-StudioRecoverySection($Parent, [string]$Name) {
    Assert-StudioRecovery ($Parent -is [System.Collections.IDictionary] -and $Parent.Contains($Name) -and $Parent[$Name] -is [System.Collections.IDictionary]) "Settings section $Name is missing."
    $Parent[$Name]
}
function Assert-StudioRecoveryId($Value, [string]$Subscription, [string]$Type, [string]$What) {
    Assert-StudioRecovery ($Value -is [string] -and $Value -match "^/subscriptions/$Subscription/resourceGroups/[^/]+/providers/$([regex]::Escape($Type))/[^/]+$") "$What is not a $Type resource ID in the stated subscription."
}
function Read-StudioRecoveryScope([string]$SettingsPath) {
    $document = Read-StudioRecoveryJson $SettingsPath
    $studio = Get-StudioRecoverySection $document 'Studio'
    $guid = '^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$'
    $subscription = $studio['SubscriptionId']
    $deployment = $studio['DeploymentName']
    Assert-StudioRecovery ($studio['TenantId'] -is [string] -and $studio['TenantId'] -cmatch $guid -and
        $subscription -is [string] -and $subscription -cmatch $guid -and
        $deployment -is [string] -and $deployment -cmatch '^[a-z][a-z0-9-]{1,14}[a-z0-9]$' -and -not $deployment.Contains('--')) 'Studio scope (tenant, subscription, deployment) is missing or malformed.'
    $control = $studio['ControlRegion']
    $regions = Get-StudioRecoverySection $studio 'Regions'
    Assert-StudioRecovery ($regions.Count -ge 1 -and $control -is [string] -and $regions.Contains($control)) 'Control region is not one of the configured regions.'
    $owned = @{}
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result = @{}
    foreach ($key in $regions.Keys) {
        Assert-StudioRecovery ($key -cmatch '^[a-z][a-z0-9]{1,31}$') 'Invalid region key.'
        $r = Get-StudioRecoverySection $regions $key
        $workspace = Get-StudioRecoverySection $r 'Workspace'
        $compute = Get-StudioRecoverySection $r 'Compute'
        $storage = Get-StudioRecoverySection $r 'Storage'
        $platform = Get-StudioRecoverySection $r 'Platform'
        $network = Get-StudioRecoverySection $r 'Network'
        $tags = Get-StudioRecoverySection $r 'OwnershipTags'
        $ml = 'Microsoft.MachineLearningServices/workspaces'
        $identity = 'Microsoft.ManagedIdentity/userAssignedIdentities'
        Assert-StudioRecoveryId $workspace['Id'] $subscription $ml "Workspace of $key"
        Assert-StudioRecoveryId $compute['IdentityId'] $subscription $identity "Compute identity of $key"
        Assert-StudioRecovery ($compute['Id'] -is [string] -and $compute['Id'] -imatch '^(?<w>.+)/computes/[^/]+$' -and
            $Matches.w -ieq $workspace['Id']) "Compute of $key is not a compute of its own workspace."
        Assert-StudioRecoveryId $storage['AccountId'] $subscription 'Microsoft.Storage/storageAccounts' "Storage of $key"
        Assert-StudioRecoveryId $platform['RegistryId'] $subscription 'Microsoft.ContainerRegistry/registries' "Registry of $key"
        Assert-StudioRecoveryId $network['GpuNsgId'] $subscription 'Microsoft.Network/networkSecurityGroups' "GPU NSG of $key"
        Assert-StudioRecoveryId $network['NatGatewayId'] $subscription 'Microsoft.Network/natGateways' "NAT gateway of $key"
        Assert-StudioRecoveryId $network['PublicIpId'] $subscription 'Microsoft.Network/publicIPAddresses' "Public IP of $key"
        Assert-StudioRecovery ($network['GpuSubnetId'] -is [string] -and
            $network['GpuSubnetId'] -imatch "^/subscriptions/$subscription/resourceGroups/[^/]+/providers/Microsoft\.Network/virtualNetworks/[^/]+/subnets/[^/]+$" -and
            $network['GpuSubnetCidr'] -is [string] -and $network['GpuSubnetCidr'] -cmatch '^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$') "GPU subnet of $key is missing or outside the stated subscription."
        $maxNodes = $compute['MaxNodes']; $maxMinutes = $compute['MaxJobMinutes']
        Assert-StudioRecovery ($compute['ManageEgress'] -is [bool] -and ($maxNodes -is [int] -or $maxNodes -is [long]) -and $maxNodes -ge 1 -and
            ($maxMinutes -is [int] -or $maxMinutes -is [long]) -and $maxMinutes -ge 1 -and $maxMinutes -le 120) "Invalid explicit regional policy for $key."
        foreach ($pair in @(@('application','wan-safety-studio'), @('deployment',$deployment), @('managedBy','terraform'), @('region',$key))) {
            Assert-StudioRecovery ($tags[$pair[0]] -ceq $pair[1]) "Missing or unexpected ownership tag $($pair[0]) for $key."
        }
        # No resource may be claimed by two regions: a release in one region must never touch another's resources.
        foreach ($id in @($workspace['Id'], $compute['Id'], $network['NatGatewayId'], $network['PublicIpId'], $network['GpuSubnetId'])) {
            Assert-StudioRecovery ($seen.Add($id)) 'The same resource is configured for more than one region.'
        }
        $result[$key] = @{
            workspaceId = $workspace['Id']; computeId = $compute['Id']; natGatewayId = $network['NatGatewayId']; publicIpId = $network['PublicIpId']
            networkSecurityGroupId = $network['GpuNsgId']; ownershipTags = $tags
            landingZone = @{ gpuSubnetId = $network['GpuSubnetId']; gpuSubnetCidr = $network['GpuSubnetCidr'] }
            policy = @{ manageComputeEgress = $compute['ManageEgress'] }
        }
    }
    $siteId = $null
    if ($document.Contains('Pipeline')) {
        $siteId = (Get-StudioRecoverySection $document 'Pipeline')['SiteId']
        if ($siteId) { Assert-StudioRecoveryId $siteId $subscription 'Microsoft.Web/sites' 'Shared App Service' }
    }
    @{ foundation = @{ scope = @{ tenantId = $studio['TenantId']; subscriptionId = $subscription; deploymentName = $deployment }
        controlRegion = $control; regions = $result }; siteId = $siteId }
}function Invoke-StudioRecoveryAz([string[]]$Arguments) {
    $output = & az @Arguments --only-show-errors -o json
    Assert-StudioRecovery ($LASTEXITCODE -eq 0) 'Azure CLI authentication/read failed.'
    ($output -join "`n") | ConvertFrom-Json -AsHashtable
}
function Invoke-StudioRecoveryArm([string]$Id, [string]$Api, [string]$Method = 'GET', $Body = $null, [switch]$Missing, [hashtable]$Headers = @{}) {
    Assert-StudioRecovery ($Id.StartsWith("/subscriptions/$($script:RecoveryScope.subscriptionId)/")) 'ARM request escaped explicit scope.'
    $token = Invoke-StudioRecoveryAz @('account','get-access-token','--resource','https://management.azure.com/')
    $allHeaders = @{ Authorization = "Bearer $($token.accessToken)" }
    foreach ($key in $Headers.Keys) { $allHeaders[$key] = $Headers[$key] }
    $request = @{
        Uri = "https://management.azure.com${Id}$(if ($Id.Contains('?')) { '&' } else { '?' })api-version=$Api"
        Method = $Method; Headers = $allHeaders; TimeoutSec = 120; SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Depth 100 -Compress; $request.ContentType = 'application/json' }
    $response = Invoke-WebRequest @request
    if ($Missing -and $response.StatusCode -eq 404) { return $null }
    Assert-StudioRecovery ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) "ARM $Method failed (HTTP $($response.StatusCode))."
    if ($response.Content) { $response.Content | ConvertFrom-Json -AsHashtable -Depth 100 }
}
function Assert-StudioRecoveryOwned($Value, [string]$Id, $Region, [switch]$Ephemeral) {
    Assert-StudioRecovery ($Value -and $Value.id -ieq $Id) 'Resource identity mismatch.'
    foreach ($key in $Region.ownershipTags.Keys) {
        $expected = if ($Ephemeral -and $key -ceq 'managedBy') { 'studio' } else { $Region.ownershipTags[$key] }
        Assert-StudioRecovery ($Value.tags[$key] -ceq $expected) 'Resource ownership mismatch; refusing mutation.'
    }
}
function Wait-StudioRecoveryAbsent([string]$Id, [string]$Api) {
    $deadline = [DateTime]::UtcNow.AddMinutes(20)
    while ($null -ne (Invoke-StudioRecoveryArm $Id $Api -Missing)) {
        Assert-StudioRecovery ([DateTime]::UtcNow -lt $deadline) 'Resource release was not verified before the deadline.'
        Start-Sleep -Seconds 5
    }
}
function Get-StudioRecoveryJobs($Region) {
    $id = "$($Region.workspaceId)/jobs"
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($id) {
        Assert-StudioRecovery ($seen.Add($id)) 'Repeated jobs page.'
        $page = Invoke-StudioRecoveryArm $id '2024-10-01'
        Assert-StudioRecovery ($page.ContainsKey('value') -and $page.value -is [array]) 'Incomplete jobs page.'
        foreach ($job in $page.value) {
            Assert-StudioRecovery ($job.id.StartsWith("$($Region.workspaceId)/jobs/") -and
                $job.properties.ContainsKey('computeId') -and $job.properties.computeId -is [string] -and
                -not [string]::IsNullOrWhiteSpace($job.properties.computeId)) 'Job identity/compute is unknown.'
            if ($job.properties.computeId -ieq $Region.computeId) { $job }
        }
        $id = $null
        if ($page.ContainsKey('nextLink') -and $page.nextLink) {
            $next = [Uri]$page.nextLink
            Assert-StudioRecovery ($next.Scheme -ceq 'https' -and $next.Host -ceq 'management.azure.com' -and
                $next.AbsolutePath -ieq "$($Region.workspaceId)/jobs" -and $next.Query -match 'api-version=2024-10-01') 'Jobs nextLink escaped the selected workspace.'
            $id = $next.PathAndQuery -replace '([?&])api-version=2024-10-01(&|$)', '$1'
            $id = $id.TrimEnd('?','&')
        }
    }
}
