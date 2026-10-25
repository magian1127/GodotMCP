# godot-mcp-daemon and stdio shim

[中文](README.md) | English

This directory owns the current control plane. The supported host path is **host → stdio → godot-mcp-shim → loopback HTTP → godot-mcp-daemon → Godot editor/runtime**. HTTP is the shim backend; remove old direct-HTTP host entries after upgrading. Hosts do not configure a token: the shim reads the machine registry.

The current shim bootstraps before reading stdin; initialize/server-discover are not zero-dependency local replies. Ordinary version headers reuse initialize negotiation, while listen forces 2026-07-28. Full per-request metadata adaptation remains incomplete.

The x-godot-cwd header uses uri:-prefixed percent-encoded ASCII. The daemon decodes it consistently for sessions/tool surfaces, supporting Unicode, spaces, and literal percent signs while retaining legacy plain ASCII headers.

## Sessions and tool surface

Each shim generates a UUID at startup and sends it on every HTTP request as `x-godot-session`, with `x-godot-cwd` and `x-godot-shim-version`. Keys isolate state; the stable Bearer token authenticates access.

| State/count | Behavior |
| --- | --- |
| Not connected, including listen-only keys | Only `godot_connect` |
| Connected | Nine core tools plus groups activated for this key; extensions are additional |
| Built-in catalog | 92 unique names = core 9 + 83 unique group members; 17 groups contain 85 member slots |
| Table/implementation | `NodeToolTable.json` has 91 entries; `godot_connect` is daemon-only. The 34 C# tool objects are not 34 startup-visible tools |
| Availability | Engine-version union and `GODOT_MCP_UNSAFE` also filter the actual list |

Core 9: `godot_connect`, `discover_tools`, `list_instances`, `list_operations`, `log_read`, `capture_screenshot`, `scene_query`, `scene_get_tree`, `node_inspect`. Only the entry has the native `godot_` prefix; use actual host namespaces, normally `mcp__godot__<native name>` in DSH.

`godot_connect(project?, instance?, groups?)` selects a default target and initial groups. With no target it tries this key’s previous target, `project.godot` directly in cwd, then the only active instance; it does not scan recursively. An explicit offline project can still be stored with a TARGET_NOT_RUNNING hint. No candidate returns NO_CANDIDATE_PROJECT.

Ordinary calls resolve explicit `instance` (path or 12-character ID), then key default, then the only active instance, otherwise an error listing candidates. list_instances has no instance argument; list_operations supports a global or explicit-instance view. Pass instance when interleaving projects.

`discover_tools(request, activate, include_schemas, reset, refresh_extensions)` changes only this key’s groups. request is a group/keyword string or array; activate is boolean. No request browses, and include_schemas alone activates nothing. reset=true unloads all; reset=[...] unloads selected groups. See [GroupCatalogue.cs](src/godot-mcp-daemon/Groups/GroupCatalogue.cs) and [skill routing](../skills/godot-control/references/tool-routing.en.md). Ungrouped extensions appear after connecting, grouped extensions require activation; refresh_extensions=true rescans.

Each request gets a filtered collection; calls use the same visibility rules. Hidden tools return isError with diagnostics for readiness, activation, version support, or unknown names. Lists have stable order, entry first, `ttlMs:0` and `cacheScope:"private"`. Unready lists carry session-state/hint metadata. TTL is an intent and does not replace host refresh support.

## Notifications, reconnection, current limits

The daemon sends acknowledged and `notifications/tools/list_changed` on keyed `subscriptions/listen` streams, with each stream’s subscription ID. A key may have multiple streams. Invalid/missing listen headers return HTTP 400, capacity exhaustion 503. Other missing/invalid-header requests get a restricted face without creating orphan keys.

The shim bootstraps the daemon, detects restarts through connection errors and `x-godot-daemon-epoch` changes, and attempts to replay its latest connect and state-changing discover requests. A new shim has a new UUID and does not restore the old shim’s target/groups.

**The following reproduced limits remain; this is not a complete release acceptance claim:**

- Legacy shims now maintain an internal listen, suppress its ack, convert list_changed, and reconnect with invalidation. Modern host-owned listen replay remains incomplete; TTL=0 does not prove refresh in every host. Five-host/version notification and mixed-version matrices still require real-host acceptance.
- Composite calls reread the default target at each subcall, so switching the same key mid-call can cross projects. Sequence switches or pass explicit instance.
- connect group replacement changes the registry without clearing the old GroupService bucket; a browse can write it back. Use discover reset to unload and verify the list.
- include_schemas returns names alone for some resident group tools. Read the complete schemas from tools/list after activation.
- eager rollback can still use per-key buckets. A fresh debug unkeyed surface has only 34 tools rather than the designed 92; invalid-header handling and combined-switch priority also differ. These switches are not validated recovery paths.
- Replaying only the last incremental discover cannot reconstruct the final group set and can restore groups removed by a newer connect. Recheck target and groups after a restart.
- The C# daemon does not read legacy GODOT_MCP_READ_ONLY; compatibility UI state does not prove mutation blocking. Configure and verify host policy; see [security guidance](../addons/godot_mcp_toolkit/docs/security-recommendations.md).
- Changing only toolCallTimeoutMs does not remount existing DSH per-session connections. New sessions read the new value; see [DSH behavior](../../../adapters/dsh/deepseek-harness-godot_unified/docs/behavior.en.md).

Passing current tests does not resolve these limits. Record protocol-client success separately from native discovery in the current chat.

## Key lifetime and diagnostics

Active means at least one listen stream. Closing the last stream enters Dormant; completing an ordinary streamless request enters IdleNoStream. Streams or in-flight requests prevent recycling. Time uses a monotonic clock.

