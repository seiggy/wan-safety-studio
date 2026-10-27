#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('settings','tfvars','backend')][string[]]$Inputs
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'Use an existing private temporary input directory.' }
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$Directory = (Resolve-Path -LiteralPath $Directory).Path
if ($Directory.Equals($root, $comparison) -or $Directory.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'Materialize customer inputs outside the checkout.'
}
foreach ($entry in @(@('SETTINGS','settings.json'), @('TFVARS','tfvars.json'), @('BACKEND','backend.json'))) {
    if ($Inputs -notcontains $entry[0].ToLowerInvariant()) { continue }
    $inline = [Environment]::GetEnvironmentVariable("STUDIO_$($entry[0])_JSON")
    $source = [Environment]::GetEnvironmentVariable("STUDIO_$($entry[0])_PATH")
    if ($inline -and $source) { throw "Select exactly one inline or cached-file $($entry[0]) input." }
    if ($source) {
        $resolved = (Resolve-Path -LiteralPath $source).Path
        if ($resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) {
            throw 'Customer inputs must be cached outside the checkout, never supplied by repository code.'
        }
        $inline = [IO.File]::ReadAllText($resolved)
    }
    if (-not $inline) { throw "Explicit $($entry[0]) input is required." }
    if ($inline -and (($inline | ConvertFrom-Json -AsHashtable -Depth 100) -isnot [Collections.IDictionary])) {
        throw 'Pipeline input must be a JSON object.'
    }
    $target = Join-Path $Directory $entry[1]
    [IO.File]::WriteAllText($target, $inline)
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode($target, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
    }
}
