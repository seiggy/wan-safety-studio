<#
.SYNOPSIS
  Git pre-commit worker: asks the docs-maintainer Copilot agent to update docs-site/ for the staged change and stages the result.
.DESCRIPTION
  Fail-open by design: a missing CLI, a timeout, or an agent error prints a warning and lets the commit continue.
  Skips when only docs-site/ or non-code files are staged.
#>
[CmdletBinding()]
param([int]$TimeoutMinutes = 15)

$ErrorActionPreference = 'Stop'
$root = (git rev-parse --show-toplevel).Trim()
Set-Location $root

function Skip([string]$reason) { Write-Host "docs-agent: $reason"; exit 0 }

$staged = @(git diff --cached --name-only --diff-filter=ACMRD)
if ($staged.Count -eq 0) { Skip 'nothing staged.' }

# Only changes that can alter what the docs say.
$relevant = @($staged | Where-Object {
    $_ -notmatch '^docs-site/' -and $_ -match '^(src|web/src|azureml|trainers|infra|scripts|tests|README\.md|AGENTS\.md|PRODUCT\.md)' -and
    $_ -notmatch '\.(lock|snap)$|package-lock\.json$|\.tfstate|\.tfvars\.json$'
})
if ($relevant.Count -eq 0) { Skip 'no documentation-relevant files staged.' }

$copilot = Get-Command copilot -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $copilot) { Skip 'copilot CLI not found; docs not updated (run it manually later).' }

$before = (git status --porcelain -- docs-site) -join "`n"
$prompt = @"
Update the documentation in docs-site/ for the currently staged git change, following your instructions. Staged files:
$($relevant -join "`n")
"@

Write-Host "docs-agent: reviewing $($relevant.Count) staged file(s) against the docs (up to $TimeoutMinutes min)..."
$env:DOCS_AGENT_RUNNING = '1'
$log = Join-Path ([IO.Path]::GetTempPath()) "docs-agent-$(Get-Date -Format yyyyMMdd-HHmmss).log"
# Start-Process does not quote array elements, so build one string; the prompt is a single line with no double quotes.
$oneLine = ($prompt -replace '"', "'") -replace '\s+', ' '
$cliArgs = "--agent docs-maintainer -p `"$oneLine`" --no-ask-user --allow-all-tools --deny-tool=`"shell(git commit)`" --deny-tool=`"shell(git push)`" --deny-tool=`"shell(git add)`" --no-color -s"
$proc = Start-Process -FilePath $copilot.Source -ArgumentList $cliArgs -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
if (-not $proc.WaitForExit($TimeoutMinutes * 60000)) {
    Stop-Process -Id $proc.Id -Force
    Write-Warning "docs-agent: timed out after $TimeoutMinutes min; commit continues without doc updates."
    exit 0
}
if ($proc.ExitCode -ne 0) { Write-Warning "docs-agent: agent exited $($proc.ExitCode) (see $log.err); commit continues."; exit 0 }

# Keep the agent inside docs-site: anything else it touched is reverted from the working tree only if it was not staged.
$changed = @(git status --porcelain -- . | ForEach-Object { $_.Substring(3).Trim('"') })
$outside = @($changed | Where-Object { $_ -notmatch '^docs-site/' -and $staged -notcontains $_ })
if ($outside.Count -gt 0) { Write-Warning "docs-agent: edited files outside docs-site/ ($($outside -join ', ')); not staging them." }

$after = (git status --porcelain -- docs-site) -join "`n"
if ($after -ne $before) {
    git add -- docs-site
    Write-Host 'docs-agent: documentation updated and staged with this commit.'
} else {
    Write-Host 'docs-agent: no documentation changes needed.'
}
exit 0
