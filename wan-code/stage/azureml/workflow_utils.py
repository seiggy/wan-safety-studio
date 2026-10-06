from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from pathlib import Path
import re
import shutil
from typing import Any

from PIL import Image


PROMPT_MODEL_INPUTS: dict[str, dict[str, tuple[str, ...]]] = {
    "UNETLoader": {
        "unet_name": ("diffusion_models", "unet"),
    },
    "CLIPLoader": {
        "clip_name": ("text_encoders", "clip"),
    },
    "VAELoader": {
        "vae_name": ("vae",),
    },
    "CheckpointLoaderSimple": {
        "ckpt_name": ("checkpoints",),
    },
    "LowVRAMCheckpointLoader": {
        "ckpt_name": ("checkpoints",),
    },
    "LTXVAudioVAELoader": {
        "ckpt_name": ("checkpoints",),
    },
    "LowVRAMAudioVAELoader": {
        "ckpt_name": ("checkpoints",),
    },
    "LoraLoaderModelOnly": {
        "lora_name": ("loras",),
    },
    "LTXAVTextEncoderLoader": {
        "text_encoder": ("text_encoders", "clip"),
        "ckpt_name": ("checkpoints",),
    },
    "LatentUpscaleModelLoader": {
        "model_name": ("latent_upscale_models",),
    },
    "LowVRAMLatentUpscaleModelLoader": {
        "model_name": ("latent_upscale_models",),
    },
}


EDITOR_MODEL_INPUTS: dict[str, list[tuple[str, tuple[str, ...], int]]] = {
    "UNETLoader": [("unet_name", ("diffusion_models", "unet"), 0)],
    "CLIPLoader": [("clip_name", ("text_encoders", "clip"), 0)],
    "VAELoader": [("vae_name", ("vae",), 0)],
    "CheckpointLoaderSimple": [("ckpt_name", ("checkpoints",), 0)],
    "LowVRAMCheckpointLoader": [("ckpt_name", ("checkpoints",), 0)],
    "LTXVAudioVAELoader": [("ckpt_name", ("checkpoints",), 0)],
    "LowVRAMAudioVAELoader": [("ckpt_name", ("checkpoints",), 0)],
    "LoraLoaderModelOnly": [("lora_name", ("loras",), 0)],
    "LTXAVTextEncoderLoader": [
        ("text_encoder", ("text_encoders", "clip"), 0),
        ("ckpt_name", ("checkpoints",), 1),
    ],
    "LatentUpscaleModelLoader": [("model_name", ("latent_upscale_models",), 0)],
    "LowVRAMLatentUpscaleModelLoader": [("model_name", ("latent_upscale_models",), 0)],
}


TEXT_VALUE_INPUT_BY_CLASS = {
    "CLIPTextEncode": "text",
    "PrimitiveString": "value",
    "PrimitiveStringMultiline": "value",
    "GemmaAPITextEncode": "prompt",
}


UPSTREAM_VALUE_INPUT_BY_CLASS = {
    "PrimitiveString": "value",
    "PrimitiveStringMultiline": "value",
    "PrimitiveInt": "value",
    "PrimitiveFloat": "value",
    "PrimitiveBoolean": "value",
    "LTXFloatToInt": "a",
}


OPTIONAL_I2V_BYPASS_NODES = {
    "LTXVImgToVideoConditionOnly",
    "LTXVImgToVideoInplace",
}

EDITOR_FALLBACK_CLASS_TYPES = {
    "LoadImage",
    "LTXVPreprocess",
    "ResizeImageMaskNode",
}

BLUEPRINT_METADATA_KEY = "_azureml_blueprint"


@dataclass(frozen=True)
class ModelReference:
    folder_name: str
    filename: str


def load_workflow_payload(path: Path) -> dict[str, Any]:
    payload = json_loads(path.read_text(encoding="utf-8"))
    return _normalize_blueprint_workflow(payload)


def json_loads(text: str) -> dict[str, Any]:
    import json

    return json.loads(text)


def _blueprint_subgraph(payload: dict[str, Any]) -> dict[str, Any] | None:
    if not isinstance(payload, dict):
        return None
    top_nodes = payload.get("nodes")
    definitions = payload.get("definitions")
    if not isinstance(top_nodes, list) or len(top_nodes) != 1 or not isinstance(definitions, dict):
        return None
    top_node = top_nodes[0]
    if not isinstance(top_node, dict):
        return None
    subgraphs = definitions.get("subgraphs")
    if not isinstance(subgraphs, list) or len(subgraphs) != 1 or not isinstance(subgraphs[0], dict):
        return None
    subgraph = subgraphs[0]
    if top_node.get("type") != subgraph.get("id"):
        return None
    return subgraph


def _normalize_blueprint_workflow(payload: dict[str, Any]) -> dict[str, Any]:
    subgraph = _blueprint_subgraph(payload)
    if subgraph is None:
        return payload

    normalized = deepcopy(subgraph)
    _materialize_blueprint_image_inputs(payload, normalized)
    blueprint_metadata = _blueprint_metadata(payload)
    if blueprint_metadata:
        normalized[BLUEPRINT_METADATA_KEY] = blueprint_metadata
    blueprint_output_sources: dict[int, tuple[str, int]] = {}
    for link in normalized.get("links", []):
        if not isinstance(link, dict):
            continue
        try:
            link_id = int(link.get("id"))
            origin_id = str(link.get("origin_id"))
            origin_slot = int(link.get("origin_slot"))
        except (TypeError, ValueError):
            continue
        blueprint_output_sources[link_id] = (origin_id, origin_slot)

    for output_spec in normalized.get("outputs", []) or []:
        if not isinstance(output_spec, dict):
            continue
        link_ids = output_spec.get("linkIds")
        if not isinstance(link_ids, list):
            continue
        for link_id in link_ids:
            if not isinstance(link_id, int):
                continue
            source = blueprint_output_sources.get(link_id)
            if source is None:
                continue
            output_spec["source_node_id"] = source[0]
            output_spec["source_output_index"] = source[1]
            break

    removed_link_ids: set[int] = set()
    normalized_links: list[dict[str, Any]] = []
    for link in normalized.get("links", []):
        if not isinstance(link, dict):
            continue
        try:
            link_id = int(link.get("id"))
        except (TypeError, ValueError):
            continue
        origin_id = link.get("origin_id")
        target_id = link.get("target_id")
        if (
            isinstance(origin_id, int)
            and origin_id < 0
        ) or (
            isinstance(target_id, int)
            and target_id < 0
        ):
            removed_link_ids.add(link_id)
            continue
        normalized_links.append(link)
    normalized["links"] = normalized_links

    for node in normalized.get("nodes", []):
        if not isinstance(node, dict):
            continue
        for input_spec in node.get("inputs", []) or []:
            if not isinstance(input_spec, dict):
                continue
            try:
                link_id = int(input_spec.get("link"))
            except (TypeError, ValueError):
                continue
            if link_id in removed_link_ids:
                input_spec["link"] = None
        for output_spec in node.get("outputs", []) or []:
            if not isinstance(output_spec, dict):
                continue
            links = output_spec.get("links")
            if isinstance(links, list):
                output_spec["links"] = [
                    link_id for link_id in links if isinstance(link_id, int) and link_id not in removed_link_ids
                ]
    return normalized


