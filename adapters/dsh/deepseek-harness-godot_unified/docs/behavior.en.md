# User-visible behavior

[中文](behavior.md) | English

## Environment and mounting

DSH range: `>=0.2.0-rc.2 <0.3.0-0`. engines.dsh includes prereleases; 0.2.0-rc.2 itself is supported. See package.json for Node requirements.

By default the official mcp-client mounts in a Godot Agent scope on creation or a blank-session preset switch. Switching away, disposal, or plugin teardown releases it. DSH locks presets after the first turn. Other sessions have no connection/tools. Subagents inherit the preset and get separate shims.

injectLegacyMode=true mounts one host-level shared connection with shared key, target, and groups, suppressing per-session mounts. This “original mode” setting is separate from legacy MCP protocol negotiation.

failOnStartupError=false, with at most ten seconds readiness grace. Failures warn. The official client backs off from 500ms to 30s and removes tools after ten consecutive failures; a stable connection resets the budget. Recover by creating a session, changing the effective server entry to remount, or switching a blank session’s preset. Prompt switches do not remount.

## Project and tool surface

1. Read serverDist from `$DSH_HOME/godot/paths.json`, falling back to godotServerDist. Empty means guidance without a mount.
2. Shim cwd is the session cwd. The adapter injects no GODOT_MCP_PROJECT_PATH and does not route from old projects entries.
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
| godotServerDist | Empty | Fallback for absent stored serverDist |
| toolCallTimeoutMs | 240000ms | Positive number/numeric string, rounded, capped at 900000; invalid uses default |

The per-session configKey compares entry, empty compatibility project field, and cwd. **Timeout-only changes do not remount existing connections**; new sessions read them. Shared mode uses mountConfigKey including timeout and does remount. A saved form value does not prove an existing connection applied it.

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
| Old list after connect/activation | Check notification/discovery support and reconnect; TTL alone is not refresh evidence |
| TARGET_NOT_RUNNING / NO_INSTANCE | Open the reported editor, then retry a read |
| SESSION_HEADER_MISSING and related errors | Check stdio shim and old HTTP entries; repeated connect does not fix headers |
| Tools removed after long disconnection | Fix entry/connection cause and recreate session; prompt switches do not reconnect |
| Saved timeout not applied | Known per-session identity gap; create a new session |
| No scene mutation tools | Check scene_edit, then version/read-only gates |
| Godot preset missing | Check preset-godot bundle declaration; legacy .agent-presets is ignored |
| Duplicate serverName or missing official client | Check profile modules and mount scope; log error type without configuration |

## Data boundary

This package writes paths.json and cleans managed historical patch blocks. It does not edit Godot source or handle credentials. The official client scrubs credential-shaped parent variables and DSH_*; explicit adapter env is currently empty. The daemon reads editor tokens and the shim reads the daemon token.

Old tools-cache.json is ignored. Generic tool debugging moved to deepseek-harness-project-mcp; no conversation.view or /godot-workbench API remains here. Resources come from host dsh-mcp-resources; annotations do not become DSH permissions.
