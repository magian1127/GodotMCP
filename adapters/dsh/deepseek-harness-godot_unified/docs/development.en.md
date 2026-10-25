# Development

[中文](development.md) | English

## Ownership and modules

This is a regular directory in the GodotMCP repository, sharing Git history. Only skills/ is a junction to `plugin/godot-mcp-unified/skills`. This adapter implements neither the bridge nor an MCP client; daemon facts belong upstream, host integration facts here.

| File/directory | Responsibility |
| --- | --- |
| src/index.ts | Per-session/shared mounting, prompt and route composition |
| src/mcp-mount.ts | Shim/historical JS launch plans, Config, shared-mode identity, errorName |
| src/injection.ts | Settings, preset/injection decisions, prefixes |
| src/section-text.ts | Bilingual workflow prose, no tool table |
| src/profile-modules.ts | Resolve schemastery and official client from current profile |
| src/path-store.ts | paths.json persistence/mtime reload; compatible project shapes |
| src/path-settings-route.ts | serverDist API; ignored project input |
| src/client/ | Config form and same-origin fetch |
| src/bin/cli/ | Install/uninstall/status, validation, read-only probes |
| src/bin/patch-row.mts | Managed-block cleanup, atomic writes/lock |
| src/tests/ | node:test regressions |
| cordis.patch.yml | dsh-godot and preset-godot declarations |
| skills/ | Six skills linked to source |

lib/, bin/, .tsbuild/ are ignored outputs. Build runs host, CLI, tests, client. tsc does not clean obsolete output: after deleting/renaming sources, verify these output paths belong to this package before recycling/rebuilding them.

## Mounting and ownership

- Mount at agent.ctx for session visibility. Subagents inherit the preset version rather than parent Agent scope, so each gets a shim/key/target/groups.
- A static client row in the preset mounts eagerly at profile load and cannot carry session cwd. Default mounting happens on Godot session creation or blank-session preset selection.
- injectLegacyMode deliberately shares one host connection and prompt, including target/groups; it is unrelated to protocol legacy.
- Bookkeep by session ID, unmount by entry identity; repeated creation with different objects must remain idempotent.
- Read serverDist from PathStore, falling back to settings; compatible projectPath is always empty. Use agent.session.header.cwd, or process.cwd() in shared mode.
- Current per-session configKey is dist/empty project/cwd; shared mountConfigKey also includes command/args/env/serverName/timeout. **Timeout-only edits do not remount existing per-session connections**; this remains a defect.
- Config: stdio, command/args, env:{}, optional cwd, toolCallTimeoutMs default 240000, failOnStartupError:false. Positive timeout values are rounded and capped at 900000.
- Catch all session-creation failures and warn. READY_GRACE_MS is ten seconds; background connection completion still depends on host discovery.

Profile modules use require.resolve, exports import entry, TLA preloading. Avoid synchronous require(esm) during incomplete host hooks. Do not add official modules as dependencies/peerDependencies. Validate with official Config before mounting; log error.name without configuration or exception messages.

## Routes and client

Register GET/POST together because webServer routes by path, then branch on method. GET returns serverDist with empty project fields; POST persists serverDist and clears projects. PathStore, routes, compatible types remain: mapping is retired, not every source file deleted.

Use loopback peer, Host, Origin, sec-fetch-site checks; responses 405/413/400/fixed 500; body limit 8MiB. Inject webServer, returning undefined when absent. Teardown is reversible through ctx.effect. A getter supplies current fallback serverDist rather than a mount-time snapshot.

Client output is classic lib/client.js. Source uses React.createElement with host-provided React; tsdown excludes react. Fiber exports.inject contains slots/locale/configForms; package-level dsh.client.inject is separate and empty. Config registration callbacks return disposers to prevent HMR/epoch leaks. POST only on Save; expose read errors in the form.

## CLI and preset

Install still validates --project, writes serverDist/projects:[], cleans old managed rows, and writes no new mcp-client row. Desktop skips bundle/patch operations. Status may display historical projects for diagnosis; this is not active host routing.

Managed-block external content is read-only. Comment-only leftovers become valid []; use same-directory locks and tmp+rename. Host/CLI constants have alignment tests. Avoid shell:true; reject unsafe-to-forward special arguments. Follow the installed CLI’s profile-argument placement.

The bundle declares @deepseek-ai/dsh-agent-preset; legacy .agent-presets is ignored. Resolve customSkillDirs from package realpath. Preserve DSH 0.2.1 adaptations:

- engines.dsh independently determines compatibility including prereleases; the lower bound 0.2.0-rc.2 is positive.
- No persona suffix; cwd comes from working-directory context, model remains a variable.
- Keep time-context and tool-schedule rows.
- No backgroundMode/enableRunInBackground; subagents use activation and parent completion notifications.
- No agent-instructions dshHome override; skill-filesystem still has its own directory settings.
- No ctx.skills calls or new skill provider; the filesystem provider reads skills.

Perform desktop link development installation while the app is stopped, then materialize profile dependencies/bundles through its package manager. Use the dedicated hot-reload workflow for long-running web hosts; do not restart or create a substitute server without authorization.

## Official client and verification

The official client owns negotiation, discovery, notifications, images, reconnection. The adapter does not guarantee ttlMs=0 forces refresh. Modern protocol can subscribe; legacy relies on ordinary notifications. See current shim limits in the [daemon README](../../../../plugin/godot-mcp-unified/server-dotnet/README.en.md).

Other dependencies: serverName unique per scope but reusable across Agent scopes; mcp__<serverName>__<rawName> names; scrubbed credential-shaped parent variables and DSH_*; no prompts, resources provided elsewhere. Verify actual source/schema on upgrades rather than reproducing an old description.

Run npm run typecheck, npm run build, npm test, npm run verify for every change. Tests cover managed blocks, Config, mounts, path API, presets. Use --dump-config for composition and separate real-host calls/visual attachment checks; mocks do not prove GUI acceptance. See [release](release.en.md).
