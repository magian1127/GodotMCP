# Agent control layers (adapters)

中文版本：[README.md](README.md)

This directory is the single home for every "agent client → Godot MCP Unified" access layer. Maintenance goals: **no duplicated files, explicit source ownership** — every client consumes the repository's single source of truth, `plugin/godot-mcp-unified`, and keeps no plugin copy of its own.

## Channel overview

| Channel | Access mechanism | Client-side copy | Needed after a change | Management entry |
| --- | --- | --- | --- | --- |
| ZCode | cache + marketplace mirror = junctions to the single source; the MCP entry comes from the **plugin-root `.mcp.json`** (stdio form + bundled shim, resolved via `${ZCODE_PLUGIN_ROOT}`) | none | restart ZCode | [`zcode/link-zcode-plugin.ps1`](zcode/link-zcode-plugin.ps1) (link); [`zcode/sync-zcode-plugin.ps1`](zcode/sync-zcode-plugin.ps1) (copy, refuses while linked); [`zcode/install-zcode-plugin.ps1`](zcode/install-zcode-plugin.ps1) (publishes artifacts, validates the contract, migrates the old user-level http registration away) |
| Codex | plugin cache = junction to the single source; `mcp.json` resolves `${PLUGIN_ROOT}` into the repo's single service entry (the bundled shim, which bootstraps the daemon) | none | restart Codex (new task) | [`codex/link-codex-plugin.ps1`](codex/link-codex-plugin.ps1) |
| DSH | the access layer spawns the service entry named by `paths.json` `serverDist` (default: the bundled shim, spawned natively; only `.mjs` entries go through node) and lists tools dynamically from `tools/list`; godot-preset skills = junctions into the single-source skills directory | none (the access layer itself lives in `dsh/`) | restart the DSH session | [`dsh/link-dsh-plugin.ps1`](dsh/link-dsh-plugin.ps1) (maintains the access-layer and preset-skill junctions); health check via `dsh/deepseek-harness-godot_unified/bin/dsh-godot.mjs status` |
| VS Code | project-level `.vscode/mcp.json` configures a stdio local server whose `command` is the bundled shim (which bootstraps the daemon) | none (pointer config) | reload the window | [`vscode/install-vscode-mcp.ps1`](vscode/install-vscode-mcp.ps1); guide in [`vscode/README.md`](vscode/README.md) |

## Directory layout

- `zcode/` — ZCode link/copy deployment and artifact publishing: `link-zcode-plugin.ps1`, `sync-zcode-plugin.ps1`, `register-zcode-plugin.py`, `install-zcode-plugin.ps1` (publishes daemon+shim into `server-dotnet/publish/<rid>/`, validates the plugin-root `.mcp.json` stdio contract, migrates away the user-level http registration that would shadow it).
- `codex/` — Codex link deployment: `link-codex-plugin.ps1`.
- `vscode/` — VS Code project-level integration: `install-vscode-mcp.ps1` (writes `.vscode/mcp.json`), `mcp.json.example` (manual template) and `README.md` (the guide).
- `dsh/` — the DSH adapter and linking scripts. It is a regular directory in this repository, sharing its Git history; only skills/ is a junction to the plugin source.

## Why the manifest slots do not move into this directory

`.zcode-plugin/` and `.codex-plugin/` stay inside `plugin/godot-mcp-unified/`: they are load-time contracts of the plugin package itself — ZCode resolves installPath via `<install root>/.zcode-plugin/plugin.json` and reads its MCP entry from the **plugin-root `.mcp.json`** (`${ZCODE_PLUGIN_ROOT}` / `${ZCODE_PROJECT_DIR}` variables); Codex references the service entry relative to `${PLUGIN_ROOT}`. They are plugin payload, not management layers; moving them would break the clients' install-path contracts. VS Code has no load-time contract (it consumes the project-level `.vscode/mcp.json` of a Godot project), so its adapter lives entirely in this directory under `vscode/`.

## The single home of the service artifacts

Every client points at the **single copy** inside the repo; no client directory keeps its own duplicate:

- Location: `plugin/godot-mcp-unified/server-dotnet/publish/<rid>/` (`godot-mcp-daemon[.exe]` + `godot-mcp-shim[.exe]`, neither needs a .NET runtime; the daemon is a self-contained single file and the shim is NativeAOT when published on its own OS — see [server-dotnet publishing](../plugin/godot-mcp-unified/server-dotnet/README.en.md#build-validate-publish)). For local development each Godot project junctions the addon directory to the single source, so **one publish serves every project** — no copying step.
- To produce: `dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=<rid>` (same for the daemon).
- Copying the plugin wholesale to another machine is the only case that needs the artifacts inside the addon's `bin/<rid>/`: opt in with `-p:CopyPublishToAddonBin=true` (see `server-dotnet/Directory.Build.targets`).

## Version bumps

After bumping the plugin version: re-run `zcode/link-zcode-plugin.ps1` for ZCode (cache directories are version-named); re-run `codex/link-codex-plugin.ps1 -CacheName <new version>+codex.link` for Codex (or keep the old cache directory name — the name is only a cache key, Codex reads the current manifest through the junction); nothing to do for DSH (it only follows the server file pointed to by `paths.json`).

## Troubleshooting

- Is a link live: `Get-Item <client cache dir> | Select-Object LinkType, Target` should report `Junction` and the single-source path.
- Hosts use stdio plus the bundled shim; loopback HTTP is its backend. Remove old direct-HTTP entries after upgrading. The shim reads the token from the machine registry.
- Which file each client reads: Codex → `.codex-plugin/mcp.json`; ZCode → the plugin-root `.mcp.json`; VS Code → the project `.vscode/mcp.json`; DSH → `serverDist` in `$DSH_HOME/godot/paths.json`. All four point at the same `server-dotnet/publish/<rid>/godot-mcp-shim[.exe]`.
- ZCode copy deployment is refused by `sync-zcode-plugin.ps1` while linked — run `-Unlink` first, so robocopy `/MIR` cannot traverse the junction into the single source.
- DSH loads skills through the preset’s dsh-skill-filesystem row and package-local skills/; customSkillDirs resolves from package realpath. Legacy .agent-presets directories are retired.

## Session connection and evidence

Call `godot_connect` first, then activate groups for that session. Enabled configuration, MCP handshake, real-editor calls, and native discovery in the current chat are separate evidence. Use the reconnect action actually available in the host version. See the [daemon README](../plugin/godot-mcp-unified/server-dotnet/README.en.md) for current notification/replay limits. Binary changes require publishing and process reconnection; filesystem links do not update an already-running daemon.
