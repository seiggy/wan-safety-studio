"""Runs inside an already-built image, against read-only staged code and all .NET-rendered WAN modes."""
import asyncio
import json
from pathlib import Path
import sys


def probe(code=Path("/code"), workflows=Path("/workflows"), scratch=Path("/check")):
    (scratch / "user").mkdir()
    (scratch / "temp").mkdir()
    sys.argv = ["main.py", "--cpu", "--disable-all-custom-nodes", "--disable-api-nodes",
                "--base-directory", str(scratch), "--temp-directory", str(scratch / "temp"),
                "--user-directory", str(scratch / "user"), "--database-url", f"sqlite:///{scratch}/comfyui.db"]
    sys.path.insert(0, str(code / "src"))
    import main
    import nodes
    import torch
    if torch.cuda.is_available():
        raise RuntimeError("CPU validation must not have GPU access.")
    asyncio.run(nodes.init_extra_nodes(init_custom_nodes=False, init_api_nodes=False))
    index = json.loads((workflows / "index.json").read_text())
    for mode, filename in index.items():
        payload = json.loads((workflows / filename).read_text())
        missing = {node["class_type"] for node in payload.values()} - nodes.NODE_CLASS_MAPPINGS.keys()
        if missing:
            raise ValueError(f"Missing nodes for served mode {mode}: {sorted(missing)}")
    print("CPU staged-source probe passed for modes: " + ", ".join(sorted(index)))


if __name__ == "__main__":
    probe()
