<#
.SYNOPSIS
  Points git at the repo's .githooks folder so the docs pre-commit hook runs. Run once per clone.
#>
$root = (git rev-parse --show-toplevel).Trim()
git -C $root config core.hooksPath .githooks
Write-Host "core.hooksPath = $(git -C $root config core.hooksPath). The docs-maintainer hook is active. Bypass once with SKIP_DOCS_AGENT=1."
