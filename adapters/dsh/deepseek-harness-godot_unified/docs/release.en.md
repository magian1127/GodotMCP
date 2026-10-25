# Release and validation

[中文](release.md) | English

## Current unreleased session tool surface

This section supersedes old behavior in historical records.

- Hosts use the bundled stdio shim; remove direct-HTTP entries. New keys connect through godot_connect before expanding into 17 groups.
- No project environment injection remains in mounts. paths.json uses serverDist with empty projects; CLI --project still validates a project.
- toolCallTimeoutMs defaults to 240000ms; existing per-session connections do not remount for timeout-only edits.
- Reproduced gaps in notification recovery, group replacement/replay, resident schemas, debug/eager switches, and in-flight targets remain. The [daemon README](../../../../plugin/godot-mcp-unified/server-dotnet/README.en.md) owns their status. Green existing tests are not complete release acceptance.
- Beyond typecheck/build/test/verify, validate initial entry, automatic list expansion after connect, group changes, restart recovery, and image attachments in real hosts. The five-host/version and mixed-version matrices remain incomplete.

## Release procedure

Run the package’s typecheck, build, test, and verify scripts. Inspect npm pack contents: skills/ is a development junction and must be materialized/snapshotted for distribution, while lib/, bin/, client bundle and declared patch must exist. Keep official DSH runtime modules external and honor engines.dsh.

Use CLI status and --dump-config for installation/composition; separate those from real editor calls and GUI behavior. Do not restart a running shared DSH host as a release check.

## Historical records

The [Chinese release archive](release.md) preserves 0.4/0.5 migration details: custom-client retirement, managed-row cleanup, preset declaration migration, workstation-tab extraction, and earlier validation results. Statements about cwd-bound project maps, static environment injection, or legacy preset directories describe those past releases, not the current contract. Current user behavior is maintained in [behavior](behavior.en.md).
