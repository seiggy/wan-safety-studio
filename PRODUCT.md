# WAN Safety Studio

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

CSAs deliver the accelerator into customer-owned Azure environments. An Entra security group of approved video creators uses the dashboard; the initial MCAPS group includes the operator account.

## Product Purpose

Create and inspect generated safety-video material using a private Azure ML WAN image-to-video workflow. Make setup, readiness, job state and cost boundaries understandable without requiring users to operate Azure directly.

## Operating Context

The current deployment has a private AML foundation and customer-managed GPU NSG. Local development uses a VPN/private DNS and Azure CLI backend credentials. The browser requires MSAL sign-in. The accelerator outcome is the same studio on a private-endpoint-only Azure App Service with customer Entra sign-in and a managed-identity backend.

## Capabilities and Constraints

- CPU preparation verifies pinned source, model checksums and image digests before GPU use.
- WAN requires a reference image. Generation is asynchronous through AML; the library contains actual completed jobs, not sample results.
- A request is a batch of 1–5 scenes × 1, 3 or 5 takes. Every take is one billed GPU job, and the total is shown before submission. Duration is capped at 10 seconds per clip.
- Starting the local portal must not arm compute. GPU use requires the existing explicit spend/network approvals and prepared-asset checks.
- The GPU target remains private Spot A100, min zero/max one, with a server-side job timeout.
- Customer networking, identity authorization and secrets remain customer-scoped. Do not enable public Azure data endpoints as a workaround.
- MSAL sign-in, the dashboard, and a one-instance private App Service host are implemented. Multi-instance production operation (shared session store) remains separate work.

## Evidence on Hand

`README.md` records the deployed foundation and predecessor WAN smoke generation. Terraform output, local operator receipts and AML APIs provide live resource/job state. There are currently no generated videos in the new workspace.

## Product Principles

- Show actual readiness and errors; never represent an unverified service as healthy.
- Keep generation, local UI startup and infrastructure/spend approvals separate.
- Reuse the pinned workflow and existing safety checks.
- Document every required customer configuration and provisioning step.

## Brand Commitments

The user selected an Azure AI Foundry / Fluent-style workspace for the first dashboard redesign. Preserve familiar Microsoft task/navigation patterns rather than a marketing-page layout.
