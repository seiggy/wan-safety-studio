# Vision trainers

Training is a plug-in. The portal never imports a model library: it runs a trainer **container image** on a compute target and reads files back.

```text
Dataset version (COCO, in Blob/Azurite/folder) --materialize--> /data  (classes.json, train.json, val.json, images/)
  docker run --gpus all -v data:/data:ro -v out:/out <image> all --preset <p> [--epochs N]
/out: best weights, metrics.json, model.onnx, deepstream/ (nvinfer config, labels.txt, NOTES.txt), train.log  -> runs/<id>/out/
```

- Contract: `trainers\sdk.py` (`Trainer`: `train`, `evaluate`, `export`, `deepstream`; `run_cli` wires the container commands).
- Registry: each `trainer.json` (image, `module`, **code and weights licences**, presets). Built-in: `yolox` (Apache-2.0; presets nano, tiny, s). Customer trainers live outside this repo: set `WAN_STUDIO_TRAINERS_DIR` to a folder of `<name>/trainer.json`.
- Add a model: copy `trainers\_template`, see its README, and put the folder under `WAN_STUDIO_TRAINERS_DIR`.
- Compute, local: `DockerComputeTarget` is the supported local .NET training mode (a developer GPU, the `comfyui` provider, `FEATURE_DATASET_BUILDER_ENABLED=true`). One run at a time; a second start returns 409 "node busy".
- Compute, hosted (implemented offline, **not enabled**): `AzureMlTrainingCompute` runs the same container as one Azure ML command job. See below.

## Hosted remote training (P8)

Hosted deployments never build or pull trainers. The pipeline does, and the server only runs what it promoted:

1. `release.yml` (`component: yolox`) builds the trainer image from the pinned YOLOX commit, verifies it offline, and seals `images/yolox/<artifact>.json` (source SHA, image digest and the exact `trainer.json` metadata). Weights download needs the separate `STUDIO_APPROVE_TRAINER_WEIGHTS` approval.
2. `promote.yml` (`component: yolox`, `publish_trainer.py`) copies that digest into each region's registry, registers an Azure ML environment, and writes the create-only binding `trainers/<name>/<artifact>.json` (`contracts\trainer.schema.json`) last.
3. The server lists those bindings per region (`TrainerBindings`), validates each against the cached scope, and uses the newest valid one. Nothing from `trainers\` is in the server image.

A run then: takes a generation admission slot (so training can never exceed the deployed or configured limits and shares capacity with generation); picks an armed region that holds the same promoted build; stages the frozen dataset create-only under `studio/training/<run>/data/` with a hash manifest; saves the run's remote identity (`train-<run>`) before the one create call; and follows the job with a timeout equal to that region's configured job limit (never above 120 minutes). Its output folder is copied back and the usual metrics, upload and failure handling apply. After a restart the saved job is followed, never recreated; a run that never reached a remote job is marked failed. A host shutdown detaches; only the run-time limit cancels the remote job, and only with fresh proof of termination.

The command is fixed: `python -m <module> all --preset <preset> [--epochs N] --data <input> --out <output>`. The module, preset names and image digest come from the validated binding, never from a request. Limits: the trainer's own stdout is not collected (Azure ML keeps it in the job); training longer than the region's job limit needs a deployment policy change; long jobs on the shared Spot node can be preempted and are reported as failed.

The hosted dataset builder stays off (`feature_dataset_builder_enabled` must be false) until a real remote training run and CPU media handling are demonstrated with separate spend approval.

Customer trainers use the same contract: a `trainer.json` with `module`, your own image build and publication step, and a binding of the same shape. `trainers\_template` is the starting point; no framework is added.

## Build and run the YOLOX image
```powershell
docker build -f trainers/yolox/Dockerfile -t wan-trainer-yolox:dev trainers
```
The image pins YOLOX commit `6ddff48` and bakes the release weights (SHA-256 list at `/weights/SHA256SUMS`: put it in the model card). **Not yet validated on GPU**: the PyTorch/CUDA pair, the ONNX export flags (`tools/export_onnx.py`) and the DeepStream-Yolo parser for this export are Phase 0 checks. Training is available in local mode (Aspire `comfyui` provider) when the dataset builder is enabled (`FEATURE_DATASET_BUILDER_ENABLED=true`); the Azure-backed portal has no `/api/vision` routes.
