# Snap Workspace 0.8.0

This release starts the public-release hardening stage while preserving the existing Route 3 restore chain.

- Login startup now uses a lightweight message-only tray host. The complete WPF main window is constructed only when the user opens it or restores a workspace; tray restore and the optional global hotkey remain available beforehand.
- Structured local diagnostic events are retained for seven days and are never uploaded automatically.
- Support bundles are privacy-minimal by default: operating/runtime data, capability state, counts and anonymous events. Application paths, launch arguments, window titles and complete workspace JSON require separate explicit opt-ins.
- Settings can export a support bundle or clear all diagnostic events. A repeatable smoke test proves the default archive does not contain a seeded private path, title or workspace name.
- Keyboard navigation adds Ctrl+N, Ctrl+Shift+C, Ctrl+S, F5, Ctrl+, and Esc actions; major navigation/status elements expose screen-reader metadata.
- Windows high-contrast changes are observed at runtime and replace custom colors with system brushes.
- A dedicated Snap Workspace icon is embedded in the executable, WPF window, tray and MSIX assets.
- `Build-Release.ps1` produces a small framework-dependent portable zip and a self-contained single-file zip with checksums. When the Windows SDK is present it also creates an MSIX and signs it only when an explicit PFX is provided.
- The GitHub release workflow builds, tests, packages, optionally signs, uploads and attests release artifacts. Tag builds can create a GitHub release through the official `gh` CLI.

The local machine does not currently have the Windows SDK, so the portable packages are locally smoke-tested while MSIX creation remains assigned to the `windows-latest` release workflow. A trusted public signing certificate is still required before distributing the MSIX to end users.
