#requires -Version 7.4
<#
Shared implementation of the standalone release scripts (Build-StudioArtifact.ps1, Publish-StudioRelease.ps1,
Deploy-StudioServer.ps1). It reproduces what the GitHub workflows' private-context action does before a stage runs:
materialize the settings document outside the checkout, sign in to Azure CLI, build the contract validator, install the
locked pipeline dependencies into a virtual environment, isolate the job's credentials and remove them afterwards.
scripts/release/lib/common.sh is the bash twin of this file; keep them at parity.
#>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:Helper = Join-Path $PSScriptRoot 'studio_release.py'
# Variables that would turn on CI-only behavior (federated identity checks, OIDC relogin) or a stale client binding.
$script:ScrubbedVariables = 'GITHUB_ACTIONS', 'STUDIO_FEDERATED_IDENTITY', 'AZURE_CLIENT_ID', 'ACTIONS_ID_TOKEN_REQUEST_URL',
    'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_OUTPUT'

function Write-Step {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive progress for a person at a console; the output stream carries results.')]
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive progress for a person at a console; the output stream carries results.')]
    param([string]$Message, [Nullable[ConsoleColor]]$Color)
    if ($null -ne $Color) { Write-Host $Message -ForegroundColor $Color } else { Write-Host $Message }
}

function Exit-StudioWithError([string]$Message) { throw [System.InvalidOperationException]::new($Message) }

function Resolve-StudioPath([string]$Path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }

function Set-JobVariable {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal job helper; the entry scripts are not -WhatIf commands.')]
    param($Job, [string]$Name, $Value)
    if (-not $Job.Saved.ContainsKey($Name)) { $Job.Saved[$Name] = [Environment]::GetEnvironmentVariable($Name) }
    [Environment]::SetEnvironmentVariable($Name, $(if ($null -eq $Value -or $Value -eq '') { $null } else { [string]$Value }))
}

function Find-StudioPython([string]$Python) {
    $candidates = if ($Python) { @($Python) } else { @('python3.12', 'python3.11', 'python3', 'python') }
    foreach ($candidate in $candidates) {
        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        $version = & $command.Source -c 'import sys; print("%d.%d" % sys.version_info[:2])' 2>$null
        if ($LASTEXITCODE -eq 0 -and $version -in @('3.11', '3.12')) { return $command.Source }
        if ($Python) { Exit-StudioWithError "Python $Python is $version; the pipeline needs Python 3.12 (or 3.11)." }
    }
    Exit-StudioWithError 'Python 3.12 (or 3.11) was not found. Install it or pass -Python <path>.'
}

function Invoke-StudioHelper($Job, [string[]]$Arguments) {
    $output = & $Job.Python $script:Helper @Arguments
    if ($LASTEXITCODE) { Exit-StudioWithError "Preflight failed: $($Arguments[0]) (see the error above)." }
    $values = @{}
    foreach ($line in @($output)) {
        $index = "$line".IndexOf('=')
        if ($index -gt 0) { $values["$line".Substring(0, $index)] = "$line".Substring($index + 1) }
    }
    return $values
}

function New-StudioJob {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal job helper; the entry scripts are not -WhatIf commands.')]
    param(
        [Parameter(Mandatory)][ValidateSet('build', 'promote', 'deploy')][string]$Stage,
        [string]$Component = 'gpu',
        [string]$Settings,
        [string[]]$Region = @(),
        [string]$Python
    )
    $job = @{
        Stage = $Stage; Component = $Component; Saved = @{}; SettingsArgument = $Settings; IsolatedAzure = $false
        Python = Find-StudioPython $Python; StagePython = $null; KeepWork = $false
    }
    foreach ($tool in 'az', 'dotnet', 'git') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Exit-StudioWithError "$tool was not found on PATH; it is required." }
    }
    $job.Work = Join-Path ([IO.Path]::GetTempPath()) "wan-studio-$Stage-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    New-Item -ItemType Directory -Path $job.Work | Out-Null
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($job.Work, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute') }
    $job.SettingsFile = Join-Path $job.Work 'settings.json'
    foreach ($name in $script:ScrubbedVariables) { Set-JobVariable $job $name $null }
    Set-JobVariable $job 'STUDIO_REGISTRY_LOG' (Join-Path $job.Work 'studio-registries')

    Write-Step 'Checking the settings document'
    $arguments = @('settings', '--out', $job.SettingsFile, '--stage', $Stage)
    if ($Settings) { $arguments += @('--settings', (Resolve-StudioPath $Settings)) }
    foreach ($key in $Region) { $arguments += @('--region', $key) }
    try {
        $job.Values = Invoke-StudioHelper $job $arguments
    } catch {
        $null = Remove-StudioJob $job
        throw
    }
    $job.Regions = @($job.Values.REGIONS -split ' ' | Where-Object { $_ })
    Write-Note "    tenant $($job.Values.TENANT_ID), subscription $($job.Values.SUBSCRIPTION_ID), control region $($job.Values.CONTROL_REGION)"
    return $job
}

