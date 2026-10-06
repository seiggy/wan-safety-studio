from __future__ import annotations

from pathlib import Path


def project_root() -> Path:
    return Path(__file__).resolve().parents[1]


def app_root(root: Path | None = None) -> Path:
    project = (root or project_root()).resolve()
    candidates = [project / "src", project]
    for candidate in candidates:
        if (candidate / "main.py").is_file() and (candidate / "folder_paths.py").is_file():
            return candidate
    checked = ", ".join(str(candidate) for candidate in candidates)
    raise FileNotFoundError(f"Could not locate the ComfyUI app root. Checked: {checked}")


def models_root(root: Path | None = None) -> Path:
    return app_root(root) / "models"
