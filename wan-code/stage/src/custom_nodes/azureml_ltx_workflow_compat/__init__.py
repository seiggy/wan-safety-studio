from __future__ import annotations

from fractions import Fraction
import os
from typing import Any

import comfy.utils
import folder_paths
import torch
from comfy.cli_args import args as comfy_args
from comfy_api.latest import InputImpl, Types
from comfy_extras.nodes_post_processing import scale_dimensions


MAX_RESOLUTION = 16384


def _placeholder_mask() -> torch.Tensor:
    return torch.zeros((64, 64), dtype=torch.float32, device=torch.device("cpu"))


def _normalize_size(value: int, *, minimum: int = 1) -> int:
    return max(minimum, int(value))


def _apply_divisible(value: int, divisor: int) -> int:
    if divisor <= 1:
        return _normalize_size(value)
    adjusted = value - (value % divisor)
    return _normalize_size(adjusted or divisor)


def _resolve_target_size(image: torch.Tensor, width: int, height: int, divisible_by: int) -> tuple[int, int]:
    source_height = int(image.shape[1])
    source_width = int(image.shape[2])
    if width <= 0 and height <= 0:
        width = source_width
        height = source_height
    elif width <= 0:
        width = round(source_width * height / source_height)
    elif height <= 0:
        height = round(source_height * width / source_width)
    return _apply_divisible(width, divisible_by), _apply_divisible(height, divisible_by)


def _fit_inside(source_width: int, source_height: int, target_width: int, target_height: int) -> tuple[int, int]:
    if target_width <= 0 or target_height <= 0:
        return source_width, source_height
    ratio = min(target_width / source_width, target_height / source_height)
    return _normalize_size(round(source_width * ratio)), _normalize_size(round(source_height * ratio))


def _total_pixels_size(source_width: int, source_height: int, width: int, height: int) -> tuple[int, int]:
    target_pixels = max(1, int(width) * int(height))
    aspect_ratio = source_width / source_height
    target_height = max(1, round((target_pixels / aspect_ratio) ** 0.5))
    target_width = max(1, round(target_height * aspect_ratio))
    return target_width, target_height


def _crop_tensor_to_aspect(
    tensor: torch.Tensor,
    *,
    target_width: int,
    target_height: int,
    crop_position: str,
) -> torch.Tensor:
    if tensor is None:
        return tensor

    source_height = int(tensor.shape[1])
    source_width = int(tensor.shape[2])
    source_aspect = source_width / source_height
    target_aspect = target_width / target_height

    if abs(source_aspect - target_aspect) < 1e-6:
        return tensor

    if source_aspect > target_aspect:
        crop_width = _normalize_size(round(source_height * target_aspect))
        crop_height = source_height
        x_lookup = {
            "left": 0,
            "right": source_width - crop_width,
        }
        x = x_lookup.get(crop_position, (source_width - crop_width) // 2)
        y = 0
    else:
        crop_width = source_width
        crop_height = _normalize_size(round(source_width / target_aspect))
        y_lookup = {
            "top": 0,
            "bottom": source_height - crop_height,
        }
        x = 0
        y = y_lookup.get(crop_position, (source_height - crop_height) // 2)

    if tensor.ndim == 4:
        return tensor[:, y : y + crop_height, x : x + crop_width, :]
    return tensor[:, y : y + crop_height, x : x + crop_width]


def _resize_mask(mask: torch.Tensor | None, width: int, height: int, upscale_method: str) -> torch.Tensor | None:
    if mask is None:
        return None
    method = "bilinear" if upscale_method == "lanczos" else upscale_method
    return scale_dimensions(mask, width, height, method, "disabled")


def _pingpong(images: torch.Tensor) -> torch.Tensor:
    if images.shape[0] <= 1:
        return images
    return torch.cat((images, torch.flip(images[1:-1], dims=(0,))), dim=0)


class ImageResizeKJv2:
    upscale_methods = ["nearest-exact", "bilinear", "area", "bicubic", "lanczos"]
    keep_proportion_modes = [
        "stretch",
        "resize",
        "pad",
        "pad_edge",
        "pad_edge_pixel",
        "crop",
        "pillarbox_blur",
        "total_pixels",
    ]
    crop_positions = ["center", "top", "bottom", "left", "right"]

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "width": ("INT", {"default": 512, "min": 0, "max": MAX_RESOLUTION, "step": 1}),
                "height": ("INT", {"default": 512, "min": 0, "max": MAX_RESOLUTION, "step": 1}),
                "upscale_method": (cls.upscale_methods,),
                "keep_proportion": (cls.keep_proportion_modes,),
                "pad_color": ("STRING", {"default": "0, 0, 0"}),
                "crop_position": (cls.crop_positions,),
                "divisible_by": ("INT", {"default": 0, "min": 0, "max": 512, "step": 1}),
            },
            "optional": {
                "mask": ("MASK",),
                "device": (["cpu", "gpu"],),
            },
        }

    RETURN_TYPES = ("IMAGE", "INT", "INT", "MASK")
    RETURN_NAMES = ("IMAGE", "width", "height", "mask")
    FUNCTION = "resize"
    CATEGORY = "image/transform"

    def resize(
        self,
        image: torch.Tensor,
        width: int,
        height: int,
        upscale_method: str,
        keep_proportion: str,
        pad_color: str,
        crop_position: str,
        divisible_by: int,
        mask: torch.Tensor | None = None,
        device: str = "cpu",
    ):
        del pad_color, device

        if mask is not None and tuple(mask.shape[-2:]) == (64, 64) and tuple(image.shape[1:3]) != (64, 64):
            mask = None

        target_width, target_height = _resolve_target_size(image, width, height, divisible_by)
        source_height = int(image.shape[1])
        source_width = int(image.shape[2])

        if keep_proportion == "crop":
            working_image = _crop_tensor_to_aspect(
                image,
                target_width=target_width,
                target_height=target_height,
                crop_position=crop_position,
            )
            working_mask = _crop_tensor_to_aspect(
                mask,
                target_width=target_width,
                target_height=target_height,
                crop_position=crop_position,
            )
            resized_image = scale_dimensions(working_image, target_width, target_height, upscale_method, "disabled")
            resized_mask = _resize_mask(working_mask, target_width, target_height, upscale_method)
        else:
            resize_width = target_width
            resize_height = target_height
            if keep_proportion in {"resize", "pad", "pad_edge", "pad_edge_pixel", "pillarbox_blur"}:
                resize_width, resize_height = _fit_inside(source_width, source_height, target_width, target_height)
            elif keep_proportion == "total_pixels":
                resize_width, resize_height = _total_pixels_size(source_width, source_height, target_width, target_height)

            resize_width = _apply_divisible(resize_width, divisible_by)
            resize_height = _apply_divisible(resize_height, divisible_by)
            resized_image = scale_dimensions(image, resize_width, resize_height, upscale_method, "disabled")
            resized_mask = _resize_mask(mask, resize_width, resize_height, upscale_method)

        return (
            resized_image,
            int(resized_image.shape[2]),
            int(resized_image.shape[1]),
            resized_mask if resized_mask is not None else _placeholder_mask(),
        )


