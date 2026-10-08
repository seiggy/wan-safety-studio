---
name: WAN Safety Studio
description: A neutral Azure AI Foundry / Fluent-style workbench where video generation, labeling and detector training sit side by side and every state is true.
colors:
  accent: "#0f6cbd"
  accent-hover: "#115ea3"
  focus-ring: "#005a9e"
  link: "#0f548c"
  ink: "#242424"
  ink-secondary: "#424242"
  muted: "#616161"
  workspace: "#f5f5f5"
  surface: "#ffffff"
  sidebar: "#fafafa"
  line: "#d1d1d1"
  line-soft: "#e0e0e0"
  field-border: "#8a8886"
  stage: "#ebedef"
  canvas-dark: "#222222"
  selected-tint: "#e5effa"
  selected-ink: "#0c3b5e"
  info-bg: "#eef5fc"
  info-line: "#b9d8f2"
  info-ink: "#20415c"
  warning-bg: "#fff5df"
  warning-line: "#dbc594"
  warning-ink: "#654800"
  success-bg: "#edf7ed"
  success-line: "#afcfae"
  success-ink: "#245323"
  danger: "#a4262c"
  danger-bg: "#fdf3f4"
  danger-line: "#dfb0b4"
  danger-ink: "#8f1e24"
  reviewed: "#107c10"
typography:
  body:
    fontFamily: "\"Segoe UI\", system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5
  page-heading:
    fontFamily: "\"Segoe UI\", system-ui, sans-serif"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.2
  brand:
    fontFamily: "\"Segoe UI\", system-ui, sans-serif"
    fontSize: "17px"
    fontWeight: 600
  label:
    fontFamily: "\"Segoe UI\", system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 600
  code:
    fontFamily: "Consolas, \"Cascadia Code\", monospace"
    fontSize: "12px"
rounded:
  sm: "4px"
  md: "5px"
  lg: "6px"
  panel: "8px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "24px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.surface}"
    rounded: "{rounded.sm}"
    padding: "7px 16px"
    height: "36px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
  button-secondary:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.accent}"
    rounded: "{rounded.sm}"
    padding: "7px 16px"
    height: "36px"
  nav-item-selected:
    backgroundColor: "{colors.selected-tint}"
    textColor: "{colors.selected-ink}"
    rounded: "{rounded.sm}"
    padding: "11px 12px"
  panel:
    backgroundColor: "{colors.surface}"
    rounded: "{rounded.lg}"
    padding: "24px"
  status-badge-warning:
    backgroundColor: "{colors.warning-bg}"
    textColor: "{colors.warning-ink}"
    rounded: "{rounded.sm}"
    padding: "4px 10px"
  status-badge-success:
    backgroundColor: "{colors.success-bg}"
    textColor: "{colors.success-ink}"
    rounded: "{rounded.sm}"
    padding: "4px 10px"
  status-badge-error:
    backgroundColor: "{colors.danger-bg}"
    textColor: "{colors.danger-ink}"
    rounded: "{rounded.sm}"
    padding: "4px 10px"
---

# Design System: WAN Safety Studio

## Overview

**Creative North Star: "The Lab Bench"**

A neutral bench where generating, labeling and training sit side by side. The user chose an Azure AI Foundry / Fluent-style workspace: familiar Microsoft task and navigation patterns, white work surfaces on a pale gray bench, one blue for action. This is an operational tool for CSAs, approved video creators and customer AI engineers, not a marketing site. The interface recedes; the video, the frame under the cursor and the training numbers lead.

**Key Characteristics:**
- Flat and bordered: depth comes from 1px lines, not shadows.
- One accent (`accent`), used only for actions and the current selection.
- State is always spelled out in words, never by color alone, and never shown healthy unless verified.
- Native inputs, details, selects and video controls; no decorative animation, remote fonts or marketing imagery.
- The same shell serves two backends (Azure ML and local ComfyUI) and changes its wording, not its layout.

## Colors

Restrained: a pale bench, white panels, one blue. Status hues exist only to label state and always appear with text.

### Primary
- **Bench Blue** (`#0f6cbd`, `accent`): primary buttons, the segmented control's selected underline, progress fill and the selected frame outline. `accent-hover` is `#115ea3`.

### Neutral
- **Pale Bench** (`#f5f5f5`, `workspace`): page background. **Clean Panel** (`#ffffff`, `surface`): every work surface. **Side Rail** (`#fafafa`, `sidebar`).
- **Ink** (`#242424`) for text, **Ink Secondary** (`#424242`) for navigation, **Muted** (`#616161`) for helper text and metadata, **Hairline** (`#d1d1d1`) for separators, `field-border` (`#8a8886`) for inputs.
- **Video Stage** (`#ebedef`) behind generated video; **Label Canvas** (`#222222`) behind the frame being labeled.

### Status (always paired with a text label)
- Info (`info-*`) for neutral notices, Warning (`warning-*`) for "not ready / disabled", Success (`success-*`) for armed or completed, Danger (`danger-*`) for failures; `reviewed` green (`#107c10`) marks frames a human has reviewed.

### Named Rules
**The One Blue Rule.** The accent is for actions and the current selection. Do not use it to decorate.
**The Words-First Rule.** A status color never carries meaning alone; the badge or notice names the state.

## Typography

**Body Font:** Segoe UI (system-ui, sans-serif fallback). **Code Font:** Consolas / Cascadia Code.