def _blueprint_top_node(payload: dict[str, Any]) -> dict[str, Any] | None:
    top_nodes = payload.get("nodes")
    if not isinstance(top_nodes, list) or len(top_nodes) != 1 or not isinstance(top_nodes[0], dict):
        return None
    return top_nodes[0]


def _placeholder_image_filename(label: str | None, index: int) -> str:
    base = label or f"input_{index + 1}"
    slug = re.sub(r"[^A-Za-z0-9._-]+", "_", base).strip("._-").lower()
    if not slug:
        slug = f"input_{index + 1}"
    return f"{slug}.png"


def _materialize_blueprint_image_inputs(payload: dict[str, Any], normalized: dict[str, Any]) -> None:
    top_node = _blueprint_top_node(payload)
    if top_node is None:
        return

    subgraph_inputs = normalized.get("inputs")
    if not isinstance(subgraph_inputs, list):
        return

    existing_nodes = normalized.get("nodes")
    if not isinstance(existing_nodes, list):
        return

    max_node_id = 0
    for node in existing_nodes:
        if not isinstance(node, dict):
            continue
        try:
            max_node_id = max(max_node_id, int(node.get("id")))
        except (TypeError, ValueError):
            continue

    slot_to_image_node_id: dict[int, int] = {}
    for slot, input_spec in enumerate(subgraph_inputs):
        if not isinstance(input_spec, dict):
            continue
        input_type = str(input_spec.get("type") or "")
        if "IMAGE" not in input_type:
            continue
        max_node_id += 1
        image_node_id = max_node_id
        slot_to_image_node_id[slot] = image_node_id
        top_inputs = top_node.get("inputs") or []
        top_input = top_inputs[slot] if slot < len(top_inputs) and isinstance(top_inputs[slot], dict) else {}
        label = input_spec.get("label") or top_input.get("label") or input_spec.get("name") or top_input.get("name")
        existing_nodes.append(
            {
                "id": image_node_id,
                "type": "LoadImage",
                "widgets_values": [_placeholder_image_filename(str(label) if label else None, slot), "image"],
                "properties": {
                    "Node name for S&R": "LoadImage",
                },
            }
        )

    if not slot_to_image_node_id:
        return

    for link in normalized.get("links", []):
        if not isinstance(link, dict):
            continue
        origin_id = link.get("origin_id")
        try:
            origin_slot = int(link.get("origin_slot"))
        except (TypeError, ValueError):
            continue
        if not isinstance(origin_id, int) or origin_id >= 0:
            continue
        image_node_id = slot_to_image_node_id.get(origin_slot)
        if image_node_id is None:
            continue
        link["origin_id"] = image_node_id
        link["origin_slot"] = 0


def _blueprint_metadata(payload: dict[str, Any]) -> dict[str, Any]:
    top_node = _blueprint_top_node(payload)
    if top_node is None:
        return {}

    top_inputs = top_node.get("inputs")
    if not isinstance(top_inputs, list):
        return {}

    proxy_widgets = top_node.get("properties", {}).get("proxyWidgets")
    if not isinstance(proxy_widgets, list):
        return {}

    widget_inputs = [input_spec for input_spec in top_inputs if isinstance(input_spec, dict) and "widget" in input_spec]
    metadata: dict[str, dict[str, Any]] = {}
    for input_spec, proxy_widget in zip(widget_inputs, proxy_widgets):
        if not isinstance(proxy_widget, list) or len(proxy_widget) != 2:
            continue
        target_node_id, widget_name = proxy_widget
        if not isinstance(widget_name, str) or not widget_name:
            continue
        aliases = {
            str(candidate).strip().lower()
            for candidate in (
                input_spec.get("label"),
                input_spec.get("name"),
                widget_name,
            )
            if isinstance(candidate, str) and candidate.strip()
        }
        entry = {
            "target_node_id": str(target_node_id),
            "widget_name": widget_name,
            "type": input_spec.get("type"),
            "label": input_spec.get("label"),
            "name": input_spec.get("name"),
        }
        for alias in aliases:
            metadata[alias] = entry
    return metadata


def workflow_is_api_prompt(payload: dict[str, Any]) -> bool:
    if not isinstance(payload, dict) or not payload:
        return False
    return all(isinstance(node, dict) and "class_type" in node for node in payload.values())


def workflow_is_editor_graph(payload: dict[str, Any]) -> bool:
    return isinstance(payload, dict) and isinstance(payload.get("nodes"), list)


def is_link(value: object) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and isinstance(value[0], str)
        and isinstance(value[1], int)
    )


def _editor_node_class_type(node: dict[str, Any]) -> str | None:
    raw_type = node.get("type")
    if isinstance(raw_type, str) and raw_type:
        return raw_type
    properties = node.get("properties") or {}
    fallback = properties.get("Node name for S&R")
    if isinstance(fallback, str) and fallback:
        return fallback
    return None


def _ordered_input_names(node_info: dict[str, Any]) -> list[str]:
    input_order = node_info.get("input_order")
    if isinstance(input_order, dict):
        ordered: list[str] = []
        for group_name in ("required", "optional"):
            group = input_order.get(group_name)
            if isinstance(group, list):
                ordered.extend(name for name in group if isinstance(name, str))
        if ordered:
            return ordered

    inputs = node_info.get("input")
    if not isinstance(inputs, dict):
        return []

    ordered = []
    for group_name in ("required", "optional"):
        group = inputs.get(group_name)
        if isinstance(group, dict):
            ordered.extend(name for name in group if isinstance(name, str))
    return ordered


def _editor_widget_value(
    raw_widget_values: object,
    input_spec: dict[str, Any],
    widget_cursor: int,
) -> tuple[bool, object | None, int]:
    if "widget" not in input_spec:
        return False, None, widget_cursor

    if isinstance(raw_widget_values, dict):
        widget = input_spec.get("widget")
        widget_name = None
        if isinstance(widget, dict):
            candidate = widget.get("name")
            if isinstance(candidate, str) and candidate:
                widget_name = candidate
        input_name = input_spec.get("name")
        for candidate_name in (widget_name, input_name):
            if isinstance(candidate_name, str) and candidate_name in raw_widget_values:
                return True, raw_widget_values.get(candidate_name), widget_cursor
        return False, None, widget_cursor

    if isinstance(raw_widget_values, list) and widget_cursor < len(raw_widget_values):
        return True, raw_widget_values[widget_cursor], widget_cursor + 1

    return False, None, widget_cursor


