# Snap Workspace 0.9.3

This maintenance release fixes restore-transaction lifetime and cold-start overlay ordering.

## Fixed

- Strict background hiding is released immediately when workspace restoration completes or aborts. User actions such as clicking the application's tray icon are no longer suppressed by the previous fixed 20-second guard.
- Cold-started compatibility windows that overlap native Snap windows receive a temporary foreground lease while native applications finish their late activation.
- The compatibility window is explicitly demoted to an ordinary non-topmost window before restoration completes, so normal focus and Z-order behavior resumes immediately afterward.
- Restore diagnostics continue to record the number of detected and reconciled overlap windows.

## Validation

- Workspace `13` was restored from a fully closed EasyPub/GuoheView state: all three native windows passed Snap geometry verification and EasyPub finished above GuoheView.
- Post-restore inspection confirmed both EasyPub and GuoheView were non-topmost.
- Background transaction regression confirms windows stay hidden during restore and can be shown immediately after the guard is released.
