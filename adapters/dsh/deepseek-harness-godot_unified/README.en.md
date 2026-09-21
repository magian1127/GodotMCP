# deepseek-harness-godot_unified

English | [中文](README.md)

Connect [GodotMCP](https://github.com/) (a Godot 4 editor & game MCP control toolkit) to DSH: the model reads and writes scenes, nodes, scripts, and resources directly in sessions, runs deterministic playtests, and verifies visuals with screenshots.

## What it does

- **Self-hosted bridge with dynamic tools**: the plugin spawns the GodotMCP bridge server itself (stdio) through a minimal embedded MCP client — tools are named `godot_<tool>` and the inventory **comes from the server's `tools/list`** (real schemas/descriptions, grown on demand via the `discover_tools` meta tool): 19 resident tools + 1 meta tool, plus 31 on-demand groups (64 tools), keeping context cost bounded (~8.8K tokens). No tool is hardcoded in the plugin.
- **Per-session cwd parameter passing**: the bridge resolves the project path from the **calling session's cwd** (`$DSH_HOME/godot/paths.json` per-cwd entries, global fallback) and passes it to the server — different sessions may target different Godot projects; never locked to one directory.
- **Workflow prompt section** (optional, mounted with the bundle row): teaches the model on-demand group activation, batch editing, the editor's FIFO mutation queue semantics, error-code-driven recovery, and screenshot verification.
- **Management CLI `dsh-godot`**: install / uninstall / status — validates the server build artifact and Godot project, writes/clears the path config, and checks the editor registry and ports.

## Prerequisites

1. DSH (the CLI ships `@deepseek-ai/dsh-mcp-client`; nothing extra to install).
2. A ready GodotMCP workspace: `<GodotMCP>/adapters/dsh/godot-http-bridge.mjs` in place (bundled with the repo; it boot-spawns the machine-level daemon — the daemon is published by GodotMCP's `adapters/zcode/install-http-face.ps1` or auto-spawned by the editor sidecar; the Node bridge was retired with plugin 1.1.0).
3. The target Godot project has the `godot_mcp_toolkit` addon installed (use GodotMCP's `install-godot-project.ps1`) and the **editor is running** (headless works: `godot --headless --editor --path <project>`). The server never launches the editor; when it is absent, tool calls return actionable errors.

## Requirements

- DeepSeek Harness ≥ `0.1.5-rc.1`; the full UI uses `web`, and Godot preset sessions use the same `web` profile
- Node.js `^22.19.0 || >=24.0.0`

## Install

```sh
# From this repo (dev install, includes the bundle + path config):
node bin/dsh-godot.mjs install --profile web \
  --project <absolute Godot project path> \
  --cwd <session working directory> \
  --godot-mcp-root <GodotMCP workspace root> \
  --link <this repo directory>

# Or without the bundle (bridge only, no prompt section/workbench):
node bin/dsh-godot.mjs install --profile web \
  --project <absolute Godot project path> \
  --server-dist <GodotMCP>/adapters/dsh/godot-http-bridge.mjs
```

Server dist resolution order: `--server-dist` > `$GODOT_MCP_SERVER_DIST` > `--godot-mcp-root` / `$GODOT_MCP_ROOT`.

Since v0.4 `install` **no longer writes an official mcp-client row** (self-hosted bridge): it writes `serverDist` and a `{cwd, projectPath}` entry into `$DSH_HOME/godot/paths.json` (and cleans up any historical official bridge row). Paths take effect without a restart — the bridge loads them when a **Godot-preset session** (`~/.dsh/.agent-presets/godot/`) is created; you can also edit per-cwd paths in "Settings → Plugins → Godot Workbench" (immediate). Different sessions may target different Godot projects.

### Common options

| Option | Meaning |
| --- | --- |
| `--cwd <dir>` | Session working directory this entry binds to (default: current dir; matched by cwd) |
| `--server-name <name>` | Tool namespace (default `godot`, i.e. `godot_*`; retained parameter) |
| `--read-only` / `--unsafe` / `--rate-limit` / `--editor-port` / `--runtime-port` / `--timeout-ms` | Former official-row env parameters; no longer written in v0.4 (warned); capabilities come from the server's defaults/project settings |

## Verify

```sh
node bin/dsh-godot.mjs status --profile web   # path config/server/project/registry/port health
dsh --profile web --dump-config | grep godot  # composition check (no boot)
```

In the GUI: create a session with the Godot preset → the model sees `godot_*` tools; you can also browse/call manually in the "Godot" workbench tab. The target project's Godot editor must be running.

## Uninstall

```sh
node bin/dsh-godot.mjs uninstall --profile web             # clear path config (and historical official rows)
dsh plugin --profile web remove deepseek-harness-godot_unified # remove the bundle (prompt section/workbench)
```

## Data & boundaries

- The bridge server dials loopback only (editor/runtime WebSockets, token-authenticated); the DSH stdio child environment is scrubbed of credential-shaped variables and `DSH_*`, while this package's explicit `GODOT_MCP_*` overrides survive.
- Server and project paths live in **`$DSH_HOME/godot/paths.json`** (machine-local, not published, never committed); the historical official mcp-client managed block is cleaned up by the CLI.
- Known limits: MCP resources (e.g. `godot://roots`) are not transparently bridged by this package's own bridge; upstream read-only/destructive tool annotations do not map to DSH permission rules (the workbench takes a conservative confirmation policy).

## Godot Workbench

A third top-level tab "Godot" in the DSH Web session UI, serving as a preview and debugging surface for this plugin and the whole GodotMCP server. It runs **outside the conversation and writes nothing back to the session**; everything goes through the same-origin `/godot-workbench/api/*` routes. The client half mounts with the plugin at cold start (a first install needs one `dsh web` restart); later `lib/client.js` changes hot-swap in-page through DSH client HMR, with no page refresh.

Capabilities:

- **Browse all tools**: lists every tool currently callable, with `godot_*` (Godot) pinned and expanded; GodotMCP's 31 on-demand tool groups are shown separately (greyed out + description + an "Activate" button), and the rest are clustered by name prefix into "Built-in" / "Other plugins".
- **Manual calls**: pick a tool and a JSON-Schema-driven form is generated (or switch to "Raw JSON" editing), press "Run" to call it directly and inspect results — text / JSON / inline screenshot images / error codes + actionable hints; can "Cancel" while running.
- **Result area, four sub-tabs**: Result, History (last 50, localStorage), Skills (the 5 GodotMCP SKILL.md + this package's prompt section), and Prompt Sections (the currently effective systemPrompt sections, to verify injection).
- **Status bar**: bridge tool count / editor registry (project · port · Godot version · pid) / `READ_ONLY` flag / server-dist path & existence / install hints; on fetch failure it shows the error reason plus a **Retry** button (one-click re-probe once the MCP bridge/editor is up) instead of an endless "Loading…".
- **View mode**: a composer-overlay view like the "Trajectory" tab — the workbench fills the viewport with its own scrollers; the bottom input box becomes a floating strip that yields space to content (root element carries `data-conversation-composer-overlay`, riding DSH's `ConversationRoot` `:has()` contract) instead of a conversation-style docked composer.

### Tool-injection policy and dynamic tools (Settings → Plugins → Godot Workbench)

**Self-hosted bridge with dynamic tools**: the plugin spawns the GodotMCP server itself (stdio) through a minimal embedded MCP client — the tool inventory **comes from the server's `tools/list`** (real schemas/descriptions, growing/shrinking with group activation and extensions). No tool is hardcoded. The server stays idle by default; a Godot-preset session triggers one async warm-up (non-blocking), it auto-closes after 10 idle minutes, and calls reconnect on demand. The bridge resolves the **calling session's cwd** and passes the project path per call — different sessions can target different Godot projects, never locked to one directory. The settings card offers:

| Setting | Default | Meaning |
| --- | --- | --- |
| Legacy injection mode | off | On = inject tools and prompt globally into every session (takes effect immediately); off = **only Godot-preset sessions inject**, all other sessions hide these tools in their agent scope |
| Workflow prompt | on | Toggle for the Godot workflow prompt section (only affects sessions that inject tools) |
| Localize prompts | off | Injected workflow prompt and bridge tool descriptions use Chinese (English by default); tool names stay English |

Paths (server dist, project path) come either from the settings card's "Godot paths" section (including **per-working-directory project entries**, matched by session cwd) or from `$DSH_HOME/godot/paths.json`; unmatched cwds fall back to the global value. When the editor is away the bridge goes quiet after a few backoff retries and the next call reconnects on demand.

Security & boundaries:

- Routes accept **same-origin + loopback only** (validate `Origin` against Host, or no `Origin`); GET is read-only and POST carries a JSON body; errors return `{error:{code,message}}`.
- Tool annotations (readOnly/destructive) are unavailable because mcp-client does not bridge them, so the workbench uses a **conservative confirmation policy**: every run asks for confirmation (tool name + argument summary) with an optional "skip confirmation this session" checkbox (kept in memory only, never persisted), but tools whose name contains `unsafe` **always require confirmation**.
- If another plugin registers a pre-execute `'ask'` policy on a tool, workbench calls (which carry no agent context) are **rejected** — the error is surfaced as-is; this is by design and not bypassed.
- History lives only in browser localStorage (cap of 50 entries, 8KB summary truncation each); nothing is written to the conversation session.

Known limits:

1. Screenshot base64 inlining is bounded by the single-response size cap (a single image over 4MB is truncated into a truncation marker).
2. Tool annotations are unavailable → conservative confirmation (confirm each time, or a session-level exemption; `unsafe` always confirms).
3. pre-execute `'ask'`-policy tools are rejected on the workbench (by design, not bypassed).
4. Server stderr is not visible to the workbench (mcp-client does not expose it).
5. The tab depends on the web profile's `ui-conversation` contract (`conversation.view` list slot) — a DSH upgrade that changes the contract would need adaptation.

MIT License.
