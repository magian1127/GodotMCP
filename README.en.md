# Godot MCP Unified workspace

[中文](README.md) | English

A plugin ecosystem providing an MCP (Model Context Protocol) control plane for Godot 4: AI coding assistant sessions (ZCode, Codex, DSH, VS Code) drive multiple Godot projects and instances through a machine-level singleton daemon — scene, node, script and resource editing and querying, deterministic playtesting, screenshot verification and LSP diagnostics.

## Core capabilities

- **83 built-in MCP tools**: 19 resident + 1 `discover_tools` meta tool at startup; 31 on-demand groups (64 tools) activated on demand to keep context cost bounded.
- **Deterministic playtesting**: run the game, simulate input, inspect runtime state, control time and step frames, screenshots returned inline.
- **Multi-instance**: a machine-level registry auto-discovers every running Godot instance; the daemon multiplexes them so several projects can be developed in parallel.
- **Local-first security**: loopback-only listening with Bearer token auth; game export builds strip the plugin automatically.
- **Multi-client access**: Codex, ZCode, DSH and VS Code each have an adapter layer; MCP tools are uniformly exposed under the `godot` server key (`mcp__godot__<tool>`; the DSH facade uses `godot_<tool>`).

## Quick start

1. **Install the plugin into a Godot project** (builds the bridge, backs up and replaces the addon, enables the plugin, verifies headless):

       & "<repo>\plugin\godot-mcp-unified\scripts\install-godot-project.ps1" -ProjectPath "<Godot project>" -GodotExecutable "<Godot executable>"

2. **Connect your AI client**: see the channel overview in [`adapters/README.md`](adapters/README.md) (one entry per client: Codex / ZCode / DSH / VS Code).
3. **Optional**: copy `.env.example` to `.env` and fill in your local Godot path so scripts run without parameters (the file is git-ignored).

Prerequisites: Godot 4.x (.NET/mono build) with the target project's editor running. Full prerequisites and troubleshooting live in each client's adapter docs.

## Repository map

| Directory / file | Responsibility | Details |
| --- | --- | --- |
| `plugin/godot-mcp-unified/` | Single plugin source: editor/runtime addon, C# daemon and shim, six skills, project templates | [README](plugin/godot-mcp-unified/README.md) |
| `adapters/` | Access-management layer per AI client (channel overview, install and link scripts) | [README](adapters/README.md) |
| `docs/` | Maintenance docs, architecture decision records (ADR), client installation guides | [Index](docs/README.md) |
| `test-project/` | Godot 4.7.2 Mono acceptance project (addon junctions to the single source) | [README](test-project/README.md) |
| `scripts/` | Repository-level development/verification tooling (doc consistency, config-discovery regression) | [README](scripts/README.md) |
| `marketplace.json` | Local plugin marketplace manifest (marketplace root = repository root) | — |

## Documentation

- **Documentation index**: [`docs/README.md`](docs/README.md)
- **Agent/contributor guide**: [`AGENTS.md`](AGENTS.md) — repository layout details, build and test commands, doc/script ownership rules, pre-release checklist
- **Domain glossary**: [`CONTEXT.md`](CONTEXT.md)
- **Architecture decision records**: [`docs/adr/`](docs/adr/)

## License

See [LICENSE](LICENSE).
