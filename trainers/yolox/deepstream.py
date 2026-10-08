"""DeepStream (nvinfer) bundle files for an exported YOLOX model. Parser details are marked for on-device verification."""
from vision.trainers.sdk import DeepStreamConfig, OnnxModel


def render_deepstream(model: OnnxModel, classes) -> DeepStreamConfig:
    width, height = model.input_size
    config = f"""[property]
gpu-id=0
# YOLOX consumes BGR 0-255 with no mean/scale normalisation.
net-scale-factor=1.0
model-color-format=1
onnx-file={model.path.name}
labelfile-path=labels.txt
batch-size=1
# 2 = FP16. Use 1 (INT8) only with a calibration cache.
network-mode=2
num-detected-classes={len(classes)}
gie-unique-id=1
process-mode=1
network-type=0
interval=0
cluster-mode=2
maintain-aspect-ratio=1
symmetric-padding=0
parse-bbox-func-name=NvDsInferParseYolo
custom-lib-path=nvdsinfer_custom_impl_Yolo/libnvdsinfer_custom_impl_Yolo.so

[class-attrs-all]
nms-iou-threshold=0.45
pre-cluster-threshold=0.25
topk=300
"""
    notes = (
        f"Model input {width}x{height}. The first run on the Jetson builds the TensorRT engine from model.onnx and caches it.",
        "Parser: build the community DeepStream-Yolo custom library (nvdsinfer_custom_impl_Yolo) on the Orin Nano and confirm it "
        "decodes this YOLOX export (raw head output vs decoded). VERIFY against DeepStream 9.1 before relying on it.",
    )
    return DeepStreamConfig({"config_infer_primary.txt": config, "labels.txt": "\n".join(classes) + "\n"}, notes)
