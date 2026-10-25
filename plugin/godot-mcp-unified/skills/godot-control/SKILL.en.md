---
name: godot-control
description: Control and edit Godot 4 projects through the Godot MCP Unified editor/runtime bridge. Use for scenes, nodes, scripts, resources, project settings, UI, 2D/3D authoring, or game-development changes. Route reported failures to godot-debug, gameplay verification to godot-playtest, installation to godot-project-setup, and new MCP tools to godot-mcp-extension.
---

Language: English | [中文](SKILL.md)

# Godot Control

First select this session’s target with `godot_connect(project="<absolute Godot project path>")`. Before connecting, only the entry tool exists; afterward nine core tools are available. Activate other capabilities with `discover_tools(request=[group names], include_schemas=true)`. Pass `instance` when interleaving projects. See [tool routing](../godot-control/references/tool-routing.en.md) for schemas and recovery.

Drive the live editor through MCP and leave the project in a verified state.

## Establish the target

1. Locate the intended project.godot on disk. Treat registry entries as discovery hints, not proof that a project still exists.
2. Confirm addons/godot_mcp_toolkit/plugin.cfg exists and that the matching editor instance is connected. If either is missing, use godot-project-setup.
3. Read the current scene/project state before changing it. Preserve unrelated user edits and open-scene state.

## Work through the smallest capable surface

Start with the core tools after connecting. Activate this phase’s groups with `discover_tools(request=[group names], include_schemas=true)` and track groups this skill loaded. `include_schemas=true` alone browses without activating. Keep at most three groups active, or five temporarily for a cross-domain task. Release groups the next phase no longer needs with `discover_tools(reset=[...])`.

- Prefer a purpose-built tool over execute_code.
- Use Codex workspace file tools to read and patch scripts; load `editor_ops` for `editor_sync` and `code_intel` for `script_check`.
- Use a tool's batch input for repeated operations instead of many single calls.
- Send project files as canonical res:// paths and save edited scenes explicitly.
- For create operations, choose if_exists deliberately. Default to returning the existing object; replace only when the requested final state requires replacement.
- Let the toolkit own scene serialization and UndoRedo. Do not hand-edit .tscn files while the editor is managing that scene.

Read [placeholder-assets.md](references/placeholder-assets.md) for deterministic placeholder textures or sounds. Read [testing.md](references/testing.md) when the user asks to write tests.

## Close the loop

Read [verification.md](references/verification.md) and verify in proportion to the change. A normal authoring change is complete only when the affected scene/resource is saved, changed scripts validate, the project can run or the headless limitation is explicit, and observed editor/runtime state matches the request.

For destructive operations, arbitrary expressions, external assets, or project-setting changes, read [security.md](references/security.md) before the call. The unsafe group is discoverable only when the server starts with GODOT_MCP_UNSAFE=1; use that switch only for the currently authorized operation when typed tools cannot express it.
