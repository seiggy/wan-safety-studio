#requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$infra = Split-Path $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('wan-contracts-' + [guid]::NewGuid().ToString('N'))
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.tftest.hcl' -File) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($provider in 'azurerm','azapi') {
        if ($content -notmatch "mock_provider\s+`"$provider`"") { throw "Native $provider mock missing; refusing contract capture." }
    }
}
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    Get-ChildItem -LiteralPath $infra -File | Where-Object { $_.Name -match '\.tf$' -or $_.Name -eq '.terraform.lock.hcl' } |
        Copy-Item -Destination $scratch
    Copy-Item -LiteralPath (Join-Path $infra 'modules'),$PSScriptRoot -Destination $scratch -Recurse
    $providers = Join-Path $infra '.terraform' 'providers'
    $initArgs = @("-chdir=$scratch",'init','-backend=false','-input=false','-no-color')
    if (Test-Path -LiteralPath $providers) { $initArgs += "-plugin-dir=$providers" }
    $null = & terraform @initArgs
    if ($LASTEXITCODE -ne 0) { throw 'Isolated mock initialization failed.' }
    & terraform "-chdir=$scratch" fmt -check -recursive
    if ($LASTEXITCODE -ne 0) { throw 'Terraform formatting failed.' }
    & terraform "-chdir=$scratch" validate -no-color
    if ($LASTEXITCODE -ne 0) { throw 'Isolated Terraform validation failed.' }
    $lines = @(& terraform "-chdir=$scratch" test -json -verbose -no-color)
    if ($LASTEXITCODE -ne 0) {
        foreach ($line in $lines) {
            $event = $line | ConvertFrom-Json -AsHashtable
            if ($event.type -eq 'diagnostic') { Write-Host "$($event.diagnostic.summary): $($event.diagnostic.detail)" }
        }
        throw 'Native mocked checks failed.'
    }
    $events = @($lines | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
    $expectedRuns = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.tftest.hcl' -File |
        ForEach-Object { [regex]::Matches((Get-Content -LiteralPath $_.FullName -Raw), '(?m)^run\s+"') }).Count
    $summaries = @($events | Where-Object { $_.type -eq 'test_summary' })
    if ($summaries.Count -ne 1 -or $summaries[0].test_summary.passed -ne $expectedRuns -or
        $summaries[0].test_summary.failed -ne 0 -or $summaries[0].test_summary.errored -ne 0 -or
        $summaries[0].test_summary.skipped -ne 0) { throw 'Native suite did not execute every expected offline case.' }

    $allowed = @('azurerm_resource_group','azurerm_user_assigned_identity','azurerm_key_vault','azurerm_container_registry',
        'azurerm_application_insights','azurerm_private_endpoint','azurerm_network_security_group',
        'azurerm_subnet_network_security_group_association','azurerm_role_assignment','azurerm_role_definition',
        'azurerm_service_plan','azurerm_linux_web_app','terraform_data','azapi_resource')
    $armAllowed = @('microsoft.storage/storageaccounts','microsoft.storage/storageaccounts/blobservices/containers',
        'microsoft.machinelearningservices/workspaces','microsoft.machinelearningservices/workspaces/datastores')
    $studioKeys = '^(Studio__|AzureMl__|Auth__|Generation__|FEATURE_|Agent__|KeyVault__|AZURE_CLIENT_ID|ASPNETCORE_)'
    $plans = @($events | Where-Object { $_.type -eq 'test_plan' })
    if ($plans.Count -eq 0) { throw 'No native plans were captured.' }
    foreach ($plan in $plans) {
        if ($plan.test_plan.output_changes -and $plan.test_plan.output_changes.ContainsKey('studio')) { throw 'Terraform must not emit a studio receipt.' }
        foreach ($change in $plan.test_plan.resource_changes) {
            if ($change.mode -eq 'data') { continue }
            $forbidden = $change.type -notin $allowed
            if ($change.type -eq 'azapi_resource') {
                $armType = ([string]$change.change.after.type -split '@')[0].ToLowerInvariant()
                if ($armType -notin $armAllowed) { $forbidden = $true }
            }
            if ($forbidden) { throw "Forbidden network/compute owner in native plan: $($change.address)" }
            if ($change.type -eq 'azurerm_linux_web_app') {
                foreach ($name in @($change.change.after.app_settings.Keys)) {
                    if ($name -match $studioKeys) { throw "Terraform must not set the studio application setting $name." }
                }
            }
        }
    }    Write-Output "PASS: $expectedRuns isolated Terraform tests; no receipt output, no studio app settings, no compute/egress/landing-zone ownership."
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
