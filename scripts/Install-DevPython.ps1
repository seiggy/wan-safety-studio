<#
.SYNOPSIS
  Creates/updates app\.venv from the committed uv.lock WITHOUT touching the lock: export hashes, install through your configured index.
  Works with a public index (customers) or an internal feed (set it in %APPDATA%\uv\uv.toml or pass -Index).
  Also installs the optional OTLP exporters (app\requirements-otlp.txt) so telemetry reaches the Aspire dashboard.
#>
[CmdletBinding()]
param([string]$Index)
$ErrorActionPreference = 'Stop'
$app = Join-Path (Split-Path $PSScriptRoot) 'app'
$requirements = Join-Path ([IO.Path]::GetTempPath()) "wan-dev-requirements-$PID.txt"
try {
    Push-Location $app
    uv export --frozen --no-dev --no-emit-project --no-header --no-annotate --format requirements.txt -o $requirements | Out-Null
    if ($LASTEXITCODE) { throw 'uv export failed.' }
    uv venv .venv --allow-existing -q
    # One install call: --require-hashes needs every dependency pinned in the same set.
    $install = @('pip', 'install', '--python', '.venv', '--require-hashes', '--only-binary', ':all:', '-r', $requirements, '-r', 'requirements-otlp.txt')
    if ($Index) { $install += @('--default-index', $Index) }
    uv @install
    if ($LASTEXITCODE) { throw 'Dependency install failed. If this is a TLS/connection error to files.pythonhosted.org, configure your approved feed (see docs/local-generation.md).' }
} finally {
    Pop-Location
    Remove-Item $requirements -Force -ErrorAction SilentlyContinue
}
