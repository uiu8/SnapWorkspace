# Snap Workspace 0.5.0

This release corrects the capture and restore role model. A visible non-Snap window is no longer stored as a background application.

## Clear application roles

- `Native Snap` windows remain visible and are included in the single Route 3 `SnapWindows` submission.
- `Compatibility positioned` windows remain visible, stay out of `SnapWindows`, and receive one bounded restore-time placement pass.
- `Background` applications consume no desktop zone and can be application-managed, minimized or hidden when a window appears.
- `Ignore` keeps the application out of the workspace.

## Capture behavior

- Smart capture proposes native, compatibility or background roles and keeps the four-window limit exclusive to the native set.
- Manual capture is a separate entry and starts with every discovered item ignored.
- Weak layout recognition now selects the closest built-in Snap Layout and asks for confirmation.
- Arbitrary floating rectangles are no longer presented as native custom Snap layouts.
- Legacy schema-v4 `captured-*` layouts migrate to schema v5 compatibility-positioned windows using their saved bounds.

## Restore semantics

- Native windows are resolved first and submitted together to the Shell.
- Compatibility windows are positioned after the Shell operation and are never described as Snap-controlled.
- A taskbar window explicitly changed to the background role keeps its identity and is hidden by default.
- Tray and pure-background applications remain application-managed unless the user chooses another policy in the editor.

## Validation

- Clean Release build completed with zero warnings and errors.
- Schema-v2/v4 migration, native matching, compatibility assignment and background presentation tests passed.
- Actual compatibility positioning converged to exact DWM visible bounds.
- Smart and manual capture screens were rendered and visually inspected.
- All eleven native layouts and the partial-layout test continue to use the unchanged Route 3 submission path.