function Connect-StudioAzure {
    param(
        [Parameter(Mandatory)]$Job,
        [ValidateSet('Current', 'Interactive', 'DeviceCode', 'ManagedIdentity', 'ServicePrincipal')][string]$Login = 'Current',
        [string]$ClientId,
        [string]$FederatedTokenFile,
        [string]$Certificate,
        # 'build' when this job also builds, so the publisher identity is enforced.
        [string]$IdentityStage
    )
    $tenant, $subscription = $Job.Values.TENANT_ID, $Job.Values.SUBSCRIPTION_ID
    if ($Login -ne 'Current') {
        $Job.IsolatedAzure = $true
        Set-JobVariable $Job 'AZURE_CONFIG_DIR' (Join-Path $Job.Work 'studio-azure')
    }
    Write-Step "Signing in to Azure CLI ($Login)"
    switch ($Login) {
        'Current' {
            $current = az account show --query id -o tsv --only-show-errors 2>$null
            if ($LASTEXITCODE) { Exit-StudioWithError 'Azure CLI is not signed in. Run az login as the publisher principal, or pass -Login Interactive, DeviceCode, ManagedIdentity or ServicePrincipal.' }
            if ($current -ne $subscription) {
                Write-Note "    Selecting subscription $subscription in your current Azure CLI profile (it was $current)."
            }
        }
        'Interactive' { az login --tenant $tenant --only-show-errors -o none }
        'DeviceCode' { az login --tenant $tenant --use-device-code --only-show-errors -o none }
        'ManagedIdentity' {
            $identity = @('--identity'); if ($ClientId) { $identity += @('--client-id', $ClientId) }
            az login @identity --only-show-errors -o none
        }
        'ServicePrincipal' {
            if (-not $ClientId) { Exit-StudioWithError '-Login ServicePrincipal needs -ClientId (the application/client ID).' }
            if ([bool]$FederatedTokenFile -eq [bool]$Certificate) {
                Exit-StudioWithError '-Login ServicePrincipal needs exactly one of -FederatedTokenFile or -Certificate. Client secrets are not supported here; sign in yourself and use -Login Current if you must use one.'
            }
            if ($FederatedTokenFile) {
                $token = (Get-Content -LiteralPath $FederatedTokenFile -Raw).Trim()
                az login --service-principal --username $ClientId --tenant $tenant --federated-token $token --only-show-errors -o none
            } else {
                az login --service-principal --username $ClientId --tenant $tenant --certificate (Resolve-StudioPath $Certificate) --only-show-errors -o none
            }
        }
    }
    if ($LASTEXITCODE) { Exit-StudioWithError "az login ($Login) failed." }
    az account set --subscription $subscription --only-show-errors
    if ($LASTEXITCODE) { Exit-StudioWithError "The signed-in identity cannot select subscription $subscription." }
    $values = Invoke-StudioHelper $Job @('identity', '--settings', $Job.SettingsFile, '--stage', ($IdentityStage ? $IdentityStage : $Job.Stage))
    Write-Note "    Azure CLI caller: $($values.CALLER) ($($values.CALLER_TYPE))"
}

function Test-StudioNetwork($Job) {
    Write-Step 'Checking private DNS and reachability of the studio endpoints'
    $arguments = @('network', '--settings', $Job.SettingsFile, '--stage', $Job.Stage, '--component', $Job.Component)
    foreach ($key in $Job.Regions) { $arguments += @('--region', $key) }
    Invoke-StudioHelper $Job $arguments | Out-Null
}

