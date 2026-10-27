#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) "wan-recovery-test-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
$global:StudioRecoveryTestChecks = 0
$savedClient = $env:AZURE_CLIENT_ID
$savedActions = $env:GITHUB_ACTIONS
$env:AZURE_CLIENT_ID = '55555555-5555-5555-5555-555555555555'
$env:GITHUB_ACTIONS = 'true'
function Check($Condition, $Message) { if (-not $Condition) { throw $Message }; $global:StudioRecoveryTestChecks++ }
function global:az {
    $state = $global:StudioRecoveryTest
    if ($args[0] -ne 'account') { throw 'Recovery unexpectedly needs another CLI surface.' }
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'show') { @{tenantId=$state.tenant; id=$state.subscription; environmentName='AzureCloud'
        user=@{type='servicePrincipal';name='55555555-5555-5555-5555-555555555555'}} | ConvertTo-Json }
    elseif ($args[1] -eq 'get-access-token') { '{"accessToken":"offline-fixture"}' }
    else { throw 'Unexpected CLI operation.' }
}
function global:Start-Sleep { throw 'Unexpected wait in immediate offline mock.' }
function global:Invoke-WebRequest {
    param($Uri, $Method, $Headers, $TimeoutSec, $SkipHttpErrorCheck, $Body, $ContentType)
    $state = $global:StudioRecoveryTest
    $uriValue = [Uri]$Uri
    $id = $uriValue.AbsolutePath
    $state.reads.Add("$Method $id")
    $status = 200
    $value = $null
    $region = $null
    $siteTags = $null
    foreach ($candidate in $state.receipt.regions.Values) {
        if ($id.StartsWith($candidate.workspaceId) -or $id -in @($candidate.natGatewayId,$candidate.publicIpId,$candidate.landingZone.gpuSubnetId)) { $region = $candidate; break }
    }
    if ($Method -ne 'GET') { $state.writes.Add("$Method $id") }
    if ($region -and $id -eq $region.workspaceId) {
        $value = @{id=$id; tags=$region.ownershipTags}
    } elseif ($region -and $id -eq "$($region.workspaceId)/jobs") {
        $jobs = @()
        if ($state.mode -eq 'cancel-fails' -and -not $state.deleted.Contains($region.computeId)) {
            $jobs = @(@{id="$($region.workspaceId)/jobs/fixture"; name='fixture'; properties=@{computeId=$region.computeId;status='Queued'}})
        }
        if ($state.mode -eq 'unknown-job') {
            $jobs = @(@{id="$($region.workspaceId)/jobs/fixture"; name='fixture'; properties=@{computeId=$null;status='Unknown'}})
        }
        if ($state.mode -eq 'unsafe-page') {
            $value = @{value=$jobs;nextLink='https://example.invalid/foreign?api-version=2024-10-01'}
        } else { $value = @{value=$jobs} }
    } elseif ($region -and $id -eq $region.computeId) {
        if ($state.mode -eq 'one-region-fails' -and $id -eq $state.firstCompute) { $status = 500 }
        elseif ($Method -eq 'DELETE') {
            Check ($uriValue.Query.Contains('underlyingResourceAction=Delete')) 'Underlying resource deletion omitted.'
            [void]$state.deleted.Add($id)
        } elseif ($state.mode -in @('compute','foreign-compute','cancel-fails','one-region-fails') -and -not $state.deleted.Contains($id)) {
            $tags = @{} + $region.ownershipTags
            $tags.managedBy = if ($state.mode -eq 'foreign-compute') { 'foreign' } else { 'studio' }
            $value = @{id=$id;tags=$tags}
        } else { $status = 404 }
    } elseif ($id -eq $state.siteId -or $id -eq "$($state.siteId)/stop") {
        if ($state.mode -notlike '*site*') { $status = 404 }
        elseif ($Method -eq 'POST') { $state.stopped = $true }
        else {
            $tags = @{} + $state.controlTags
            if ($state.mode -eq 'foreign-site') { $tags.managedBy = 'foreign' }
            $value = @{id=$id;tags=$tags;properties=@{state=$(if ($state.stopped) { 'Stopped' } else { 'Running' })}}
        }
    } elseif ($region -and $id.EndsWith('/cancel')) { $status = 400 }
    elseif ($region -and $state.mode -like '*nat*' -and $region.policy.manageComputeEgress -and $id -in @($region.natGatewayId,$region.publicIpId)) {
        if ($state.deleted.Contains($id)) { $status = 404 }
        elseif ($Method -eq 'DELETE') { [void]$state.deleted.Add($id) }
        else {
            $tags = @{} + $region.ownershipTags
            $tags.managedBy = 'studio'
            $subnetId = if ($state.mode -eq 'nat-other-subnet') { $region.landingZone.gpuSubnetId + '-foreign' } else { $region.landingZone.gpuSubnetId }
            $value = @{id=$id;tags=$tags;properties=@{subnets=@(@{id=$subnetId})}}
        }
    } elseif ($region -and $id -eq $region.landingZone.gpuSubnetId) {
        if ($Method -eq 'PUT') {
            $written = $Body | ConvertFrom-Json -AsHashtable
            Check ($Headers['If-Match'] -eq 'fixture-etag') 'Subnet write lacked exact ETag.'
            Check ($written.properties.routeTable.id -eq 'preserve-route' -and $written.properties.serviceEndpoints.Count -eq 1) 'Unrelated subnet properties changed.'
            Check (-not $written.properties.ContainsKey('natGateway')) 'NAT association was not removed.'
            [void]$state.detached.Add($id)
        }
        $properties = @{addressPrefix=$region.landingZone.gpuSubnetCidr;networkSecurityGroup=@{id=$region.networkSecurityGroupId}
            defaultOutboundAccess=$false;delegations=@();routeTable=@{id='preserve-route'};serviceEndpoints=@(@{service='fixture'})}
        if (-not $state.detached.Contains($id)) {
            $properties.natGateway = @{id=$(if ($state.mode -eq 'foreign-nat') { $region.natGatewayId + '-foreign' } else { $region.natGatewayId })}
        }
        $value = @{id=$id;etag='fixture-etag';properties=$properties}
    }
    else { $status = 404 }
    @{StatusCode=$status;Content=$(if ($null -ne $value) { $value | ConvertTo-Json -Depth 100 -Compress } else { '' })}
}
# Independent mapping of the explicit settings to the fields the mock ARM needs (does not use the recovery code under test).
function Convert-Settings($Settings) {
    $regions = @{}
    foreach ($key in $Settings.Studio.Regions.Keys) {
        $r = $Settings.Studio.Regions[$key]
        $regions[$key] = @{workspaceId=$r.Workspace.Id; computeId=$r.Compute.Id; natGatewayId=$r.Network.NatGatewayId; publicIpId=$r.Network.PublicIpId
            networkSecurityGroupId=$r.Network.GpuNsgId; ownershipTags=$r.OwnershipTags
            landingZone=@{gpuSubnetId=$r.Network.GpuSubnetId; gpuSubnetCidr=$r.Network.GpuSubnetCidr}; policy=@{manageComputeEgress=$r.Compute.ManageEgress}}
    }
    @{regions=$regions}
}
function Save-Settings { $script:settings | ConvertTo-Json -Depth 100 | Set-Content "$temp\settings.json" }
function Fresh-Settings { $script:settings = Get-Content (Join-Path $root 'contracts\fixtures\studio-settings.json') -Raw | ConvertFrom-Json -AsHashtable; Save-Settings }
function Expect-Rejected([string]$Name, [scriptblock]$Mutate) {
    Fresh-Settings
    & $Mutate
    Save-Settings
    Run-Case 'absent' -Failure
    Check ($script:reads.Count -eq 0 -and $script:writes.Count -eq 0) "$Name reached Azure."
    Fresh-Settings
}
function Run-Case([string]$Mode, [switch]$Failure) {
    $script:mode = $Mode
    $script:reads = [Collections.Generic.List[string]]::new()
    $script:writes = [Collections.Generic.List[string]]::new()
    $script:deleted = [Collections.Generic.HashSet[string]]::new()
    $control = $script:fixtureControl
    $global:StudioRecoveryTest = @{receipt=$script:fixtureReceipt;firstCompute=$script:firstCompute
        tenant=$script:tenant;subscription=$script:subscription;siteId=$script:fixtureSite;controlTags=$control.OwnershipTags;stopped=$false
        mode=$Mode;reads=$script:reads;writes=$script:writes;deleted=$script:deleted;detached=[Collections.Generic.HashSet[string]]::new()}
    $caught = $false
    try { & (Join-Path $PSScriptRoot 'Stop-Studio.ps1') -SettingsPath "$temp\settings.json" 6>$null 3>$null }
    catch { $caught = $true; if (-not $Failure) { throw } }
    Check ($caught -eq [bool]$Failure) "Unexpected outcome: $Mode"
}
try {
    Fresh-Settings
    $script:fixtureReceipt = Convert-Settings $script:settings
    $script:fixtureControl = $script:settings.Studio.Regions[$script:settings.Studio.ControlRegion]
    $script:fixtureSite = $script:settings.Pipeline.SiteId
    $script:tenant = $script:settings.Studio.TenantId
    $script:subscription = $script:settings.Studio.SubscriptionId
    $script:firstCompute = $script:settings.Studio.Regions[$script:settings.Studio.ControlRegion].Compute.Id
    $inputNames = @('STUDIO_SETTINGS_JSON','STUDIO_SETTINGS_PATH','STUDIO_TFVARS_JSON','STUDIO_TFVARS_PATH','STUDIO_BACKEND_JSON','STUDIO_BACKEND_PATH')
    $savedInputs = @{}
    foreach ($name in $inputNames) {
        $savedInputs[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    try {
        $inputDirectory = Join-Path $temp 'pipeline-inputs'
        New-Item -ItemType Directory -Path $inputDirectory | Out-Null
        $writer = Join-Path $root 'scripts\ci\Write-StudioPipelineInputs.ps1'
        $env:STUDIO_SETTINGS_JSON = '{"Studio":{}}'
        & $writer -Directory $inputDirectory -Inputs settings
        Check ((Get-Content (Join-Path $inputDirectory 'settings.json') -Raw) -eq $env:STUDIO_SETTINGS_JSON) 'Inline input changed.'
        Check (-not (Test-Path (Join-Path $inputDirectory 'tfvars.json')) -and -not (Test-Path (Join-Path $inputDirectory 'backend.json'))) 'Unrequested inputs were materialized.'
        $env:STUDIO_SETTINGS_JSON = $null
        $env:STUDIO_SETTINGS_PATH = Join-Path $temp 'large-settings.json'
        [IO.File]::WriteAllText($env:STUDIO_SETTINGS_PATH, ('{"Studio":{}}' + (' ' * 70000)))
        & $writer -Directory $inputDirectory -Inputs settings
        Check ((Get-Item (Join-Path $inputDirectory 'settings.json')).Length -gt 65536) 'Large settings input was truncated.'
        $env:STUDIO_SETTINGS_JSON = '{"Studio":{}}'
        $rejected = $false
        try { & $writer -Directory $inputDirectory -Inputs settings } catch { $rejected = $true }
        Check $rejected 'Ambiguous inline/file input was accepted.'
        $env:STUDIO_SETTINGS_JSON = $null; $env:STUDIO_SETTINGS_PATH = $null
        $rejected = $false
        try { & $writer -Directory $inputDirectory -Inputs settings } catch { $rejected = $true }
        Check $rejected 'Missing required settings input was accepted.'
        $env:STUDIO_TFVARS_JSON = '{"regions":{}}'; $env:STUDIO_BACKEND_JSON = '{"key":"k"}'
        & $writer -Directory $inputDirectory -Inputs tfvars,backend
        Check ((Get-Content (Join-Path $inputDirectory 'tfvars.json') -Raw) -eq $env:STUDIO_TFVARS_JSON -and (Test-Path (Join-Path $inputDirectory 'backend.json'))) 'Terraform inputs were not materialized.'
        $env:STUDIO_TFVARS_JSON = '[]'
        $rejected = $false
        try { & $writer -Directory $inputDirectory -Inputs tfvars,backend } catch { $rejected = $true }
        Check $rejected 'A non-object input was accepted.'    } finally {
        foreach ($name in $inputNames) { [Environment]::SetEnvironmentVariable($name, $savedInputs[$name]) }
    }
    Run-Case 'absent'
    Check ($script:writes.Count -eq 0) 'Repeated Stop mutated already absent resources.'
    Run-Case 'compute'
    Check ($script:deleted.Count -eq 2) 'Not every selected region was released.'
    Run-Case 'compute'
    Run-Case 'foreign-compute' -Failure
    Check ($script:writes.Count -eq 0) 'Foreign compute was mutated.'
    Run-Case 'cancel-fails'
    Check ($script:deleted.Count -eq 2) 'Cancellation failure blocked verified compute release.'
    Run-Case 'one-region-fails' -Failure
    Check ($script:deleted.Count -eq 1) 'Failure in one region prevented trying the other.'
    Run-Case 'unsafe-page' -Failure
    Check (-not @($script:reads | Where-Object { $_ -match 'example.invalid' }).Count) 'Followed a foreign nextLink.'
    Run-Case 'unknown-job' -Failure
    Run-Case 'site'
    Check ($script:writes.Count -eq 1 -and $script:writes[0] -like 'POST *Microsoft.Web/sites/*/stop') 'Shared host was not stopped exactly once.'
    Run-Case 'foreign-site' -Failure
    Check ($script:writes.Count -eq 0) 'Foreign-owned site was mutated.'
    $control = $script:settings.Studio.ControlRegion
    $script:settings.Studio.Regions[$control].Compute.ManageEgress = $true
    Save-Settings
    $script:fixtureReceipt = Convert-Settings $script:settings
    Run-Case 'owned-nat'
    Check ($script:deleted.Count -eq 2 -and $global:StudioRecoveryTest.detached.Count -eq 1) 'Owned NAT/PIP release did not complete.'
    Run-Case 'foreign-nat' -Failure
    Check ($script:writes.Count -eq 0) 'Foreign NAT association was mutated.'
    Run-Case 'nat-other-subnet' -Failure
    Check ($script:writes.Count -eq 0) 'NAT attached to another subnet was mutated.'
    Fresh-Settings
    $script:fixtureReceipt = Convert-Settings $script:settings
    Run-Case 'absent'
    $subscription = $script:subscription
    Expect-Rejected 'Subscription mismatch' { $script:settings.Studio.SubscriptionId = '44444444-4444-4444-4444-444444444444' }
    $script:settings.Studio.TenantId = '44444444-4444-4444-4444-444444444444'
    Save-Settings
    Run-Case 'absent' -Failure
    Check ($script:reads.Count -eq 0 -and $script:writes.Count -eq 0) 'Tenant mismatch reached Azure.'
    Expect-Rejected 'Control region outside regions' { $script:settings.Studio.ControlRegion = 'northeurope' }
    Expect-Rejected 'Compute of another workspace' { $script:settings.Studio.Regions.eastus2.Compute.Id = $script:settings.Studio.Regions.westeurope.Compute.Id }
    Expect-Rejected 'Wrong resource type' { $script:settings.Studio.Regions.eastus2.Network.NatGatewayId = $script:settings.Studio.Regions.eastus2.Network.PublicIpId }
    Expect-Rejected 'Foreign-subscription resource' { $script:settings.Studio.Regions.eastus2.Network.PublicIpId = $script:settings.Studio.Regions.eastus2.Network.PublicIpId.Replace($subscription, '44444444-4444-4444-4444-444444444444') }
    Expect-Rejected 'Resource shared by two regions' { $script:settings.Studio.Regions.westeurope.Network.NatGatewayId = $script:settings.Studio.Regions.eastus2.Network.NatGatewayId }
    Expect-Rejected 'Missing ownership tag' { $script:settings.Studio.Regions.eastus2.OwnershipTags.Remove('deployment') }
    Expect-Rejected 'Foreign ownership tag' { $script:settings.Studio.Regions.westeurope.OwnershipTags.region = 'eastus2' }
    Expect-Rejected 'Unsafe job limit' { $script:settings.Studio.Regions.eastus2.Compute.MaxJobMinutes = 500 }
    Expect-Rejected 'Non-site Pipeline.SiteId' { $script:settings.Pipeline.SiteId = $script:settings.Studio.Regions.eastus2.Workspace.Id }
    Expect-Rejected 'Missing Studio section' { $script:settings.Remove('Studio') }
    Write-Host "PASS: $global:StudioRecoveryTestChecks native recovery checks passed; no Azure or other external tools used."
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
    Remove-Item Function:\global:az,Function:\global:Invoke-WebRequest,Function:\global:Start-Sleep -ErrorAction SilentlyContinue
    $env:AZURE_CLIENT_ID = $savedClient
    $env:GITHUB_ACTIONS = $savedActions
}
