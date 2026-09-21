Language: English | [中文](tool-routing.md)

# Tool routing

Start with the always-visible tools, then load only the group that owns the missing operation.

| Need | Prefer |
|---|---|
| Current scene, nodes, properties | scene_get_tree, scene_query, node_inspect; load scene_spatial for layout measurements |
| Scene/node changes | scene_create, scene_create_node, node_manage, node_set_property, node_set_script, scene_delete_node, editor_save_scene |
| Scripts | Codex workspace reads/patches and script_check; load LSP groups for engine-backed diagnostics/symbols |
| Project configuration | project_get_settings; load project_config for writes/autoloads, node_advanced for node groups, and input_map for input mappings |
| Resources and files | resource_io, asset_ops, cleanup, user_data; resource_io includes folder_create |
| 2D authoring | path_editing, tilemap, tileset, tileset_edit, spriteframes, particles, navigation, procedural |
| 3D authoring | scene_create_3d from 3d_tools, particles, navigation, procedural, plus ordinary scene/node/property tools |
| UI and themes | node_advanced, theme, and ordinary node/property tools; load signals for signal operations |
| Animation and audio | animation_authoring, runtime_advanced, audio |
| Playtest/runtime | game_start, game_stop, capture_screenshot, runtime_inspect_node, input_simulate, node_set_property(channel="runtime"); load runtime_advanced for animation/time control |
| Debugger | log_read and script_check; load debugger for debug_inspect/breakpoint control and LSP groups when needed |
| Engine API discovery | load classdb and use classdb_query before guessing class names, methods, properties, enums, or defaults |
| Placeholder assets | read placeholder-assets.md and run the bundled scripts; use asset_import for supplied assets |

After external file changes, load editor_advanced and call editor_sync.

execute_code and node_call_method live in the default-disabled unsafe group. They are RCE-equivalent escape hatches; load them only when the server started with GODOT_MCP_UNSAFE=1, the user explicitly authorized the current operation, and no typed tool can express it.
