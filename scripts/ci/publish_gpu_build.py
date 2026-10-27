"""Publish a CPU-sealed build's immutable payloads, never AML registrations or a ready binding."""
import argparse
from pathlib import Path

from pipeline import Azure, contracts, immutable_bytes, json_bytes, load, require, snapshot


def publish(settings, stage, assets):
    manifest_path = stage / "manifest.json"
    checked = contracts(settings, manifest_path)
    receipt, build = checked["foundation"], checked["build"]
    azure = Azure(receipt["scope"])
    require(load(settings).get("Pipeline", {}).get("PublisherPrincipalId") == azure.actor_id,
            "Build publication must use the explicit Pipeline.PublisherPrincipalId principal.")
    region = receipt["regions"][receipt["controlRegion"]]
    container = azure.container(region)
    for kind, directory in (("code", stage / "code"), ("models", assets / "models"),
                            ("licenses", assets / "licenses"), ("buildInputs", stage / "build-inputs")):
        snapshot(container, kind, build[kind], lambda record: (directory / record["path"]).open("rb"))
    immutable_bytes(container, f"builds/{build['artifactId']}/manifest.json", json_bytes(build))
    return build["artifactId"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args()
    print("Published immutable GPU build " + publish(args.settings, args.stage, args.assets) + "; no region activated.")
