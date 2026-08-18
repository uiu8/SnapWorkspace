# Snap Workspace 0.3.1

This update corrects the capture model: capture now represents the current application set rather than treating every running item as one of four Snap windows.

## Changes

- Automatically classify application inventory items as `Snap window`, `launch only` or `ignore`.
- Detect current-session processes without an eligible top-level window as launch-only/background applications.
- Treat small, minimized and overflow windows as launch-only by default while allowing manual role changes.
- Limit only the Snap subset to four windows; launch-only applications are unlimited.
- Deduplicate identical executable/AUMID launch targets and filter Windows system components.
- Carry automatically detected launch-only applications directly into the workspace editor.
- Fall back to the nearest built-in layout when overlapping windows cannot form a valid custom layout.
- Preserve manual add/remove and launch-option editing after capture.

## Validation

- Clean Release build with zero warnings.
- Schema-v3 test passed with two window applications and six launch-only applications.
- Live application catalog passed duplicate-target and Windows-component filtering checks.
- Built-in layout recognition, overlapping-window fallback and background-process reuse tests passed.
