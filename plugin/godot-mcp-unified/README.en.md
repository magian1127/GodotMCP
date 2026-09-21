# Godot MCP Unified

中文版：[README.md](README.md)

Godot MCP Unified is a local plugin for controlling Godot 4 editor projects and debugging game sessions. The capability surface includes 83 built-in tools, 159 operations, 31 on-demand groups, MCP resources, and five agent skills; only 19 business tools plus `discover_tools` are visible by default.

The control plane is a machine-level singleton **godot-mcp-daemon** (.NET self-contained single file, built from `server-dotnet/` in this repo): it exposes a loopback Streamable HTTP face to all hosts (default `127.0.0.1:6590`, Bearer auth) with on-demand spawn and self-heal (the Godot editor addon sidecar / the godot-mcp-shim for stdio hosts can both spawn it). MCP tool prose defaults to Chinese independently of client or editor language. After refreshing plugin caches, use a new Codex task or restart ZCode so its windows load the updated content.

| Client | Adapter | How it loads |
| --- | --- | --- |
| Codex | `.codex-plugin/` (plugin.json + mcp.json, url form) | Personal/repo plugin marketplace, or `codex plugins add` pointing at this directory |
| ZCode | `.zcode-plugin/plugin.json` + root `.mcp.json` (http form) | Local plugin marketplace whose root is the repository root (see root `marketplace.json`) |
| VS Code | `adapters/vscode/` (repo access layer: README + mcp.json example + install script) | Project-level `.vscode/mcp.json` written by the install script |
| DSH (stdio-only) | `adapters/dsh/godot-http-bridge.mjs` (node stdio ↔ daemon HTTP) | point the DSH godotServerDist setting at that file |

> To adapt a new client, add a small adapter directory/file at the plugin root — do **not** copy the whole plugin.

The validated Godot target: Godot 4.7.2 stable (.NET/mono build); the verified binary reports 4.7.2.stable.mono.official.ed1daf0bf.

## What is included

- Editor authoring: scenes, nodes, properties, signals, scripts, resources, project settings, input maps, autoloads, 2D/3D helpers, animation, audio, particles, navigation, TileSet/TileMap, placeholders, and assets.
- Language/debug: GDScript parse checks, Godot LSP diagnostics/navigation, debugger state and breakpoints, editor/game logs, crash context.
- Runtime/playtest: live node state, script variables, input sequences, screenshots, animation control, property changes, arbitrary-expression escape hatch, and bounded freeze/step/step-until time control.
- Security: loopback-only sockets, rotating session tokens, project path guards, read-only mode, tool annotations, audit logs, response caps, and untrusted-content envelopes.
- Skills: `godot-control`, `godot-debug`, `godot-playtest`, `godot-project-setup`, and `godot-mcp-extension`.

## Client install

### Codex

Codex reads the plugin from `.codex-plugin/plugin.json` (skills and the MCP server config live in `.codex-plugin/mcp.json`, url form pointing at the daemon HTTP face). Register this directory as a personal/repo marketplace plugin, or run `codex plugins add <absolute path>` and enable `godot-mcp-unified`; provide the Bearer token via the user environment variable `GODOT_MCP_DAEMON_TOKEN` (the installer writes it).

### ZCode

ZCode installs through the local plugin marketplace whose root is the repo root (`marketplace.json`), with the plugin source pointing at this directory. ZCode reads `.zcode-plugin/plugin.json` and the root `.mcp.json` (http form + Authorization header, same token).

Install/update: run `& ".\adapters\zcode\install-http-face.ps1"` from the repo root (publishes the daemon, spawns it, flips the registration; idempotent) or `& ".\adapters\zcode\link-zcode-plugin.ps1"` (junction deploy), then restart ZCode.

### VS Code

VS Code has no marketplace/plugin-package concept; it consumes a project-level `.vscode/mcp.json`. Run:

    & "<repo>\adapters\vscode\install-vscode-mcp.ps1" -ProjectPath "D:\Games\MyProject"

The script writes `.vscode/mcp.json` into the target project (http form pointing at the daemon HTTP face + Bearer token). Open the project in VS Code and the `godot` server (`mcp__godot__*`) is available. Full details in [`../../adapters/vscode/README.md`](../../adapters/vscode/README.md).

## Install into a Godot project

Run:

    & "<plugin-root>\scripts\install-godot-project.ps1" -ProjectPath "D:\Games\MyProject" -GodotExecutable "<Godot executable>"

The installer:

1. verifies Godot 4;
2. backs up any existing addon and `project.godot`;
3. installs `addons/godot_mcp_toolkit` and enables it (the sidecar auto-spawns the daemon on demand);
4. writes the project-level `.vscode/mcp.json` (http form) while preserving other servers;
5. launches a headless editor and requires a confirmed authenticated loopback listener.

Use `-Template empty`, `default`, `2d-platformer` or `3d-fps` to create a new project in an empty target directory.

## Verification

Automated acceptance (regression entry):

    dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"

Full suite: `dotnet test tests/godot-mcp-daemon.tests`.

## Architecture

All hosts (ZCode/Codex via http config, DSH via the stdio bootstrap bridge) connect to the machine-level singleton **godot-mcp-daemon**; the daemon discovers the matching project in the toolkit's machine registry and authenticates over localhost WebSocket to the editor; during playtest it additionally opens an authenticated runtime channel. With the plugin export hook enabled, game exports exclude the addon.

This is a locally maintained integration with no third-party runtime dependencies; upstream licenses and attributions live in the plugin's `LICENSE` and `addons/godot_mcp_toolkit/ATTRIBUTIONS.md`.
