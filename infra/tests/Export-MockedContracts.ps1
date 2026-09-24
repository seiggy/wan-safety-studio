#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$infraDirectory = Split-Path -Parent $PSScriptRoot
$testFile = Join-Path 'tests' 'safety.tftest.hcl'
$testContent = Get-Content (Join-Path $infraDirectory $testFile) -Raw
foreach ($provider in @('azurerm', 'azapi')) {
    if ($testContent -notmatch "mock_provider\s+`"$provider`"") {
        throw "Refusing contract capture without a native $provider mock."
    }
}

$lines = @(& terraform "-chdir=$infraDirectory" test "-filter=$testFile" -json -verbose -no-color)
if ($LASTEXITCODE -ne 0) {
    throw 'Mocked Terraform tests failed; contract fixtures were not updated.'
}
$events = @($lines | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })

function Assert-KnownValue {
    param($Value)
    if ($Value -is [bool] -and $Value) {
        throw 'A mocked studio output is still unknown; refusing an incomplete receipt.'
    }
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($child in $Value.Values) { Assert-KnownValue $child }
    }
    elseif ($Value -is [System.Collections.IList]) {
        foreach ($child in $Value) { Assert-KnownValue $child }
    }
}

$captures = [ordered]@{
    'studio-off.json' = 'default_off_private_foundation'
    'studio-on.json'  = 'on_with_explicit_owned_egress'
    'studio-customer-nsg.json' = 'customer_managed_nsg_handoff'
}
$receipts = @{}
foreach ($entry in $captures.GetEnumerator()) {
    $plans = @($events | Where-Object { $_.type -eq 'test_plan' -and $_.'@testrun' -eq $entry.Value })
    if ($plans.Count -ne 1) { throw "Expected exactly one JSON plan for $($entry.Value)." }
    $change = $plans[0].test_plan.output_changes.studio
    if (-not $change -or -not $change.after) { throw "Missing studio output in $($entry.Value)." }
    Assert-KnownValue $change.after_unknown
    $receipt = $change.after
    if ($receipt.subscriptionId -ne '11111111-1111-1111-1111-111111111111' -or
        $receipt.tenantId -ne '22222222-2222-2222-2222-222222222222') {
        throw 'Refusing to persist anything other than the synthetic example scope.'
    }
    $receipts[$entry.Key] = $receipt | ConvertTo-Json -Depth 32
}

$fixtureDirectory = Join-Path $PSScriptRoot 'fixtures'
$null = New-Item -Path $fixtureDirectory -ItemType Directory -Force
foreach ($entry in $receipts.GetEnumerator()) {
    Set-Content -Path (Join-Path $fixtureDirectory $entry.Key) -Value $entry.Value -Encoding utf8NoBOM
}
Write-Output 'PASS: captured native mocked studio OFF/ON/customer-NSG receipts without Azure calls.'
