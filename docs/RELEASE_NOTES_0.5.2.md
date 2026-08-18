# Snap Workspace 0.5.2

This release corrects the meaning and enforcement of background startup, then begins the next roadmap phase with reusable capture-role rules.

- A background application now always means **no visible top-level window during workspace restore startup**.
- Background suppression is armed before launch, applies immediately to reused processes, and keeps watching for delayed startup windows without blocking native Snap restore.
- Executable launches also receive the Windows hidden-window startup hint.
- Existing schema-v5 workspaces that stored application-managed or minimize behavior migrate to strict hidden startup when loaded.
- Tray and pure-background entries in enabled capture scopes default to the background role, including manual capture; taskbar windows in manual capture remain ignored until selected.
- The editor no longer exposes ambiguous application-managed/minimize choices.
- Capture rows provide an explicit **Remember** action for per-application roles; saved rules can be removed from Settings.

Regression coverage includes immediate and delayed background windows, non-blocking preparation, old-workspace migration, all eight capture-scope combinations, role-rule persistence, window matching, preflight and layout recognition.
