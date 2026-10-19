# Issue tracker: GitHub

Issues and specs live in GitHub Issues for `seiggy/wan-safety-studio`. Use `gh` from the repo root; it resolves the repository from the Git remote.

## Conventions

- Create: `gh issue create --title "..." --body "..."`.
- Read: `gh issue view <number> --json number,title,body,labels,comments`.
- List: `gh issue list --state open --json number,title,body,labels,comments`; add label filters as needed.
- Comment: `gh issue comment <number> --body "..."`.
- Label: `gh issue edit <number> --add-label "..."` or `--remove-label "..."`.
- Close: `gh issue close <number> --comment "..."`.

For multiline bodies in PowerShell, pipe a single-quoted here-string to `gh issue create --title "..." --body-file -`. Keep credentials out of issue bodies.

## Pull requests as a triage surface

**PRs as a request surface: no.**

GitHub shares issue and PR numbers. Resolve ambiguous references with `gh pr view <number>`, then `gh issue view <number>` if needed.

## Publishing and ticket lookup

When a skill says to publish to the issue tracker, create a GitHub issue. To fetch a ticket, use the Read command above.

## Wayfinding

Use one issue labelled `wayfinder:map` for the map. Link child tickets with `gh issue create --parent <map-number> ...` or `gh issue edit <map-number> --add-sub-issue <child-number>`. If unavailable, put `Part of #<map-number>` in the child and add it to the map's task list.

Child labels are `wayfinder:research`, `wayfinder:prototype`, `wayfinder:grilling` or `wayfinder:task`. Create a missing label when the workflow first needs it.

Record blockers with GitHub's native issue dependencies. The API requires the blocker's numeric database ID, available from `gh api repos/seiggy/wan-safety-studio/issues/<number> --jq .id`:

`gh api --method POST repos/seiggy/wan-safety-studio/issues/<child-number>/dependencies/blocked_by -F issue_id=<blocker-database-id>`

If dependencies are unavailable, add a `Blocked by: #<number>` line instead. Choose the first open, unassigned child in map order whose blockers are closed. Claim it with `gh issue edit <number> --add-assignee @me`.

When resolved, comment with the answer, close the child, and add a context link to the map's Decisions-so-far section.
