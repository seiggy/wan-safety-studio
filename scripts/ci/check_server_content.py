"""Fail if a server publish folder (or image /app) carries operator, CI, infrastructure or customer files, or lacks what the host needs."""
import argparse
from pathlib import Path
import sys

# Only the three production WAN mode templates ship; reference-only workflows live in docs.
WORKFLOWS = {"wan22_i2v.api.json", "wan22_i2v_lightning.api.json", "wan22_ti2v_5b.api.json"}
FORBIDDEN_SUFFIXES = (".ps1", ".sh", ".py", ".tf", ".tfvars", ".tfstate", ".hcl", ".pem", ".pfx", ".key", ".patch", ".lock", ".bicep")
FORBIDDEN_NAMES = {"terraform.tfvars.json", "nuget.config", ".npmrc", ".env", "global.json", "dockerfile", "settings.json", "tfvars.json", "backend.json"}
FORBIDDEN_DIRS = {"infra", "scripts", "trainers", "build", "contracts", "tests", "azureml", ".git", ".github", ".terraform", "node_modules", "docs", "docs-site"}
REQUIRED = ("VideoSafetyStudio.Server.dll", "appsettings.json", "wwwroot/index.html")


def problems(root):
    root = Path(root)
    found = []
    files = [path for path in root.rglob("*") if path.is_file()]
    relative = {path.relative_to(root).as_posix() for path in files}
    for path in files:
        rel = path.relative_to(root).as_posix()
        parts = [part.lower() for part in Path(rel).parts]
        # Third-party runtime content (for example the Copilot runtime's own skills and schemas) lives under runtimes/ and is not ours.
        if parts[0] == "runtimes":
            continue
        if path.suffix.lower() in FORBIDDEN_SUFFIXES or path.name.lower() in FORBIDDEN_NAMES or FORBIDDEN_DIRS & set(parts[:-1]):
            found.append(f"Operator, CI, infrastructure or customer file shipped: {rel}")
    shipped = {Path(rel).name for rel in relative if rel.startswith("Workflows/")}
    if shipped != WORKFLOWS:
        found.append(f"Workflow templates differ from the production modes: extra {sorted(shipped - WORKFLOWS)}, missing {sorted(WORKFLOWS - shipped)}")
    found += [f"Required file missing: {name}" for name in REQUIRED if name not in relative]
    return found


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    result = problems(parser.parse_args().directory)
    for line in result:
        print(line, file=sys.stderr)
    if result:
        raise SystemExit(1)
    print("Server content verified: no operator, CI, infrastructure or customer files; exact production workflow set.")
