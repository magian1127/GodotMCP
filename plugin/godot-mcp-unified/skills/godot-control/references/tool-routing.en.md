Language: English | [中文](tool-routing.md)

# Tool routing

## Connect this session

Before connecting, only `godot_connect` exists. Call `godot_connect(project="<absolute Godot project path>", groups=[...])`, then start with the nine core tools. Without a project, resolution uses this session’s default target, `project.godot` directly in cwd, then the only active instance; it does not scan recursively. Ordinary tools resolve explicit `instance` before the session default, then the only active instance.

Use names from the latest tool list. The entry is `godot_connect`; other native names such as `scene_get_tree` have no common `godot_` prefix. Hosts may add a namespace: DSH normally shows `mcp__godot__godot_connect` and `mcp__godot__scene_get_tree`.

## Load by task

Activate with `discover_tools(request=[group names], include_schemas=true)`. `activate=false` only browses; `reset=[group names]` unloads selected groups and `reset=true` unloads all groups. `include_schemas=true` alone activates nothing. Track groups loaded for this phase and release those the next phase no longer needs.

| Need | Core tools or group to activate first |
| --- | --- |
| Inspect scenes/nodes | Core `scene_get_tree`, `scene_query`, `node_inspect` |
| Create/edit nodes and scenes, attach scripts, save, inspect spatial layout | `scene_edit` |
| Refresh external edits, wait for imports, editor windows/dialogs | `editor_ops` |
| GDScript checks, LSP, engine API lookup | `code_intel`: `script_check`, `lsp_*`, `classdb_query` |
| C# build, status, wait | `build_csharp` |
| Project settings, autoloads, layer names, input maps | `project_config` |
| Resource I/O, asset lookup/import, directory creation | `assets` |
| Close scenes, delete project files | `cleanup` |
| TileMap and TileSet | `tilemap` |
| Keyframes, AnimationTree, SpriteFrames | `animation` |
| Particles, gradients/curves/noise, Theme, Path2D, texture collisions | `art_fx` |
| 3D scenes and navigation | `world_3d`; general node editing remains in `scene_edit` |
| Inspect/connect/emit signals | `signals`; runtime emission also belongs to `playtest` |
| Start/stop games, input, runtime inspection, time/animation control | `playtest`; screenshots and logs are core |
| Debugger, breakpoints, continue | `debugger` |
| Audio buses | `audio` |
| User-data reads, save writes/deletes | `user_data` |
| Arbitrary code/method calls | `unsafe`: `execute_code`, `node_call_method` |
| Placeholder assets | [placeholder-assets.en.md](placeholder-assets.en.md); import existing assets with `assets` |

Use the static description and catalog of `discover_tools` for current group names. Tool and group names differ: `tileset_edit`, `input_map_edit`, `theme_edit`, and `classdb_query` belong to `tilemap`, `project_config`, `art_fx`, and `code_intel` respectively.

## Schemas and recovery

- Use schemas in the host’s latest tool list. Currently `include_schemas=true` returns names alone for some resident group tools; inspect `tools/list` after activation rather than guessing.
- Confirm that the host refreshed its tool list after connecting/activating. `ttlMs=0` does not guarantee every host reloads. A programmatic client’s success does not prove tools were mounted in the current chat. Use the host’s actual reconnect action or a new session when necessary.
- Recheck targets/groups after a daemon restart. The shim currently replays only the most recent connect/discover requests, which cannot preserve an arbitrary sequence of group changes; host listen streams are not automatically restored. Reconnect and activate this task’s groups.
- Adjust existing groups with `discover_tools(reset=...)`. A browse can currently restore an old group bucket after `godot_connect(groups=...)` replaces groups; verify the actual list after unloading.
- Pass `instance` when interleaving projects. Sequence target switches and composite calls within one session to avoid crossing projects mid-call.
- For `SESSION_HEADER_MISSING` and related header errors, inspect the stdio shim configuration; repeated connect calls cannot repair missing transport headers. For `TARGET_NOT_RUNNING`, start the editor at the reported project path and retry a read.
- Inspect state after a timeout or transport interruption before resubmitting a mutation. `unsafe` requires daemon startup with `GODOT_MCP_UNSAFE=1`, current user authorization, and an operation that dedicated tools cannot express.
