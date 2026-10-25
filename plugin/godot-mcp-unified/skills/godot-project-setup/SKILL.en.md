---
name: godot-project-setup
description: Install, update, or reconnect Godot MCP Unified for a Godot 4 project; validate the Godot executable, copy and enable the editor addon, build the local MCP bridge, or create a project from the bundled templates. Use for setup, deployment, missing tools, missing addon, connection/auth/port failures, or a new Godot project.
---

Language: English | [中文](SKILL.md)

# Godot Project Setup

Use the bundled installer at ../../scripts/install-godot-project.ps1, resolved relative to this skill.

1. Resolve the exact target directory and confirm it contains project.godot, unless the user requested a new project from a bundled template.
2. Prefer the explicit Godot executable. The validated target for this package is Godot 4.7.2 stable (.NET/mono build); verify --headless --version rather than trusting the filename. Local paths belong in the repository-root `.env` under `GODOT_EXECUTABLE` (see `.env.example`), never in docs.
3. Run the installer with -ProjectPath, -GodotExecutable, and optional -Template. It builds the bridge, backs up an existing addon before replacement, copies addons/godot_mcp_toolkit, enables the plugin entry, and performs a headless editor load.
4. Confirm the editor output contains [MCPServer] listening, the machine registry points to the same canonical project path, and the bridge can authenticate. A port alone is not identity proof.
5. Confirm the client uses the bundled `godot-mcp-shim` stdio entry; remove old direct-HTTP entries after upgrading. The shim reads the daemon token and generates its session UUID.
6. Call `godot_connect(project="<absolute Godot project path>")`. Confirm expansion, then probe with `list_instances` and `scene_get_tree`. Activate `project_config` before `project_get_settings`.

Distinguish enabled configuration, a successful handshake, and a successful real-editor call. A programmatic client’s success does not prove the current chat refreshed its native list. Check the host’s discovery support and use its actual reconnect action; some hosts apply configuration changes in a new session.

Do not install into every discovered Godot project. Touch only the project or project set the user placed in scope. Do not overwrite an existing addon without the installer's timestamped backup.
