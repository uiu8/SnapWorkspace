# Snap Workspace 0.4.1

This is the packaged reliability-stage build. It contains all 0.4.0 matching, preflight, reporting and backup features, plus one restore-semantics correction.

- A missing executable for a background-only application is now a preflight warning instead of a blocking error.
- Required desktop windows still block before Shell submission when no existing match or valid launch target is available.
- This preserves the workspace invariant: background failures are reported, but do not prevent ready desktop windows from entering the native Snap Layout.

All 0.4.0 validation results were rerun against this build.
