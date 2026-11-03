#requires -Version 7.4

<#
.SYNOPSIS
Build, verify and seal one studio artifact (the GPU release or the server image) without GitHub.

.DESCRIPTION
The standalone equivalent of .github/workflows/release.yml. It checks the settings document, signs in to Azure CLI as the
publisher principal, checks private DNS and reachability, checks the container engine, installs the locked pipeline
dependencies, builds the contract validator, then runs scripts/ci/build_image.py. It ends by printing the artifact ID.

For the GPU component that ID is the Release artifact ID: promote it with Publish-StudioRelease.ps1, then select it per
region in Administration. Nothing is deployed and no GPU is allocated.

Run it from a machine that resolves the studio's private DNS zones and reaches its private endpoints. Azure CLI must be
signed in as Pipeline.PublisherPrincipalId (see -Login). bash twin: scripts/release/build-artifact.sh.

.PARAMETER Component
gpu (default) or server.
.PARAMETER Settings
The settings document (Studio and Pipeline sections), kept outside the checkout. Defaults to STUDIO_SETTINGS_PATH or
STUDIO_SETTINGS_JSON.
.PARAMETER ContainerEngine
Docker, Podman or Wslc: the engine that builds, pushes and checks the image. Required.
.PARAMETER Login
How to sign in to Azure CLI: Current (default; use the existing az login), Interactive, DeviceCode, ManagedIdentity or
ServicePrincipal. Every mode except Current uses a job-only Azure CLI profile that is deleted at the end.
.PARAMETER ClientId
Client ID for ManagedIdentity (a user-assigned identity) or ServicePrincipal.
.PARAMETER FederatedTokenFile
ServicePrincipal: a file holding a federated (workload identity) token.
.PARAMETER Certificate
ServicePrincipal: a PEM certificate file with its private key.
.PARAMETER UpstreamSource
GPU only: persistent folder for the pinned upstream source (STUDIO_UPSTREAM_SOURCE). Optional cache.
.PARAMETER ModelCache
GPU only: persistent folder for the pinned model weights, about 45 GB (STUDIO_MODEL_CACHE). Optional cache.
.PARAMETER LicenseCache
GPU only: persistent folder for the pinned model cards (STUDIO_LICENSE_CACHE). Optional cache.
.PARAMETER AllowDirty
Build even if the checkout has uncommitted changes (the artifact still records HEAD as its source commit).
.PARAMETER Python
Python 3.12 (or 3.11) to create the pipeline virtual environment with. Defaults to the first one on PATH.
.PARAMETER Venv
Virtual environment folder. Defaults to .artifacts/release-venv in the checkout; it is reused while the locks are unchanged.
.PARAMETER PreflightOnly
Run every check (settings, sign-in and publisher identity, network, container engine, feed) and stop before building.
.PARAMETER KeepWork
Keep the job folder (it holds a copy of the settings document) for troubleshooting.
.PARAMETER Help
Show this help.

.EXAMPLE
./scripts/release/Build-StudioArtifact.ps1 -Settings ~/wan/settings.json -ContainerEngine Wslc
.EXAMPLE
./scripts/release/Build-StudioArtifact.ps1 -Component server -Settings ~/wan/settings.json -ContainerEngine Podman -Login DeviceCode
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive progress for a person at a console; the output stream carries results.')]
[CmdletBinding()]
param(
    [ValidateSet('gpu', 'server', 'yolox')][string]$Component = 'gpu',
    [string]$Settings,
    [ValidateSet('Docker', 'Podman', 'Wslc')][string]$ContainerEngine,
    [ValidateSet('Current', 'Interactive', 'DeviceCode', 'ManagedIdentity', 'ServicePrincipal')][string]$Login = 'Current',
    [string]$ClientId,
    [string]$FederatedTokenFile,
    [string]$Certificate,
    [string]$UpstreamSource,
    [string]$ModelCache,
    [string]$LicenseCache,
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
    if (-not $ContainerEngine) { Exit-StudioWithError 'Pass -ContainerEngine Docker, Podman or Wslc.' }
    if ($Component -ne 'gpu' -and ($UpstreamSource -or $ModelCache -or $LicenseCache)) { Exit-StudioWithError 'The cache folders apply only to -Component gpu.' }
    $approveTrainerWeights = $env:STUDIO_APPROVE_TRAINER_WEIGHTS -eq 'true'
    if ($Component -eq 'yolox' -and -not $approveTrainerWeights) {
        Exit-StudioWithError 'The yolox build downloads the trainer''s pinned release weights; approve that with STUDIO_APPROVE_TRAINER_WEIGHTS=true.'
    }
    $job = New-StudioJob -Stage build -Component $Component -Settings $Settings -Python $Python
    $job.KeepWork = [bool]$KeepWork
    Test-StudioEngine $job $ContainerEngine
    if ($Component -ne 'server') { Resolve-StudioFeed $job }
    Connect-StudioAzure -Job $job -Login $Login -ClientId $ClientId -FederatedTokenFile $FederatedTokenFile -Certificate $Certificate
    Test-StudioNetwork $job
    if ($PreflightOnly) { Write-Step 'Preflight passed. Nothing was built (-PreflightOnly).'; return }
    Initialize-StudioToolchain $job $Venv
    $artifact = Invoke-StudioBuild -Job $job -AllowDirty:$AllowDirty -UpstreamSource $UpstreamSource -ModelCache $ModelCache `
        -LicenseCache $LicenseCache -ApproveTrainerWeights:$approveTrainerWeights
    Show-StudioBanner $job 'build' $artifact
} catch {
    Write-Note "ERROR: $($_.Exception.Message)" -Color Red
    $exitCode = 1
} finally {
    if (-not (Remove-StudioJob $job)) { $exitCode = 1 }
}
if ($exitCode) { exit $exitCode }
