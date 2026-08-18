# Snap Workspace 0.3.3

This update makes the tray scope correspond to Windows notification-area applications and adds persistent capture exclusions.

## Tray detection

- Reads Windows notification-area registration records for the current user.
- Resolves GUID-based known-folder paths and ordinary executable paths.
- Keeps only entries whose executable currently has a running process.
- Deduplicates taskbar, tray and pure-background categories by launch target.
- No longer treats every process with a hidden top-level window as a tray application.

## Capture exclusions

- Every capture row now has a `Block` action.
- Blocked applications are matched by executable path, packaged-app AUMID or display name fallback.
- Rules apply to taskbar, tray and pure-background capture scopes.
- Settings includes a local capture exclusion list with `Unblock` actions.
- Windows Security and other system notification applications can be excluded without hard-coding them for every user.

## Validation

- Live catalog identified separate taskbar, running notification-area and pure-background sets without duplicates.
- Capture exclusion filtering and settings persistence tests passed.
- All eight capture-scope combinations and the popup-wheel regression passed.
- Clean Release build completed with zero warnings.