function Test-StudioEngine($Job, [string]$ContainerEngine) {
    if (-not $ContainerEngine) {
        Exit-StudioWithError 'Building needs -ContainerEngine Docker, Podman or Wslc (the engine that builds, pushes and checks the image).'
    }
    $engine = $ContainerEngine.ToLowerInvariant()
    Write-Step "Checking container engine $engine"
    $values = Invoke-StudioHelper $Job @('engine', '--engine', $engine)
    Write-Note "    $($values.ENGINE_CLI): $($values.ENGINE_VERSION)"
    Set-JobVariable $Job 'STUDIO_CONTAINER_ENGINE' $engine
}

function Resolve-StudioFeed($Job) {
    $url = $env:PIP_INDEX_URL
    if (-not $url) {
        $url = (& $Job.Python -m pip config get global.index-url 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE) { $url = '' }
    }
    $values = Invoke-StudioHelper $Job @('feed', '--url', "$url")
    Set-JobVariable $Job 'PIP_INDEX_URL' $values.PIP_INDEX_URL
}

function Initialize-StudioToolchain($Job, [string]$Venv) {
    $Venv = if ($Venv) { Resolve-StudioPath $Venv } else { Join-Path $script:Root '.artifacts/release-venv' }
    $python = if ($IsWindows) { Join-Path $Venv 'Scripts/python.exe' } else { Join-Path $Venv 'bin/python' }
    if (-not (Test-Path -LiteralPath $python)) {
        Write-Step "Creating the pipeline virtual environment in $Venv"
        & $Job.Python -m venv $Venv
        if ($LASTEXITCODE) { Exit-StudioWithError "Could not create a virtual environment in $Venv." }
    }
    $locks = @('scripts/ci/requirements.lock', 'build/gpu/requirements-build.lock') | ForEach-Object { Join-Path $script:Root $_ }
    # Same stamp format as lib/common.sh, so switching shells does not reinstall.
    $stamp = ($locks | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + ',' }) -join ''
    $stampFile = Join-Path $Venv '.studio-locks'
    if (-not (Test-Path -LiteralPath $stampFile) -or (Get-Content -LiteralPath $stampFile -Raw).Trim() -ne $stamp) {
        Write-Step 'Installing the locked pipeline dependencies (hash-checked, from your configured pip index)'
        foreach ($lock in $locks) {
            & $python -m pip install --quiet --disable-pip-version-check --require-hashes -r $lock
            if ($LASTEXITCODE) { Exit-StudioWithError "Installing $lock failed. Check that pip's index (pip config list) is your approved feed and reachable." }
        }
        Set-Content -LiteralPath $stampFile -Value $stamp
    }
    $Job.StagePython = $python
    Write-Step 'Building the shared contract validator'
    $contracts = Join-Path $script:Root '.artifacts/release-contracts'
    dotnet build (Join-Path $script:Root 'tools/VideoSafetyStudio.Contracts') --artifacts-path $contracts -v quiet -nologo | Out-Host
    if ($LASTEXITCODE) { Exit-StudioWithError 'Building tools/VideoSafetyStudio.Contracts failed.' }
    Set-JobVariable $Job 'STUDIO_CONTRACT_TOOL' (Join-Path $contracts 'bin/VideoSafetyStudio.Contracts/debug/VideoSafetyStudio.Contracts.dll')
}

function Invoke-StudioStage($Job, [string]$Script, [string[]]$Arguments) {
    $lines = [Collections.Generic.List[string]]::new()
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Job.StagePython (Join-Path $script:Root "scripts/ci/$Script") @Arguments 2>&1 | ForEach-Object {
            $line = "$_"
            Write-Note $line
            $lines.Add($line)
        }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ ExitCode = $code; Lines = $lines }
}

