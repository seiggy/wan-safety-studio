# Vision trainers

Trainer plug-ins run as **container images**; the .NET server only runs the image (`all --preset <p> [--epochs N]`, dataset at `/data` read-only, outputs at `/out`) and reads the results. See `docs/vision-trainers.md` for the contract.

- `sdk.py` - the `Trainer` contract and `run_cli` (standard library only).
- `yolox/` - the Apache-2.0 YOLOX trainer (presets nano, tiny, s) and its `Dockerfile`.
- `_template/` - copy this to add a model. Folders starting with `_` are not registered.
- Build: `docker build -f trainers/yolox/Dockerfile -t wan-trainer-yolox:dev trainers` (pass `--build-arg PIP_INDEX_URL=<feed>` on feed-only machines).
- Customer trainers live outside this repo: add their parent folder to `Trainers:Directories`.