def _editor_literal_source_value(node: dict[str, Any]) -> object:
    class_type = _editor_node_class_type(node)
    raw_widget_values = node.get("widgets_values")

    candidate: object | None = None
    if isinstance(raw_widget_values, list) and raw_widget_values:
        candidate = raw_widget_values[0]
    elif isinstance(raw_widget_values, dict):
        for key in ("value", "text", "string", "boolean"):
            if key in raw_widget_values:
                candidate = raw_widget_values.get(key)
                break

    if class_type == "FloatConstant" and isinstance(candidate, int | float):
        return float(candidate)
    if class_type == "INTConstant" and isinstance(candidate, int | float):
        return int(candidate)
    if class_type == "PrimitiveNode" and isinstance(candidate, str | int | float | bool):
        return candidate
    return _MISSING


_MISSING = object()


def _editor_link_value(
    *,
    nodes_by_id: dict[str, dict[str, Any]],
    link_sources: dict[int, tuple[str, int]],
    link_id: object,
) -> object:
    try:
        source_node_id, source_output_index = link_sources[int(link_id)]
    except (KeyError, TypeError, ValueError):
        return _MISSING

    source_node = nodes_by_id.get(source_node_id)
    if isinstance(source_node, dict):
        literal_value = _editor_literal_source_value(source_node)
        if literal_value is not _MISSING:
            return literal_value

    return [source_node_id, source_output_index]


def _editor_prompt_inputs_from_specs(
    node: dict[str, Any],
    *,
    nodes_by_id: dict[str, dict[str, Any]],
    link_sources: dict[int, tuple[str, int]],
    node_info: dict[str, Any] | None = None,
) -> dict[str, Any]:
    inputs: dict[str, Any] = {}
    raw_widget_values = node.get("widgets_values")
    widget_cursor = 0
    ordered_input_names = _ordered_input_names(node_info or {})
    editor_input_names: list[str] = []

    for input_spec in node.get("inputs", []) or []:
        if not isinstance(input_spec, dict):
            continue
        input_name = input_spec.get("name")
        if not isinstance(input_name, str) or not input_name:
            continue
        editor_input_names.append(input_name)

        link_id = input_spec.get("link")
        if link_id is not None:
            link_value = _editor_link_value(
                nodes_by_id=nodes_by_id,
                link_sources=link_sources,
                link_id=link_id,
            )
            if link_value is not _MISSING:
                inputs[input_name] = link_value

        has_value, widget_value, widget_cursor = _editor_widget_value(
            raw_widget_values,
            input_spec,
            widget_cursor,
        )
        if has_value and input_name not in inputs:
            inputs[input_name] = widget_value

    hidden_input_names = [
        input_name
        for input_name in ordered_input_names
        if input_name not in editor_input_names
    ]
    if isinstance(raw_widget_values, list) and hidden_input_names:
        for input_name in hidden_input_names:
            if widget_cursor >= len(raw_widget_values):
                break
            if input_name in inputs:
                continue
            inputs[input_name] = raw_widget_values[widget_cursor]
            widget_cursor += 1
    elif isinstance(raw_widget_values, dict) and hidden_input_names:
        for input_name in hidden_input_names:
            if input_name in inputs or input_name not in raw_widget_values:
                continue
            inputs[input_name] = raw_widget_values[input_name]

    return inputs


def _collect_editor_reachable_node_ids(
    workflow: dict[str, Any],
    object_info: dict[str, Any],
) -> set[str]:
    nodes_by_id: dict[str, dict[str, Any]] = {}
    output_nodes: list[str] = []
    for node in workflow.get("nodes", []):
        if not isinstance(node, dict):
            continue
        class_type = _editor_node_class_type(node)
        if not class_type:
            continue
        if class_type not in object_info and class_type not in EDITOR_FALLBACK_CLASS_TYPES:
            continue
        node_id = str(node.get("id"))
        nodes_by_id[node_id] = node
        if class_type in object_info and bool(object_info[class_type].get("output_node")):
            output_nodes.append(node_id)

    if not output_nodes:
        return set(nodes_by_id)

    link_sources = _editor_link_sources(workflow)

    reachable: set[str] = set()
    stack = list(output_nodes)
    while stack:
        node_id = stack.pop()
        if node_id in reachable:
            continue
        reachable.add(node_id)
        node = nodes_by_id.get(node_id)
        if node is None:
            continue
        for input_spec in node.get("inputs", []) or []:
            if not isinstance(input_spec, dict):
                continue
            link_id = input_spec.get("link")
            if link_id is None:
                continue
            try:
                source_node_id, _ = link_sources[int(link_id)]
            except (KeyError, TypeError, ValueError):
                continue
            stack.append(source_node_id)
    return reachable


def _editor_linked_inputs(
    node: dict[str, Any],
    *,
    nodes_by_id: dict[str, dict[str, Any]],
    link_sources: dict[int, tuple[str, int]],
) -> dict[str, Any]:
    linked_inputs: dict[str, Any] = {}
    for input_spec in node.get("inputs", []) or []:
        if not isinstance(input_spec, dict):
            continue
        input_name = input_spec.get("name")
        link_id = input_spec.get("link")
        if not isinstance(input_name, str) or link_id is None:
            continue
        link_value = _editor_link_value(
            nodes_by_id=nodes_by_id,
            link_sources=link_sources,
            link_id=link_id,
        )
        if link_value is _MISSING:
            continue
        linked_inputs[input_name] = link_value
    return linked_inputs


def _editor_link_sources(workflow: dict[str, Any]) -> dict[int, tuple[str, int]]:
    raw_link_sources: dict[int, tuple[str, int]] = {}
    nodes_by_id: dict[str, dict[str, Any]] = {}
    for node in workflow.get("nodes", []):
        if not isinstance(node, dict):
            continue
        nodes_by_id[str(node.get("id"))] = node

    for link in workflow.get("links", []):
        if isinstance(link, list) and len(link) >= 4:
            try:
                raw_link_sources[int(link[0])] = (str(link[1]), int(link[2]))
            except (TypeError, ValueError):
                continue
            continue
        if not isinstance(link, dict):
            continue
        try:
            raw_link_sources[int(link.get("id"))] = (str(link.get("origin_id")), int(link.get("origin_slot")))
        except (TypeError, ValueError):
            continue

    def resolve_source(node_id: str, output_index: int, visited: set[str]) -> tuple[str, int]:
        if node_id in visited:
            return node_id, output_index
        node = nodes_by_id.get(node_id)
        if node is None or _editor_node_class_type(node) != "Reroute":
            return node_id, output_index
        for input_spec in node.get("inputs", []) or []:
            if not isinstance(input_spec, dict):
                continue
            link_id = input_spec.get("link")
            if link_id is None:
                continue
            try:
                upstream_node_id, upstream_output_index = raw_link_sources[int(link_id)]
            except (KeyError, TypeError, ValueError):
                continue
            return resolve_source(
                upstream_node_id,
                upstream_output_index,
                visited | {node_id},
            )
        return node_id, output_index

    return {
        link_id: resolve_source(node_id, output_index, set())
        for link_id, (node_id, output_index) in raw_link_sources.items()
    }


