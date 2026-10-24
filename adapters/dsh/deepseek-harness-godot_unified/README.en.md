# deepseek-harness-godot_unified

English | [中文](README.md)

Connect [GodotMCP](https://github.com/magian1127/GodotMCP) (a Godot 4 editor & game MCP control toolkit) to DSH: the model reads and writes scenes, nodes, scripts, and resources directly in sessions, runs deterministic playtests, and verifies visuals with screenshots.

## What it does

- **Mounts the host's official MCP client in Godot sessions**: since v0.5 this package **does not implement an MCP client**. When a session that joined the Godot preset is created, DSH's own `@deepseek-ai/dsh-mcp-client` is mounted into **that session's agent scope**, and it owns the connection, handshake, tool discovery, `list_changed` increments, reconnects, and image projection. Tool names follow the official convention `mcp__godot__<raw tool name>` (the `serverName` can be overridden by the plugin row config; changing it changes the tool names the model sees).
- **The tool inventory still comes from the server**: the list comes from the GodotMCP server's `tools/list` (real schemas and descriptions), and **no tool is hardcoded** in the plugin. The resident surface has 19 tools + 1 meta tool `discover_tools`, plus 31 on-demand tool groups; when `discover_tools` activates a group the server sends `list_changed` and the official client adds those tools to this session.
- **Only Godot sessions have tools**: non-Godot sessions **mount nothing** — no connection, no tools. Not "registered and then hidden".
- **Project path per session cwd**: on mount, the Godot project path is resolved from **that session's working directory** (`$DSH_HOME/godot/paths.json` per-cwd entries, global fallback) and passed to the server as `GODOT_MCP_PROJECT_PATH`; the child process cwd is the session cwd too — different sessions may target different Godot projects, never locked to one directory.
- **Entering a session connects immediately**: entering a Godot session establishes the connection (no more "start on the first call", and no idle auto-close). A closed editor never fails session creation, and disconnects reconnect automatically through the official client (500ms, doubling, capped at 30s); screenshots and other image results are now **real image attachments**.
- **Workflow prompt section** (optional, mounted with the bundle row): teaches the model on-demand group activation, batch editing, the editor's FIFO mutation queue semantics, error-code-driven recovery, and screenshot verification.
- **Management CLI `dsh-godot`**: install / uninstall / status — validates the server build artifact and Godot project, writes/clears the path config, and checks the editor registry and ports.
- **Plugin-page config form**: the three injection toggles plus a "Related paths" section (a single global server entry + per-workspace project entries); saving writes the host-side `paths.json`.

> The generic tool-debugging UI (tool tree / manual calls / results & history / status bar) is not Godot-specific and has moved to the "Tool Debugging" tab of `deepseek-harness-project-mcp`; this package no longer adds a top-level session tab.

## Prerequisites

1. DSH (the CLI ships `@deepseek-ai/dsh-mcp-client` — that is what this plugin mounts; nothing extra to install).
2. A ready GodotMCP workspace: the bundled shim in place — `<GodotMCP workspace>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim[.exe]` (produced by the repo's publish step: `dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=<rid>`). This plugin hands that entry to the host's official client as a stdio server and the client spawns it directly; the shim makes sure the machine-level daemon is running (the sibling `godot-mcp-daemon[.exe]`) and forwards stdio to the daemon's HTTP face, so the daemon need not be started elsewhere first. Only for an stdio fallback/rollback use `<GodotMCP workspace>/adapters/dsh/godot-http-bridge.mjs` (a Node script, run through node; the Node bridge was retired with plugin 1.1.0).
3. The target Godot project has the `godot_mcp_toolkit` addon installed (use GodotMCP's `install-godot-project.ps1`) and the **editor is running** (headless works: `godot --headless --editor --path <Godot project>`). The server never launches the editor; when it is absent, tool calls return actionable errors, but neither the connection nor session creation is affected.

## Requirements

- DeepSeek Harness `>=0.2.0-rc.2 <0.3.0-0` (declared as `engines.dsh` in `package.json`; DSH enforces it from 0.2.1 on, so 0.1.x and 0.3.x refuse to load this plugin); the full UI uses `web`, and Godot preset sessions use the same `web` profile
- Node.js `^22.19.0 || >=24.0.0`

## Install

```sh
# From this repo (dev install, includes the bundle + path config):
node bin/dsh-godot.mjs install --profile web \
  --project <Godot project> \
  --cwd <session working directory> \
  --godot-mcp-root <GodotMCP workspace> \
  --link <this repo>

# Or without the bundle (mounting only, no prompt section/config form):
node bin/dsh-godot.mjs install --profile web \
  --project <Godot project> \
  --server-dist <GodotMCP workspace>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim.exe
```

Server dist resolution order: `--server-dist` > `$GODOT_MCP_SERVER_DIST` > `--godot-mcp-root` / `$GODOT_MCP_ROOT`.

**DSH Desktop (the Electron app)**: supported (desktop Host port 19387). The path config `$DSH_HOME/godot/paths.json` is machine-wide and shared, so `install` still writes it in the desktop scenario (`--profile desktop` only skips the bundle install); the shim the official client spawns is a native exe and does not depend on node being on PATH. For a link dev install of the bundle (prompt section + plugin-page config form; while the app is **not running**), edit `~/.dsh/profiles/desktop/package.json`:

```jsonc
"dependencies": { "deepseek-harness-godot_unified": "link:<this repo>" },
"dsh": { "profile": { "bundles": [ /* keep official bundles, append */ "deepseek-harness-godot_unified" ] } }
```

then `pnpm install` inside the profile directory; start the app to mount, `npm run build` + restart after source changes, remove both entries + `pnpm install` to uninstall.

Since v0.5 `install` **writes no mcp-client row into the profile**: it writes `serverDist` plus a `{cwd, projectPath}` entry into `$DSH_HOME/godot/paths.json` (and cleans up any historical static mcp-client managed block, preventing a double track). Paths take effect without a restart — when a **Godot-preset session** (the `Godot` mode, declared by this package's bundle patch row `preset-godot`; no hand-installed preset directory) is created, the plugin resolves paths from that session's cwd and mounts the host's official MCP client into the session; you can also edit per-cwd paths in the sidebar "Plugins" page → deepseek-harness-godot_unified (immediate save, DSH 0.1.6+). The host re-reads `paths.json` when its mtime changes, so paths written by the CLI from another process also take effect on the next resolution. Different sessions may target different Godot projects.

### Common options

| Option | Meaning |
| --- | --- |
| `--cwd <dir>` | Session working directory this entry binds to (default: current dir; matched by session cwd) |
| `--server-name <name>` | Retained parameter: v0.5 writes no profile row, so tool names come from the plugin row config's `serverName` (default `godot`, i.e. `mcp__godot__*`) |
| `--read-only` / `--unsafe` / `--rate-limit` / `--editor-port` / `--runtime-port` / `--timeout-ms` | Former official-row env parameters; no longer written in v0.5 (warned); capabilities come from the server's defaults/project settings |

## Verify

```sh
node bin/dsh-godot.mjs status --profile web   # path config/server/project/registry/port health
dsh --profile web --dump-config | grep godot  # composition check (no boot)
```

In the GUI: create a session with the Godot preset → the model sees `mcp__godot__*` tools (the connection is established when the session starts; no manual first call needed). The target project's Godot editor must be running. If the server entry is empty, that session has no tools and only a "how to configure" prompt. For manual tool-call debugging, use the "Tool Debugging" tab of `deepseek-harness-project-mcp`.

**Skills directory**: the 6 Godot workflow skills bundled with this package (godot-control / godot-debug / godot-mcp-extension / godot-playtest / godot-project-setup / godot-ui) ship with the Godot preset; their source is the package's `skills/`, a junction to the upstream GodotMCP `plugin/godot-mcp-unified/skills`, so **upstream edits apply immediately** (no rebuild of this package needed). The historical `~/.dsh/.agent-presets/godot/skills/` directory is obsolete (DSH 0.1.6+ no longer reads it).

**DSH 0.2.1 behavior changes** (already adapted here; no user action needed):

- **The `{{cwd}}` prompt variable is gone**: the persona row no longer declares a `suffix`; the working directory now arrives as user context from DSH's working-directory service. `{{model}}` is still a registered variable.
- **Delegation is no longer foreground/background**: every `subagent` / `subagent_fork` call returns an activation and the final answer arrives through the parent completion notice; `job_output` / `job_kill` no longer collect child results. This package's preset no longer sets `backgroundMode`.

## Uninstall

```sh
node bin/dsh-godot.mjs uninstall --profile web             # clear path config (and historical static mcp-client rows)
dsh plugin --profile web remove deepseek-harness-godot_unified # remove the bundle (prompt section/config form)
```

## Data & boundaries

- The GodotMCP server dials loopback only (editor/runtime WebSockets, token-authenticated); the stdio child environment is scrubbed by the host's official MCP client (credential-shaped variables and `DSH_*` are removed), while this package's explicit `GODOT_MCP_PROJECT_PATH` survives.
- The server entry and project paths live in **`$DSH_HOME/godot/paths.json`** (machine-local, not published, never committed); the historical static mcp-client managed block is cleaned up by the CLI.
- By default (per-session mounting) each Godot session (including subagents that inherit the preset) holds one lightweight stdio shim connection; the machine-level daemon remains a singleton reused by the shim. Legacy mode instead has a single shared connection on the host.
- Removed in v0.5: the tool-inventory cache `$DSH_HOME/godot/tools-cache.json` (**no longer read**; a stale file is harmless on disk and is never deleted automatically), the upstream Chinese-description translation table, and the deny/restrict injection patch.
- Known limits: MCP prompt templates are not supported by the official client; resources (e.g. `godot://roots`) come from the profile's `dsh-mcp-resources` row, this package does not forward them itself; upstream read-only/destructive tool annotations do not map to DSH permission rules.

## Tool-injection policy and paths (plugin-page config form)

**Per-session mounting of the host's official MCP client**: the plugin only decides which sessions need it and which server it connects to. When a session is created (`agent/created`) with preset `godot`, the official client is mounted into that session's agent scope; switching a **blank session (before its first turn)** to the Godot preset does the same, and switching away / disposing the session / unloading the plugin unmounts it (DSH refuses preset changes after the first turn). Mounting never awaits the connection (failures are logged and never break session creation): session creation waits at most 10 seconds for the tools to be ready, after which they land in the background and become visible on the next turn. The config form offers:

| Setting | Default | Meaning |
| --- | --- | --- |
| Legacy mode (global mount) | off | On = one **shared connection** on the host plus a global workflow prompt: every session sees the `mcp__godot__*` tools (project path from the global setting, no longer switching with the session cwd); off = **only Godot-preset sessions mount**, all other sessions **simply have none** of these tools (not hidden) |
| Workflow prompt | on | Toggle for the workflow prompt section (only affects sessions that mounted the Godot MCP: the scoped section of a Godot preset, or the global section in legacy mode) |
| Localize prompt | off | Switches **only the workflow prompt** to Chinese (English by default); tool descriptions and error messages come from the upstream server and stay as provided. Tool names stay English |

The "Related paths" section (a single global server entry + **per-working-directory project entries**) reads and writes `$DSH_HOME/godot/paths.json` through the same-origin `GET/POST /godot-config/api/path-settings` route; on session creation the project path is matched by that session's cwd (a single entry acts as fallback), falling back to the global value when nothing matches. When the server entry is empty nothing is mounted and the Godot session receives a "how to configure" prompt (so the model does not invent tool calls and instead points the user at the path fields); other sessions receive no prompt at all.

Security & boundaries:

- The path-settings route accepts **loopback + same-origin only** (`Origin`/`Host`/`sec-fetch-site` triple check); methods other than GET/POST get 405, request bodies are capped at 8MiB (413), invalid JSON gets 400; errors return `{error:{code,message}}`.

MIT License.
