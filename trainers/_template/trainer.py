"""Template trainer: copy this folder to vision/trainers/<name>/, implement the four methods, add a Dockerfile and trainer.json.

Only the Trainer protocol in vision/trainers/sdk.py is the contract. Convert the COCO data in request.data to your model's
native format INSIDE this class; nothing outside the folder knows it.
"""
from vision.trainers.sdk import DeepStreamConfig, Metrics, OnnxModel, TrainerInfo, TrainRequest, run_cli


class MyTrainer:
    info = TrainerInfo("my-trainer", "0.1.0", "SPDX-code-licence", "weights-licence", ("small", "large"), "small")

    def train(self, request: TrainRequest):
        # Read request.data/{train,val}.json + images; write checkpoints under request.out; honour request.resume.
        raise NotImplementedError

    def evaluate(self, weights, request: TrainRequest) -> Metrics:
        raise NotImplementedError

    def export(self, weights, out, request: TrainRequest) -> OnnxModel:
        # Write out/model.onnx and return its input size. request carries the preset, so export works standalone.
        raise NotImplementedError

    def deepstream(self, model: OnnxModel, classes) -> DeepStreamConfig:
        # nvinfer config + labels (+ parser notes). See vision/trainers/yolox/deepstream.py.
        raise NotImplementedError


if __name__ == "__main__":
    raise SystemExit(run_cli(MyTrainer()))
