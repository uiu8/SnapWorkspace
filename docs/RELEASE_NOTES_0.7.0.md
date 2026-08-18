# Snap Workspace 0.7.0

This release completes the daily-use lifecycle slice without creating alternative restore paths.

- A native Windows notification-area icon lists every saved workspace and restores through the same preflight, launch and Shell pipeline as the main library.
- One optional global hotkey restores a user-selected quick workspace. Conflicting registrations fail visibly instead of replacing another application's shortcut.
- Optional current-user start-at-login supports direct tray startup and never requires elevation.
- A named per-session mutex keeps one resident process; subsequent launches activate the existing window.
- Successful restores create an in-memory running-workspace session with a visible status banner, repeat-restore action and tray status.
- Safe end distinguishes reused windows from windows launched by the current restore. It can leave everything open or send normal close requests only to the latter; it never terminates processes or closes background applications.
- The tray implementation uses `Shell_NotifyIcon` directly, so the WPF application does not load WinForms or a browser runtime.

Regression coverage includes hotkey parsing and settings persistence, startup-command generation, single-instance activation, native tray creation, safe window ownership boundaries, all existing capture/editor/repository tests, strict Everything background startup and all ten native layout submissions.