Current SessionKeyPolicy defaults: unconnected Dormant grace 30 seconds, unconnected IdleNoStream TTL 10 minutes, connected streamless keys (including Dormant) TTL **24 hours**, capacity **1024**. Capacity eviction considers only unconnected, streamless, non-in-flight IdleNoStream or grace-expired Dormant keys. Connected keys are not capacity-evicted. No capacity returns SESSION_LIMIT and a key summary; connected keys still expire by TTL.

The reaper scans every second. Summaries are written every five seconds and on selected state changes to `state/sessions.json` relative to the state directory. Fields include short key, groups, target, stream/in-flight counts, cwd, shim version; full keys are omitted. `godot-mcp-daemon --status` reads this file without acquiring the singleton lock. It is a potentially stale diagnostic snapshot, not a real-time key-count assertion.

## Timeouts and retries

| Layer | Current default |
| --- | --- |
| Daemon single wire operation | 30 seconds ordinary; 180 seconds for ToolCallTimeouts long names |
| Shim tools/call | 210 seconds, env override in milliseconds |
| Other short shim requests | 30 seconds |
| DSH host | 240 seconds, toolCallTimeoutMs setting |
| C# build wait | 180000ms default, clamped to 1000..200000ms, plus individual status-query time |

Long names include execute_code, node_call_method, editor_save_scene, editor_build_csharp/status/wait, editor_sync; [ToolCallTimeouts.cs](src/godot-mcp-daemon/Tools/ToolCallTimeouts.cs) is authoritative. Per-stage budgets are not a whole composite-call deadline. Extensions may declare timeout_ms.

tools/call timeouts are not retried. Retry only a connection failure known to occur before sending. Abandoned waits or uncertain transport failures return isError saying the operation may have executed; inspect state before resubmitting mutations.

Only one service process may run machine-wide. Startup checks older same-name processes and acquires a named mutex independent of path, port, and STATE_DIR. A later instance exits immediately with code 2, leaving the original alive. OS releases the mutex on exit/crash; --status reads diagnostics without starting a service. Process regressions run serially while the production daemon is stopped; isolated state does not permit parallel daemons.

## Configuration and process

| Environment variable | Default/purpose |
| --- | --- |
| GODOT_MCP_DAEMON_PORT | 6590, loopback backend |
| GODOT_MCP_DAEMON_STATE_DIR | Machine registry directory; lock, token, logs, summaries |
| GODOT_MCP_DAEMON_EXE | Shim daemon override; defaults to release sibling, may be a dll in development |
| GODOT_MCP_DAEMON_IDLE_SECONDS | Unset/0 disables exit; positive enables it |
| GODOT_MCP_TOOL_CALL_TIMEOUT | Shim tools/call milliseconds, default 210000 |
| GODOT_MCP_UNSAFE | 1 in the daemon process permits unsafe; off by default |
| GODOT_MCP_DEBUG_UNKEYED | Diagnostic switch, limits above |
| GODOT_MCP_TOOLFACE | eager rollback switch, limits above |
| GODOT_MCP_SESSION_GRACE_SECONDS | Test policy override; default 30 |
| GODOT_MCP_SESSION_NO_STREAM_TTL_SECONDS | Test policy override; default 600 |
| GODOT_MCP_SESSION_CONNECTED_TTL_SECONDS | Test policy override; default 86400 |

Defaults: Windows `%APPDATA%/godot-mcp-toolkit`; macOS `~/Library/Application Support/godot-mcp-toolkit`; Linux `$XDG_DATA_HOME/godot-mcp-toolkit` or `~/.local/share/godot-mcp-toolkit`. This is also the registry-consumption directory. Isolated editor/daemon tests need the same registry; changing only the daemon directory does not discover instances registered elsewhere.

The daemon stays resident by default. Requests, listen streams, or connected Godot instances prevent idle exit; with opt-in exit, all must end for the threshold to elapse. It does not start editors. Logs go to stderr and daemon.log; shim stdout is protocol-only. Tokens remain in the machine registry, outside host configuration and user environment variables.

Exit codes: 0 normal; 1 startup environment failure; 2 lock already held; 3 lock state unclear; 4 bind failure; other nonzero values are runtime exceptions.

## Build, validate, publish

Run from this directory:

```bash
dotnet build godot-mcp-daemon.slnx
dotnet test tests/godot-mcp-daemon.tests
dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"
dotnet publish src/godot-mcp-daemon -c Release -p:PublishProfile=<rid>
dotnet publish src/godot-mcp-shim -c Release -p:PublishProfile=<rid>
```

Tests use isolated state, random ports, real daemon/shim processes, and editor substitutes. Validate real-host list updates, real Godot calls, and GUI/rendering independently. A pass covers the asserted inputs and sequences.

Publish profiles live under `Properties/PublishProfiles/`. The daemon is a self-contained single file. The shim uses NativeAOT with a native target toolchain; cross-system or toolchain-free builds can use:
```text
-p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true
```
Windows AOT needs Visual Studio desktop C++, macOS Xcode, Linux clang/zlib development packages. RIDs: win-x64, linux-x64, osx-arm64, osx-x64.

Decisions: [keys](../../../docs/adr/0006-session-key-listen-lifetime.en.md), [tool face](../../../docs/adr/0007-tool-face-per-session.en.md), [default target](../../../docs/adr/0008-per-key-default-target.en.md).

## Platform acceptance history

Commit 7ac9b3a records native Apple Silicon/Godot 4.7.2 mono publishing, prior unit tests, and real-editor end-to-end checks. The new per-session face, machine-wide mutex, and cwd encoding have not been revalidated on a Mac. Recheck current code before release; historical platform evidence does not prove these additions.
