# Snap Workspace 0.4.0

This release completes the first reliability stage while deliberately keeping the product single-monitor.

## Window matching

- Workspace schema v4 adds an independent matching rule to every window application.
- Process matching supports automatic identity, exact path/AUMID, executable name or ignore.
- Title matching supports automatic identity, exact text, contained text, wildcard, bounded regular expression or ignore.
- Window class can be made mandatory for applications that expose several window types.
- Existing schema-v2 and schema-v3 files migrate automatically and retain the previous automatic matching behavior.

## Restore preflight and reports

- Every restore checks the Route 3 capability, submission limit, workspace validity, launch target, working directory and current candidates before starting applications.
- Ambiguous equal-score windows are reported before restore and can be resolved with a title rule.
- A new `Check` action runs the same read-only preflight from a workspace card.
- Application results identify reused, started, failed and non-launchable entries, including the selected window, match score and reason.

## Backup and portability

- Saving an existing workspace automatically keeps its previous JSON revision, with the latest 20 revisions retained per workspace.
- `Backup` exports all workspaces to a portable `.snapworkspace` archive.
- `Import` accepts archives and individual workspace JSON files.
- Id conflicts preserve the local workspace and import a separately named copy.

## Validation

- Clean Release build completed with zero warnings and errors.
- Schema migration, wildcard/regex matching, ambiguity detection and missing-executable preflight tests passed.
- Automatic revision backup and archive export/import round trip passed.
- The editor preview was rendered and visually inspected in the Fluent UI.
