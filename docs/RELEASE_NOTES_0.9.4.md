# Snap Workspace 0.9.4

This release fixes mixed system-surface, background, native Snap and non-Snap application restoration.

## Fixed

- Captured Windows Settings windows are migrated from the non-launchable `ApplicationFrameHost.exe` target to the canonical `ms-settings:` URI.
- A missing visible application no longer discards every prepared window. Available native windows are still submitted as a partial layout and the unavailable Zone remains desktop-visible.
- Windows that structurally cannot participate in Shell Snap are routed directly to compatibility placement using their assigned Zone bounds.
- Preparation diagnostics now record per-application status and missing application IDs without storing titles or executable paths.
- Background reuse status reports the number of detected processes, making successful windowless startup visible in the UI report.

## Validated with `我的工作区`

- Chrome: seven existing windowless background processes detected.
- Windows Settings: cold-started through `ms-settings:`.
- Notepad and Settings: two-window native Snap batch passed geometry verification.
- Obsidian: skipped native Snap as requested and placed once in the `side-bottom` Zone through compatibility positioning.
- Full cold-start restore completed successfully.
