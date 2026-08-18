# Snap Workspace 0.6.0

This release completes the capture-review and workspace-authoring slice without changing the three established launch semantics.

- Native Snap windows still go through `SnapWindows`; compatibility windows remain visible and receive one bounded placement pass; strict background applications must not leave a visible top-level window.
- Capture now shows a final layout preview and checks over-limit native sets, missing launch targets, duplicate launch identities, weak recognition and multi-window ambiguity before generation.
- Applications no longer have to be running before they can be added. The picker indexes Start Menu shortcuts, AppsFolder/UWP-MSIX identities and installed-program registrations, with search, favorites and recent-use ordering.
- Correction for the 0.6.1 follow-up: geometry verification alone does not prove Snap Group membership. The editor now normalizes divider/split/merge actions to a supported native model; leaving a slot empty shows the desktop without deleting that model zone.
- Capture exclusions support executable wildcards, package-family patterns and publisher patterns. Recommended Windows-system exclusions are opt-in and individually removable.

Regression coverage includes all eight capture-scope combinations, capture-role and exclusion persistence, installed-application indexing, picker search/favorites, capture review rendering, visual-editor operations, all supported built-in models, and rejection of unsupported arbitrary geometry before Shell submission.
