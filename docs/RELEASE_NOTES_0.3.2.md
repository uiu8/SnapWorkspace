# Snap Workspace 0.3.2

This update adds composable capture scopes so large process inventories are opt-in instead of appearing on every capture.

## Capture scopes

- `Taskbar applications` — enabled by default.
- `Tray / resident applications` — disabled by default.
- `Pure background processes` — disabled by default.
- All three switches are independent, supporting every single, pair and all-enabled combination.
- A tray-only or background-only selection can create a launch-only workspace without a Snap window.

## Classification behavior

- Taskbar items keep the per-application `Snap`, `launch only` and `ignore` roles.
- Tray and pure-background items support `launch only` or `ignore`.
- Tray/resident candidates are separated from pure background processes using hidden top-level owner-window evidence.
- Windows system components and duplicate executable/AUMID targets remain filtered.

## Validation

- All eight taskbar/tray/background scope combinations passed UI filtering tests.
- Clean Release build completed with zero warnings.
- Live application-catalog, schema-v3, layout-recognizer, background-reuse and popup-wheel tests passed.
