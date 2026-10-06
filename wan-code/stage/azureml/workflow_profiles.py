from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable


WAN_DEFAULT_NEGATIVE_PROMPT = (
    "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，"
    "最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，"
    "画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，"
    "杂乱的背景，三条腿，背景人很多，倒着走"
)

LTX_DEFAULT_NEGATIVE_PROMPT = "pc game, console game, video game, cartoon, childish, ugly"


@dataclass(frozen=True)
class ImageInputSpec:
    key: str
    label: str
    required: bool


@dataclass(frozen=True)
class WorkflowProfile:
    key: str
    label: str
    description: str
    workflow: str
    environment_name: str
    models_name: str
    experiment_name: str
    display_name_prefix: str
    models_subdir: str
    width: int
    height: int
    fps: float
    steps: int | None
    cfg: float | None
    sampler_name: str | None
    scheduler: str | None
    denoise: float | None
    batch_size: int
    filename_prefix: str
    duration_seconds: float
    duration_step_seconds: float
    negative_prompt: str
    length_mode: str
    frame_quantum: int
    supports_image: bool
    requires_image: bool
    image_inputs: tuple[ImageInputSpec, ...]
    enable_custom_nodes: bool
    prompt_label: str = "Prompt"
    uses_ltx_runner: bool = False
    submit_prompt_input_name: str = "positive_prompt"
    models_asset_kind: str = "data"
    comfy_low_vram: bool = False
    comfy_disable_smart_memory: bool = False
    comfy_reserve_vram_gb: float | None = None


WAN_PROFILE = WorkflowProfile(
    key="wan",
    label="Wan 2.2",
    description="Image-to-video Wan 2.2 workflow with a required reference image.",
    workflow="azureml/workflows/image_to_video_wan22_i2v_api.json",
    environment_name="comfyui-wan-cu128",
    models_name="comfyui-wan-models",
    experiment_name="comfyui-wan",
    display_name_prefix="comfyui-wan22-video-",
    models_subdir="wan",
    width=768,
    height=768,
    fps=16.0,
    steps=20,
    cfg=3.5,
    sampler_name="euler",
    scheduler="simple",
    denoise=1.0,
    batch_size=1,
    filename_prefix="ComfyUI",
    duration_seconds=5.0,
    duration_step_seconds=0.25,
    negative_prompt=WAN_DEFAULT_NEGATIVE_PROMPT,
    length_mode="quantized",
    frame_quantum=4,
    supports_image=True,
    requires_image=True,
    image_inputs=(
        ImageInputSpec(
            key="input_image",
            label="Reference image",
            required=True,
        ),
    ),
    enable_custom_nodes=False,
    prompt_label="Prompt",
    uses_ltx_runner=False,
    submit_prompt_input_name="positive_prompt",
    models_asset_kind="data",
)


LTX_PROFILE = WorkflowProfile(
    key="ltx",
    label="LTX First/Last Frame",
    description="LTX first/last-frame workflow with required start and end images.",
    workflow="azureml/workflows/ltx_2_first_last_ttp_workflow.json",
    environment_name="comfyui-ltx-cu128",
    models_name="comfyui-ltx-models",
    experiment_name="comfyui-ltx",
    display_name_prefix="comfyui-ltx2-video-",
    models_subdir="ltx",
    width=768,
    height=512,
    fps=24.0,
    steps=None,
    cfg=None,
    sampler_name=None,
    scheduler=None,
    denoise=None,
    batch_size=1,
    filename_prefix="video/ComfyUI",
    duration_seconds=4.0,
    duration_step_seconds=1.0,
    negative_prompt=LTX_DEFAULT_NEGATIVE_PROMPT,
    length_mode="seconds_plus_one",
    frame_quantum=8,
    supports_image=True,
    requires_image=True,
    image_inputs=(
        ImageInputSpec(
            key="start_image",
            label="Start image",
            required=True,
        ),
        ImageInputSpec(
            key="end_image",
            label="End image",
            required=True,
        ),
    ),
    enable_custom_nodes=True,
    prompt_label="CLIP Text Encode (Prompt)",
    uses_ltx_runner=True,
    submit_prompt_input_name="clip_text_encode_prompt",
    models_asset_kind="model",
)


