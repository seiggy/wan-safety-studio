#requires -Version 7.4

<#
.SYNOPSIS
Promote a sealed GPU release to one, several or all configured regions without GitHub.

.DESCRIPTION
The standalone equivalent of .github/workflows/promote.yml. For each region it runs scripts/ci/publish_region.py: it copies the image by digest with az acr import, copies and verifies every blob,
registers the Azure ML assets and writes the create-only regional binding last. No GPU is allocated.

Promotion is idempotent and resumable: snapshots and bindings are create-only and verified, so rerunning the same artifact
for a region that failed (or already succeeded) is safe. A failed region does not stop the others.

The script ends by printing the Release artifact ID and the regions it was promoted to. Then, in Administration, paste it
into each region's 'Release artifact ID' box and choose 'Select release' (the region must be Off; selecting starts no
compute). bash twin: scripts/release/promote-release.sh.

.PARAMETER Artifact
The 64-character artifact ID the build printed (Build-StudioArtifact.ps1). Required.
.PARAMETER Region
One or more region keys from Studio.Regions. Defaults to every configured region.
.PARAMETER Settings
The settings document, kept outside the checkout. Defaults to STUDIO_SETTINGS_PATH or STUDIO_SETTINGS_JSON.
.PARAMETER ContainerEngine
Docker, Podman or Wslc. Optional and unused: promotion copies images inside Azure (az acr import) and runs no container.
Accepted so every release script takes the same option.
.PARAMETER Login
Current (default), Interactive, DeviceCode, ManagedIdentity or ServicePrincipal. See Build-StudioArtifact.ps1 -Help.
.PARAMETER ClientId
Client ID for ManagedIdentity or ServicePrincipal.
.PARAMETER FederatedTokenFile
ServicePrincipal: a file holding a federated token.
.PARAMETER Certificate
ServicePrincipal: a PEM certificate file with its private key.
.PARAMETER Python
Python 3.12 (or 3.11). Defaults to the first one on PATH.
.PARAMETER Venv
Virtual environment folder. Defaults to .artifacts/release-venv in the checkout.
.PARAMETER PreflightOnly
Run every check (settings, sign-in and publisher identity, network) and stop before promoting.
.PARAMETER KeepWork
Keep the job folder for troubleshooting.
.PARAMETER Help
Show this help.

.EXAMPLE
./scripts/release/Publish-StudioRelease.ps1 -Settings ~/wan/settings.json -Artifact c26383a8b01bfb720c7fc9774d60af808fc7de7ed7a4f2a57675f33d81736841
.EXAMPLE
./scripts/release/Publish-StudioRelease.ps1 -Settings ~/wan/settings.json -Artifact <id> -Region westeurope
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive progress for a person at a console; the output stream carries results.')]
[CmdletBinding()]
param(
    [ValidatePattern('^[a-f0-9]{64}$')][string]$Artifact,
    [ValidateSet('gpu', 'yolox')][string]$Component = 'gpu',
    [string[]]$Region = @(),
    [string]$Settings,
    [ValidateSet('Docker', 'Podman', 'Wslc')][string]$ContainerEngine,
    [ValidateSet('Current', 'Interactive', 'DeviceCode', 'ManagedIdentity', 'ServicePrincipal')][string]$Login = 'Current',
    [string]$ClientId,
    [string]$FederatedTokenFile,
    [string]$Certificate,
    [string]$Python,
    [string]$Venv,
    [switch]$PreflightOnly,
    [switch]$KeepWork,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
Import-Module (Join-Path $PSScriptRoot 'StudioRelease.psm1') -Force
# Accepted so every release script takes the same option; promotion copies images inside Azure and runs no container.
$null = $ContainerEngine
$job = $null
$exitCode = 0
try {
    if (-not $Artifact) { Exit-StudioWithError 'Pass -Artifact <64-character artifact ID> (the ID the build printed).' }
    $job = New-StudioJob -Stage promote -Component $Component -Settings $Settings -Region $Region -Python $Python
    $job.KeepWork = [bool]$KeepWork
    Write-Note "    promoting $Component artifact $Artifact to: $($job.Regions -join ', ')"
    Connect-StudioAzure -Job $job -Login $Login -ClientId $ClientId -FederatedTokenFile $FederatedTokenFile -Certificate $Certificate
    Test-StudioNetwork $job
    if ($PreflightOnly) { Write-Step 'Preflight passed. Nothing was promoted (-PreflightOnly).'; return }
    Initialize-StudioToolchain $job $Venv
    $script = if ($Component -eq 'yolox') { 'publish_trainer.py' } else { 'publish_region.py' }
    $marker = if ($Component -eq 'yolox') { 'Verified trainer binding: ' } else { 'Verified regional binding: ' }
    $ok, $failed = @(), @()
    foreach ($key in $job.Regions) {
        Write-Step "Promoting to $key"
        $result = Invoke-StudioStage $job $script @('--settings', $job.SettingsFile, '--artifact', $Artifact, '--region', $key)
        if (-not $result.ExitCode -and @($result.Lines | Where-Object { $_.StartsWith($marker) }).Count) { $ok += $key }
        else { $failed += $key; Write-Note "    $key was not promoted; it stays unavailable for this release." -Color Yellow }
    }
    Show-StudioBanner $job 'promote' $Artifact -Ok $ok -Failed $failed
    if ($failed) { $exitCode = 1 }
} catch {
    Write-Note "ERROR: $($_.Exception.Message)" -Color Red
    $exitCode = 1
} finally {
    if (-not (Remove-StudioJob $job)) { $exitCode = 1 }
}
if ($exitCode) { exit $exitCode }
