# Agent instructions

## Aspire (local dev)

- **Do not stop or restart the AppHost** (`aspire stop`/`aspire start`) unless `apphost.cs` itself changed (resources, parameters, env wiring, packages). If it is already running, leave it running.
- After changing code in one app, restart only that resource: `aspire resource api restart` (Python dev API, `app\`) or `aspire resource web restart` (Vite UI, `web\`), or the equivalent Aspire MCP `execute_resource_command`. Then `aspire wait <resource>` before testing. The web UI normally hot-reloads, so it rarely needs a restart.
- Check state with `aspire describe`, `aspire logs <resource>` and `aspire otel traces|logs` instead of restarting to "see what happened".
- Start the AppHost only when none is running (`aspire ps`). Always pass `--non-interactive`.
- Python dependencies are installed with `scripts\Install-DevPython.ps1`, never `uv sync`/`uv add` (they rewrite `app\uv.lock` with internal feed URLs; never commit those).
- `app\test_portal.py` needs `web\dist` (`npm run build` in `web\`).
