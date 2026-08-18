# Snap Workspace 0.9.0

This release replaces the single quick-workspace hotkey with a keyboard-first command system.

- A global command palette searches application actions and every saved workspace. `Up`, `Down`, `Enter` and `Esc` provide complete keyboard operation.
- Three quick-workspace slots each have an independent workspace selector and global gesture. Assigned workspaces are pinned at the top of the command palette.
- The command palette, main window, smart capture and new-workspace editor can each receive their own optional global shortcut.
- Shortcut editors record a pressed combination, accept normalized text input and clear with `Delete`.
- Registration uses `RegisterHotKey` with repeat suppression and reports malformed gestures, duplicate bindings and shortcuts occupied by other applications.
- Existing 0.8 settings migrate the previous quick-workspace gesture and target to slot 1.
- The command palette follows the application theme and is available from both the global shortcut and notification-area menu.
- Regression coverage verifies digit-key virtual-key mapping, multiple simultaneous registrations, duplicate rejection, three-slot persistence and lightweight login-startup compatibility.
