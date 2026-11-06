# User-visible behavior

[中文](behavior.md) | English

## Environment and mounting

DSH range: `>=0.2.0-rc.2 <0.3.0-0`. engines.dsh includes prereleases; 0.2.0-rc.2 itself is supported. See package.json for Node requirements.

By default the official mcp-client mounts in a Godot Agent scope on creation or a blank-session preset switch. Switching away, disposal, or plugin teardown releases it. DSH locks presets after the first turn. Other sessions have no connection/tools. Subagents inherit the preset and get separate connections.

**Two transports** (`transportMode`):

- `auto` (default) / `http`: connect straight to the daemon's `streamable-http` face (`http://127.0.0.1:6590/`). No per-session shim process exists; the machine runs only the daemon, and subagents add one HTTP connection each. Unavailable endpoint (port down, no token, daemon older than 2026-07-28) falls back to stdio; forced `http` logs a warning when it falls back.
- `stdio`: `host → stdio → shim → HTTP → daemon`, one 12–13 MB shim process per session. Kept for older daemons or scenarios needing the shim's protocol adaptation.

**Daemon session key scope** (`sessionKeyScope`):

- `session` (default): the key derives from the DSH session id (deterministic UUID v5 shape). Every session starts with one tool; sessions stay independent; reconnecting the same session keeps its activated tool face and default target.
- `project`: the key derives from session cwd. Sessions of one project share the default target and groups, so a **new session inherits an already-expanded tool face**. Two sessions of the same project changing the default target affect each other; pass instance when working in parallel.

injectLegacyMode=true mounts one host-level shared connection with shared key, target, and groups, suppressing per-session mounts. On the direct path it is a single host HTTP connection with no child process. This “original mode” setting is separate from legacy MCP protocol negotiation.

failOnStartupError=false, with at most ten seconds readiness grace. Failures warn. The official client backs off from 500ms to 30s and removes tools after ten consecutive failures; a stable connection resets the budget. Recover by creating a session, changing the effective server entry to remount, or switching a blank session’s preset. Prompt switches do not remount.

## Project and tool surface

1. Read serverDist from `$DSH_HOME/godot/paths.json`, falling back to godotServerDist. Empty means guidance without a mount.
2. On the direct path no shim exists: the session cwd travels in the `x-godot-cwd` header (`uri:` + RFC3986 percent-encoding). On stdio it is the shim child's working directory. The adapter injects no GODOT_MCP_PROJECT_PATH and does not route from old projects entries.
3. Initially only `mcp__godot__godot_connect` exists. Explicit project/instance selects a target; omission tries key default, project.godot directly in cwd, then the only active instance. The daemon does not launch editors.
4. Connected sessions see nine core tools plus their active groups. request carries group/keyword strings, activate is boolean; include_schemas alone activates nothing. Names/schemas come from the latest server tools/list.
5. The native entry is godot_connect. Other names such as scene_get_tree have no common godot_ prefix. The host adds mcp__<serverName>__, normally godot.

The shim now maintains/reconnects a legacy notification stream; modern host-owned listen replay remains incomplete. Last-frame replay cannot preserve every sequence of incremental group changes. TTL=0 does not guarantee host refresh. Check actual notification/discovery support, reconnect using the host’s supported action, and redeclare needed groups. The [daemon README](../../../../plugin/godot-mcp-unified/server-dotnet/README.en.md) owns detailed limits.

include_schemas=true returns names alone for some resident group tools. Obtain full schemas from tools/list after activation. Unload using discover reset and verify the result; pass instance when interleaving projects.

## Settings and timeouts

| Setting | Default | Behavior |
| --- | --- | --- |
| injectLegacyMode | false | Shared host connection / default per Godot session |
| promptGuidance | true | Workflow section injection |
| zhPrompt | false | Chinese section; no server-description translation |
| transportMode | auto | auto = direct when possible, else the shim; http = force direct (falls back with a warning); stdio = always the shim |
| sessionKeyScope | session | session = per-session isolation (new session starts with one tool); project = shared target/groups (new sessions inherit the tool face) |
| godotServerDist | Empty | Fallback for absent stored serverDist; the direct path derives the daemon from its directory |
| toolCallTimeoutMs | 240000ms | Positive number/numeric string, rounded, capped at 900000; invalid uses default |

