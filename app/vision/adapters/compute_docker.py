"""Local Docker compute target (dev machine GPU, e.g. the CMP 170HX). Same image and arguments as the Azure ML job."""
from __future__ import annotations

from pathlib import Path
import subprocess


class LocalDockerCompute:
    name = "local-docker"

    def __init__(self, gpus: str = "all", shm_size: str = "8g"):
        self.gpus, self.shm_size = gpus, shm_size

    def run(self, image: str, args: list[str], data: Path, out: Path, log: Path) -> int:
        command = ["docker", "run", "--rm", "--gpus", self.gpus, "--shm-size", self.shm_size,
                   "-v", f"{data.resolve()}:/data:ro", "-v", f"{out.resolve()}:/out", image, *args]
        with log.open("wb") as handle:
            return subprocess.run(command, stdout=handle, stderr=subprocess.STDOUT, check=False).returncode
