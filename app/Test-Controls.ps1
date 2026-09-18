#requires -Version 7.4
param([switch]$PowerShellOnly)
$ErrorActionPreference = 'Stop'
$operator = Join-Path (Split-Path $PSScriptRoot) 'scripts' 'Invoke-WanSafetyStudio.ps1'
$errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($operator, [ref]$null, [ref]$errors)
if ($errors) { throw ($errors.Message -join "`n") }
. $operator -Action Status

function Must-Reject([scriptblock]$Test) {
    $rejected = $false
    try { & $Test | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'A fail-closed test unexpectedly accepted unsafe input.' }
}
function Copy-Value($Value) { $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable }
$checkRoot = Join-Path $PSScriptRoot '.check' ("ps-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $checkRoot -Force | Out-Null
try {
    $ConfigPath = Join-Path $checkRoot 'inputs.json'
    $CacheDirectory = Join-Path $checkRoot 'cache'
    $infra = Join-Path (Split-Path $PSScriptRoot) 'infra'
    $example = Get-Content (Join-Path $infra 'terraform.tfvars.json.example') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($mode in @('off','on')) {
        $realReceipt=Get-Content (Join-Path $infra 'tests' 'fixtures' "studio-$mode.json") -Raw | ConvertFrom-Json -AsHashtable
        $example.manage_compute_egress=$realReceipt.manageComputeEgress
        $example | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath
        Initialize-Configuration
        Set-Foundation $realReceipt
        Assert-True ($Foundation.computeName -eq 'wan-gpu' -and $Foundation.datastoreName -eq 'wan_blob') 'Actual Terraform receipt was not consumed correctly.'
    }
    $sub = '11111111-1111-1111-1111-111111111111'
    $tenant = '22222222-2222-2222-2222-222222222222'
    $network = "/subscriptions/$sub/resourceGroups/customer-network/providers/Microsoft.Network"
    $inputValues = @{
        deployment_name='example'; subscription_id=$sub; tenant_id=$tenant
        operator_principal_id='33333333-3333-3333-3333-333333333333'; location='eastus2'
        gpu_subnet_id="$network/virtualNetworks/customer-vnet/subnets/gpu"; gpu_subnet_cidr='10.20.0.0/26'
        private_endpoint_subnet_id="$network/virtualNetworks/customer-vnet/subnets/endpoints"
        gpu_subnet_dedicated=$true; compute_enabled=$false; manage_compute_egress=$true; max_payg_hourly_usd=4
        log_analytics_workspace_id="/subscriptions/$sub/resourceGroups/customer-logs/providers/Microsoft.OperationalInsights/workspaces/logs"
        private_dns_zone_ids=@{
            blob="$network/privateDnsZones/privatelink.blob.core.windows.net"
            file="$network/privateDnsZones/privatelink.file.core.windows.net"
            vault="$network/privateDnsZones/privatelink.vaultcore.azure.net"
            registry="$network/privateDnsZones/privatelink.azurecr.io"
            api="$network/privateDnsZones/privatelink.api.azureml.ms"
            notebooks="$network/privateDnsZones/privatelink.notebooks.azure.net"
        }
    }
    $inputValues | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath
    Initialize-Configuration
    Assert-AzureContext @{id=$sub; tenantId=$tenant}
    Must-Reject { Assert-AzureContext @{id=$tenant; tenantId=$tenant} }
    Must-Reject { Assert-AzureContext @{id=$sub; tenantId=$sub} }
    foreach ($change in @(
        @{subscription_id='bad'}, @{deployment_name='../unsafe'}, @{deployment_name='bad--slug'}, @{deployment_name='bad-'},
        @{gpu_subnet_dedicated=$false}, @{manage_compute_egress='false'}, @{max_payg_hourly_usd='4'},
        @{gpu_subnet_id="$network/virtualNetworks/customer-vnet/subnets/endpoints"},
        @{private_endpoint_subnet_id=$inputValues.gpu_subnet_id}, @{compute_enabled=$true},
        @{gpu_subnet_cidr='10.20.0.0/8'}, @{max_payg_hourly_usd=0}
    )) {
        $bad = Copy-Value $inputValues
        foreach ($key in $change.Keys) { $bad[$key]=$change[$key] }
        $bad | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath
        Must-Reject { Initialize-Configuration }
    }
    $inputValues | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath
    Initialize-Configuration
    $group = "/subscriptions/$sub/resourceGroups/rg-example"
    $workspace = "$group/providers/Microsoft.MachineLearningServices/workspaces/mlw-example"
    $receipt = @{
        subscriptionId=$sub; tenantId=$tenant; location='eastus2'; deploymentName='example'
        resourceGroupName='rg-example'; workspaceName='mlw-example'; workspaceId=$workspace
        computeName='gpu-example'; computeId="$workspace/computes/gpu-example"
        storageAccountName='stexample'; storageAccountId="$group/providers/Microsoft.Storage/storageAccounts/stexample"
        registryName='crexample'; registryLoginServer='crexample.azurecr.io'
        keyVaultName='kv-example'; keyVaultId="$group/providers/Microsoft.KeyVault/vaults/kv-example"
        containerName='studio'; datastoreName='studio'
        workspaceIdentityId="$group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-workspace"
        computeIdentityId="$group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-gpu"
        computeIdentityClientId='44444444-4444-4444-4444-444444444444'
        gpuSubnetId=$inputValues.gpu_subnet_id; gpuSubnetCidr=$inputValues.gpu_subnet_cidr
        privateEndpointSubnetId=$inputValues.private_endpoint_subnet_id
        natGatewayId="$group/providers/Microsoft.Network/natGateways/nat-example"
        publicIpId="$group/providers/Microsoft.Network/publicIPAddresses/pip-example"
        networkSecurityGroupId="$group/providers/Microsoft.Network/networkSecurityGroups/nsg-example"
        ownershipTags=@{application='wan-safety-studio'; deployment='example'; managedBy='terraform'}
        maxJobSeconds=7200; instanceCount=1; computeEnabled=$false; manageComputeEgress=$true; maxPaygHourlyUsd=4
        privateConnectivityHosts=@('stexample.blob.core.windows.net','stexample.file.core.windows.net',
            'crexample.azurecr.io','kv-example.vault.azure.net','example.api.azureml.ms')
    }
    Set-Foundation $receipt
    foreach ($change in @(
        @{subscriptionId=$tenant}, @{tenantId=$sub}, @{deploymentName='other'},
        @{computeId="$workspace/computes/other"}, @{natGatewayId="$network/natGateways/customer"},
        @{gpuSubnetCidr='10.21.0.0/26'}, @{maxJobSeconds=7201}, @{instanceCount=2},
        @{ownershipTags=@{application='wan-safety-studio'; deployment='other'; managedBy='terraform'}}
    )) {
        $bad = Copy-Value $receipt
        foreach ($key in $change.Keys) { $bad[$key]=$change[$key] }
        Must-Reject { Set-Foundation $bad }
    }
    $receipt | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Cache 'foundation.json')
    Read-Foundation
    $originalInfra=$InfraRoot
    $InfraRoot=Join-Path $checkRoot 'infra'
    New-Item -ItemType Directory $InfraRoot -Force | Out-Null
    try {
        Assert-TerraformInputs
        '{}' | Set-Content (Join-Path $InfraRoot 'unexpected.auto.tfvars.json')
        Must-Reject { Assert-TerraformInputs }
    } finally { $InfraRoot=$originalInfra }
    $timeComparison = (Get-Command Stop-Portal).ScriptBlock.Ast.Find({
        param($node)
        $node -is [Management.Automation.Language.BinaryExpressionAst] -and $node.Operator -eq 'Ieq' -and
            $node.Extent.Text.StartsWith('$process.StartTime.ToUniversalTime()')
    }, $true)
    $compareStartTime = [scriptblock]::Create($timeComparison.Extent.Text)
    $stamp = '2026-09-17T17:12:07.0161222Z'
    $process = [pscustomobject]@{StartTime=[DateTimeOffset]::Parse($stamp).UtcDateTime}
    $state = @{startTime=$stamp} | ConvertTo-Json | ConvertFrom-Json
    Assert-True (& $compareStartTime) 'JSON DateTime portal identity mismatch.'
    $state.startTime = $stamp
    Assert-True (& $compareStartTime) 'JSON date-string portal identity mismatch.'
    $process.StartTime = $process.StartTime.AddSeconds(1)
    Must-Reject { Assert-True (& $compareStartTime) 'Reused portal PID accepted.' }
    $fakeEnvironment = @{
        PATH='test-path'; HOME='test-home'; AZURE_CONFIG_DIR='test-cli-cache'
        ARM_CLIENT_SECRET='not-forwarded'; ARM_ACCESS_KEY='not-forwarded'; GH_TOKEN='not-forwarded'
        UNKNOWN_VENDOR_SECRET='not-forwarded'; AZURE_CLIENT_SECRET='not-forwarded'
        TF_VAR_compute_enabled='true'; PIP_INDEX_URL='unapproved'; UV_INDEX_URL='unapproved'
        UV_NATIVE_TLS='true'; UV_INSECURE_HOST='must-not-forward'; PYTHONHTTPSVERIFY='0'
    }
    $clean = Get-RuntimeEnvironment $fakeEnvironment
    Assert-True ($clean.HOME -eq 'test-home' -and $clean.AZURE_CONFIG_DIR -eq 'test-cli-cache' -and
        $clean.AZURE_TOKEN_CREDENTIALS -eq 'AzureCliCredential' -and
        $clean.WAN_STUDIO_CONFIG -eq $ConfigPath -and $clean.AZUREML_COMPUTE -eq $receipt.computeName) 'Portable child context lost.'
    Assert-True (@($clean.Keys | Where-Object { $_ -match 'SECRET|^ARM_|^TF_|^GH_|^PIP_|^UV_INDEX|^UV_INSECURE|^PYTHONHTTPSVERIFY' }).Count -eq 0) 'Secret, alternate index or unsafe Terraform input leaked into child environment.'
    Must-Reject { Get-RuntimeEnvironment @{WAN_STUDIO_PYPI_INDEX='https://user:password@example.com/simple/'} }
    $clean = Get-RuntimeEnvironment @{WAN_STUDIO_PYPI_INDEX='https://pypi.org/simple'}
    Assert-True ($clean.WAN_STUDIO_PYPI_INDEX -eq 'https://pypi.org/simple') 'Explicit credential-free package index rejected.'
    $info = New-RuntimeProcessInfo 'python' @('runtime.py','portal')
    Assert-True (-not $info.UseShellExecute -and $info.Environment.Count -eq (Get-RuntimeEnvironment).Count) 'Child launch bypasses environment filtering.'
    Invoke-RuntimeProcess (Get-Command python -CommandType Application | Select-Object -First 1).Source @('-c',
        "import os; assert not any(k in os.environ for k in ('ARM_CLIENT_SECRET','ARM_ACCESS_KEY','GH_TOKEN','TF_VAR_compute_enabled')); assert os.environ['AZURE_TOKEN_CREDENTIALS']=='AzureCliCredential'; assert os.environ['WAN_STUDIO_CONFIG']; print('PASS: sanitized real child process')")
    $probeScript = Join-Path $checkRoot 'portal-probe.py'
    'import time; time.sleep(60)' | Set-Content $probeScript
    $probeExe = (Get-Command python -CommandType Application | Select-Object -First 1).Source
    $probe = [Diagnostics.Process]::Start((New-RuntimeProcessInfo $probeExe @($probeScript)))
    try {
        Assert-True (-not $probe.HasExited) 'Process identity probe did not start.'
        $portalState = @{pid=$probe.Id; startTime=$probe.StartTime.ToUniversalTime().AddSeconds(-1).ToString('o'); script=$probeScript; executable=$probeExe}
        $portalState | ConvertTo-Json | Set-Content (Join-Path $Cache 'portal.json')
        Must-Reject { Stop-Portal }
        Assert-True (-not $probe.HasExited) 'Mismatched process identity killed a live process.'
        $portalState.startTime=$probe.StartTime.ToUniversalTime().ToString('o')
        $portalState | ConvertTo-Json | Set-Content (Join-Path $Cache 'portal.json')
        Stop-Portal
        Assert-True ($probe.HasExited -and -not (Test-Path (Join-Path $Cache 'portal.json'))) 'Exact process release was not verified.'
    } finally {
        if (-not $probe.HasExited) { Stop-Process -Id $probe.Id -Force }
        $probe.Dispose()
    }
    & {
        $originalMirror=$env:WAN_STUDIO_PYPI_INDEX
        $script:dependencyCalls=[Collections.Generic.List[object]]::new()
        function Invoke-RuntimeProcess([string]$Executable, [string[]]$Arguments) { $script:dependencyCalls.Add($Arguments) }
        try {
            $env:WAN_STUDIO_PYPI_INDEX=$null
            Install-RuntimeDependencies $checkRoot
            $default=$script:dependencyCalls[0]
            Assert-True ($script:dependencyCalls.Count -eq 1 -and '--no-config' -in $default -and
                '--locked' -in $default -and '--native-tls' -in $default -and
                '--default-index' -notin $default) 'Default dependency installation bypassed the public lock.'
            $script:dependencyCalls.Clear()
            $env:WAN_STUDIO_PYPI_INDEX='https://mirror.example.com/simple'
            Install-RuntimeDependencies $checkRoot
            $export=$script:dependencyCalls[0]; $install=$script:dependencyCalls[-1]
            Assert-True ('export' -in $export -and '--offline' -in $export -and '--locked' -in $export -and
                '--require-hashes' -in $install -and '--only-binary' -in $install -and '--strict' -in $install -and
                'https://mirror.example.com/simple' -in $install -and '--native-tls' -in $install) 'Explicit mirror installation changed the lock or bypassed artifact hashes/TLS.'
        } finally { $env:WAN_STUDIO_PYPI_INDEX=$originalMirror }
    }

    $tags = Copy-Value $receipt.ownershipTags
    Assert-Owned @{id=$GroupId; name='rg-example'; tags=$tags} $GroupId
    foreach ($key in $tags.Keys) {
        $badTags=$tags.Clone(); $badTags.Remove($key)
        Must-Reject { Assert-Owned @{id=$GroupId; name='rg-example'; tags=$badTags} $GroupId }
    }
    $subnetFixture = @{id=$SubnetId; name='gpu'; etag='fixture-etag'; properties=@{
        addressPrefix=$Config.gpu_subnet_cidr; networkSecurityGroup=@{id=$receipt.networkSecurityGroupId}
        routeTable=@{id="$network/routeTables/customer-route"}
    }}
    Assert-DemoSubnet $subnetFixture
    $cluster = @{
        id=$ComputeId; name=$Foundation.computeName; tags=$tags
        identity=@{type='UserAssigned'; userAssignedIdentities=@{$Foundation.computeIdentityId=@{}}}
        properties=@{computeType='AmlCompute'; provisioningState='Succeeded'; disableLocalAuth=$true; properties=@{
            vmSize=$VmSize; vmPriority='LowPriority'; osType='Linux'
            scaleSettings=@{minNodeCount=0; maxNodeCount=1; nodeIdleTimeBeforeScaleDown='PT120S'}
            enableNodePublicIp=$false; remoteLoginPortPublicAccess='Disabled'; subnet=@{id=$SubnetId}
        }}
    }
    Assert-Cluster $cluster
    foreach ($key in @('vmPriority','vmSize','enableNodePublicIp','remoteLoginPortPublicAccess','osType')) {
        $bad=Copy-Value $cluster; $bad.properties.properties[$key]='unsafe'
        Must-Reject { Assert-Cluster $bad }
    }
    $bad=Copy-Value $cluster; $bad.properties.properties.scaleSettings.maxNodeCount=2
    Must-Reject { Assert-Cluster $bad }
    $bad=Copy-Value $cluster; $bad.identity.userAssignedIdentities=@{}
    Must-Reject { Assert-Cluster $bad }
    $bad = Copy-Value $subnetFixture; $bad.properties.natGateway=@{id="$network/natGateways/customer"}
    Must-Reject { Assert-DemoSubnet $bad }
    $bad = Copy-Value $subnetFixture; $bad.properties.networkSecurityGroup.id="$network/networkSecurityGroups/customer"
    Must-Reject { Assert-DemoSubnet $bad }
    $bad = Copy-Value $subnetFixture; $bad.properties.addressPrefix='10.21.0.0/26'
    Must-Reject { Assert-DemoSubnet $bad }
    $rate = @{
        armSkuName=$VmSize; armRegionName=$Region; type='Consumption'; currencyCode='USD'
        unitOfMeasure='1 Hour'; isPrimaryMeterRegion=$true; productName='Virtual Machines NCads A100 v4 Series'
        skuName='NC24ads A100 v4'; meterName='NC24ads A100 v4'; retailPrice=3.673
    }
    Assert-True ((Select-PaygRate @($rate)) -eq 3.673) 'Valid PAYG rate rejected.'
    Must-Reject { Select-PaygRate @() }
    Must-Reject { Select-PaygRate @($rate,$rate) }
    foreach ($change in @(@{retailPrice=4.01},@{retailPrice=[double]::NaN},@{productName='Windows'},@{skuName='Spot'},@{armRegionName='other'})) {
        $bad=$rate.Clone(); foreach ($key in $change.Keys) {$bad[$key]=$change[$key]}
        Must-Reject { Select-PaygRate @($bad) }
    }
    $total = @{name=@{value='TotalLowPriorityCores'}; currentValue=0; limit=24}
    $family = @{name=@{value='standardNCADSA100v4Family'}; currentValue=0; limit=-1
        type='Microsoft.MachineLearningServices/vmFamily/lowPriorityCores/usages'}
    Assert-Quota @($total,$family)
    $dedicated = Copy-Value $family; $dedicated.type='Microsoft.MachineLearningServices/vmFamily/dedicatedCores/usages'
    Must-Reject { Assert-Quota @($total,$dedicated) }
    $total.limit=23
    Must-Reject { Assert-Quota @($total,$family) }
    $stop = ((Get-Command Stop-Demo).ScriptBlock.ToString() -split "`n" |
        Where-Object { -not $_.TrimStart().StartsWith('#') }) -join "`n"
    Assert-True ($stop -notmatch 'Get-Prepared|Invoke-Runtime|Assert-LiveCost|preview') 'Emergency Stop depends on preparation/start prerequisites.'
    Assert-True ($stop.IndexOf('underlyingResourceAction=Delete') -lt $stop.IndexOf('Repair-ReleasedState')) 'Native release must precede state reconciliation.'

    $script:events = [Collections.Generic.List[string]]::new()
    $script:present = @{$ComputeId=$true; $NatId=$true; $PipId=$true}
    $script:liveSubnet = Copy-Value $subnetFixture
    $script:liveSubnet.properties.natGateway=@{id=$NatId}
    $script:statusJobs=@(@{name='queued'; properties=@{status='Queued'; computeId=$ComputeId}})
    $script:stateEntries = @((Get-ReleaseStateTargets | ForEach-Object { @{address=$_.address; mode='managed'; values=@{id=$_.id}} }))
    $script:stateEntries += @{address='azurerm_subnet_network_security_group_association.compute'; mode='managed'; values=@{id=$SubnetId}}
    $script:completeJobsOnDelete=$true
    function Invoke-Arm($Path, $Api, $Method='GET', $Body, [switch]$AllowMissing, $Headers) {
        if ($Method -eq 'POST' -and $Path.EndsWith('/cancel')) { throw 'Synthetic unsupported cancellation API' }
        if ($Method -eq 'DELETE') {
            if ($Path -eq $ComputeId) {
                Assert-True ($Api.EndsWith('&underlyingResourceAction=Delete')) 'Compute delete omitted underlying resources.'
                if ($script:completeJobsOnDelete) { foreach ($job in $script:statusJobs) { $job.properties.status='Canceled' } }
            }
            $script:events.Add("delete:$Path"); $script:present[$Path]=$false; return
        }
        if ($Path -eq $SubnetId) {
            if ($Method -eq 'PUT') {
                Assert-True ($Headers['If-Match'] -eq 'fixture-etag' -and
                    $Body.properties.routeTable.id -eq "$network/routeTables/customer-route" -and
                    $Body.properties.networkSecurityGroup.id -eq $receipt.networkSecurityGroupId -and
                    -not $Body.properties.ContainsKey('natGateway')) 'NAT detach changed customer routing/NSG or lost ETag guard.'
                $script:events.Add('detach'); $script:liveSubnet.properties=Copy-Value $Body.properties
            }
            return Copy-Value $script:liveSubnet
        }
        if ($Path -in @($GroupId,$WorkspaceId) -or $script:present[$Path]) {
            return @{id=$Path; name=($Path -split '/')[-1]; tags=$tags}
        }
    }
    function Get-DemoJobs { $script:statusJobs }
    function Stop-Portal { $script:events.Add('portal') }
    function Save-Outputs { $script:events.Add('outputs') }
    function Invoke-Terraform([string[]]$Arguments, [switch]$Capture) {
        $script:events.Add("terraform:$($Arguments -join ' ')")
        if ($Arguments[0] -eq 'show') {
            return @{format_version='1.0'; values=@{root_module=@{resources=$script:stateEntries}}} | ConvertTo-Json -Depth 20
        }
        if ($Arguments[0] -eq 'state') {
            Assert-True ($Arguments[1] -eq 'rm' -and $Arguments.Count -eq 4 -and
                $Arguments[3] -cin @(Get-ReleaseStateTargets | ForEach-Object address)) 'State removal escaped approved exact addresses.'
            $script:stateEntries=@($script:stateEntries | Where-Object address -CNE $Arguments[3])
        }
        if ($Arguments[0] -eq 'apply') {
            Assert-True ('-refresh-only' -in $Arguments -and '-var=compute_enabled=false' -in $Arguments) 'Stop reconciliation must not apply resource changes.'
        }
    }
    '{}' | Set-Content (Join-Path $Cache 'armed.json')
    $warnings=@(Stop-Demo 3>&1)
    Assert-True (-not (Test-Path (Join-Path $Cache 'armed.json')) -and $script:events[0] -eq 'portal') 'Stop did not disable local submissions first.'
    Assert-True ($script:events.IndexOf("delete:$ComputeId") -lt $script:events.IndexOf('detach') -and
        $script:events.IndexOf('detach') -lt $script:events.IndexOf("delete:$NatId") -and
        $script:events.IndexOf("delete:$NatId") -lt $script:events.IndexOf("delete:$PipId")) 'Emergency release ordering regressed.'
    Assert-True (@($warnings | Where-Object { $_ -is [Management.Automation.WarningRecord] -and "$_" -match 'all demo jobs are terminal' }).Count -eq 1) 'Cancellation recovery must warn only after verified terminal jobs.'
    Assert-True ($script:stateEntries.Count -eq 1 -and $script:stateEntries[0].address -eq 'azurerm_subnet_network_security_group_association.compute') 'State recovery changed unrelated subnet association.'
    $deletes=@($script:events | Where-Object { $_.StartsWith('delete:') }).Count
    Stop-Demo
    Assert-True (@($script:events | Where-Object { $_.StartsWith('delete:') }).Count -eq $deletes) 'Repeated Stop deleted additional resources.'
    $script:present[$ComputeId]=$true
    Must-Reject { Repair-ReleasedState }
    $script:present[$ComputeId]=$false
    $script:stateEntries=@(@{address='azapi_resource.compute[0]'; mode='managed'; values=@{id='unrelated'}})
    Must-Reject { Repair-ReleasedState }
    $script:stateEntries=@()
    $script:statusJobs=@(@{name='queued'; properties=@{status='Queued'}})
    Must-Reject { Wait-NoActiveDemoJobs -Minutes 0 }
    $status=Get-LiveStatus
    Assert-True ($status.State -eq 'PARTIAL/ERROR' -and $status.ActiveJobs.Count -eq 1) 'Queued job became a false OFF status.'
    $script:statusJobs=@()
    Assert-True ((Get-LiveStatus).State -eq 'OFF') 'Empty cloud state is not OFF.'
    function Repair-ReleasedState { throw 'Synthetic broken Terraform' }
    $script:present[$ComputeId]=$true; $script:present[$NatId]=$true; $script:present[$PipId]=$true
    Must-Reject { Stop-Demo }
    Assert-True (-not $script:present[$ComputeId] -and -not $script:present[$NatId] -and -not $script:present[$PipId]) 'Broken Terraform prevented native release.'
    Assert-True (Test-Path (Join-Path $Cache 'release-required.json')) 'Failed reconciliation lost the Start blocking marker.'

    function Assert-Foundation {}
    function Get-Prepared { if ($script:failPrepared) { throw 'Synthetic unprepared assets' }; @{version='fixture'} }
    function Assert-PrivateConnectivity { if ($script:failConnectivity) { throw 'Synthetic public DNS' } }
    function Assert-LiveCost { throw 'Synthetic price endpoint unavailable' }
    $script:startMutation=$false
    function Set-ComputeEnabled { $script:startMutation=$true; throw 'BUG: compute mutation reached during failed Start preflight' }
    $NoPortal=[switch]$true
    $ApproveGpuSpend=[switch]$false
    Must-Reject { Start-Demo }
    $ApproveGpuSpend=[switch]$true
    Must-Reject { Start-Demo }
    Remove-Item -LiteralPath (Join-Path $Cache 'release-required.json')
    $script:failPrepared=$true
    Must-Reject { Start-Demo }
    $script:failPrepared=$false; $script:failConnectivity=$true
    Must-Reject { Start-Demo }
    $script:failConnectivity=$false
    Must-Reject { Start-Demo }
    Assert-True (-not $script:startMutation -and -not (Test-Path (Join-Path $Cache 'armed.json'))) 'Failed Start mutated compute or armed submissions.'
    if (-not $PowerShellOnly) {
        $env:PYTHONDONTWRITEBYTECODE='1'
        Invoke-Native python @((Join-Path $PSScriptRoot 'test_controls.py'),'--scratch',$checkRoot)
    }
    Write-Host 'PASS: framework-free portable scope, process, price/quota, Start and exact-owned Stop checks. No Azure mutations or GPU allocations.'
} finally {
    Remove-Item -LiteralPath $checkRoot -Recurse -Force
}
