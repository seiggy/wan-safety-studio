<#
.SYNOPSIS
Remove this job's short-lived credentials: the isolated Azure CLI profile and every wslc registry login.
wslc keeps registry logins in the runner user's Windows Credential Manager (wslc-credential/<registry>), outside the job
folder, so each registry recorded in STUDIO_REGISTRY_LOG is logged out explicitly.
#>
$ErrorActionPreference = 'Stop'
$failed = @()
$log = if ($env:STUDIO_REGISTRY_LOG) { $env:STUDIO_REGISTRY_LOG } else { Join-Path $env:RUNNER_TEMP 'studio-registries' }
if (Test-Path -LiteralPath $log) {
    $wslc = @($env:STUDIO_WSLC, (Get-Command wslc.exe -ErrorAction SilentlyContinue).Source,
              '/mnt/c/Program Files/WSL/wslc.exe', 'C:\Program Files\WSL\wslc.exe') |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    foreach ($registry in @(Get-Content -LiteralPath $log | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)) {
        if (-not $wslc) { $failed += $registry; continue }
        $output = & $wslc logout $registry 2>&1 | Out-String
        if ($LASTEXITCODE -and $output -notmatch 'Not logged in') {
            Write-Warning "wslc logout $registry failed: $($output.Trim())"
            $failed += $registry
        } else {
            "Logged out of $registry."
        }
    }
    Remove-Item -LiteralPath $log -Force
}
foreach ($name in @('studio-azure', 'studio-docker')) {
    $path = Join-Path $env:RUNNER_TEMP $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if ($failed) {
    throw "Registry login(s) not removed: $($failed -join ', '). Delete wslc-credential/<registry> from the runner user's Windows Credential Manager."
}
