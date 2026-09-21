---
name: godot-mcp-unified
description: >
  Build, inspect, debug, and playtest Godot 4 projects through the Godot MCP Unified.
  Covers the current consolidated tool surface, on-demand group lifecycle, safety,
  and evidence-based completion.
when_to_use: >
  When an MCP-connected agent works on a Godot project through tools such as
  scene_get_tree, node_inspect, node_set_property, game_start, or discover_tools.
---

# Godot MCP Unified

Drive the live editor through the smallest capable tool surface and leave the project in a verified state.

## Tool-surface lifecycle

The startup surface contains 19 business tools plus `discover_tools`. Load a group only for the current work phase, track the groups you loaded, keep at most three active in ordinary work, and release groups the next phase no longer needs with `discover_tools(reset=[...])`. A cross-domain task may temporarily use up to five groups.

Use `discover_tools(refresh_extensions:true)` after adding, editing, or removing a project extension. On Godot 4.2, a changed existing extension may require an editor restart; inspect `extension_refresh.hint`.

The `unsafe` group is absent unless the server starts with `GODOT_MCP_UNSAFE=1`. Enable it only for an explicitly authorized operation that typed tools cannot express.

## Current routing

| Goal | Tool or group |
|---|---|
| Inspect a scene | `scene_get_tree`, `scene_query`, `node_inspect`; load `scene_spatial` for geometry |
| Edit nodes | `scene_create_node`, `node_manage`, `node_set_property`, `node_set_script`, `scene_delete_node` |
| Edit project files | Use the host's file read/patch tools, then load `editor_advanced` and call `editor_sync` |
| Project configuration | `project_get_settings`; load `project_config`, `node_advanced`, `input_map`, or `layer_naming` as needed |
| Assets/resources | Load `asset_ops`, `resource_io`, or `cleanup`; `project_delete` owns typed project-path deletion |
| Runtime/playtest | `game_start`, `runtime_inspect_node`, `input_simulate`, `capture_screenshot`, `log_read`; load `runtime_advanced` for time/animation control |
| GDScript intelligence | `script_check`; load `lsp_code_analysis`, `lsp_code_navigation`, or `classdb` |
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

Start with `game_start(wait_for_runtime=true)`. Capture a structured baseline with `runtime_inspect_node` and `log_read`. For timing-sensitive behavior, freeze and advance with `runtime_time_control`; send input as one bounded `input_simulate` sequence when possible. Use `capture_screenshot(target="runtime")` for visual evidence only. Always thaw and release held input during cleanup.

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