class LTXVFirstLastFrameControl_TTP:
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "vae": ("VAE",),
                "latent": ("LATENT",),
                "first_strength": ("FLOAT", {"default": 1.0, "min": 0.0, "max": 1.0, "step": 0.05}),
                "last_strength": ("FLOAT", {"default": 1.0, "min": 0.0, "max": 1.0, "step": 0.05}),
            },
            "optional": {
                "first_image": ("IMAGE",),
                "last_image": ("IMAGE",),
                "middle_frames": ("*",),
            },
        }

    RETURN_TYPES = ("LATENT",)
    RETURN_NAMES = ("latent",)
    FUNCTION = "execute"
    CATEGORY = "conditioning/video_models"

    def execute(
        self,
        vae,
        latent,
        first_strength: float = 1.0,
        last_strength: float = 1.0,
        first_image: torch.Tensor | None = None,
        last_image: torch.Tensor | None = None,
        middle_frames: dict[str, list[dict[str, Any]]] | None = None,
    ):
        frames_list = []
        if isinstance(middle_frames, dict):
            frames_list = list(middle_frames.get("frames", []))
        if first_image is None and last_image is None and not frames_list:
            return (latent,)

        samples = latent["samples"].clone()
        batch, _, latent_frames, latent_height, latent_width = samples.shape
        _, height_scale_factor, width_scale_factor = vae.downscale_index_formula
        target_width = latent_width * width_scale_factor
        target_height = latent_height * height_scale_factor

        existing_mask = latent.get("noise_mask")
        if isinstance(existing_mask, torch.Tensor):
            noise_mask = existing_mask.clone()
        else:
            noise_mask = torch.ones(
                (batch, 1, latent_frames, 1, 1),
                dtype=torch.float32,
                device=samples.device,
            )

        if first_image is not None and first_strength > 0.0:
            first_latent = self._encode_image(vae, first_image, target_height, target_width)
            first_frames = min(int(first_latent.shape[2]), latent_frames)
            samples[:, :, :first_frames] = first_latent[:, :, :first_frames]
            noise_mask[:, :, :first_frames] = torch.minimum(
                noise_mask[:, :, :first_frames],
                torch.full_like(noise_mask[:, :, :first_frames], 1.0 - first_strength),
            )

        if last_image is not None and last_strength > 0.0:
            last_latent = self._encode_image(vae, last_image, target_height, target_width)
            last_frames = min(int(last_latent.shape[2]), latent_frames)
            last_start = latent_frames - last_frames
            samples[:, :, last_start:] = last_latent[:, :, :last_frames]
            noise_mask[:, :, last_start:] = torch.minimum(
                noise_mask[:, :, last_start:],
                torch.full_like(noise_mask[:, :, last_start:], 1.0 - last_strength),
            )

        for frame_data in frames_list:
            image = frame_data.get("image")
            position = float(frame_data.get("position", 0.5))
            strength = float(frame_data.get("strength", 1.0))
            if image is None or strength <= 0.0:
                continue
            middle_latent = self._encode_image(vae, image, target_height, target_width)
            middle_frames_count = min(int(middle_latent.shape[2]), latent_frames)
            frame_index = round(position * max(latent_frames - 1, 0))
            frame_index = max(0, min(frame_index, latent_frames - middle_frames_count))
            samples[:, :, frame_index : frame_index + middle_frames_count] = middle_latent[:, :, :middle_frames_count]
            noise_mask[:, :, frame_index : frame_index + middle_frames_count] = torch.minimum(
                noise_mask[:, :, frame_index : frame_index + middle_frames_count],
                torch.full_like(
                    noise_mask[:, :, frame_index : frame_index + middle_frames_count],
                    1.0 - strength,
                ),
            )

        return ({"samples": samples, "noise_mask": noise_mask},)

    @staticmethod
    def _encode_image(vae, image: torch.Tensor, target_height: int, target_width: int) -> torch.Tensor:
        if image.shape[1] != target_height or image.shape[2] != target_width:
            pixels = comfy.utils.common_upscale(
                image.movedim(-1, 1),
                target_width,
                target_height,
                "bilinear",
                "center",
            ).movedim(1, -1)
        else:
            pixels = image
        return vae.encode(pixels[:, :, :, :3])