Mount identity now always comes from `mountConfigKey`: the stdio branch covers entry/args/env/cwd/timeout, the http branch covers url/headers/timeout, and the two prefixes differ — **transport switches, token rotation, key-scope changes, and timeout changes all remount**. A saved form value does not prove an existing connection applied it.

Defaults: host 240s > shim tools/call 210s > daemon long single-wire operations 180s; ordinary daemon calls 30s. Composite stage budgets are not a 180s whole-call deadline. Inspect state after uncertain timeouts/transport loss before resubmitting.

Server instructions enter the host prompt and currently require connecting first. Prompts templates are unsupported. Images become attachments when host/model capabilities permit, otherwise diagnostics are shown. Headless runs do not prove visual output.

## CLI and path API

Install still requires --project to validate project.godot, but persists serverDist with empty projects rather than a cwd binding. --cwd remains compatibility input without runtime target routing. --link optionally installs the bundle; desktop skips bundle/patch operations. Managed historical static rows are cleaned without touching outside content.

`GET/POST /godot-config/api/path-settings` and PathStore remain:

- GET returns serverDist, projects:[], currentCwd:null, currentProjectPath:null.
- POST persists serverDist only and ignores projects; compatibility shape remains, mapping is retired.
- Loopback peer plus Host/Origin/sec-fetch-site checks; wrong method 405, over 8MiB 413, invalid JSON 400.
- The page posts only on Save. PathStore rereads external edits by mtime; fallback serverDist is read through a getter.
- serverDist is DSH-home-global. Targets/groups belong to the shim session.

## Failures and recovery

| Symptom | Check/recovery |
| --- | --- |
| No Godot tools, configuration guidance | Set an executable shim entry and verify preset mounting; create a session if necessary |
| Only godot_connect | Normal unconnected state; call it and check the returned target |
| A `godot-mcp-shim` process per session | You are on the stdio fallback: check port 6590, `daemon-token` in the state directory, and whether the daemon supports 2026-07-28; `transportMode=stdio` means it was chosen explicitly |
| Old list after connect/activation | Check notification/discovery support and reconnect; TTL alone is not refresh evidence |
| TARGET_NOT_RUNNING / NO_INSTANCE | Open the reported editor, then retry a read |
| SESSION_HEADER_MISSING and related errors | Check stdio shim and old HTTP entries; repeated connect does not fix headers |
| Tools removed after long disconnection | Fix entry/connection cause and recreate session; prompt switches do not reconnect |
| Saved timeout not applied | Confirm the remount happened (timeout is in the identity key); otherwise create a new session |
| A new session inherits the previous tool face | Expected with `sessionKeyScope=project`; switch back to `session` for isolation |
| No scene mutation tools | Check scene_edit, then version/read-only gates |
| Godot preset missing | Check the preset-godot bundle declaration; the legacy .agent-presets directory is no longer read. If the row exists and is active, verify that every preset row `name` resolves in the current runtime — one broken row (such as the `dsh-tool-subagent-control/list-agents` subpath removed in 0.2.2-rc.1) marks the whole preset broken and clients list healthy presets only. Restart the application or reload that bundle after editing the patch |
| Duplicate serverName or missing official client | Check profile modules and mount scope; log error type without configuration |

## Data boundary

This package writes paths.json and cleans managed historical patch blocks. It does not edit Godot source. On the direct path it reads the daemon token from the machine state directory (Windows `%APPDATA%/godot-mcp-toolkit`, overridable with `GODOT_MCP_DAEMON_STATE_DIR`; port via `GODOT_MCP_DAEMON_PORT`) and keeps it in in-memory request headers only — **never in config, logs, session records, or snapshots**; diagnostics record fixed strings and error type names only. On stdio the daemon reads editor tokens and the shim reads the daemon token. The official client scrubs credential-shaped parent variables and DSH_*; explicit adapter env is currently empty.

Old tools-cache.json is ignored. Generic tool debugging moved to deepseek-harness-project-mcp; no conversation.view or /godot-workbench API remains here. Resources come from host dsh-mcp-resources; annotations do not become DSH permissions.
