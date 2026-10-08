<#
.SYNOPSIS
  Writes src\VideoSafetyStudio.AppHost\appsettings.Development.json (git-ignored) for local Aspire runs. Defaults: Azurite storage, ComfyUI at http://localhost:8188 over its API.
.EXAMPLE
  .\scripts\Initialize-LocalDev.ps1
  .\scripts\Initialize-LocalDev.ps1 -Provider azureml    # use the deployed Azure ML workspace instead of ComfyUI
  .\scripts\Initialize-LocalDev.ps1 -Storage local -StoragePath \\nas\wan
  .\scripts\Initialize-LocalDev.ps1 -ComfyUiUrl http://gpubox:8188 -Retrieval share -ComfyUiOutputDir Z:\ComfyUI\output
#>
[CmdletBinding()]
param(
    [ValidateSet('comfyui', 'azureml')][string]$Provider = 'comfyui',  # comfyui = fully local; azureml = your deployed Azure ML workspace
    [string]$AzureMlConfigPath,     # azureml only: -ConfigPath for Invoke-WanSafetyStudio.ps1
    [string]$AzureMlCacheDirectory, # azureml only: -CacheDirectory
    [ValidateSet('azurite', 'local', 'azure')][string]$Storage = 'azurite',
    [string]$StoragePath,        # local only: folder or mounted LAN share (\\server\share, /mnt/share)
    [string]$StorageAccount,     # azure only
    [string]$ComfyUiUrl = 'http://localhost:8188',
    [ValidateSet('api', 'share')][string]$Retrieval = 'api',
    [string]$ComfyUiOutputDir,   # share only
    [string]$WorkflowsDir,
    [string]$UnetHigh, [string]$UnetLow, [string]$Vae, [string]$Clip, [string]$LoraHigh, [string]$LoraLow, [string]$Unet5B, [string]$Vae5B   # model file names on the ComfyUI server
)
$ErrorActionPreference = 'Stop'
if ($Storage -eq 'azure' -and -not $StorageAccount) { throw '-StorageAccount is required with -Storage azure.' }
if ($Retrieval -eq 'share' -and -not $ComfyUiOutputDir) { throw '-ComfyUiOutputDir is required with -Retrieval share.' }
$storageSection = [ordered]@{ Backend = $Storage }
if ($Storage -eq 'local' -and $StoragePath) { $storageSection.Path = $StoragePath }
if ($Storage -eq 'azure') { $storageSection.Account = $StorageAccount }
$comfy = [ordered]@{ Retrieval = $Retrieval }
if ($ComfyUiOutputDir) { $comfy.OutputDir = $ComfyUiOutputDir }
foreach ($pair in @(@('UnetHigh', $UnetHigh), @('UnetLow', $UnetLow), @('Vae', $Vae), @('Clip', $Clip), @('LoraHigh', $LoraHigh), @('LoraLow', $LoraLow), @('Unet5B', $Unet5B), @('Vae5B', $Vae5B))) { if ($pair[1]) { $comfy[$pair[0]] = $pair[1] } }
if ($WorkflowsDir) { $comfy.WorkflowsDir = (Resolve-Path $WorkflowsDir).Path }
if ($Provider -eq 'azureml') {
    $azure = [ordered]@{}
    if ($AzureMlConfigPath) { $azure.ConfigPath = (Resolve-Path $AzureMlConfigPath).Path }
    if ($AzureMlCacheDirectory) { $azure.CacheDirectory = [IO.Path]::GetFullPath($AzureMlCacheDirectory) }
    $config = [ordered]@{ Generation = [ordered]@{ Provider = 'azureml' }; AzureMl = $azure }
} else {
    $config = [ordered]@{ Generation = [ordered]@{ Provider = 'comfyui' }; Storage = $storageSection; ComfyUi = $comfy; Parameters = [ordered]@{ 'comfyui-url' = $ComfyUiUrl } }
}
$path = Join-Path (Split-Path $PSScriptRoot) 'src\VideoSafetyStudio.AppHost\appsettings.Development.json'
$config | ConvertTo-Json -Depth 5 | Set-Content -Path $path -Encoding utf8
Write-Host "Wrote $path"
if ($Provider -eq 'azureml') { Write-Host 'Provider=azureml: az login to the studio tenant/subscription first; Prepare, Start (arm) and Initialize-PortalAuth must be done.'; Write-Host 'Run: aspire start'; return }
Write-Host 'Note: a comfyui-url already saved from the Aspire dashboard (user secrets) overrides -ComfyUiUrl; change it there.'
Write-Host "Storage=$Storage Retrieval=$Retrieval ComfyUI=$ComfyUiUrl. Run: aspire start"
