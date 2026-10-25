# Godot MCP Unified

中文 | [English](README.en.md)

Godot MCP Unified 为 Godot 4 编辑器和运行中游戏提供本地 MCP 控制面，包含六个配套技能。宿主统一使用 **stdio → godot-mcp-shim → loopback HTTP → godot-mcp-daemon**，daemon 再按注册表连接编辑器与运行时；HTTP 是后端传输，不是推荐的宿主直连入口。

当前内置目录共 **92 个唯一工具名称**。未连接时仅有 `godot_connect`；连接后是 **9 个核心工具 + 本会话激活的组**，17 个内置组共有 85 个成员槽位、去重 83 个工具。扩展另计，实际可见面还受引擎版本与 unsafe 门控影响。完整语义、配置和已知限制见 [daemon README](server-dotnet/README.md)。

## 安装

1. 在目标 Godot 项目安装 addon、构建桥接并验证加载：

   ```powershell
   & "<仓库>/plugin/godot-mcp-unified/scripts/install-godot-project.ps1" -ProjectPath "<Godot 项目>" -GodotExecutable "<Godot 可执行文件>"
   ```

   安装器备份已有 addon 与 project.godot，复制并启用插件，写入项目根 stdio .mcp.json，并进行 headless 加载。可用 -Template empty、default、2d-platformer、3d-fps 创建项目。验证目标是 Godot 4.7.2 stable .NET/mono；实际版本用 --headless --version 核对。本机可执行路径放在仓库根 .env。

2. 按宿主选择入口：

   | 宿主 | 配置/管理入口 |
   | --- | --- |
   | Codex | 插件 `.codex-plugin/plugin.json` 与 `.codex-plugin/mcp.json`；开发联接使用 `<仓库>/adapters/codex/link-codex-plugin.ps1` |
   | ZCode | `.zcode-plugin/plugin.json` 与插件根 `.mcp.json`；在仓库根运行 `adapters/zcode/install-zcode-plugin.ps1` 或联接脚本 |
   | DSH | [接入层指南](../../adapters/dsh/deepseek-harness-godot_unified/README.md)，serverDist 指向随包 shim |
   | VS Code | [项目配置指南](../../adapters/vscode/README.md)，运行其 install-vscode-mcp.ps1 |
   | Claude 桌面端 | [包与联接指南](../../adapters/claude/README.md) |

   shim 与 daemon 发布在 `server-dotnet/publish/<rid>/`。开发构建 dll 可通过 dotnet exec 启动 shim，并用 GODOT_MCP_DAEMON_EXE 指向匹配的 daemon dll。升级后请删除旧的 HTTP 直连条目；宿主不需要配置 token。

3. 打开目标 Godot 编辑器，再在 MCP 会话调用：

   ```text
   godot_connect(project="<Godot 项目绝对路径>", groups=["scene_edit"])
   list_instances()
   scene_get_tree()
   discover_tools(request=["code_intel", "editor_ops"], include_schemas=true)
   ```

   使用宿主实际展示的工具名。DSH 默认入口为 mcp__godot__godot_connect，其它原生工具如 scene_get_tree 没有统一 godot_ 前缀。连接和分组是每个 shim 的状态，宿主共享同一 shim 时也会共享目标与组。

## 连接与更新

- daemon 只启动桥接服务，不启动 Godot 编辑器。cwd 发现只检查本层 project.godot；工作区包含多个项目时传 project。
- 节点/场景编辑加载 scene_edit，代码检查加载 code_intel，游戏操作加载 playtest；[技能路由](skills/godot-control/references/tool-routing.md) 给出当前分工。
- 原生工具列表与程序 MCP 探针分开验证。部分宿主会缓存目录；ttlMs=0 不能保证自动刷新。使用宿主实际提供的重连机制，必要时在新会话加载。
- 技能或 addon 的联接会让磁盘内容跟随真源；daemon/shim 代码变更仍需重新构建/发布，并确认运行中的进程已切换版本。新 shim 不会自动替换仍在运行的旧 daemon。
- 当前仍有通知恢复、组替换/重放、常驻 schema 与调试开关的已知缺口，见 daemon README；不要把设计目标或测试通过写成已验证的宿主行为。

## 能力与技能

编辑器能力包括场景、节点、脚本、资源、项目配置、2D/3D、动画、音频、TileMap/TileSet 和 LSP/调试器。运行时能力包括输入、状态、截图和有界逐帧控制。unsafe 组默认关闭；daemon 进程显式启用且当前操作获得授权后才使用。

六个技能是 [godot-control](skills/godot-control/SKILL.md)、[godot-debug](skills/godot-debug/SKILL.md)、[godot-playtest](skills/godot-playtest/SKILL.md)、[godot-project-setup](skills/godot-project-setup/SKILL.md)、[godot-mcp-extension](skills/godot-mcp-extension/SKILL.md)、[godot-ui](skills/godot-ui/SKILL.md)。addon 随附 CompanionSkills 为工程内的模型工作流参考。

## 构建与验证

在 server-dotnet 运行 dotnet test tests/godot-mcp-daemon.tests；构建/发布和验收范围见该目录 README。Godot 回归由 [验收项目](../../test-project/README.md) 承载。仅结构化读取可在无头编辑器验证；截图等渲染能力需要显示环境。

源码与分发归属见 [工作区 README](../../README.md)、[接入层总览](../../adapters/README.md)。上游归属见 LICENSE 和 addons/godot_mcp_toolkit/ATTRIBUTIONS.md。