def _fallback_editor_prompt_node(
    node: dict[str, Any],
    class_type: str,
    *,
    nodes_by_id: dict[str, dict[str, Any]],
    link_sources: dict[int, tuple[str, int]],
) -> dict[str, Any] | None:
    raw_widget_values = node.get("widgets_values")
    if class_type == "LoadImage":
        image_name: str | None = None
        if isinstance(raw_widget_values, list) and raw_widget_values:
            first_value = raw_widget_values[0]
            if isinstance(first_value, str):
                image_name = first_value
        elif isinstance(raw_widget_values, dict):
            candidate = raw_widget_values.get("image")
            if isinstance(candidate, str):
                image_name = candidate
        return {
            "class_type": class_type,
            "inputs": {
                "image": image_name or "",
            },
        }
    if class_type == "LTXVPreprocess":
        img_compression = 35
        if isinstance(raw_widget_values, list) and raw_widget_values:
            first_value = raw_widget_values[0]
            if isinstance(first_value, int):
                img_compression = first_value
        elif isinstance(raw_widget_values, dict):
            candidate = raw_widget_values.get("img_compression")
            if isinstance(candidate, int):
                img_compression = candidate
        inputs = _editor_linked_inputs(node, nodes_by_id=nodes_by_id, link_sources=link_sources)
        inputs["img_compression"] = img_compression
        return {
            "class_type": class_type,
            "inputs": inputs,
        }
    if class_type == "ResizeImageMaskNode":
        resize_type_value = "scale dimensions"
        scale_method = "area"
        extra_value: object | None = None
        extra_value_2: object | None = None
        if isinstance(raw_widget_values, list):
            if len(raw_widget_values) >= 1 and isinstance(raw_widget_values[0], str):
                resize_type_value = raw_widget_values[0]
            if len(raw_widget_values) >= 2:
                extra_value = raw_widget_values[1]
            if len(raw_widget_values) >= 3:
                if isinstance(raw_widget_values[2], str):
                    scale_method = raw_widget_values[2]
                else:
                    extra_value_2 = raw_widget_values[2]
            if len(raw_widget_values) >= 4 and isinstance(raw_widget_values[3], str):
                scale_method = raw_widget_values[3]
        elif isinstance(raw_widget_values, dict):
            candidate = raw_widget_values.get("resize_type")
            if isinstance(candidate, str):
                resize_type_value = candidate
            candidate = raw_widget_values.get("scale_method")
            if isinstance(candidate, str):
                scale_method = candidate

        inputs = _editor_linked_inputs(node, nodes_by_id=nodes_by_id, link_sources=link_sources)
        inputs["resize_type"] = resize_type_value
        inputs["scale_method"] = scale_method

        if resize_type_value == "scale dimensions":
            if isinstance(extra_value, int):
                inputs["resize_type.width"] = extra_value
            if isinstance(extra_value_2, int):
                inputs["resize_type.height"] = extra_value_2
            crop_value = None
            if isinstance(raw_widget_values, list):
                if len(raw_widget_values) >= 4 and isinstance(raw_widget_values[3], str):
                    crop_value = raw_widget_values[3]
                elif len(raw_widget_values) >= 3 and isinstance(raw_widget_values[2], str) and raw_widget_values[2] in {"disabled", "center"}:
                    crop_value = raw_widget_values[2]
            if crop_value is not None:
                inputs["resize_type.crop"] = crop_value
        elif resize_type_value == "scale by multiplier" and isinstance(extra_value, (int, float)):
            inputs["resize_type.multiplier"] = float(extra_value)
        elif resize_type_value == "scale longer dimension" and isinstance(extra_value, int):
            inputs["resize_type.longer_size"] = extra_value
        elif resize_type_value == "scale shorter dimension" and isinstance(extra_value, int):
            inputs["resize_type.shorter_size"] = extra_value
        elif resize_type_value == "scale width" and isinstance(extra_value, int):
            inputs["resize_type.width"] = extra_value
        elif resize_type_value == "scale height" and isinstance(extra_value, int):
            inputs["resize_type.height"] = extra_value
        elif resize_type_value == "scale total pixels" and isinstance(extra_value, (int, float)):
            inputs["resize_type.megapixels"] = float(extra_value)
        elif resize_type_value == "scale to multiple" and isinstance(extra_value, int):
            inputs["resize_type.multiple"] = extra_value
        elif resize_type_value == "match size":
            crop_value = None
            if isinstance(extra_value, str):
                crop_value = extra_value
            elif isinstance(extra_value_2, str):
                crop_value = extra_value_2
            if crop_value is not None:
                inputs["resize_type.crop"] = crop_value

        return {
            "class_type": class_type,
            "inputs": inputs,
        }
    return None


def _prompt_has_output_node(prompt: dict[str, Any], object_info: dict[str, Any]) -> bool:
    for node in prompt.values():
        if not isinstance(node, dict):
            continue
        class_type = str(node.get("class_type"))
        if bool(object_info.get(class_type, {}).get("output_node")):
            return True
    return False


def _next_prompt_node_id(prompt: dict[str, Any]) -> str:
    max_numeric_id = 0
    for node_id in prompt:
        try:
            max_numeric_id = max(max_numeric_id, int(str(node_id)))
        except (TypeError, ValueError):
            continue
    return str(max_numeric_id + 1)


def _append_blueprint_output_nodes(prompt: dict[str, Any], workflow: dict[str, Any], object_info: dict[str, Any]) -> None:
    if _prompt_has_output_node(prompt, object_info):
        return

    outputs = workflow.get("outputs")
    if not isinstance(outputs, list):
        return

    next_node_id = _next_prompt_node_id(prompt)
    for output_spec in outputs:
        if not isinstance(output_spec, dict):
            continue
        source_node_id = output_spec.get("source_node_id")
        source_output_index = output_spec.get("source_output_index")
        output_type = output_spec.get("type")
        if not isinstance(source_node_id, str) or not isinstance(source_output_index, int):
            continue
        if source_node_id not in prompt:
            continue

        if output_type == "VIDEO" and "SaveVideo" in object_info:
            prompt[next_node_id] = {
                "class_type": "SaveVideo",
                "inputs": {
                    "video": [source_node_id, source_output_index],
                    "filename_prefix": "video/ComfyUI",
                    "format": "auto",
                    "codec": "auto",
                },
            }
        elif output_type == "IMAGE" and "SaveImage" in object_info:
            prompt[next_node_id] = {
                "class_type": "SaveImage",
                "inputs": {
                    "images": [source_node_id, source_output_index],
                    "filename_prefix": "ComfyUI",
                },
            }
        else:
            continue
        next_node_id = _next_prompt_node_id(prompt)


