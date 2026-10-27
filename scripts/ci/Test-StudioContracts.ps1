#requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$contracts = Join-Path $root 'contracts'
$raw = Get-Content -LiteralPath (Join-Path $contracts 'fixtures' 'studio.json') -Raw
$fixture = [System.Text.Json.JsonDocument]::Parse($raw, [System.Text.Json.JsonDocumentOptions]::new())
try {
    foreach ($name in 'build','release','settings','job') {
        $json = $fixture.RootElement.GetProperty($name).GetRawText()
        $schema = Join-Path $contracts "$name.schema.json"
        if (-not (Test-Json -Json $json -SchemaFile $schema -ErrorAction Stop)) {
            throw "Invalid structural contract fixture: $name"
        }
    }
} finally { $fixture.Dispose() }
Write-Output 'PASS: four shared structural contract fixtures; the foundation is validated by the shared .NET tool and tests, which enforce scope, identity, policy and state rules.'
