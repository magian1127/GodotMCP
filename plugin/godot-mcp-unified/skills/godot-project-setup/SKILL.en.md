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
5. Run one read-only MCP probe such as scene_get_tree or project_get_settings.

Do not install into every discovered Godot project. Touch only the project or project set the user placed in scope. Do not overwrite an existing addon without the installer's timestamped backup.
