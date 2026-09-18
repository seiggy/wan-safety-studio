#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Deploy','Prepare','Start','Submit','Stop','Status','Check')]
    [string]$Action = 'Status',
    [ValidateSet('wan','ltx','ltx_i2v','minimax_h3')][string]$Profile = 'wan',
    [switch]$ApprovePersistentCosts,
    [switch]$ApproveGpuSpend,
    [string]$InputImage,
    [string]$LocalModelsPath,
    [ValidateRange(1,120)][int]$TimeoutMinutes = 120,
    [switch]$Wait,
    [string]$DownloadDirectory,
    [switch]$NoPortal,
    [string]$ConfigPath = (Join-Path (Split-Path $PSScriptRoot) 'infra' 'terraform.tfvars.json'),
    [string]$CacheDirectory = $env:WAN_STUDIO_CACHE
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot
$DemoRoot = Join-Path $RepoRoot 'app'
$InfraRoot = Join-Path $RepoRoot 'infra'
$VmSize = 'Standard_NC24ads_A100_v4'
$MlApi = '2024-04-01'
$NetworkApi = '2024-05-01'
$PersistentWarning = 'Stop does NOT mean a zero Azure bill: Premium ACR, storage/models/videos, private endpoints and logging remain. Supplied landing-zone resources are never destroyed.'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Invoke-Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed (exit $LASTEXITCODE)." }
}
function Initialize-Configuration {
    $script:ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    Assert-True (Test-Path -LiteralPath $ConfigPath -PathType Leaf) "Missing customer configuration: $ConfigPath. Copy infra/terraform.tfvars.json.example and supply your landing-zone inputs."
    $script:Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in @('subscription_id','tenant_id','operator_principal_id')) {
        $parsed = [guid]::Empty
        Assert-True ([guid]::TryParseExact([string]$Config[$key], 'D', [ref]$parsed) -and $parsed -ne [guid]::Empty) "Invalid $key."
    }
    Assert-True ($Config.deployment_name -cmatch '^[a-z][a-z0-9-]{1,14}[a-z0-9]$' -and
        -not $Config.deployment_name.Contains('--')) 'deployment_name must be 3-16 lowercase letters/digits/single hyphens, starting with a letter and ending with a letter/digit.'
    Assert-True ($Config.location -cmatch '^[a-z][a-z0-9]+$') 'location must be the Azure region name, such as eastus2.'
    Assert-True ($Config.gpu_subnet_dedicated -is [bool] -and $Config.gpu_subnet_dedicated) 'Explicit gpu_subnet_dedicated=true is required; shared subnet association changes are forbidden.'
    foreach ($key in @('compute_enabled','manage_compute_egress')) {
        if (-not $Config.ContainsKey($key)) { $Config[$key]=$false }
        Assert-True ($Config[$key] -is [bool]) "$key must be a JSON boolean."
    }
    Assert-True (-not $Config.compute_enabled) 'Keep compute_enabled=false in customer configuration. Only Start may enable compute.'
    $script:Subscription = $Config.subscription_id
    $script:Tenant = $Config.tenant_id
    $script:Region = $Config.location
    $script:SubnetId = $Config.gpu_subnet_id
    if (-not $Config.ContainsKey('max_payg_hourly_usd')) { $Config.max_payg_hourly_usd=4 }
    Assert-True ($Config.max_payg_hourly_usd -is [ValueType] -and $Config.max_payg_hourly_usd -isnot [bool]) 'max_payg_hourly_usd must be a JSON number.'
    $script:MaxPaygHourlyUsd = [double]$Config.max_payg_hourly_usd
    Assert-True ([double]::IsFinite($MaxPaygHourlyUsd) -and $MaxPaygHourlyUsd -gt 0) 'max_payg_hourly_usd must be finite and positive.'
    foreach ($key in @('gpu_subnet_id','private_endpoint_subnet_id')) {
        Assert-True ($Config[$key] -imatch "^/subscriptions/$Subscription/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$") "$key must be a full subnet ID in the configured subscription."
    }
    Assert-True ($Config.gpu_subnet_id -ine $Config.private_endpoint_subnet_id) 'GPU and private endpoint subnets must be distinct.'
    [Net.IPNetwork]$network = [Net.IPNetwork]::new([Net.IPAddress]::Any,0)
    Assert-True ([Net.IPNetwork]::TryParse([string]$Config.gpu_subnet_cidr, [ref]$network) -and
        $network.BaseAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        $network.ToString() -ceq $Config.gpu_subnet_cidr) 'gpu_subnet_cidr must be a canonical IPv4 network CIDR, not a host address.'
    Assert-True ($Config.log_analytics_workspace_id -imatch "^/subscriptions/$Subscription/resourceGroups/[^/]+/providers/Microsoft.OperationalInsights/workspaces/[^/]+$") 'Logging workspace must be supplied in the configured subscription.'
    $zones = @{
        blob='privatelink.blob.core.windows.net'; file='privatelink.file.core.windows.net'
        vault='privatelink.vaultcore.azure.net'; registry='privatelink.azurecr.io'
        api='privatelink.api.azureml.ms'; notebooks='privatelink.notebooks.azure.net'
    }
    Assert-True ($Config.private_dns_zone_ids.Count -eq $zones.Count) 'Exactly six private DNS zone IDs must be supplied.'
    foreach ($key in $zones.Keys) {
        Assert-True ($Config.private_dns_zone_ids[$key] -imatch "^/subscriptions/$Subscription/resourceGroups/[^/]+/providers/Microsoft.Network/privateDnsZones/$([regex]::Escape($zones[$key]))$") "Missing/invalid same-subscription private DNS zone: $key."
    }
    if ($CacheDirectory) { $script:Cache = [IO.Path]::GetFullPath($CacheDirectory) }
    else {
        $base = if ($IsWindows) { [Environment]::GetFolderPath('LocalApplicationData') }
            elseif ($IsMacOS) { Join-Path $HOME 'Library' 'Caches' }
            elseif ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME }
            else { Join-Path $HOME '.cache' }
        $script:Cache = Join-Path $base 'wan-safety-studio' "$Subscription-$($Config.deployment_name)"
    }
    New-Item -ItemType Directory -Path $Cache -Force | Out-Null
}
function Set-Foundation($Value) {
    Assert-True ($Value.subscriptionId -ieq $Subscription -and $Value.tenantId -ieq $Tenant -and
        $Value.location -ceq $Region -and $Value.deploymentName -ceq $Config.deployment_name -and
        $Value.gpuSubnetId -ieq $SubnetId -and $Value.gpuSubnetCidr -ceq $Config.gpu_subnet_cidr -and
        $Value.privateEndpointSubnetId -ieq $Config.private_endpoint_subnet_id -and
        $Value.manageComputeEgress -eq [bool]$Config.manage_compute_egress -and
        $Value.maxPaygHourlyUsd -eq $MaxPaygHourlyUsd -and
        $Value.maxJobSeconds -eq 7200 -and $Value.instanceCount -eq 1) 'Terraform outputs differ from the explicit customer scope or safety contract.'
    Assert-True ($Value.resourceGroupName -match '^[a-zA-Z0-9._()-]+$' -and
        $Value.workspaceName -match '^[a-zA-Z0-9_-]+$' -and $Value.computeName -match '^[a-zA-Z0-9_-]+$') 'Invalid output resource names.'
    $group = "/subscriptions/$Subscription/resourceGroups/$($Value.resourceGroupName)"
    Assert-True ($Value.workspaceId -ieq "$group/providers/Microsoft.MachineLearningServices/workspaces/$($Value.workspaceName)" -and
        $Value.computeId -ieq "$($Value.workspaceId)/computes/$($Value.computeName)") 'Inconsistent output workspace/compute IDs.'
    foreach ($entry in @{
        natGatewayId='Microsoft.Network/natGateways'; publicIpId='Microsoft.Network/publicIPAddresses'
        networkSecurityGroupId='Microsoft.Network/networkSecurityGroups'; storageAccountId='Microsoft.Storage/storageAccounts'
        keyVaultId='Microsoft.KeyVault/vaults'; workspaceIdentityId='Microsoft.ManagedIdentity/userAssignedIdentities'
        computeIdentityId='Microsoft.ManagedIdentity/userAssignedIdentities'
    }.GetEnumerator()) {
        Assert-True ($Value[$entry.Key] -imatch "^$([regex]::Escape($group))/providers/$([regex]::Escape($entry.Value))/[^/]+$") "Unexpected owned resource output: $($entry.Key)."
    }
    Assert-True ($Value.registryLoginServer -ceq "$($Value.registryName).azurecr.io" -and
        $Value.storageAccountId -ieq "$group/providers/Microsoft.Storage/storageAccounts/$($Value.storageAccountName)" -and
        $Value.keyVaultId -ieq "$group/providers/Microsoft.KeyVault/vaults/$($Value.keyVaultName)") 'Inconsistent service names in outputs.'
    foreach ($entry in @{application='wan-safety-studio'; deployment=$Config.deployment_name; managedBy='terraform'}.GetEnumerator()) {
        Assert-True ($Value.ownershipTags[$entry.Key] -ceq $entry.Value) "Unexpected ownership tag: $($entry.Key)."
    }
    $script:Foundation = $Value
    $script:GroupId = $group
    $script:WorkspaceId = $Value.workspaceId
    $script:ComputeId = $Value.computeId
    $script:NatId = $Value.natGatewayId
    $script:PipId = $Value.publicIpId
}
function Read-Foundation {
    $path = Join-Path $Cache 'foundation.json'
    Assert-True (Test-Path -LiteralPath $path) "Missing cached Terraform output receipt: $path. Recover with Terraform output before operating; no resources will be guessed."
    Set-Foundation (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable)
}
function Initialize-Demo {
    $context = Invoke-Native az @('account','show','--output','json') | ConvertFrom-Json
    Assert-AzureContext $context
    $raw = Invoke-Native az @('account','get-access-token','--resource','https://management.azure.com/','--query','accessToken','--output','tsv')
    $script:ArmToken = ConvertTo-SecureString ($raw -join '').Trim() -AsPlainText -Force
    $raw = $null
}
function Assert-AzureContext($Context) {
    Assert-True ($Context.id -ieq $Subscription -and $Context.tenantId -ieq $Tenant) 'Wrong Azure CLI context. Use az login --tenant and az account set explicitly; alternate credentials/subscriptions are refused.'
}
function Invoke-Arm([string]$Path, [string]$Api = $MlApi, [string]$Method = 'GET',
    $Body = $null, [switch]$AllowMissing, [hashtable]$Headers = @{}) {
    $uri = if ($Path.StartsWith('https://management.azure.com/')) { $Path } else { "https://management.azure.com${Path}?api-version=$Api" }
    Assert-True ($uri.StartsWith("https://management.azure.com/subscriptions/$Subscription/", [StringComparison]::OrdinalIgnoreCase)) 'ARM request escaped the approved subscription.'
    $parameters = @{
        Uri=$uri; Method=$Method; Authentication='Bearer'; Token=$script:ArmToken
        TimeoutSec=90; SkipHttpErrorCheck=$true; Headers=$Headers
    }
    if ($null -ne $Body) { $parameters.Body = ConvertTo-Json $Body -Depth 50 -Compress; $parameters.ContentType = 'application/json' }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -eq 404 -and $AllowMissing) { return $null }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "ARM $Method failed HTTP $($response.StatusCode) for $Path. $($response.Content)"
    }
    if ($response.Content) { return $response.Content | ConvertFrom-Json -AsHashtable }
}
function Get-ArmList([string]$Path) {
    $page = Invoke-Arm $Path
    while ($page) {
        foreach ($item in $page.value) { $item }
        $page = if ($page.nextLink) { Invoke-Arm $page.nextLink } else { $null }
    }
}
function Assert-Owned($Resource, [string]$ExpectedId) {
    $expectedName = $ExpectedId.Substring($ExpectedId.LastIndexOf('/') + 1)
    Assert-True ($null -ne $Resource -and $Resource.id -eq $ExpectedId -and
        $Resource.name -eq $expectedName) "Unexpected/missing resource: $ExpectedId"
    foreach ($entry in $Foundation.ownershipTags.GetEnumerator()) {
        Assert-True ($Resource.tags[$entry.Key] -ceq $entry.Value) "Ownership tags missing on $ExpectedId; refusing mutation."
    }
}
function Assert-DemoSubnet($Subnet) {
    Assert-True ($null -ne $Subnet -and $Subnet.id -eq $SubnetId -and
        $Subnet.name -eq ($SubnetId -split '/')[-1] -and $Config.gpu_subnet_dedicated -eq $true) 'Refusing mutation of an unexpected/shared customer subnet.'
    $prefixes = @($Subnet.properties.addressPrefixes)
    if ($Subnet.properties.addressPrefix) { $prefixes += $Subnet.properties.addressPrefix }
    $prefixes = @($prefixes | Where-Object { $_ } | Select-Object -Unique)
    Assert-True ($prefixes.Count -eq 1 -and $prefixes[0] -ceq $Config.gpu_subnet_cidr) 'GPU subnet CIDR differs from the explicit dedicated reservation.'
    if ($Subnet.properties.networkSecurityGroup) {
        Assert-True ($Subnet.properties.networkSecurityGroup.id -ieq $Foundation.networkSecurityGroupId) 'Customer subnet has an unrelated NSG; refusing to replace its association.'
    }
    if ($Subnet.properties.natGateway) {
        Assert-True ($Subnet.properties.natGateway.id -ieq $NatId) 'Customer subnet has an unrelated NAT; refusing to replace/detach its association.'
    }
}
function Assert-Foundation {
    Assert-Owned (Invoke-Arm $GroupId '2022-09-01') $GroupId
    Assert-Owned (Invoke-Arm $WorkspaceId) $WorkspaceId
}
function Get-Compute { Invoke-Arm $ComputeId $MlApi -AllowMissing }
function Assert-Cluster($Compute) {
    Assert-Owned $Compute $ComputeId
    $p = $Compute.properties
    $c = $p.properties
    Assert-True ($p.computeType -eq 'AmlCompute' -and $p.provisioningState -eq 'Succeeded' -and $p.disableLocalAuth -eq $true -and
        $c.vmSize -eq $VmSize -and $c.vmPriority -eq 'LowPriority' -and $c.osType -eq 'Linux' -and
        $c.scaleSettings.minNodeCount -eq 0 -and $c.scaleSettings.maxNodeCount -eq 1 -and
        $c.scaleSettings.nodeIdleTimeBeforeScaleDown -in @('PT120S','PT2M') -and
        $c.enableNodePublicIp -eq $false -and $c.remoteLoginPortPublicAccess -eq 'Disabled' -and
        $c.subnet.id -eq $SubnetId -and $Compute.identity.type -eq 'UserAssigned' -and
        @($Compute.identity.userAssignedIdentities.Keys).Count -eq 1 -and
        $Foundation.computeIdentityId -in $Compute.identity.userAssignedIdentities.Keys) 'Live compute violates the one-node private Spot/identity/idle-120 contract.'
}
function Get-DemoJobs {
    @(Get-ArmList "$WorkspaceId/jobs" | Where-Object {
        $_.properties.computeId -eq $ComputeId -or $_.properties.computeId -eq $Foundation.computeName
    })
}
function Get-ActiveJobs($Jobs) {
    @($Jobs | Where-Object { $_.properties.status -notin @('Completed','Failed','Canceled','Cancelled') })
}
function Get-LiveStatus {
    $compute = Get-Compute
    $workspace = Invoke-Arm $WorkspaceId $MlApi -AllowMissing
    $jobs = @(if ($workspace) { Get-DemoJobs })
    $nat = Invoke-Arm $NatId $NetworkApi -AllowMissing
    $pip = Invoke-Arm $PipId $NetworkApi -AllowMissing
    $state = 'OFF'
    $nodes = $null
    if ($compute) {
        $counts = $compute.properties.properties.nodeStateCounts
        if ($null -eq $counts) { $state = 'PARTIAL/ERROR' }
        else {
            $nodes = 0
            foreach ($value in $counts.Values) {
                Assert-True ($null -ne $value -and "$value" -match '^\d+$') 'Unknown node-count format; cannot assert OFF.'
                $nodes += [int]$value
            }
            $state = if ($nodes -gt 0) { 'RUNNING/NODESALLOCATED' } else { 'IDLE (0 nodes; armed)' }
            if ($compute.properties.provisioningState -ne 'Succeeded') { $state = 'PARTIAL/ERROR' }
        }
    } elseif ($nat -or $pip -or (Get-ActiveJobs $jobs).Count) { $state = 'PARTIAL/ERROR' }
    [pscustomobject]@{
        State=$state; Nodes=$nodes; ComputePresent=($null -ne $compute)
        NatPresent=($null -ne $nat); PublicIpPresent=($null -ne $pip)
        ActiveJobs=@(Get-ActiveJobs $jobs | ForEach-Object { "$($_.name):$($_.properties.status)" })
    }
}
function Select-PaygRate($Items) {
    $rates = @($Items | Where-Object {
        $_.armSkuName -ceq $VmSize -and $_.armRegionName -ceq $Region -and
        $_.type -eq 'Consumption' -and $_.currencyCode -eq 'USD' -and $_.unitOfMeasure -eq '1 Hour' -and
        $_.isPrimaryMeterRegion -eq $true -and $_.productName -match '^Virtual Machines ' -and $_.productName -notmatch 'Windows' -and
        $_.skuName -notmatch 'Spot|Low Priority' -and $_.meterName -notmatch 'Spot|Low Priority'
    })
    Assert-True ($rates.Count -eq 1) 'Missing/ambiguous exact Linux PAYG rate; Start blocked.'
    $price = [double]$rates[0].retailPrice
    Assert-True ([double]::IsFinite($price) -and $price -gt 0 -and $price -le $MaxPaygHourlyUsd) "Published PAYG $price USD/node-hour is invalid or exceeds the approved $MaxPaygHourlyUsd ceiling."
    return $price
}
function Assert-Quota($Usages, [int]$AlreadyAllocatedCores = 0) {
    Assert-True ($AlreadyAllocatedCores -in @(0,24)) 'Unexpected current allocation.'
    $total = @($Usages | Where-Object { $_.name.value -eq 'TotalLowPriorityCores' })
    $family = @($Usages | Where-Object {
        $_.name.value -eq 'standardNCADSA100v4Family' -and
        $_.type -eq 'Microsoft.MachineLearningServices/vmFamily/lowPriorityCores/usages'
    })
    Assert-True ($total.Count -eq 1 -and $family.Count -eq 1) 'Missing/ambiguous AML Spot quota or A100 family quota.'
    foreach ($q in @($total[0], $family[0])) {
        Assert-True ($null -ne $q.currentValue -and $null -ne $q.limit -and
            "$($q.currentValue)" -match '^\d+$' -and "$($q.limit)" -match '^-?\d+$') 'Malformed AML quota.'
    }
    Assert-True ($total[0].limit -ge 24 -and $total[0].currentValue -ge $AlreadyAllocatedCores -and
        ($total[0].limit - $total[0].currentValue + $AlreadyAllocatedCores) -ge 24) 'Insufficient TotalLowPriorityCores for 24 cores.'
    Assert-True ($family[0].limit -eq -1 -or
        ($family[0].limit -ge 24 -and $family[0].currentValue -ge $AlreadyAllocatedCores -and
        ($family[0].limit - $family[0].currentValue + $AlreadyAllocatedCores) -ge 24)) 'Insufficient A100 Spot family quota (-1 is shared pool, not unavailable).'
}
function Assert-LiveCost {
    $filter = [Uri]::EscapeDataString("armSkuName eq '$VmSize' and armRegionName eq '$Region' and priceType eq 'Consumption'")
    $url = "https://prices.azure.com/api/retail/prices?currencyCode='USD'&`$filter=$filter"
    $items = @()
    while ($url) {
        Assert-True ($url.StartsWith('https://prices.azure.com/')) 'Unexpected retail continuation origin.'
        $page = Invoke-RestMethod $url -TimeoutSec 90
        $items += $page.Items
        $url = $page.NextPageLink
    }
    $price = Select-PaygRate $items
    $skus = Invoke-Native az @('vm','list-skus','--location',$Region,'--size',$VmSize,'--all','--output','json') | ConvertFrom-Json
    $sku = @($skus | Where-Object { $_.name -ceq $VmSize -and $_.resourceType -eq 'virtualMachines' })
    Assert-True ($sku.Count -eq 1 -and @($sku[0].restrictions).Count -eq 0) 'A100 SKU is absent, ambiguous or restricted; no fallback is allowed.'
    $cores = 0
    $compute = Get-Compute
    if ($compute) {
        Assert-Cluster $compute
        $status = Get-LiveStatus
        Assert-True ($null -ne $status.Nodes -and $status.Nodes -in @(0,1)) 'Cannot establish current node allocation.'
        $cores = 24 * $status.Nodes
    }
    Assert-Quota @(Get-ArmList "/subscriptions/$Subscription/providers/Microsoft.MachineLearningServices/locations/$Region/usages") $cores
    Write-Host "LIVE Linux PAYG check: $price USD/node-hour <= $MaxPaygHourlyUsd. This is not a Spot max-bid setting or billing cap; capacity is not guaranteed."
}
function Assert-PrivateConnectivity {
    Assert-True (@($Foundation.privateConnectivityHosts).Count -ge 5) 'Missing private service connectivity endpoints in Terraform outputs.'
    foreach ($hostName in $Foundation.privateConnectivityHosts) {
        $addresses = @([Net.Dns]::GetHostAddresses($hostName) | Where-Object AddressFamily -eq InterNetwork)
        Assert-True ($addresses.Count -gt 0 -and @($addresses | Where-Object {
            $b=$_.GetAddressBytes(); -not ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168))
        }).Count -eq 0) "Private DNS/VPN required: $hostName resolved outside private address space."
        $client = [Net.Sockets.TcpClient]::new()
        try { $client.ConnectAsync($hostName,443).WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() }
        finally { $client.Dispose() }
    }
}
function Invoke-Terraform([string[]]$Arguments, [switch]$Capture) {
    Assert-TerraformInputs
    $executable = (Get-Command terraform -CommandType Application | Select-Object -First 1).Source
    $info = New-RuntimeProcessInfo $executable (@("-chdir=$InfraRoot") + $Arguments)
    $info.Environment['ARM_SUBSCRIPTION_ID'] = $Subscription
    $info.Environment['ARM_TENANT_ID'] = $Tenant
    $info.Environment['ARM_USE_CLI'] = 'true'
    $info.Environment['ARM_USE_MSI'] = 'false'
    $info.Environment['ARM_USE_OIDC'] = 'false'
    $info.Environment['TF_IN_AUTOMATION'] = '1'
    $info.Environment['TF_INPUT'] = '0'
    $info.RedirectStandardOutput = $Capture.IsPresent
    $process = [Diagnostics.Process]::Start($info)
    try {
        $text = if ($Capture) { $process.StandardOutput.ReadToEnd() }
        $process.WaitForExit()
        Assert-True ($process.ExitCode -eq 0) "Terraform failed (exit $($process.ExitCode)); inspect its error before continuing."
        if ($Capture) { return $text }
    } finally { $process.Dispose() }
}
function Assert-TerraformInputs {
    foreach ($file in Get-ChildItem -LiteralPath $InfraRoot -File) {
        if ($file.Name -in @('terraform.tfvars','terraform.tfvars.json') -or $file.Name -match '\.auto\.tfvars(\.json)?$') {
            Assert-True ($file.FullName -eq $ConfigPath) "Additional automatic Terraform input file is not allowed: $($file.Name). Use only the selected customer JSON configuration."
        }
    }
}
function Set-ComputeEnabled([bool]$Enabled) {
    $value = $Enabled.ToString().ToLowerInvariant()
    Invoke-Terraform @('apply','-input=false','-auto-approve',"-var-file=$ConfigPath","-var=compute_enabled=$value")
}
function Save-Outputs {
    New-Item -ItemType Directory -Path $Cache -Force | Out-Null
    $outputs = Invoke-Terraform @('output','-json','studio') -Capture | ConvertFrom-Json -AsHashtable
    Set-Foundation $outputs
    $temporary = Join-Path $Cache 'foundation.pending'
    $outputs | ConvertTo-Json -Depth 20 | Set-Content $temporary
    Move-Item $temporary (Join-Path $Cache 'foundation.json') -Force
}
function Initialize-Terraform {
    Invoke-Terraform @('init','-input=false')
}
function Get-RuntimeEnvironment([System.Collections.IDictionary]$Source = [Environment]::GetEnvironmentVariables()) {
    $allowed = @(
        'SystemRoot','WINDIR','COMSPEC','PATH','PATHEXT','USERPROFILE','HOMEDRIVE','HOMEPATH',
        'APPDATA','LOCALAPPDATA','PROGRAMDATA','ProgramFiles','ProgramFiles(x86)','ProgramW6432',
        'HOME','XDG_CACHE_HOME','LANG','LC_ALL','TMPDIR','TEMP','TMP','OS','NUMBER_OF_PROCESSORS','PROCESSOR_ARCHITECTURE',
        'AZURE_CONFIG_DIR','DOCKER_HOST','DOCKER_CONTEXT','DOCKER_CONFIG','DOCKER_CERT_PATH','DOCKER_TLS_VERIFY',
        'SSL_CERT_FILE','SSL_CERT_DIR','REQUESTS_CA_BUNDLE','CURL_CA_BUNDLE',
        'UV_PROJECT_ENVIRONMENT','UV_CACHE_DIR','UV_NATIVE_TLS','PYTHONDONTWRITEBYTECODE'
    )
    $clean = @{}
    foreach ($entry in $Source.GetEnumerator()) {
        if ($entry.Key -in $allowed) { $clean[$entry.Key] = [string]$entry.Value }
    }
    $clean.AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
    $clean.AZUREML_SUBSCRIPTION_ID = $Subscription
    $clean.WAN_STUDIO_CONFIG = $ConfigPath
    $clean.WAN_STUDIO_FOUNDATION = Join-Path $Cache 'foundation.json'
    if ($Foundation) {
        $clean.AZUREML_RESOURCE_GROUP = $Foundation.resourceGroupName
        $clean.AZUREML_WORKSPACE_NAME = $Foundation.workspaceName
        $clean.AZUREML_COMPUTE = $Foundation.computeName
    }
    if ($Source['WAN_STUDIO_PYPI_INDEX']) {
        $index = [uri]$Source['WAN_STUDIO_PYPI_INDEX']
        Assert-True ($index.IsAbsoluteUri -and $index.Scheme -ceq 'https' -and -not $index.UserInfo -and
            -not $index.Query -and -not $index.Fragment) 'Package mirror must use HTTPS with no embedded credentials, query or fragment.'
        $clean.WAN_STUDIO_PYPI_INDEX = $index.AbsoluteUri
    }
    return $clean
}
function New-RuntimeProcessInfo([string]$Executable, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.Environment.Clear()
    foreach ($entry in (Get-RuntimeEnvironment).GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return $info
}
function Invoke-RuntimeProcess([string]$Executable, [string[]]$Arguments) {
    $process = [Diagnostics.Process]::Start((New-RuntimeProcessInfo $Executable $Arguments))
    try {
        $process.WaitForExit()
        Assert-True ($process.ExitCode -eq 0) "$Executable failed (exit $($process.ExitCode))."
    } finally { $process.Dispose() }
}
function Invoke-Runtime([string[]]$Arguments) {
    $python = Get-RuntimePython
    Assert-True (Test-Path $python) 'Run Prepare first (locked uv environment missing).'
    Invoke-RuntimeProcess $python (@((Join-Path $DemoRoot 'runtime.py'),'--cache',$Cache) + $Arguments)
}
function Get-RuntimePython {
    if ($IsWindows) { return Join-Path $Cache 'venv' 'Scripts' 'python.exe' }
    return Join-Path $Cache 'venv' 'bin' 'python'
}
function Install-RuntimeDependencies([string]$PythonProject) {
    $uv = (Get-Command uv -CommandType Application | Select-Object -First 1).Source
    $mirror = (Get-RuntimeEnvironment)['WAN_STUDIO_PYPI_INDEX']
    if (-not $mirror) {
        Invoke-RuntimeProcess $uv @('--no-config','sync','--project',$PythonProject,'--python','3.12','--locked','--native-tls','--no-dev')
        return
    }
    $requirements = Join-Path $PythonProject 'requirements-locked.txt'
    Invoke-RuntimeProcess $uv @('--no-config','--quiet','export','--project',$PythonProject,'--locked','--offline','--no-dev',
        '--no-emit-project','--no-header','--no-annotate','--format','requirements.txt','-o',$requirements)
    $python = Get-RuntimePython
    if (-not (Test-Path -LiteralPath $python)) {
        Invoke-RuntimeProcess $uv @('--no-config','venv','--python','3.12',(Join-Path $Cache 'venv'))
    }
    Invoke-RuntimeProcess $uv @('--no-config','pip','sync','--python',$python,'--require-hashes','--only-binary',':all:',
        '--default-index',$mirror,'--keyring-provider','disabled','--native-tls','--strict',$requirements)
}
function Get-Prepared {
    $path = Join-Path $Cache "prepared-$Profile.json"
    Assert-True (Test-Path $path) "Profile $Profile is unprepared; Start cannot download models on GPU."
    Invoke-Runtime @('verify','--profile',$Profile)
    return Get-Content $path -Raw | ConvertFrom-Json
}
function Stop-Portal {
    $statePath = Join-Path $Cache 'portal.json'
    if (-not (Test-Path $statePath)) { return }
    $state = Get-Content $statePath -Raw | ConvertFrom-Json
    $process = Get-Process -Id $state.pid -ErrorAction SilentlyContinue
    if ($process) {
        $command = Get-ProcessCommandLine $state.pid
        Assert-True ($process.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$state.startTime).UtcDateTime -and
            $command.Contains($state.script) -and $command.Contains('portal') -and
            $process.Path -eq $state.executable) 'Portal PID reused or command changed; refusing to kill an unrelated process.'
        Stop-Process -Id $state.pid -Force
        Assert-True ($process.WaitForExit(10000)) 'Portal termination not verified; local submissions may still be available.'
    }
    Remove-Item $statePath
}
function Get-ProcessCommandLine([int]$ProcessId) {
    if ($IsWindows) { return (Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId").CommandLine }
    if ($IsLinux) { return (Get-Content -LiteralPath "/proc/$ProcessId/cmdline" -Raw).Replace([char]0, ' ') }
    $ps = (Get-Command ps -CommandType Application | Select-Object -First 1).Source
    return (Invoke-Native $ps @('-p',"$ProcessId",'-o','command=') -join ' ')
}
function Disable-LocalSubmissions {
    $gate = Join-Path $Cache 'armed.json'
    if (Test-Path $gate) { Remove-Item $gate }
    Stop-Portal
}
function Wait-Absent([string]$Id, [string]$Api, [int]$Minutes = 30) {
    $deadline = [DateTime]::UtcNow.AddMinutes($Minutes)
    do {
        if ($null -eq (Invoke-Arm $Id $Api -AllowMissing)) { return }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Release NOT verified: $Id still exists after $Minutes minutes. Run Stop again; GPU/network charges may continue." }
        Start-Sleep -Seconds 10
    } while ($true)
}
function Repair-ReleasedState {
    foreach ($id in @($ComputeId,$NatId,$PipId)) {
        $api = if ($id -eq $ComputeId) { $MlApi } else { $NetworkApi }
        Assert-True ($null -eq (Invoke-Arm $id $api -AllowMissing)) "Refusing to forget a live resource: $id"
    }
    $subnet = Invoke-Arm $SubnetId $NetworkApi
    Assert-DemoSubnet $subnet
    Assert-True (-not $subnet.properties.natGateway) 'NAT association release not verified; state reconciliation refused.'
    $state = Invoke-Terraform @('show','-json') -Capture | ConvertFrom-Json -AsHashtable
    Assert-True ($state.format_version -eq '1.0' -and $state.values.root_module -and
        -not $state.values.root_module.child_modules) 'Unexpected Terraform state format/module layout; automatic reconciliation refused.'
    $resources = @($state.values.root_module.resources)
    $targets = Get-ReleaseStateTargets
    foreach ($target in $targets) {
        $matches = @($resources | Where-Object { $_.address -ceq $target.address })
        Assert-True ($matches.Count -le 1) 'Duplicate Terraform state address.'
        if ($matches.Count) {
            Assert-True ($matches[0].values.id -ieq $target.id) "State address $($target.address) belongs to an unexpected resource; no state was removed."
        }
        $duplicates = @($resources | Where-Object { $_.mode -eq 'managed' -and $_.values.id -ieq $target.id -and $_.address -cne $target.address })
        # Both NSG and NAT associations legitimately use the dedicated subnet ID.
        if ($target.id -ine $SubnetId) {
            Assert-True ($duplicates.Count -eq 0) 'Resource is tracked at an unexpected additional state address.'
        }
    }
    foreach ($target in $targets) {
        if (@($resources | Where-Object { $_.address -ceq $target.address }).Count) {
            Invoke-Terraform @('state','rm','-lock-timeout=60s',$target.address)
        }
    }
    Invoke-Terraform @('apply','-refresh-only','-input=false','-auto-approve',"-var-file=$ConfigPath",'-var=compute_enabled=false')
    Save-Outputs
}
function Get-ReleaseStateTargets {
    @(
        @{address='azapi_resource.compute[0]'; id=$ComputeId}
        @{address='azurerm_subnet_nat_gateway_association.compute[0]'; id=$SubnetId}
        @{address='azurerm_nat_gateway_public_ip_association.compute[0]'; id="$NatId|$PipId"}
        @{address='azurerm_nat_gateway.compute[0]'; id=$NatId}
        @{address='azurerm_public_ip.compute[0]'; id=$PipId}
    )
}
function Wait-NoActiveDemoJobs([int]$Minutes = 5) {
    $deadline = [DateTime]::UtcNow.AddMinutes($Minutes)
    while (@(Get-ActiveJobs @(Get-DemoJobs)).Count) {
        Assert-True ([DateTime]::UtcNow -lt $deadline) 'Demo jobs are still nonterminal after compute deletion; Start must remain blocked.'
        Start-Sleep -Seconds 5
    }
}
function Stop-Demo {
    # Emergency release deliberately has no Prepare, Python, Docker, retail or preview prerequisite.
    $errors = [Collections.Generic.List[string]]::new()
    $cancellationErrors = [Collections.Generic.List[string]]::new()
    $releaseMarker = Join-Path $Cache 'release-required.json'
    try {
        @{computeId=$ComputeId; requestedAt=[DateTime]::UtcNow.ToString('o')} |
            ConvertTo-Json | Set-Content $releaseMarker
    } catch { $errors.Add("Persist release status: $_") }
    $gate = Join-Path $Cache 'armed.json'
    try { if (Test-Path $gate) { Remove-Item $gate } } catch { $errors.Add("Disable local submissions: $_") }
    try { Stop-Portal } catch { $errors.Add("Local portal: $_") }
    $group = Invoke-Arm $GroupId '2022-09-01' -AllowMissing
    if ($group) { Assert-Owned $group $GroupId }
    $workspace = Invoke-Arm $WorkspaceId $MlApi -AllowMissing
    if ($workspace) {
        Assert-Owned $workspace $WorkspaceId
        try {
            foreach ($job in @(Get-ActiveJobs @(Get-DemoJobs))) {
                try { Invoke-Arm "$WorkspaceId/jobs/$([Uri]::EscapeDataString($job.name))/cancel" $MlApi 'POST' | Out-Null }
                catch { $cancellationErrors.Add("Cancel job $($job.name): $_") }
            }
        } catch { $cancellationErrors.Add("List jobs: $_") }
        $compute = Get-Compute
        if ($compute) {
            Assert-Owned $compute $ComputeId
            Invoke-Arm $ComputeId "$MlApi&underlyingResourceAction=Delete" 'DELETE' | Out-Null
            Wait-Absent $ComputeId $MlApi
        }
    }
    Assert-True ($null -eq (Get-Compute)) 'GPU release NOT verified. NAT/PIP preserved until compute is absent.'
    $subnet = Invoke-Arm $SubnetId $NetworkApi -AllowMissing
    if ($subnet) { Assert-DemoSubnet $subnet }
    if ($subnet -and $subnet.properties.natGateway) {
        Assert-True ($subnet.properties.natGateway.id -eq $NatId) 'Subnet references an unrelated NAT; refusing to detach it.'
        $properties = $subnet.properties
        foreach ($key in @('natGateway','provisioningState','ipConfigurations','resourceNavigationLinks','serviceAssociationLinks','privateEndpoints','purpose')) { $properties.Remove($key) }
        Invoke-Arm $SubnetId $NetworkApi 'PUT' @{properties=$properties} -Headers @{'If-Match'=$subnet.etag} | Out-Null
        $deadline = [DateTime]::UtcNow.AddMinutes(10)
        do {
            $subnet = Invoke-Arm $SubnetId $NetworkApi
            Assert-DemoSubnet $subnet
            if (-not $subnet.properties.natGateway) { break }
            Assert-True ([DateTime]::UtcNow -lt $deadline) 'GPU absent but NAT detach not verified; network billing may continue.'
            Start-Sleep 5
        } while ($true)
    }
    foreach ($id in @($NatId,$PipId)) {
        $resource = Invoke-Arm $id $NetworkApi -AllowMissing
        if ($resource) {
            Assert-Owned $resource $id
            Invoke-Arm $id $NetworkApi 'DELETE' | Out-Null
            Wait-Absent $id $NetworkApi 10
        }
    }
    if ($workspace) {
        try {
            Wait-NoActiveDemoJobs
            foreach ($problem in $cancellationErrors) {
                Write-Warning "$problem; subsequent ARM reads confirm all demo jobs are terminal after compute deletion."
            }
        } catch {
            $errors.Add("Job termination not verified: $_")
            $errors.AddRange($cancellationErrors)
        }
    }
    if (Get-Command terraform -ErrorAction SilentlyContinue) {
        try {
            Repair-ReleasedState
        } catch { $errors.Add("Compute/NAT/PIP absence verified; Terraform reconciliation failed: $_") }
    } else { $errors.Add('Compute/NAT/PIP absence verified; Terraform unavailable. Re-run Stop to reconcile state before Start.') }
    if ($errors.Count) { throw ($errors -join "`n") }
    Remove-Item -LiteralPath $releaseMarker
    Write-Host 'VERIFIED OFF: owned compute, NAT and public IP absent; all owned-compute jobs terminal; Terraform state reconciled.'
}
function Start-Portal {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,51881)
    try { $listener.Start() } finally { $listener.Stop() }
    $executable = Get-RuntimePython
    $scriptPath = [IO.Path]::GetFullPath((Join-Path $DemoRoot 'runtime.py'))
    $psi = New-RuntimeProcessInfo $executable @($scriptPath,'--cache',$Cache,'portal','--profile',$Profile)
    $process = [Diagnostics.Process]::Start($psi)
    @{
        pid=$process.Id; startTime=$process.StartTime.ToUniversalTime().ToString('o')
        script=$scriptPath; executable=$executable
    } | ConvertTo-Json | Set-Content (Join-Path $Cache 'portal.json')
    $healthy = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        if ($process.HasExited) { throw 'Portal exited before health check.' }
        try { $healthy = (Invoke-WebRequest 'http://127.0.0.1:51881/healthz' -TimeoutSec 2).StatusCode -eq 200 } catch {}
        if ($healthy) { break }
        Start-Sleep 1
    }
    Assert-True $healthy 'Portal did not become healthy.'
    Write-Host 'Portal: http://127.0.0.1:51881/ (loopback only; one prepared profile). Run Stop in another terminal.'
}
function Start-Demo {
    Assert-True $ApproveGpuSpend.IsPresent 'Start requires -ApproveGpuSpend (variable Spot, maximum one node).'
    Assert-True (-not (Test-Path (Join-Path $Cache 'release-required.json'))) 'A previous Stop did not finish. Re-run Stop and verify release/state reconciliation before Start.'
    Assert-Foundation
    Assert-DemoSubnet (Invoke-Arm $SubnetId $NetworkApi)
    $manifest = Get-Prepared
    Assert-PrivateConnectivity
    Assert-True ((Get-ActiveJobs @(Get-DemoJobs)).Count -eq 0) 'Existing queued/running/unknown-state jobs could resume on Start. Stop/cancel them first.'
    if (-not $NoPortal) {
        Assert-True (-not (Test-Path (Join-Path $Cache 'portal.json'))) 'A portal state exists. Stop first.'
        $testListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,51881)
        try { $testListener.Start() } finally { $testListener.Stop() }
    }
    Assert-LiveCost
    try {
        Set-ComputeEnabled $true
        Save-Outputs
        Assert-Cluster (Get-Compute)
        @{compute=$Foundation.computeName; profile=$Profile; version=$manifest.version} |
            ConvertTo-Json | Set-Content (Join-Path $Cache 'armed.json')
        if (-not $NoPortal) { Start-Portal }
        Write-Host 'Armed at min=0/max=1; only jobs allocate Spot GPU nodes. Each job has a server-side 120-minute maximum, not a global 2-hour or dollar budget.'
    } catch {
        $failure = $_
        try { Stop-Demo } catch { Write-Error "Start failed AND emergency cleanup failed: $_" -ErrorAction Continue }
        throw $failure
    }
}

