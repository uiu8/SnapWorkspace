# Snap Workspace 0.9.2

This maintenance release fixes two restore-order races found in mixed native/compatibility workspaces.

## Fixed

- AppsFolder-only desktop identities such as `com.yuewen.authorwrite.pc` are resolved to their real executable before strict background startup.
- Background startup is now a prerequisite transaction stage. If an application cannot be kept windowless, visible applications are not launched and no native Snap submission is attempted.
- Application discovery now preserves both the AppsFolder AUMID and resolved executable path for future workspace saves.
- Compatibility-positioned windows are placed after native Snap. Overlapping compatibility windows receive a bounded, non-activating ordinary Z-order reconciliation during cold-start stabilization, so a late-activating native window does not cover them.
- Compatibility windows are not marked always-on-top and remain outside the native Snap group by design.

## Validation

- Real AUMID background test with `com.yuewen.authorwrite.pc` resolves to `D:\software\zuojiazhushou\yuewenedit\program\yuewenedit.exe` and leaves zero visible windows.
- Synthetic overlap test verifies the compatibility window finishes above the intersecting native window without activation.
- Existing-window, delayed-window, and child-process background hiding regressions pass.