def convert_editor_workflow_to_prompt(
    workflow: dict[str, Any],
    object_info: dict[str, Any],
    *,
    reachable_only: bool = True,
) -> dict[str, Any]:
    link_sources = _editor_link_sources(workflow)
    nodes_by_id = {
        str(node.get("id")): node
        for node in workflow.get("nodes", [])
        if isinstance(node, dict)
    }

    reachable = _collect_editor_reachable_node_ids(workflow, object_info) if reachable_only else None
    prompt: dict[str, Any] = {}

    for node in workflow.get("nodes", []):
        if not isinstance(node, dict):
            continue
        node_id = str(node.get("id"))
        if reachable is not None and node_id not in reachable:
            continue

        class_type = _editor_node_class_type(node)
        if not class_type:
            continue
        if class_type not in object_info:
            fallback_node = _fallback_editor_prompt_node(
                node,
                class_type,
                nodes_by_id=nodes_by_id,
                link_sources=link_sources,
            )
            if fallback_node is not None:
                prompt[node_id] = fallback_node
            continue

        node_info = object_info.get(class_type, {})
        inputs = _editor_prompt_inputs_from_specs(
            node,
            nodes_by_id=nodes_by_id,
            link_sources=link_sources,
            node_info=node_info,
        )
        if not inputs:
            raw_widget_values = node.get("widgets_values")
            widget_cursor = 0
            for input_name in _ordered_input_names(node_info):
                has_value = False
                widget_value: object | None = None
                if isinstance(raw_widget_values, dict):
                    has_value = input_name in raw_widget_values
                    widget_value = raw_widget_values.get(input_name)
                elif isinstance(raw_widget_values, list) and widget_cursor < len(raw_widget_values):
                    has_value = True
                    widget_value = raw_widget_values[widget_cursor]
                    widget_cursor += 1

                if has_value:
                    inputs[input_name] = widget_value

        prompt[node_id] = {
            "class_type": class_type,
            "inputs": inputs,
        }

    _append_blueprint_output_nodes(prompt, workflow, object_info)
    return prompt


def _editor_node_by_id(workflow: dict[str, Any], node_id: str) -> dict[str, Any] | None:
    for node in workflow.get("nodes", []):
        if not isinstance(node, dict):
            continue
        if str(node.get("id")) == node_id:
            return node
    return None


def _set_editor_node_widget_value(
    workflow: dict[str, Any],
    *,
    node_id: str,
    widget_name: str,
    value: object,
) -> bool:
    node = _editor_node_by_id(workflow, node_id)
    if node is None:
        return False

    raw_widget_values = node.get("widgets_values")
    if isinstance(raw_widget_values, dict):
        raw_widget_values[widget_name] = value
        return True

    widget_order: list[str] = []
    for input_spec in node.get("inputs", []) or []:
        if not isinstance(input_spec, dict) or "widget" not in input_spec:
            continue
        widget = input_spec.get("widget")
        widget_candidates = []
        if isinstance(widget, dict):
            widget_candidates.append(widget.get("name"))
        widget_candidates.append(input_spec.get("name"))
        for candidate in widget_candidates:
            if isinstance(candidate, str) and candidate:
                widget_order.append(candidate)
                break

    if not widget_order:
        node["widgets_values"] = {widget_name: value}
        return True

    index = 0
    for candidate_index, candidate_name in enumerate(widget_order):
        if candidate_name == widget_name:
            index = candidate_index
            break
    else:
        if len(widget_order) != 1:
            return False

    if not isinstance(raw_widget_values, list):
        raw_widget_values = []
        node["widgets_values"] = raw_widget_values
    while len(raw_widget_values) <= index:
        raw_widget_values.append(None)
    raw_widget_values[index] = value
    return True


def _coerce_blueprint_widget_value(input_type: object, value: object) -> object:
    type_name = str(input_type or "").upper()
    if type_name == "INT":
        numeric = float(value)
        return int(numeric + 0.5) if numeric >= 0 else int(numeric - 0.5)
    if type_name == "FLOAT":
        return float(value)
    if type_name == "BOOLEAN":
        return bool(value)
    return value


def _set_blueprint_widget_input(
    workflow: dict[str, Any],
    metadata: dict[str, Any],
    *,
    aliases: tuple[str, ...],
    value: object,
) -> bool:
    if value is None:
        return False
    for alias in aliases:
        entry = metadata.get(alias.strip().lower())
        if not isinstance(entry, dict):
            continue
        coerced = _coerce_blueprint_widget_value(entry.get("type"), value)
        return _set_editor_node_widget_value(
            workflow,
            node_id=str(entry["target_node_id"]),
            widget_name=str(entry["widget_name"]),
            value=coerced,
        )
    return False


def patch_editor_workflow_inputs(workflow: dict[str, Any], args: Any) -> None:
    metadata = workflow.get(BLUEPRINT_METADATA_KEY)
    if not isinstance(metadata, dict) or not metadata:
        return

    _set_blueprint_widget_input(workflow, metadata, aliases=("text",), value=getattr(args, "positive_prompt", None))
    _set_blueprint_widget_input(workflow, metadata, aliases=("width",), value=getattr(args, "width", None))
    _set_blueprint_widget_input(workflow, metadata, aliases=("height",), value=getattr(args, "height", None))
    _set_blueprint_widget_input(
        workflow,
        metadata,
        aliases=("duration",),
        value=getattr(args, "duration_seconds", None),
    )
    _set_blueprint_widget_input(workflow, metadata, aliases=("fps",), value=getattr(args, "fps", None))
    _set_blueprint_widget_input(
        workflow,
        metadata,
        aliases=("noise_seed",),
        value=getattr(args, "seed", None),
    )


def _clear_editor_input_link(node: dict[str, Any], input_name: str) -> bool:
    changed = False
    for input_spec in node.get("inputs", []) or []:
        if not isinstance(input_spec, dict) or input_spec.get("name") != input_name:
            continue
        if input_spec.get("link") is None:
            continue
        input_spec["link"] = None
        changed = True
    return changed


def prepare_editor_workflow(workflow: dict[str, Any], args: Any) -> None:
    patch_editor_workflow_inputs(workflow, args)

    if getattr(args, "middle_image_url", None) is not None:
        return

    for node in workflow.get("nodes", []) or []:
        if not isinstance(node, dict):
            continue
        if _editor_node_class_type(node) != "LTXVFirstLastFrameControl_TTP":
            continue
        _clear_editor_input_link(node, "middle_frames")


