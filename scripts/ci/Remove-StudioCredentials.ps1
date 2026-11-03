<#
.SYNOPSIS
Remove this job's short-lived credentials: the isolated Azure CLI profile and every container registry login.
wslc keeps registry logins in the runner user's Windows Credential Manager (wslc-credential/<registry>), and Docker and
Podman in their own config or auth file, possibly outside the job folder, so each registry recorded in STUDIO_REGISTRY_LOG
is logged out explicitly with the engine named by STUDIO_CONTAINER_ENGINE (default wslc).
.PARAMETER Directory
The job's temporary folder that holds studio-azure and studio-docker. Defaults to RUNNER_TEMP.
#>
[CmdletBinding()]
param([string]$Directory = $env:RUNNER_TEMP)
$ErrorActionPreference = 'Stop'
$failed = @()
$log = if ($env:STUDIO_REGISTRY_LOG) { $env:STUDIO_REGISTRY_LOG } elseif ($Directory) { Join-Path $Directory 'studio-registries' }
if ($log -and (Test-Path -LiteralPath $log)) {
    $engine = if ($env:STUDIO_CONTAINER_ENGINE) { $env:STUDIO_CONTAINER_ENGINE.Trim().ToLowerInvariant() } else { 'wslc' }
    $candidates = switch ($engine) {
        'wslc' { @($env:STUDIO_WSLC, (Get-Command wslc.exe -ErrorAction SilentlyContinue).Source,
                   '/mnt/c/Program Files/WSL/wslc.exe', 'C:\Program Files\WSL\wslc.exe') }
        'podman' { @($env:STUDIO_PODMAN, (Get-Command podman -ErrorAction SilentlyContinue).Source) }
        'docker' { @($env:STUDIO_DOCKER, (Get-Command docker -ErrorAction SilentlyContinue).Source) }
        default { @() }
    }
    $cli = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    foreach ($registry in @(Get-Content -LiteralPath $log | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)) {
        if (-not $cli) { $failed += $registry; continue }
        $output = & $cli logout $registry 2>&1 | Out-String
        if ($LASTEXITCODE -and $output -notmatch 'Not logged in') {
            Write-Warning "$engine logout $registry failed: $($output.Trim())"
            $failed += $registry
        } else {
            "Logged out of $registry."
        }
    }
    Remove-Item -LiteralPath $log -Force
}
if ($Directory) {
    foreach ($name in @('studio-azure', 'studio-docker')) {
        $path = Join-Path $Directory $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}
if ($failed) {
    throw "Registry login(s) not removed: $($failed -join ', '). Delete wslc-credential/<registry> from the runner user's Windows Credential Manager, or run docker logout / podman logout <registry>."
}
