# WAN Safety Studio

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

- CSAs deliver the accelerator into customer-owned Azure environments. An Entra security group of approved video creators uses the dashboard; the initial MCAPS group includes the operator account.
- Customer AI engineers build datasets and train detectors for their own equipment and use cases. They need open formats, replaceable trainers and honest quality signals, and may bring their own models.

## Product Purpose

Run the full loop in one place: generate synthetic safety and equipment-failure video, label it with bounding boxes, train a fast detector, and validate it on a supported edge AI device (Jetson Orin Nano Super is the v1 target) by replaying a held-out generated video as a virtual camera. Generation runs on a private Azure ML WAN image-to-video workflow, or on a local ComfyUI server for development. Make setup, readiness, job state and cost boundaries understandable without requiring users to operate Azure directly.

## Operating Context

Local development runs under an Aspire AppHost (.NET 10 server plus the React UI) with ComfyUI generation, Azurite, folder/LAN or Azure storage and a local GPU for training; the Azure-backed Azure ML provider remains for machines too weak to generate locally. Everything except the final edge device runs in Azure when deployed. Training shares the single generation node. The edge device is reached through the operator workstation over VPN (no IoT Hub, no device credentials for Azure).

The current deployment has a private AML foundation and customer-managed GPU NSG. Local development uses a VPN/private DNS and Azure CLI backend credentials. The browser requires MSAL sign-in. The accelerator outcome is the same studio on a private-endpoint-only Azure App Service with customer Entra sign-in and a managed-identity backend.

## Capabilities and Constraints

- CPU preparation verifies pinned source, model checksums and image digests before GPU use.
- WAN requires a reference image. Generation is asynchronous through AML; the library contains actual completed jobs, not sample results.
- A request is a batch of 1–5 scenes × 1, 3 or 5 takes. Every take is one billed GPU job, and the total is shown before submission. Duration is capped at 10 seconds per clip.
- Starting the local portal must not arm compute. GPU use requires the existing explicit spend/network approvals and prepared-asset checks.
- The GPU target remains private Spot A100, min zero/max one, with a server-side job timeout.
- Customer networking, identity authorization and secrets remain customer-scoped. Do not enable public Azure data endpoints as a workaround.
- MSAL sign-in, the dashboard, and a one-instance private App Service host are implemented in the Python portal. The .NET server has no sign-in yet (Development only). Multi-instance production operation (shared session store) remains separate work.
- Vision workflow: import or generate videos, extract frames, label boxes (review before use), freeze versioned COCO datasets split by source video, and train through pluggable trainer containers. Only Apache-2.0 trainers ship (YOLOX Nano/Tiny/s); customers add licensed models through the trainer contract. Synthetic-only results prove the pipeline, not real-world accuracy.
- Updates are pushed over server-sent events rather than polled.

## Evidence on Hand

`README.md` records the deployed foundation and predecessor WAN smoke generation. Terraform output, local operator receipts and AML APIs provide live resource/job state. There are currently no generated videos in the new workspace. The vision plan is in docs/vision-training-plan.md; trainer and local-generation guides are in docs/vision-trainers.md and docs/local-generation.md. No trained model or edge validation result exists yet; do not fabricate metrics.

## Product Principles

- Show actual readiness and errors; never represent an unverified service as healthy.
- Keep generation, local UI startup and infrastructure/spend approvals separate.
- Reuse the pinned workflow and existing safety checks.
- Document every required customer configuration and provisioning step.
- Open formats and replaceable stages: AI engineers can enter or leave the loop (COCO/YOLO, ONNX, plain MP4/JPEG).
- Never overstate model quality: split by scene, label provenance and licences stay visible.

## Brand Commitments

The user selected an Azure AI Foundry / Fluent-style workspace for the first dashboard redesign. Preserve familiar Microsoft task/navigation patterns rather than a marketing-page layout.
