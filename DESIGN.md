# Dashboard design

The user selected an Azure AI Foundry / Fluent-style workspace. This is an operational tool used by CSAs and approved video creators, not a marketing site.

## Visual language

- Segoe UI / system sans-serif; 14px base, 28px desktop page headings, 24px on mobile.
- White work surfaces on a `#f5f5f5` background; `#d1d1d1` separators and `#616161` secondary text.
- `#0f6cbd` primary actions, restrained selected navigation, and distinct warning/error/success states with text labels.
- Four-to-six-pixel corner radii; no decorative animation, remote fonts or marketing imagery.
- Visible keyboard focus, native inputs/details/video controls, semantic headings and a skip link.

## Structure

The command header contains product identity, local-workspace context and the signed-in account. Desktop side navigation separates Create video, Video library and Environment.

Creation places reference-image upload, prompt, duration and optional negative prompt beside the output preview and asynchronous job status. Mobile collapses navigation horizontally and stacks form/output without horizontal scrolling. Empty states explain the next step rather than showing fabricated videos.

## States and truth

- Sign-in is required before API access. The sign-in page is public; it reveals no customer resource details.
- Asset preparation, cluster provisioning, zero-node scaling and operator submission approval are distinct states.
- A configured cluster with zero nodes may accept jobs when armed; an absent target is "Not configured", not "offline".
- Connection errors disable generation. Failed or unconfirmed submissions never retry automatically.
- Error messages remain visible rather than disappearing during periodic connection refreshes.
- Customer IDs and outputs come from the selected environment; screenshots with test responses are explicitly identified as fixtures.

## Validation boundary

Desktop and 390px mobile browser checks covered navigation, disabled generation, empty library, upload-preview removal and no horizontal overflow. The initially reported image-source warning is a dormant hidden upload-preview element: JavaScript assigns a Blob URL before showing it and hides it when cleared; it is not a broken visible image.

Real creator-group MSAL sign-in was confirmed by the user. Render checks used isolated browser fixtures and do not prove live generation. The configured visual-review subagent could not start because its default model was unavailable; the screenshot review was completed directly.