def _set_upstream_value(prompt: dict[str, Any], node_id: str, value: object) -> bool:
    node = prompt.get(node_id)
    if not isinstance(node, dict):
        return False
    class_type = node.get("class_type")
    input_name = UPSTREAM_VALUE_INPUT_BY_CLASS.get(str(class_type))
    if not input_name:
        return False

    inputs = node.setdefault("inputs", {})
    current_value = inputs.get(input_name)
    if is_link(current_value):
        return _set_upstream_value(prompt, current_value[0], value)
    inputs[input_name] = value
    return True


def _set_input_or_upstream_value(
    prompt: dict[str, Any],
    *,
    class_types: set[str],
    input_name: str,
    value: object,
) -> None:
    for node in prompt.values():
        if not isinstance(node, dict) or node.get("class_type") not in class_types:
            continue
        inputs = node.setdefault("inputs", {})
        if input_name not in inputs:
            continue
        current_value = inputs.get(input_name)
        if is_link(current_value):
            _set_upstream_value(prompt, current_value[0], value)
        else:
            inputs[input_name] = value


def _patch_upstream_prompt_text(
    prompt: dict[str, Any],
    node_id: str,
    *,
    kind: str,
    text: str,
    visited: set[str],
) -> None:
    if node_id in visited:
        return
    visited.add(node_id)

    node = prompt.get(node_id)
    if not isinstance(node, dict):
        return

    class_type = str(node.get("class_type"))
    text_input_name = TEXT_VALUE_INPUT_BY_CLASS.get(class_type)
    if text_input_name and text_input_name in node.setdefault("inputs", {}):
        node["inputs"][text_input_name] = text
        return

    inputs = node.setdefault("inputs", {})
    next_link = inputs.get(kind)
    if is_link(next_link):
        _patch_upstream_prompt_text(prompt, next_link[0], kind=kind, text=text, visited=visited)


def update_ksamplers(prompt: dict[str, Any], args: Any) -> None:
    for node in prompt.values():
        class_type = node.get("class_type")
        if class_type not in {"KSampler", "KSamplerAdvanced"}:
            continue
        inputs = node.setdefault("inputs", {})
        if args.seed is not None:
            if class_type == "KSamplerAdvanced":
                inputs["noise_seed"] = args.seed
            else:
                inputs["seed"] = args.seed
        if args.steps is not None:
            inputs["steps"] = args.steps
        if args.cfg is not None:
            inputs["cfg"] = args.cfg
        if args.sampler_name is not None:
            inputs["sampler_name"] = args.sampler_name
        if args.scheduler is not None:
            inputs["scheduler"] = args.scheduler
        if args.denoise is not None and "denoise" in inputs:
            inputs["denoise"] = args.denoise


def update_conditioning(prompt: dict[str, Any], args: Any) -> None:
    for node in prompt.values():
        inputs = node.get("inputs", {})
        if not isinstance(inputs, dict):
            continue
        positive = inputs.get("positive")
        negative = inputs.get("negative")
        if args.positive_prompt is not None and is_link(positive):
            _patch_upstream_prompt_text(
                prompt,
                positive[0],
                kind="positive",
                text=args.positive_prompt,
                visited=set(),
            )
        if args.negative_prompt is not None and is_link(negative):
            _patch_upstream_prompt_text(
                prompt,
                negative[0],
                kind="negative",
                text=args.negative_prompt,
                visited=set(),
            )


def update_ltx_video_defaults(prompt: dict[str, Any], args: Any) -> None:
    if args.width is not None:
        _set_input_or_upstream_value(
            prompt,
            class_types={"EmptyLTXVLatentVideo"},
            input_name="width",
            value=args.width,
        )
    if args.height is not None:
        _set_input_or_upstream_value(
            prompt,
            class_types={"EmptyLTXVLatentVideo"},
            input_name="height",
            value=args.height,
        )
    if args.length is not None:
        _set_input_or_upstream_value(
            prompt,
            class_types={"EmptyLTXVLatentVideo"},
            input_name="length",
            value=args.length,
        )
        _set_input_or_upstream_value(
            prompt,
            class_types={"LTXVEmptyLatentAudio"},
            input_name="frames_number",
            value=args.length,
        )
    if args.batch_size is not None:
        _set_input_or_upstream_value(
            prompt,
            class_types={"EmptyLTXVLatentVideo", "LTXVEmptyLatentAudio"},
            input_name="batch_size",
            value=args.batch_size,
        )
    if args.fps is not None:
        _set_input_or_upstream_value(
            prompt,
            class_types={"LTXVConditioning", "CreateVideo", "LTXVEmptyLatentAudio"},
            input_name="frame_rate",
            value=args.fps,
        )
        _set_input_or_upstream_value(
            prompt,
            class_types={"CreateVideo"},
            input_name="fps",
            value=args.fps,
        )


def update_wan_latent_nodes(prompt: dict[str, Any], args: Any) -> None:
    for node in prompt.values():
        if node.get("class_type") not in {"Wan22ImageToVideoLatent", "WanImageToVideo"}:
            continue
        inputs = node.setdefault("inputs", {})
        if args.width is not None:
            inputs["width"] = args.width
        if args.height is not None:
            inputs["height"] = args.height
        if args.length is not None:
            inputs["length"] = args.length
        if args.batch_size is not None:
            inputs["batch_size"] = args.batch_size


def update_minimax_h3_defaults(prompt: dict[str, Any], args: Any) -> None:
    for node in prompt.values():
        if node.get("class_type") != "MiniMaxH3ImageToVideo":
            continue
        inputs = node.setdefault("inputs", {})
        if args.positive_prompt is not None:
            inputs["prompt"] = args.positive_prompt
        if args.width is not None:
            inputs["width"] = args.width
        if args.height is not None:
            inputs["height"] = args.height
        if args.length is not None:
            inputs["length"] = args.length


def update_custom_sampling_nodes(prompt: dict[str, Any], args: Any) -> None:
    if args.seed is not None:
        for node in prompt.values():
            if node.get("class_type") == "RandomNoise":
                node.setdefault("inputs", {})["noise_seed"] = args.seed

    if args.steps is not None:
        for node in prompt.values():
            if node.get("class_type") in {"LTXVScheduler", "BasicScheduler"}:
                node.setdefault("inputs", {})["steps"] = args.steps

    if args.scheduler is not None:
        for node in prompt.values():
            if node.get("class_type") == "BasicScheduler":
                node.setdefault("inputs", {})["scheduler"] = args.scheduler

    if args.denoise is not None:
        for node in prompt.values():
            if node.get("class_type") == "BasicScheduler":
                node.setdefault("inputs", {})["denoise"] = args.denoise

    if args.cfg is not None:
        for node in prompt.values():
            if node.get("class_type") == "CFGGuider":
                node.setdefault("inputs", {})["cfg"] = args.cfg

    if args.sampler_name is not None:
        for node in prompt.values():
            if node.get("class_type") == "KSamplerSelect":
                node.setdefault("inputs", {})["sampler_name"] = args.sampler_name


