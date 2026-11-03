#requires -Version 7.4

<#
.SYNOPSIS
Deploy a verified server artifact to the studio App Service, optionally building it first, without GitHub.

.DESCRIPTION
The standalone equivalent of .github/workflows/deploy-server.yml (and, with -Build, release.yml with component server
first). scripts/ci/deploy_server.py points the App Service at the image by registry digest, starts it, waits for /healthz
and confirms /api/status requires sign-in; if it does not, the site is stopped. It never arms submissions and never
changes a region's release.

The site must be disarmed (AzureMl__Armed=false) and have Auth__ClientId and Auth__TenantId set first. The control region's
registry and storage, and the site's private hostname, must resolve privately from this machine.

The server artifact ID is not the Release artifact ID: never paste it into Administration. bash twin:
scripts/release/deploy-server.sh.

.PARAMETER Artifact
The 64-character server artifact ID that Build-StudioArtifact.ps1 -Component server printed. Use either -Artifact or -Build.
.PARAMETER Build
Build, verify and seal the server image from this checkout first, then deploy it. Needs -ContainerEngine.
.PARAMETER ContainerEngine
Docker, Podman or Wslc. Required with -Build; unused otherwise.
.PARAMETER Settings
The settings document (with Pipeline.SiteId), kept outside the checkout. Defaults to STUDIO_SETTINGS_PATH or STUDIO_SETTINGS_JSON.
.PARAMETER Login
Current (default), Interactive, DeviceCode, ManagedIdentity or ServicePrincipal. See Build-StudioArtifact.ps1 -Help.
.PARAMETER ClientId
Client ID for ManagedIdentity or ServicePrincipal.
.PARAMETER FederatedTokenFile
ServicePrincipal: a file holding a federated token.
.PARAMETER Certificate
ServicePrincipal: a PEM certificate file with its private key.
.PARAMETER AllowDirty
With -Build: build even if the checkout has uncommitted changes.
.PARAMETER Python
Python 3.12 (or 3.11). Defaults to the first one on PATH.
.PARAMETER Venv
Virtual environment folder. Defaults to .artifacts/release-venv in the checkout.
.PARAMETER PreflightOnly
Run every check and stop before building or deploying.
.PARAMETER KeepWork
Keep the job folder for troubleshooting.
.PARAMETER Help
Show this help.

.EXAMPLE
./scripts/release/Deploy-StudioServer.ps1 -Settings ~/wan/settings.json -Build -ContainerEngine Wslc
.EXAMPLE
./scripts/release/Deploy-StudioServer.ps1 -Settings ~/wan/settings.json -Artifact <server artifact id>
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive progress for a person at a console; the output stream carries results.')]
[CmdletBinding()]
param(
    [ValidatePattern('^[a-f0-9]{64}$')][string]$Artifact,
    [switch]$Build,
    [ValidateSet('Docker', 'Podman', 'Wslc')][string]$ContainerEngine,
    [string]$Settings,
    [ValidateSet('Current', 'Interactive', 'DeviceCode', 'ManagedIdentity', 'ServicePrincipal')][string]$Login = 'Current',
    [string]$ClientId,
    [string]$FederatedTokenFile,
    [string]$Certificate,
    [switch]$AllowDirty,
    [string]$Python,
    [string]$Venv,
    [switch]$PreflightOnly,
    [switch]$KeepWork,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
Import-Module (Join-Path $PSScriptRoot 'StudioRelease.psm1') -Force
$job = $null
$exitCode = 0
try {
    if ([bool]$Artifact -eq [bool]$Build) { Exit-StudioWithError 'Pass exactly one of -Artifact <server artifact ID> or -Build.' }
    if ($Build -and -not $ContainerEngine) { Exit-StudioWithError '-Build needs -ContainerEngine Docker, Podman or Wslc.' }
    $job = New-StudioJob -Stage deploy -Component server -Settings $Settings -Python $Python
    $job.KeepWork = [bool]$KeepWork
    if ($Build) { Test-StudioEngine $job $ContainerEngine }
    Connect-StudioAzure -Job $job -Login $Login -ClientId $ClientId -FederatedTokenFile $FederatedTokenFile -Certificate $Certificate `
        -IdentityStage ($Build ? 'build' : 'deploy')
    Test-StudioNetwork $job
    if ($PreflightOnly) { Write-Step 'Preflight passed. Nothing was built or deployed (-PreflightOnly).'; return }
    Initialize-StudioToolchain $job $Venv
    if ($Build) {
        $Artifact = Invoke-StudioBuild -Job $job -AllowDirty:$AllowDirty
        Write-Note "    Sealed server artifact $Artifact."
    }
    Write-Step "Deploying server artifact $Artifact to the App Service in $($job.Values.CONTROL_REGION)"
    $result = Invoke-StudioStage $job 'deploy_server.py' @('--settings', $job.SettingsFile, '--artifact', $Artifact, '--region', $job.Values.CONTROL_REGION)
    if ($result.ExitCode) { Exit-StudioWithError "Server deployment did not verify (exit $($result.ExitCode)); see the output above." }
    Show-StudioBanner $job 'deploy' $Artifact
} catch {
    Write-Note "ERROR: $($_.Exception.Message)" -Color Red
    $exitCode = 1
} finally {
    if (-not (Remove-StudioJob $job)) { $exitCode = 1 }
}
if ($exitCode) { exit $exitCode }
