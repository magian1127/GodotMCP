# Release and validation

[中文](release.md) | English

## Current unreleased: direct daemon connection (0.6.0)

This section supersedes old behavior in historical records.

- **New direct transport (default)**: `transportMode=auto` connects over `streamable-http` when the daemon's 2026-07-28 face is available, so no per-session shim process is spawned; `http` forces it, `stdio` keeps the shim. Older daemons, closed ports, or a missing token fall back to stdio automatically.
- **New `src/daemon-endpoint.ts`**: port/state-dir/token resolution, detached bootstrap, `server/discover` 2026-07-28 proof, UUID-v5-shaped session keys, `x-godot-cwd` encoding; every dependency is injectable and tested without a real daemon.
- **New session key scope**: `sessionKeyScope=session` (default, derived from the DSH session id — new sessions start at one tool, reconnects keep the activated face) or `project` (derived from cwd — sessions share target/groups and a new session inherits an expanded face).
- **Concurrency**: `syncAgent` serializes per session id (`syncInFlight`) because endpoint waiting yields; endpoint readiness triggers one `resyncAll`.
- **Timeout remount gap fixed**: identity comes from `mountConfigKey` (stdio/http prefixes differ), so transport switches, token rotation, key-scope changes, and timeout edits all remount.
- **Credential boundary**: the daemon token lives in in-memory headers only; diagnostics log fixed prose and `error.name`, never config or headers.
- **Tests**: new `daemon-endpoint.test.mts` (14 cases), four http cases in `mcp-mount.test.mts`, six direct-path cases in `plugin-smoke.test.mts`; 93 total green.
- Validation: typecheck/test/verify green, plus one live direct mount using the built `lib/index.js` against the local daemon (streamable-http, UUID key, Chinese cwd header, `tools/list` returning `godot_connect`). **Not executed**: GUI Godot-preset tool calls and image attachments inside a running `dsh web`.
- No project environment injection remains in mounts. paths.json uses serverDist with empty projects; CLI --project still validates a project.
- Reproduced gaps in notification recovery, group replacement/replay, resident schemas, debug/eager switches, and in-flight targets remain. The [daemon README](../../../../plugin/godot-mcp-unified/server-dotnet/README.en.md) owns their status. Green existing tests are not complete release acceptance.
- Beyond typecheck/build/test/verify, validate initial entry, automatic list expansion after connect, group changes, restart recovery, and image attachments in real hosts. The five-host/version and mixed-version matrices remain incomplete.

## Compatibility fix: DSH 0.2.2-rc.1 (Godot preset vanished from the mode picker)

- **Symptom**: after upgrading to 0.2.2-rc.1 the mode picker offers no Godot for a new session, while the Host still reports the `preset-godot` row as active — so the declaration itself was not lost.
- **Cause**: the preset's `plugins` still listed `@deepseek-ai/dsh-tool-subagent-control/list-agents`. In 0.2.2-rc.1 that package's `exports` shrank to `.`, `./src/*`, `./package.json`, `./locale/*.json`; the `list_agents` tool moved into the main entry and `./list-agents` no longer exists. One unresolvable child row marks the whole preset broken, and the client `presetOptions` lists healthy presets only (a `broken` row shows only on the Agent presets management page), so Godot disappears from the picker rather than one tool disappearing.
- **Change**: the row is removed (`list_agents` comes from the `@deepseek-ai/dsh-tool-subagent-control` main entry); `src/tests/preset-patch.test.mts` now guards against reintroducing the subpath and asserts the main entry stays.
- **Validation**: `npm run typecheck` / `npm test` (94 cases) / `npm run verify` green; `dsh --profile web --dump-config` no longer contains `list-agents` and still contains `preset-godot`. **Not executed**: live GUI acceptance — bundle patches are read at Host startup, so the application must restart (or the bundle reload) before the picker reflects this.
- **Rule**: every preset row `name` must resolve in the current runtime. `--dump-config` only validates composition and never activates plugins, so it cannot catch this class of breakage; after a DSH upgrade, re-check each subpath in the preset against the new `exports`.

## Release procedure

Run the package’s typecheck, build, test, and verify scripts. Inspect npm pack contents: skills/ is a development junction and must be materialized/snapshotted for distribution, while lib/, bin/, client bundle and declared patch must exist. Keep official DSH runtime modules external and honor engines.dsh.

Use CLI status and --dump-config for installation/composition; separate those from real editor calls and GUI behavior. Do not restart a running shared DSH host as a release check.

## Historical records

The [Chinese release archive](release.md) preserves 0.4/0.5 migration details: custom-client retirement, managed-row cleanup, preset declaration migration, workstation-tab extraction, and earlier validation results. Statements about cwd-bound project maps, static environment injection, or legacy preset directories describe those past releases, not the current contract. Current user behavior is maintained in [behavior](behavior.en.md).