if ($Action -eq 'Check') {
    & (Join-Path $DemoRoot 'Test-Controls.ps1')
    return
}
# Dot-sourcing is supported only by the framework-free offline test.
if ($MyInvocation.InvocationName -eq '.') { return }
Initialize-Configuration
if ($Action -eq 'Stop') {
    try { Disable-LocalSubmissions }
    catch { Write-Warning "Local shutdown failed; native release will still be attempted: $_" }
}
if ($Action -ne 'Deploy') { Read-Foundation }
Initialize-Demo
Write-Warning $PersistentWarning
switch ($Action) {
    'Status' { Get-LiveStatus | Format-List }
    'Stop' { Stop-Demo }
    'Deploy' {
        Assert-True $ApprovePersistentCosts.IsPresent 'Deploy requires -ApprovePersistentCosts.'
        Assert-True (-not (Test-Path (Join-Path $Cache 'release-required.json'))) 'A previous Stop did not finish. Re-run Stop before Deploy.'
        Initialize-Terraform
        $current = Invoke-Terraform @('output','-json') -Capture | ConvertFrom-Json -AsHashtable
        if ($current.studio) {
            Set-Foundation $current.studio.value
            Assert-True ($null -eq (Get-Compute)) 'Compute already exists (even if idle). Stop first; Deploy will not change an armed studio.'
            Assert-Owned (Invoke-Arm $GroupId '2022-09-01') $GroupId
            Assert-DemoSubnet (Invoke-Arm $SubnetId $NetworkApi)
        }
        Set-ComputeEnabled $false
        Save-Outputs
        Assert-True ($null -eq (Get-Compute)) 'Deploy unexpectedly created compute.'
        Get-LiveStatus | Format-List
    }
    'Prepare' {
        Assert-Foundation
        Assert-True ($null -eq (Get-Compute)) 'Stop first: Prepare must run with compute absent.'
        Assert-PrivateConnectivity
        Save-Outputs
        $pythonProject = Join-Path $Cache 'python-project'
        New-Item -ItemType Directory $pythonProject -Force | Out-Null
        Copy-Item (Join-Path $DemoRoot 'pyproject.toml') (Join-Path $pythonProject 'pyproject.toml')
        Copy-Item (Join-Path $DemoRoot 'uv.lock') (Join-Path $pythonProject 'uv.lock')
        $env:UV_PROJECT_ENVIRONMENT = Join-Path $Cache 'venv'
        Install-RuntimeDependencies $pythonProject
        $prepareArguments = @('prepare','--profile',$Profile)
        if ($LocalModelsPath) {
            $prepareArguments += @('--local-models-path',[IO.Path]::GetFullPath($LocalModelsPath))
        }
        Invoke-Runtime $prepareArguments
    }
    'Start' { Start-Demo }
    'Submit' {
        Assert-Foundation
        Assert-Cluster (Get-Compute)
        $null = Get-Prepared
        Assert-True ($Profile -eq 'wan' -and $InputImage -and (Test-Path -LiteralPath $InputImage -PathType Leaf)) 'Smoke Submit requires prepared WAN and -InputImage <synthetic PNG>. Other prepared profiles use the portal.'
        $arguments = @('submit','--profile',$Profile,'--input-image',[IO.Path]::GetFullPath($InputImage),'--timeout-minutes',"$TimeoutMinutes")
        if ($Wait) {
            Assert-True ([bool]$DownloadDirectory) '-Wait requires -DownloadDirectory for MP4 validation.'
            $arguments += @('--wait','--download-directory',[IO.Path]::GetFullPath($DownloadDirectory))
        }
        Invoke-Runtime $arguments
    }
}
