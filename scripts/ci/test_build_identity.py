"""Independent, standard-library cross-language check of the committed build identity vectors."""
import copy
import hashlib
import json
from pathlib import Path


def artifact_id(manifest):
    value = copy.deepcopy(manifest)
    value["artifactId"] = ""
    for section in ("code", "models", "licenses", "buildInputs"):
        value[section].sort(key=lambda file: file["path"].encode("utf-16-be"))
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[2]
    fixture = json.loads((root / "contracts" / "fixtures" / "studio.json").read_text(encoding="utf-8"))
    for name in ("build", "unicodeBuild"):
        manifest = fixture[name]
        assert artifact_id(manifest) == manifest["artifactId"], f"Cross-language artifact identity differs: {name}"
    print("PASS: ASCII and Unicode build identities match the .NET golden vectors.")
