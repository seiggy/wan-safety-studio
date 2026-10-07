# Vision trainers

Training is a plug-in. The portal never imports a model library: it runs a trainer **container image** on a compute target and reads files back.

```text
Dataset version (COCO, in Blob/Azurite/folder) --materialize--> /data  (classes.json, train.json, val.json, images/)
  docker run --gpus all -v data:/data:ro -v out:/out <image> all --preset <p> [--epochs N]
/out: best weights, metrics.json, model.onnx, deepstream/ (nvinfer config, labels.txt, NOTES.txt), train.log  -> runs/<id>/out/
```

- Contract: `app\vision\trainers\sdk.py` (`Trainer`: `train`, `evaluate`, `export`, `deepstream`; `run_cli` wires the container commands).
- Registry: each `trainer.json` (image, **code and weights licences**, presets). Built-in: `yolox` (Apache-2.0; presets nano, tiny, s). Customer trainers live outside this repo: set `WAN_STUDIO_TRAINERS_DIR` to a folder of `<name>/trainer.json`.
- Add a model: copy `app\vision\trainers\_template`, see its README. Add your class to `CONTRACT_TRAINERS` in `app\test_trainer_contract.py`.
- Compute: `LocalDockerCompute` (dev GPU) today; the Azure ML adapter on the shared generation node is next. One run at a time; a second start returns 409 "node busy".

## Build and run the YOLOX image
```powershell
docker build -f app/vision/trainers/yolox/Dockerfile -t wan-trainer-yolox:dev app
```
The image pins YOLOX commit `6ddff48` and bakes the release weights (SHA-256 list at `/weights/SHA256SUMS`: put it in the model card). **Not yet validated on GPU**: the PyTorch/CUDA pair, the ONNX export flags (`tools/export_onnx.py`) and the DeepStream-Yolo parser for this export are Phase 0 checks. Training is available in local mode (Aspire `comfyui` provider); the Azure-backed portal returns 404 for `/api/vision/trainers`, which hides the panel.