def update_save_nodes(prompt: dict[str, Any], args: Any) -> None:
    for node in prompt.values():
        class_type = node.get("class_type")
        if class_type not in {"SaveWEBM", "SaveAnimatedWEBP", "SaveImage", "SaveVideo", "VHS_VideoCombine"}:
            continue
        inputs = node.setdefault("inputs", {})
        if args.filename_prefix is not None:
            inputs["filename_prefix"] = args.filename_prefix
        if args.fps is not None and "fps" in inputs:
            inputs["fps"] = args.fps
        if args.fps is not None and "frame_rate" in inputs:
            inputs["frame_rate"] = args.fps


def update_optional_image_bypass(prompt: dict[str, Any], *, has_image: bool) -> None:
    _set_input_or_upstream_value(
        prompt,
        class_types=OPTIONAL_I2V_BYPASS_NODES,
        input_name="bypass",
        value=not has_image,
    )


def patch_prompt(prompt: dict[str, Any], args: Any) -> dict[str, Any]:
    update_ksamplers(prompt, args)
    update_conditioning(prompt, args)
    update_wan_latent_nodes(prompt, args)
    update_minimax_h3_defaults(prompt, args)
    update_ltx_video_defaults(prompt, args)
    update_custom_sampling_nodes(prompt, args)
    update_save_nodes(prompt, args)
    has_any_image = (
        getattr(args, "input_image_url", None) is not None
        or getattr(args, "start_image_url", None) is not None
        or getattr(args, "end_image_url", None) is not None
    )
    update_optional_image_bypass(prompt, has_image=has_any_image)
    return prompt


def _load_image_filenames(prompt: dict[str, Any]) -> set[str]:
    filenames: set[str] = set()
    for node in prompt.values():
        if node.get("class_type") != "LoadImage":
            continue
        image_name = node.get("inputs", {}).get("image")
        if isinstance(image_name, str) and image_name:
            filenames.add(image_name)
    return filenames


def ensure_placeholder_input_images(prompt: dict[str, Any], input_dir: Path) -> None:
    filenames = _load_image_filenames(prompt)
    for filename in filenames:
        destination = input_dir / filename
        if destination.is_file():
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        Image.new("RGB", (8, 8), color=(0, 0, 0)).save(destination)


def download_file(url: str, destination: Path) -> None:
    import shutil
    import urllib.request

    destination.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output, length=16 * 1024 * 1024)


def _load_image_node_ids(prompt: dict[str, Any]) -> list[str]:
    return [
        str(node_id)
        for node_id, node in prompt.items()
        if isinstance(node, dict) and node.get("class_type") == "LoadImage"
    ]


def _sort_node_ids(node_ids: set[str] | list[str]) -> list[str]:
    return sorted(
        {str(node_id) for node_id in node_ids},
        key=lambda candidate: (0, int(candidate)) if str(candidate).isdigit() else (1, str(candidate)),
    )


def _resolve_scalar_value(prompt: dict[str, Any], value: object, visited: set[str] | None = None) -> object | None:
    if not is_link(value):
        return value

    node_id = str(value[0])
    if visited is None:
        visited = set()
    if node_id in visited:
        return None

    node = prompt.get(node_id)
    if not isinstance(node, dict):
        return None

    class_type = str(node.get("class_type"))
    input_name = UPSTREAM_VALUE_INPUT_BY_CLASS.get(class_type)
    if not input_name:
        return None
    return _resolve_scalar_value(
        prompt,
        node.setdefault("inputs", {}).get(input_name),
        visited | {node_id},
    )


def _collect_upstream_load_image_nodes(
    prompt: dict[str, Any],
    value: object,
    *,
    visited: set[str] | None = None,
) -> set[str]:
    if not is_link(value):
        return set()

    node_id = str(value[0])
    if visited is None:
        visited = set()
    if node_id in visited:
        return set()

    node = prompt.get(node_id)
    if not isinstance(node, dict):
        return set()
    if node.get("class_type") == "LoadImage":
        return {node_id}

    results: set[str] = set()
    for upstream_value in node.setdefault("inputs", {}).values():
        results.update(
            _collect_upstream_load_image_nodes(
                prompt,
                upstream_value,
                visited=visited | {node_id},
            )
        )
    return results


def _first_last_load_image_roles(prompt: dict[str, Any]) -> dict[str, set[str]]:
    roles = {
        "start": set(),
        "end": set(),
    }
    for node in prompt.values():
        if not isinstance(node, dict):
            continue
        class_type = str(node.get("class_type"))
        inputs = node.setdefault("inputs", {})
        if class_type == "LTXVFirstLastFrameControl_TTP":
            roles["start"].update(_collect_upstream_load_image_nodes(prompt, inputs.get("first_image")))
            roles["end"].update(_collect_upstream_load_image_nodes(prompt, inputs.get("last_image")))
            continue
        if class_type != "LTXVAddGuide":
            continue
        frame_idx = _resolve_scalar_value(prompt, inputs.get("frame_idx"))
        if isinstance(frame_idx, str):
            try:
                frame_idx = int(frame_idx)
            except ValueError:
                frame_idx = None
        if not isinstance(frame_idx, (int, float)):
            continue
        if int(frame_idx) == 0:
            roles["start"].update(_collect_upstream_load_image_nodes(prompt, inputs.get("image")))
        elif int(frame_idx) < 0:
            roles["end"].update(_collect_upstream_load_image_nodes(prompt, inputs.get("image")))
    return roles


def _target_filename_for_nodes(
    prompt: dict[str, Any],
    node_ids: list[str],
    *,
    explicit_filename: str | None,
    image_url: str,
) -> str:
    if explicit_filename is not None:
        return explicit_filename

    filenames = {
        prompt[node_id].get("inputs", {}).get("image")
        for node_id in node_ids
        if isinstance(prompt.get(node_id), dict)
        and isinstance(prompt[node_id].get("inputs", {}).get("image"), str)
        and prompt[node_id].get("inputs", {}).get("image")
    }
    if len(filenames) == 1:
        return next(iter(filenames))

    from urllib.parse import urlsplit

    return Path(urlsplit(image_url).path).name


