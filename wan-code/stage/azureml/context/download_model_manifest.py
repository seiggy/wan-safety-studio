#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import urllib.request


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output-root", required=True)
    return parser.parse_args()


def download_file(url: str, destination: Path) -> None:
    if destination.exists() and destination.stat().st_size > 0:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    temp_path = destination.with_suffix(destination.suffix + ".part")
    if temp_path.exists():
        temp_path.unlink()
    with urllib.request.urlopen(url) as response, temp_path.open("wb") as output:
        shutil.copyfileobj(response, output, length=16 * 1024 * 1024)
    temp_path.replace(destination)


def main() -> int:
    args = parse_args()
    manifest_path = Path(args.manifest).resolve()
    output_root = Path(args.output_root).resolve()

    payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(payload, list):
        raise ValueError(f"Expected a list manifest in {manifest_path}")

    total = len(payload)
    for index, item in enumerate(payload, start=1):
        if not isinstance(item, dict):
            raise ValueError(f"Invalid manifest entry: {item!r}")
        folder_name = str(item["folder_name"])
        filename = str(item["filename"])
        url = str(item["url"])
        print(
            f"PRELOAD_MODEL [{index}/{total}] {folder_name}/{filename}",
            flush=True,
        )
        download_file(url, output_root / folder_name / filename)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
