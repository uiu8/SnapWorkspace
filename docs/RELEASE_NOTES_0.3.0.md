# Snap Workspace 0.3.0

This Route 3 preview adds the first complete workspace preparation flow before the native Shell Snap submission.

## Highlights

- Capture all or selected desktop windows and recognize the nearest built-in Snap Layout.
- Generate an editable custom layout when current window geometry does not closely match a built-in model.
- Add, replace or remove captured window processes in the normal workspace editor.
- Store launch-only background applications without assigning them a desktop zone.
- Reuse already-running applications by default, or request a new instance per application.
- Launch executable and packaged AUMID targets with arguments, working directory and optional elevation.
- Migrate schema-v2 workspace files to the unified schema-v3 application model.
- Keep empty zones desktop-visible and make exactly one `SnapWindows` call after required windows are ready.

## Verified locally

- Clean Release build with zero warnings.
- All 11 built-in layouts passed DWM visible-bounds verification on Windows build 10.0.29639.1000.
- Partial four-zone restore passed with two occupied and two desktop-visible zones.
- Layout recognizer, schema migration and popup wheel regression tests passed.

## Known limitations

- Restore currently targets the primary monitor.
- The validated private Shell path accepts at most four windows in one submission.
- Applications with launch brokers or highly variable window identities may still need editable matching rules in a later release.
