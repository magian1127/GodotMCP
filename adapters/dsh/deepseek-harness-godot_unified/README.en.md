# deepseek-harness-godot_unified

[中文](README.md) | English

Connect Godot MCP Unified to DSH. This adapter selects which sessions mount which server. The host’s `@deepseek-ai/dsh-mcp-client` owns negotiation, discovery, notifications, reconnection, and image projection; this package does not implement a client.

## Current behavior

- By default only Godot-preset sessions mount a connection. Each Agent scope owns one stdio shim and its own key, target, and groups; subagents get separate connections. Names follow `mcp__<serverName>__<native name>`; the default entry is `mcp__godot__godot_connect`.
- Only godot_connect appears initially; after calling it, nine core tools and this key’s active groups are visible. Seventeen built-in groups come from the server; no tool table is hardcoded or cached here.
- The adapter passes session cwd, with no project-path environment injection. Connect selects project/instance; omission uses the key default, project.godot directly in cwd, then the only active instance.
- paths.json supplies global serverDist only. Old projects entries and godotProjectPath do not route calls; compatibility classes/API shapes remain. CLI install still requires --project for validation, but does not bind it to a session.
- Mounting starts during session creation, separately from selecting a Godot project. Blank sessions may switch presets; DSH locks them after the first turn. A session waits at most ten seconds for readiness; errors warn, with failOnStartupError=false.
- injectLegacyMode mounts one host-level shared connection. All sessions share its key, target, and groups; this mode has no per-session isolation.
- The host defaults to **240000ms**, configured by toolCallTimeoutMs; shim defaults to 210 seconds, daemon long single-wire operations to 180 seconds. Changing timeout alone does not remount existing per-session connections; create a new session. Prompt switches update text only.

Notification delivery and recovery depend on protocol support. The shim now maintains/reconnects a legacy stream; modern host-listen restoration after restart remains incomplete. TTL=0 does not prove automatic refresh. See [behavior](docs/behavior.en.md) and the [daemon README](../../../plugin/godot-mcp-unified/server-dotnet/README.en.md).

## Install and upgrade

Requirements: DSH `>=0.2.0-rc.2 <0.3.0-0`, Node.js `^22.19.0 || >=24.0.0`, and a Godot project with the toolkit addon. Publish matching daemon/shim binaries together and point serverDist to the shim:

```sh
node bin/dsh-godot.mjs install --profile web   --project "<Godot 项目>"   --godot-mcp-root "<GodotMCP 工作区>"   --link "<接入层目录>"

node bin/dsh-godot.mjs install --profile web   --project "<Godot 项目>"   --server-dist "<GodotMCP 工作区>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim.exe"
```

CLI resolution: --server-dist > GODOT_MCP_SERVER_DIST > --godot-mcp-root / GODOT_MCP_ROOT. At runtime, the host reads serverDist from paths.json, then falls back to godotServerDist.

Remove old direct-HTTP entries and handwritten static mcp-client rows after upgrading. Install cleans managed blocks while preserving user content outside them, and writes serverDist with empty projects. The historical godot-http-bridge is not the supported entry. Old --read-only/--unsafe/--rate-limit/port/--timeout-ms row options are no longer written; configure host timeout in the plugin page.

With --profile desktop, CLI skips bundle installation and patch operations while updating the shared path file. Load bundles through desktop plugin management; perform manual link development installation while the application is stopped. See [development](docs/development.en.md).

## Use

1. Save the bundled shim entry on the plugin page. Empty server configuration produces guidance without mounting.
2. Create a Godot-preset session and open the intended Godot editor.
3. Call godot_connect(project="<absolute Godot project path>"), confirm expansion, then inspect with list_instances and scene_get_tree.
4. Activate phase groups with discover_tools(request=[group names], include_schemas=true), release with reset=[...]. If only names are returned, inspect schemas in the latest tools/list.

A project .mcp.json does not prove DSH mounted the plugin. A programmatic call does not prove native tool discovery in the current chat. Use the host’s actual reconnect mechanism or a new session when necessary.

The preset’s dsh-skill-filesystem row loads six skills from package-local skills/, a junction to plugin source in development. Legacy ~/.dsh/.agent-presets/godot/skills/ is retired. Source edits require no skill build, but confirm loaded sessions reread them. Binary changes require publishing and reconnection.

## Settings and local data

| Setting | Default | Purpose |
| --- | --- | --- |
| injectLegacyMode | false | Shared host connection; otherwise per Godot session |
| promptGuidance | true | Workflow prompt injection |
| zhPrompt | false | Prompt language, not server descriptions |
| godotServerDist | Empty | Fallback when paths.json has no serverDist |
| toolCallTimeoutMs | 240000 | Milliseconds; positive number/numeric string; capped at 900000 during resolution; invalid values use default |

GET/POST /godot-config/api/path-settings persists serverDist only. GET returns empty projects; POST ignores projects. Access is loopback and same-origin, with 405/413/400 for method, size, JSON failures. Retained project fields are compatibility shapes, not active routing.

CLI and UI write machine-local `$DSH_HOME/godot/paths.json`. The host rereads by mtime; a changed file is not proof of remounting. The daemon/shim owns credentials; the adapter does not handle tokens.

## Verify and uninstall

```sh
node bin/dsh-godot.mjs status --profile web
dsh --profile web --dump-config
npm run typecheck
npm run build
npm test
npm run verify
node bin/dsh-godot.mjs uninstall --profile web
dsh plugin --profile web remove deepseek-harness-godot_unified
```

Status is installation/registry diagnostics, not a substitute for real calls. Generic tool debugging moved to deepseek-harness-project-mcp; this package has no conversation tab. Prompts are unsupported, resources come from host dsh-mcp-resources, and tool annotations do not become DSH permissions.

See [AGENTS.md](AGENTS.md), [release](docs/release.en.md). MIT License.