### Hierarchy
- **Page heading** (600, 28px desktop / 24px mobile, 1.2): one per view.
- **Brand** (600, 17px): product name in the command header.
- **Body** (400, 14px, 1.5): everything else; form labels keep body size.
- **Label** (600, 12px): navigation title, badges, metadata, table headers.
- **Code** (12px): IDs, paths and commands, wrapping anywhere.

### Named Rules
**The Plain Voice Rule.** No display type. Hierarchy comes from weight and spacing in one family.

## Layout

A command header (identity, workspace context, account) over a two-column workspace: a side rail with Create video, Video library, Vision datasets and Environment, and a content area. Creation sits beside the output and job status. The Vision view has a 300px side column (videos, dataset versions) beside the label editor; the Train panel sits below the editor. Spacing runs 4/8/12/16/24px. Below 1080px and 800px the layout collapses; navigation becomes horizontal and form and output stack with no horizontal scrolling. Long copy caps at about 70-75ch.

## Elevation & Depth

Flat. Panels are white with a 1px `line` border; there are no shadows. Selection adds an inset 2px accent underline (segmented control) or a 2px outline (frame filmstrip). Keyboard focus is a 2px `focus-ring` outline with 3px offset on every interactive element. Transitions are 160ms ease-out on background only and are disabled under `prefers-reduced-motion`.

## Shapes

Small, consistent radii: 4px for buttons, inputs and badges; 5px for scene and job rows; 6px for panels and video stages; 8px only for the sign-in panel. Dashed 1px borders mark drop areas. No pills; the brand mark is a 5px rounded square.

## Components

### Buttons
Primary: `accent` fill, white text, 36px tall, 7px 16px padding, 600 weight; hover `accent-hover`. Secondary: white fill, accent text and border. Text button: link-blue, no border, 600 weight. Disabled: gray fill and text with a not-allowed cursor.

### Inputs and fields
White fill, 1px `field-border` with a darker bottom edge, 4px radius, 9px 11px padding. Segmented controls share one 1px border; the checked option gets `selected-tint` and an inset accent underline. Selects and number inputs in toolbars are 36px tall.

### Navigation
Side-rail items: 4px radius, 11px 12px padding, a 16px glyph and a label; selected uses `selected-tint` and `selected-ink` with 600 weight. A footer block states the active workflow ("Local ComfyUI workflow" or "Private Azure ML workflow").

### Status badge and notice
Badge: 12px, 4px radius, tinted fill with matching border and text (neutral, success, error, warning by default). Notices are 4px-radius bordered bars (info by default, warning when something is disabled) with `role="status"`; errors use `role="alert"` and stay on screen rather than vanishing during refreshes.

### Frame filmstrip and label canvas (signature)
A horizontal strip of 72x48 frame buttons with a 2px border; reviewed frames take the `reviewed` green border, the selected frame a 2px accent outline. The canvas sits on `canvas-dark`; box strokes use a six-color class palette, with "(auto)" appended to unreviewed pre-labels. The tools row holds the class select, "Remove last box" and "Save as reviewed".

### Train panel and runs table (signature)
A white 6px panel: a wrapping form row (dataset, trainer with code and weights licences shown beneath, preset, epochs, Start training) above a full-width table of runs. Status is a badge; mAP@0.5 and mAP@0.5:0.95 appear for completed runs, failures show red 12px text, and per-class AP sits in a native `<details>`.

### Job progress
A native `<progress>` (5px, accent) with a text line naming the stage ("Sampling (high noise)") and step count (5/10). Progress arrives over server-sent events; the label, not the bar, is the source of truth.

## Do's and Don'ts

### Do:
- **Do** name every state in words: "Not configured" is not "offline"; "Unreachable" is not "failed".
- **Do** keep generation, startup and spend approvals visibly separate.
- **Do** show the total job count before submitting a batch.
- **Do** keep errors visible until the user dismisses or resolves them; never retry a submission automatically.
- **Do** surface licences (code and weights) beside every trainer, and provenance on every dataset.
- **Do** stay within the Foundry/Fluent grammar: native controls, 1px lines, one accent.

### Don't:
- **Don't** show an unverified service as healthy, or invent sample videos, metrics or results; empty states explain the next step.
- **Don't** use shadows, gradients, decorative motion, remote fonts or marketing imagery.
- **Don't** let color alone carry status.
- **Don't** use inline styles or inline scripts: the Content-Security-Policy forbids them, so visual variants are classes.
- **Don't** present synthetic-only validation as real-world accuracy.

## States and truth

- Sign-in is required before API access on the Azure-backed portal. The sign-in page reveals no customer resource details. The local server has no sign-in and says "Local mode".
- Asset preparation, cluster provisioning, zero-node scaling and operator submission approval are distinct states. A configured cluster with zero nodes may accept jobs when armed; an absent target is "Not configured", not "offline".
- In local mode the notice states ComfyUI reachability and whether a workflow file exists; storage and ComfyUI endpoints appear in Environment.
- Connection errors disable generation. Failed or unconfirmed submissions never retry automatically.
- Customer IDs and outputs come from the selected environment; screenshots with test responses are identified as fixtures.

## Validation boundary

Desktop and 390px mobile browser checks covered navigation, disabled generation, empty library, upload-preview removal and no horizontal overflow for the original dashboard. The Vision, Train, live-progress and local-mode screens have been type-checked, built and unit tested but have not had a visual review. Real creator-group MSAL sign-in was confirmed by the user on the Azure-backed portal.