class VHS_VideoCombine:
    supported_formats = {"auto", "mp4", "video/mp4", "video/h264-mp4"}

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "images": ("IMAGE",),
                "frame_rate": ("FLOAT", {"default": 24.0, "min": 1.0, "max": 120.0, "step": 1.0}),
                "loop_count": ("INT", {"default": 0, "min": 0, "max": 100, "step": 1}),
                "filename_prefix": ("STRING", {"default": "video/ComfyUI"}),
                "format": (sorted(cls.supported_formats), {"default": "video/h264-mp4"}),
                "pingpong": ("BOOLEAN", {"default": False}),
                "save_output": ("BOOLEAN", {"default": True}),
            },
            "optional": {
                "audio": ("AUDIO",),
                "meta_batch": ("VHS_BatchManager",),
                "vae": ("VAE",),
            },
            "hidden": {
                "prompt": "PROMPT",
                "extra_pnginfo": "EXTRA_PNGINFO",
            },
        }

    RETURN_TYPES = ("VHS_FILENAMES",)
    RETURN_NAMES = ("Filenames",)
    FUNCTION = "combine_video"
    OUTPUT_NODE = True
    CATEGORY = "video"

    def combine_video(
        self,
        images: torch.Tensor,
        frame_rate: float,
        loop_count: int,
        filename_prefix: str,
        format: str,
        pingpong: bool,
        save_output: bool,
        audio=None,
        meta_batch=None,
        vae=None,
        prompt=None,
        extra_pnginfo=None,
    ):
        del loop_count, meta_batch
        if vae is not None and isinstance(images, dict):
            raise ValueError("Latent video inputs are not supported by this VHS compatibility node.")
        if not isinstance(images, torch.Tensor) or images.shape[0] == 0:
            return ((save_output, []),)

        format_value = str(format).lower()
        if format_value not in self.supported_formats:
            raise ValueError(f"Unsupported video format {format!r}.")

        if pingpong:
            images = _pingpong(images)

        output_dir = folder_paths.get_output_directory() if save_output else folder_paths.get_temp_directory()
        full_output_folder, filename, counter, subfolder, _ = folder_paths.get_save_image_path(
            filename_prefix,
            output_dir,
            int(images.shape[2]),
            int(images.shape[1]),
        )
        file = f"{filename}_{counter:05}_.{Types.VideoContainer.get_extension('auto')}"
        file_path = os.path.join(full_output_folder, file)

        metadata = None
        if not comfy_args.disable_metadata:
            metadata = {}
            if isinstance(extra_pnginfo, dict):
                metadata.update(extra_pnginfo)
            if prompt is not None:
                metadata["prompt"] = prompt
            if not metadata:
                metadata = None

        video = InputImpl.VideoFromComponents(
            Types.VideoComponents(
                images=images,
                audio=audio,
                frame_rate=Fraction(round(frame_rate * 1000), 1000),
            )
        )
        video.save_to(file_path, format=Types.VideoContainer.AUTO, codec=Types.VideoCodec.AUTO, metadata=metadata)
        return ((save_output, [file_path]),)


NODE_CLASS_MAPPINGS = {
    "ImageResizeKJv2": ImageResizeKJv2,
    "LTXVFirstLastFrameControl_TTP": LTXVFirstLastFrameControl_TTP,
    "VHS_VideoCombine": VHS_VideoCombine,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "ImageResizeKJv2": "Resize Image v2 (AzureML Compat)",
    "LTXVFirstLastFrameControl_TTP": "LTX First/Last Frame Control (AzureML Compat)",
    "VHS_VideoCombine": "Video Combine (AzureML Compat)",
}
