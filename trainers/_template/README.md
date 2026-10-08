# Adding a trainer
1. Copy `_template` to `vision/trainers/<name>/` and implement `MyTrainer` (4 methods, see `sdk.py`).
2. Write a `Dockerfile` that copies `vision/trainers/sdk.py` and your folder, with `ENTRYPOINT ["python", "-m", "vision.trainers.<name>"]` (see `yolox/Dockerfile`).
3. Fill in `trainer.json` (image tag, **both licences**, presets). Keep non-Apache trainers in your own repo and point `WAN_STUDIO_TRAINERS_DIR` at the folder holding `<name>/trainer.json`.
4. Build the image, then run `python -m unittest test_trainer_contract` after adding your trainer class to `CONTRACT_TRAINERS` there.
The portal only runs the image with `all --preset <p> [--epochs N]`, mounting the dataset at `/data` (read-only) and outputs at `/out`.
