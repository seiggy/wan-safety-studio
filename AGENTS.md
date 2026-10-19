# Agent instructions

## Aspire (local dev)

- **Do not stop or restart the AppHost** (`aspire stop`/`aspire start`) unless `AppHost.cs` itself changed (resources, parameters, env wiring, packages). If it is already running, leave it running.
- After changing code in one app, restart only that resource: `aspire resource api rebuild` (.NET API, `src\`) or `aspire resource web restart` (Vite UI, `web\`), or the equivalent Aspire MCP `execute_resource_command`. Then `aspire wait <resource>` before testing. The web UI normally hot-reloads, so it rarely needs a restart.
- Check state with `aspire describe`, `aspire logs <resource>` and `aspire otel traces|logs` instead of restarting to "see what happened".
- Start the AppHost only when none is running (`aspire ps`). Always pass `--non-interactive`.
- The backend is the .NET 10 solution in `src\` (Clean layering: `Domain` -> `Application` -> `Infrastructure` -> `Server`; `AppHost` + `ServiceDefaults` for Aspire). Tests: `dotnet test tests\VideoSafetyStudio.Tests`. `azureml\` is the Python Azure ML operator control plane (Prepare/Start/Stop/Submit/Status, cost guard); the Azure ML generation provider itself lives in the .NET server (`Generation__Provider=azureml`), which is also what the hosted container (`src\VideoSafetyStudio.Server\Dockerfile`) runs. `trainers\` holds the Python trainer containers. The vision dataset builder is gated by `FEATURE_DATASET_BUILDER_ENABLED=true`.
- Restart a changed .NET resource with `aspire resource api restart`; the UI pushes updates over SSE (`/api/events`) instead of polling.
- Python control-plane checks: `python azureml\test_controls.py --scratch $env:TEMP\tc` and `azureml\Test-Controls.ps1` (no web build needed).
- While the AppHost runs, `dotnet test` into the repo cannot overwrite locked server binaries: use `aspire resource api rebuild` to compile, and `dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts` to test.

## Agent skills

### Issue tracker

Track work in GitHub Issues for `seiggy/wan-safety-studio`. See `docs\agents\issue-tracker.md`.

### Triage labels

Use the five default triage labels. See `docs\agents\triage-labels.md`.

### Domain docs

Use a single-context layout: root `GLOSSARY.md` and `docs\adr\`. See `docs\agents\domain.md`.