def prepare_input_images(prompt: dict[str, Any], args: Any, input_dir: Path) -> None:
    start_image_url = getattr(args, "start_image_url", None)
    end_image_url = getattr(args, "end_image_url", None)
    if start_image_url is not None or end_image_url is not None:
        if start_image_url is None or end_image_url is None:
            raise ValueError("--start-image-url and --end-image-url must be provided together.")

        roles = _first_last_load_image_roles(prompt)
        start_node_ids = _sort_node_ids(roles["start"])
        end_node_ids = _sort_node_ids(roles["end"])
        if not start_node_ids or not end_node_ids:
            load_image_node_ids = _sort_node_ids(_load_image_node_ids(prompt))
            if len(load_image_node_ids) == 2:
                start_node_ids = [load_image_node_ids[0]]
                end_node_ids = [load_image_node_ids[1]]
        if not start_node_ids or not end_node_ids:
            raise ValueError(
                "Start/end images were provided but the workflow does not expose distinct start and end LoadImage nodes."
            )

        start_filename = _target_filename_for_nodes(
            prompt,
            start_node_ids,
            explicit_filename=getattr(args, "start_image_filename", None),
            image_url=start_image_url,
        )
        end_filename = _target_filename_for_nodes(
            prompt,
            end_node_ids,
            explicit_filename=getattr(args, "end_image_filename", None),
            image_url=end_image_url,
        )
        if not start_filename or not end_filename:
            raise ValueError("Could not determine target filenames for start/end images.")

        for node_id in start_node_ids:
            prompt[node_id].setdefault("inputs", {})["image"] = start_filename
        for node_id in end_node_ids:
            prompt[node_id].setdefault("inputs", {})["image"] = end_filename

        download_file(start_image_url, input_dir / start_filename)
        download_file(end_image_url, input_dir / end_filename)
        return

    if args.input_image_url is None:
        if any(node.get("class_type") in OPTIONAL_I2V_BYPASS_NODES for node in prompt.values()):
            ensure_placeholder_input_images(prompt, input_dir)
        return

    load_image_node_ids = _sort_node_ids(_load_image_node_ids(prompt))
    minimax_h3_nodes = [
        node
        for node in prompt.values()
        if node.get("class_type") == "MiniMaxH3ImageToVideo"
    ]
    if minimax_h3_nodes:
        first_frame_node_ids = {
            str(first_frame[0])
            for node in minimax_h3_nodes
            if isinstance((first_frame := node.get("inputs", {}).get("first_frame")), list)
            and len(first_frame) == 2
            and isinstance(prompt.get(str(first_frame[0])), dict)
            and prompt[str(first_frame[0])].get("class_type") == "LoadImage"
        }
        if not first_frame_node_ids:
            raise ValueError(
                "--input-image-url was provided but the MiniMax H3 first_frame input "
                "is not connected to a LoadImage node."
            )
        load_image_node_ids = _sort_node_ids(first_frame_node_ids)
    if not load_image_node_ids:
        raise ValueError("--input-image-url was provided but the workflow does not contain a LoadImage node.")
    target_filename = _target_filename_for_nodes(
        prompt,
        load_image_node_ids,
        explicit_filename=args.input_image_filename,
        image_url=args.input_image_url,
    )

    if not target_filename:
        raise ValueError("Could not determine a target filename for --input-image-url.")

    for node_id in load_image_node_ids:
        prompt[node_id].setdefault("inputs", {})["image"] = target_filename

    download_file(args.input_image_url, input_dir / target_filename)


def _iter_prompt_model_references(prompt: dict[str, Any]) -> set[ModelReference]:
    required: set[ModelReference] = set()
    for node in prompt.values():
        class_type = node.get("class_type")
        if class_type not in PROMPT_MODEL_INPUTS:
            continue
        inputs = node.get("inputs", {})
        for input_name, folders in PROMPT_MODEL_INPUTS[class_type].items():
            filename = inputs.get(input_name)
            if not isinstance(filename, str) or not filename:
                continue
            required.add(ModelReference(folders[0], filename))
    return required


def _widget_value_for_editor_node(node: dict[str, Any], index: int) -> str | None:
    raw = node.get("widgets_values")
    if isinstance(raw, list) and index < len(raw):
        value = raw[index]
        if isinstance(value, str):
            return value
    return None


def _iter_editor_model_references(workflow: dict[str, Any]) -> set[ModelReference]:
    required: set[ModelReference] = set()
    for node in workflow.get("nodes", []):
        if not isinstance(node, dict):
            continue
        class_type = _editor_node_class_type(node)
        if not class_type or class_type not in EDITOR_MODEL_INPUTS:
            continue
        for _, folders, widget_index in EDITOR_MODEL_INPUTS[class_type]:
            filename = _widget_value_for_editor_node(node, widget_index)
            if not filename:
                continue
            required.add(ModelReference(folders[0], filename))
    return required


def collect_required_model_references(payload: dict[str, Any]) -> set[ModelReference]:
    if workflow_is_api_prompt(payload):
        return _iter_prompt_model_references(payload)
    if workflow_is_editor_graph(payload):
        return _iter_editor_model_references(payload)
    raise ValueError("Unsupported workflow JSON format.")


def validate_required_models(prompt: dict[str, Any], models_dir: Path) -> None:
    missing: list[str] = []
    for ref in sorted(_iter_prompt_model_references(prompt), key=lambda item: (item.folder_name, item.filename)):
        expected_paths = [
            models_dir / folder_name / ref.filename
            for folder_name in _local_folder_aliases(ref.folder_name)
        ]
        if any(path.is_file() for path in expected_paths):
            continue
        searched = ", ".join(str(path) for path in expected_paths)
        missing.append(f"{ref.folder_name}:{ref.filename} (looked in {searched})")
    if missing:
        raise FileNotFoundError(
            "Mounted models input is missing workflow-required files:\n" + "\n".join(missing)
        )


def _local_folder_aliases(folder_name: str) -> tuple[str, ...]:
    aliases = {
        "diffusion_models": ("diffusion_models", "unet"),
        "text_encoders": ("text_encoders", "clip"),
    }
    return aliases.get(folder_name, (folder_name,))


def find_local_model_file(models_dir: Path, ref: ModelReference) -> Path:
    for folder_name in _local_folder_aliases(ref.folder_name):
        candidate = models_dir / folder_name / ref.filename
        if candidate.is_file():
            return candidate
    expected_paths = [models_dir / folder_name / ref.filename for folder_name in _local_folder_aliases(ref.folder_name)]
    searched = ", ".join(str(path) for path in expected_paths)
    raise FileNotFoundError(f"Required local model file not found: {ref.filename} (looked in {searched})")


def stage_models_package(source_models_dir: Path, staging_dir: Path, payload: dict[str, Any]) -> None:
    model_refs = collect_required_model_references(payload)
    if not model_refs:
        raise ValueError("Could not infer required model files from the selected workflow.")

    configs_source = source_models_dir / "configs"
    configs_target = staging_dir / "configs"
    configs_target.mkdir(parents=True, exist_ok=True)
    if configs_source.is_dir():
        for config_path in sorted(configs_source.glob("*.yaml")):
            target_path = configs_target / config_path.name
            target_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(config_path, target_path)

    for ref in sorted(model_refs, key=lambda item: (item.folder_name, item.filename)):
        source_path = find_local_model_file(source_models_dir, ref)
        target_path = staging_dir / ref.folder_name / ref.filename
        target_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_path, target_path)
