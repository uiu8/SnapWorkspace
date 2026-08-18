# Snap Workspace 0.6.1

This corrective release clarifies and enforces the boundary between native Snap Group layouts, compatibility positioning and strict background startup.

- Everything background entries now use its application-supported background protocol (`-close` followed by `-startup`) instead of relying only on a generic hidden-window hint.
- Background matching follows final target processes by executable path, process name, AUMID and tracked child/launcher process IDs. A post-launch visibility check reports failure instead of silently accepting a remaining desktop window.
- Route 3 now rejects arbitrary geometry before calling `SnapWindows`. Matching rectangles are no longer treated as proof of Snap Group membership.
- The visual editor is now a native-layout arranger. Divider, split and merge operations normalize to the closest supported model.
- Empty desktop areas are represented by unassigned slots in a complete native model. Removing an application no longer deletes its Zone.
- Existing unsupported custom geometry remains loadable, but preflight blocks native restoration and the visual editor converts it to a supported model.
- The unsupported vertical-only split and arbitrary custom-layout samples were removed from the native catalog.

Regression coverage verifies strict Everything background startup, generic background guarding, all ten native models, partial layouts with empty slots, unsupported-geometry rejection and the visual arranger workflow.
