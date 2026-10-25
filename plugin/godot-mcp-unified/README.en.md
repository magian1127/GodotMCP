# Godot MCP Unified

[中文](README.md) | English

Godot MCP Unified provides local MCP control for Godot 4 editors and running games, with six companion workflow skills. Hosts use **stdio → godot-mcp-shim → loopback HTTP → godot-mcp-daemon**. The daemon discovers editor/runtime endpoints in the registry; HTTP is its backend, not the supported direct host entry.

The built-in catalog has **92 unique names**. Before connecting, only `godot_connect` exists; afterward the surface is **nine core tools plus this session’s active groups**. Seventeen groups contain 85 member slots and 83 unique tools. Extensions are additional; engine versions and unsafe gates filter visibility. The [daemon README](server-dotnet/README.en.md) owns detailed behavior, settings, and current limits.

## Install

1. Install the addon, build the bridge, and validate loading:

   ```powershell
   & "<仓库>/plugin/godot-mcp-unified/scripts/install-godot-project.ps1" -ProjectPath "<Godot 项目>" -GodotExecutable "<Godot 可执行文件>"
   ```

   The installer backs up the addon and project.godot, copies/enables the toolkit, writes a project-root stdio .mcp.json, and performs a headless editor load. -Template accepts empty, default, 2d-platformer, or 3d-fps. Godot 4.7.2 stable .NET/mono is the validated target; check --headless --version. Local executable paths belong in repository-root .env.

2. Choose the host entry:

   | Host | Configuration/management |
   | --- | --- |
   | Codex | `.codex-plugin/plugin.json` and `.codex-plugin/mcp.json`; development linking uses `<repo>/adapters/codex/link-codex-plugin.ps1` |
   | ZCode | `.zcode-plugin/plugin.json` and plugin-root `.mcp.json`; use adapters/zcode/install-zcode-plugin.ps1 or the link script from the repository root |
   | DSH | [Adapter guide](../../adapters/dsh/deepseek-harness-godot_unified/README.en.md); serverDist points to the bundled shim |
   | VS Code | [Project configuration](../../adapters/vscode/README.en.md), using install-vscode-mcp.ps1 |
   | Claude desktop | [Package/link guide](../../adapters/claude/README.md) |

   Both binaries publish to `server-dotnet/publish/<rid>/`. Development dll builds can launch the shim with dotnet exec and select its matching daemon through GODOT_MCP_DAEMON_EXE. Remove old direct-HTTP entries after upgrading. Hosts do not configure tokens.

3. Open the target Godot editor, then call:

   ```text
   godot_connect(project="<Godot 项目绝对路径>", groups=["scene_edit"])
   list_instances()
   scene_get_tree()
   discover_tools(request=["code_intel", "editor_ops"], include_schemas=true)
   ```

   Use the names displayed by the host. DSH’s default entry is mcp__godot__godot_connect; other native names such as scene_get_tree have no common godot_ prefix. Target/groups belong to each shim; hosts sharing a shim also share that state.

## Connect and update

- The daemon starts the bridge, not the editor. Cwd discovery checks project.godot directly; pass project when a workspace contains several projects.
- Load scene_edit for authoring, code_intel for checks, playtest for game operations. See [skill routing](skills/godot-control/references/tool-routing.en.md).
- Validate native tool discovery separately from programmatic probes. Hosts may cache lists; ttlMs=0 does not guarantee refresh. Use the host’s actual reconnect mechanism or load in a new session.
- Linked skills/addons follow source files. Daemon/shim changes still need rebuilding/publishing and verification of running versions; a new shim does not replace an existing old daemon.
- Notification recovery, group replacement/replay, resident schemas, and diagnostic switches have known limits in the daemon README. Design intentions and passing tests are not verified host behavior.

## Capabilities and skills

Editor capabilities cover scenes, nodes, scripts, resources, project settings, 2D/3D, animation, audio, TileMap/TileSet, LSP, and debugging. Runtime tools cover input, state, screenshots, and bounded stepping. Unsafe is disabled by default and requires daemon opt-in and authorization for the current operation.

Six skills: [godot-control](skills/godot-control/SKILL.en.md), [godot-debug](skills/godot-debug/SKILL.en.md), [godot-playtest](skills/godot-playtest/SKILL.en.md), [godot-project-setup](skills/godot-project-setup/SKILL.en.md), [godot-mcp-extension](skills/godot-mcp-extension/SKILL.en.md), [godot-ui](skills/godot-ui/SKILL.en.md). The addon also carries project-facing CompanionSkills.

## Build and verify

Run dotnet test tests/godot-mcp-daemon.tests from server-dotnet. Its README covers builds, publishing, and acceptance scope. [test-project](../../test-project/README.en.md) owns Godot regressions. Headless editors support structured reads; screenshots and rendered behavior need a display.

See the [workspace README](../../README.en.md), [adapter overview](../../adapters/README.en.md), LICENSE, and addons/godot_mcp_toolkit/ATTRIBUTIONS.md for layout and attribution.
