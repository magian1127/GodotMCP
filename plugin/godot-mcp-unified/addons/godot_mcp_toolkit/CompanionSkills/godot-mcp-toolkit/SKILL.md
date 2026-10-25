---
name: godot-mcp-unified
description: >
  Build, inspect, debug, and playtest Godot 4 projects through the Godot MCP Unified.
  Covers the current consolidated tool surface, on-demand group lifecycle, safety,
  and evidence-based completion.
---

# Godot MCP Unified

Drive the live editor through the smallest capable tool surface and leave the project in a verified state.

## Tool-surface lifecycle

Call `godot_connect(project="<absolute Godot project path>", groups=[...])` first. Before connecting, only the entry exists; afterward the surface contains nine core tools plus this session’s active groups, from 17 built-in groups. Load by phase with `discover_tools(request=[group names], include_schemas=true)`, keep at most three groups active in ordinary work or five temporarily, and release with `discover_tools(reset=[...])`. `include_schemas=true` alone does not activate.

Use `discover_tools(refresh_extensions:true)` after adding, editing, or removing a project extension. On Godot 4.2, a changed existing extension may require an editor restart; inspect `extension_refresh.hint`.

The `unsafe` group is absent unless the server starts with `GODOT_MCP_UNSAFE=1`. Enable it only for an explicitly authorized operation that typed tools cannot express.

See [instances and sessions](../../docs/multi-instance.md) for discovery, recovery, and current implementation limits. `include_schemas=true` currently returns names alone for some resident tools; use the latest list’s schemas after activation. Pass `instance` when interleaving projects.

## Current routing

| Goal | Tool or group |
|---|---|
| Inspect a scene | `scene_get_tree`, `scene_query`, `node_inspect`; load `scene_edit` for geometry |
| Edit nodes | `scene_create_node`, `node_manage`, `node_set_property`, `node_set_script`, `scene_delete_node` |
| Edit project files | Use the host's file read/patch tools, then load `editor_ops` and call `editor_sync` |
| Project configuration | Load `project_config` for settings, autoloads, layer names, input maps; `scene_edit` for node groups |
| Assets/resources | `assets`; `cleanup` for deletion and `user_data` for user data |
| Runtime/playtest | `game_start`, `runtime_inspect_node`, `input_simulate`, `capture_screenshot`, `log_read`; load `playtest` for time/animation control |
| GDScript intelligence | Load `code_intel` for `script_check`, `lsp_*`, and `classdb_query` |
| Debugger | `log_read`; load `debugger` for `debug_inspect`, breakpoints, and continue |
| 3D/tiles/animation | Load the narrow domain group; use `scene_create_3d`, `tileset_edit`, and `spriteframes_create` rather than retired per-action tools |

Read [input-events.md](references/input-events.md) only for detailed `input_simulate` event payloads. Read [parallel-sessions.md](references/parallel-sessions.md) when several agents or editor instances share a project. Read [type-wrappers.md](references/type-wrappers.md) when sending Godot engine values or nested resources.

## Authoring loop

1. Resolve the exact project and inspect the current scene/project state.
2. Load only the missing group and perform the smallest typed mutation. Batch repeated edits.
3. Save edited scenes explicitly. After host-side file changes, call `editor_sync`.
4. Run `script_check` for changed GDScript and `lsp_diagnostics(scope="project")` when the change crosses files.
5. Run or playtest when behavior changed. Use structured state/log evidence for logic and screenshots for appearance.
6. Restore runtime state, stop only sessions you started, and release no-longer-needed groups.

## Deterministic playtests

Activate `playtest`, then start with `game_start(wait_for_runtime=true)`. Capture a structured baseline with `runtime_inspect_node` and `log_read`. For timing-sensitive behavior, freeze and advance with `runtime_time_control`; send input as one bounded `input_simulate` sequence when possible. Use `capture_screenshot(target="runtime")` for visual evidence only. Always thaw and release held input during cleanup.

## Safety and errors

- Preserve unrelated user edits and active scenes.
- Treat `project_delete`, recursive deletion, project settings, external asset import, `node_call_method`, and `execute_code` as consequential.
- Use `project_delete(dry_run=true)` before a deletion whose inferred kind or scope is not obvious.
- Keep file paths inside `res://` or the intended `user://` scope and retain server-side guards.
- `GAME_NOT_RUNNING`: inspect `log_read(channel="auto")`, fix errors, then start again.
- `NOT_FOUND`: inspect `scene_get_tree`, `scene_query`, `asset_query`, or the workspace before retrying.
- `COMPILATION_FAILED`: call `editor_sync`, then `script_check` and `log_read(channel="editor")`.
- A timeout after a serialized mutation may mean the mutation already ran; inspect state before retrying.

Completion requires saved editor state, clean relevant diagnostics, observed runtime/editor behavior matching the request, and cleanup of temporary sessions and loaded groups.