function Invoke-StudioBuild {
    param([Parameter(Mandatory)]$Job, [switch]$AllowDirty, [string]$UpstreamSource, [string]$ModelCache, [string]$LicenseCache,
          [switch]$ApproveTrainerWeights)
    $commit = (git -C $script:Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -or $commit -notmatch '^[a-f0-9]{40}$') { Exit-StudioWithError 'The build must run from a git checkout of the studio.' }
    $dirty = git -C $script:Root status --porcelain --untracked-files=no
    if ($dirty) {
        if (-not $AllowDirty) {
            Exit-StudioWithError "The checkout has uncommitted changes, but the artifact records commit $commit as its source. Commit or stash them, or pass -AllowDirty."
        }
        Write-Warning "Building with uncommitted changes; the artifact still records commit $commit."
    }
    Set-JobVariable $Job 'STUDIO_BUILD_SOURCE_SHA' $commit
    Set-JobVariable $Job 'STUDIO_BUILD_RUN_ID' (Get-Date -AsUTC -Format 'yyyyMMddHHmmss')
    Set-JobVariable $Job 'STUDIO_BUILD_RUN_ATTEMPT' '1'
    $output = Join-Path $Job.Work 'build-output.txt'
    Set-JobVariable $Job 'STUDIO_BUILD_OUTPUT' $output
    foreach ($pair in @(@('STUDIO_UPSTREAM_SOURCE', $UpstreamSource), @('STUDIO_MODEL_CACHE', $ModelCache), @('STUDIO_LICENSE_CACHE', $LicenseCache))) {
        if ($pair[1]) { Set-JobVariable $Job $pair[0] (Resolve-StudioPath $pair[1]) }
    }
    if ($ApproveTrainerWeights) { Set-JobVariable $Job 'STUDIO_APPROVE_TRAINER_WEIGHTS' 'true' }
    Write-Step "Building, verifying and sealing the $($Job.Component) artifact from commit $commit"
    $result = Invoke-StudioStage $Job 'build_image.py' @('--component', $Job.Component, '--settings', $Job.SettingsFile, '--work', (Join-Path $Job.Work 'build'))
    if ($result.ExitCode) { Exit-StudioWithError "The $($Job.Component) build did not seal an artifact (exit $($result.ExitCode)); see the output above." }
    $artifact = if (Test-Path -LiteralPath $output) {
        Get-Content -LiteralPath $output | Where-Object { $_ -match '^artifact=[a-f0-9]{64}$' } | ForEach-Object { $_.Substring(9) } | Select-Object -Last 1
    }
    if (-not $artifact) { Exit-StudioWithError 'The build finished but reported no artifact ID.' }
    return $artifact
}

function Show-StudioBanner($Job, [string]$Stage, [string]$Artifact, [string[]]$Ok = @(), [string[]]$Failed = @()) {
    $arguments = @('banner', '--stage', $Stage, '--component', $Job.Component, '--artifact', $Artifact, '--shell', 'pwsh')
    if ($Job.SettingsArgument) { $arguments += @('--settings', $Job.SettingsArgument) }
    foreach ($key in $Ok) { $arguments += @('--ok', $key) }
    foreach ($key in $Failed) { $arguments += @('--failed', $key) }
    & $Job.Python $script:Helper @arguments | Out-Host
}

function Remove-StudioJob {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal job helper; the entry scripts are not -WhatIf commands.')]
    param($Job)
    # Returns $false when a registry login could not be removed, so the entry script fails like the bash twin.
    if (-not $Job) { return $true }
    $removed = $true
    try {
        & (Join-Path $script:Root 'scripts/ci/Remove-StudioCredentials.ps1') -Directory $Job.Work | Out-Host
    } catch {
        Write-Note "ERROR: $($_.Exception.Message)" -Color Red
        $removed = $false
    } finally {
        if (-not $Job.KeepWork -and (Test-Path -LiteralPath $Job.Work)) { Remove-Item -LiteralPath $Job.Work -Recurse -Force -ErrorAction SilentlyContinue }
        elseif ($Job.KeepWork) { Write-Note "Job folder kept: $($Job.Work) (it holds the settings document; delete it when done)." }
        foreach ($name in $Job.Saved.Keys) { [Environment]::SetEnvironmentVariable($name, $Job.Saved[$name]) }
    }
    return $removed
}

Export-ModuleMember -Function New-StudioJob, Connect-StudioAzure, Test-StudioNetwork, Test-StudioEngine, Resolve-StudioFeed,
    Initialize-StudioToolchain, Invoke-StudioStage, Invoke-StudioBuild, Show-StudioBanner, Remove-StudioJob, Write-Step, Write-Note, Exit-StudioWithError
