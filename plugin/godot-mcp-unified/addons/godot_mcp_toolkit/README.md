# Godot MCP Unified

A Godot 4.2+ editor plugin that runs a localhost WebSocket server so an AI
coding assistant — Claude Code, or any MCP-compatible client — can create
scenes, edit scripts, inspect nodes, and run playtests inside your editor.
The plugin is half the stack: a machine-level daemon does the work, and your
assistant reaches it through a bundled **stdio shim** that the MCP client
spawns (`server-dotnet/publish/<rid>/godot-mcp-shim`). The shim bootstraps the
daemon when it is not already running, and the daemon connects back to this
WebSocket server through the machine registry.

Everything runs locally. Nothing leaves your machine.

## Quick start

1. **Enable the plugin:** Project Settings → Plugins → **Godot MCP Unified** →
   check **Active**. The MCP dock appears in the bottom panel and the Output
   log prints `[MCPServer] listening on 127.0.0.1:6550` (the port may land
   anywhere in 6550–6560; the dock names the live one). That line is the
   plugin's own WebSocket server inside the editor, which the daemon dials
   after discovering it via the registry.
2. **Write the client config:** run the bundled
   `scripts/install-godot-project.ps1`. It writes a project-root `.mcp.json`
   whose entry is `type: "stdio"` and points at the bundled shim
   (`server-dotnet/publish/<rid>/godot-mcp-shim`), which bootstraps the daemon
   on first use. No local Node.js is required anymore (the bundled Node bridge
   was retired with plugin 1.1.0), and no host-side auth token is needed — the
   shim reads the machine-level token itself.
3. **Connect:** launch your MCP client from the project root. It spawns the
   shim, which brings the daemon up and authenticates against it; the dock's
   peer count increments on connection.

If a step misfires, start with the bundled
[advanced configuration guide](docs/advanced_configuration.md).

## Where things are

- **The dock** (bottom panel, "MCP") — server status and the bound port,
  connected peers, the read-only toggle, audit-log viewer, response limits,
  and `.mcp.json` health with a one-click fix.
- **Project → Tools → Godot MCP Unified** — quick actions: write or open
  `.mcp.json`, regenerate the auth token, show the audit log, open the
  plugin's Project Settings. Also in the Command Palette (Ctrl+Shift+P).
- **Info / Help** (button in the dock) — connection details, the registered
  tool list, version compatibility, multi-instance guidance, and links,
  including a button that opens the shipped compatibility guide.

## Read-only mode

For supervised environments (classrooms, CI, demos), flip the dock's
read-only toggle (or set `GODOT_MCP_READ_ONLY=1` in the `.mcp.json` env
block). Every mutating tool is hidden from the agent. Turn it off and
reconnect the client to restore full access — the tool list is decided at
connect time.

## Documentation

Shipped with this addon, in `addons/godot_mcp_toolkit/docs/`:

- [compatibility.md](docs/compatibility.md) — supported Godot versions,
  per-tool and headless matrices, degraded behavior, the C# (.NET editor)
  requirement, export stripping, and how to disable the plugin safely.
- [security-recommendations.md](docs/security-recommendations.md) — the
  security model and recommended client-side permission rules.
- [extending.md](docs/extending.md) — register your own MCP tools in
  GDScript (C# supported), with hot-reload, timeouts, and cancellation.
- [multi-instance.md](docs/multi-instance.md) — several editors or git
  worktrees side by side.
- [advanced_configuration.md](docs/advanced_configuration.md) — ports,
  limits, environment variables, macOS specifics.

The addon also bundles [agent skills](docs/companion-skills.md) — a workflow
skill and an extension-authoring skill — in
`addons/godot_mcp_toolkit/CompanionSkills/`; copy a skill folder into your
client's skills directory to use them.

The complete generated tool reference is stored locally in the integrated
plugin at `server/docs/tool-reference/README.md`.

## Uninstalling

Disable via Project Settings → Plugins (a dialog offers to clean up
`.mcp.json`), or remove the addon folder. If you delete the folder while the
plugin is still enabled, the cleanup step cannot run — delete `.mcp.json`
from your project root yourself. The shipped compatibility guide covers why
you should not disable the plugin by hand-editing `project.godot`.

## License

MIT: the full text travels with the addon in [LICENSE](LICENSE). Third-party
attributions are in [ATTRIBUTIONS.md](ATTRIBUTIONS.md).

## Trademarks

Godot and the Godot logo are trademarks of the Godot Foundation. This add-on is
an independent community project with no affiliation with or endorsement from the
Foundation, and it is not an official Godot product. The name describes what the
add-on runs on: the Godot Engine.