LTX_I2V_PROFILE = WorkflowProfile(
    key="ltx_i2v",
    label="LTX Image to Video",
    description="LTX image-to-video workflow with one required reference image.",
    workflow="src/blueprints/Image to Video (LTX-2.3).json",
    environment_name="comfyui-ltx-cu128",
    models_name="comfyui-ltx-i2v-models",
    experiment_name="comfyui-ltx-i2v",
    display_name_prefix="comfyui-ltx-i2v-video-",
    models_subdir="ltx",
    width=1280,
    height=720,
    fps=25.0,
    steps=None,
    cfg=None,
    sampler_name=None,
    scheduler=None,
    denoise=None,
    batch_size=1,
    filename_prefix="video/ComfyUI",
    duration_seconds=5.0,
    duration_step_seconds=1.0,
    negative_prompt=LTX_DEFAULT_NEGATIVE_PROMPT,
    length_mode="seconds_plus_one",
    frame_quantum=8,
    supports_image=True,
    requires_image=True,
    image_inputs=(
        ImageInputSpec(
            key="input_image",
            label="Reference image",
            required=True,
        ),
    ),
    enable_custom_nodes=True,
    prompt_label="CLIP Text Encode (Prompt)",
    uses_ltx_runner=True,
    submit_prompt_input_name="clip_text_encode_prompt",
    models_asset_kind="data",
)


MINIMAX_H3_PROFILE = WorkflowProfile(
    key="minimax_h3",
    label="MiniMax H3",
    description="MiniMax H3 image-to-video workflow with native stereo audio and a required first frame.",
    workflow="azureml/workflows/image_to_video_minimax_h3_api.json",
    environment_name="comfyui-minimax-h3-cu128",
    models_name="comfyui-minimax-h3-models",
    experiment_name="comfyui-minimax-h3",
    display_name_prefix="comfyui-minimax-h3-video-",
    models_subdir="minimax-h3",
    width=736,
    height=416,
    fps=24.0,
    steps=20,
    cfg=None,
    sampler_name="res_multistep",
    scheduler="simple",
    denoise=1.0,
    batch_size=1,
    filename_prefix="video/MiniMax_H3",
    duration_seconds=2.0,
    # The backend snaps the requested duration to H3's 17k+5 frame grid. Keep
    # the web control human-friendly so values such as exactly 10 seconds are
    # valid HTML number inputs instead of exposing the model's frame quantum.
    duration_step_seconds=0.5,
    negative_prompt="",
    length_mode="minimax_h3",
    frame_quantum=17,
    supports_image=True,
    requires_image=True,
    image_inputs=(
        ImageInputSpec(
            key="input_image",
            label="First frame",
            required=True,
        ),
    ),
    enable_custom_nodes=False,
    prompt_label="Prompt (include audio direction)",
    uses_ltx_runner=False,
    submit_prompt_input_name="positive_prompt",
    models_asset_kind="model",
    comfy_low_vram=True,
    comfy_disable_smart_memory=True,
    comfy_reserve_vram_gb=2.0,
)


PROFILES: dict[str, WorkflowProfile] = {
    WAN_PROFILE.key: WAN_PROFILE,
    LTX_PROFILE.key: LTX_PROFILE,
    LTX_I2V_PROFILE.key: LTX_I2V_PROFILE,
    MINIMAX_H3_PROFILE.key: MINIMAX_H3_PROFILE,
}


def get_profile(key: str) -> WorkflowProfile:
    try:
        return PROFILES[key]
    except KeyError as exc:
        choices = ", ".join(sorted(PROFILES))
        raise KeyError(f"Unknown workflow profile {key!r}. Expected one of: {choices}.") from exc


def all_image_input_keys(profiles: Iterable[WorkflowProfile] | None = None) -> tuple[str, ...]:
    selected_profiles = tuple(PROFILES.values()) if profiles is None else tuple(profiles)
    keys = {
        spec.key
        for profile in selected_profiles
        for spec in profile.image_inputs
    }
    return tuple(sorted(keys))


def duration_seconds_to_length(profile: WorkflowProfile, seconds: float, *, fps: float | None = None) -> int:
    if seconds <= 0:
        raise ValueError("Duration must be greater than 0 seconds.")
    effective_fps = profile.fps if fps is None else fps
    if profile.length_mode == "seconds_plus_one":
        return max(2, int(round(seconds * effective_fps)) + 1)
    if profile.length_mode == "minimax_h3":
        target_frames = max(5, round(seconds * effective_fps))
        return int(target_frames + (5 - (target_frames % profile.frame_quantum)) % profile.frame_quantum)

    target_frames = max(1, round(seconds * effective_fps))
    remainder = (target_frames - 1) % profile.frame_quantum
    if remainder:
        target_frames += profile.frame_quantum - remainder
    return int(target_frames)
