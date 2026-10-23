# Claude 桌面端适配

Claude 桌面端（Windows / macOS 应用）通过 **My Uploads 上传 `.plugin` 包**安装本插件。本目录是 Godot MCP Unified 的 Claude 桌面端适配器（客户端接入层 `adapters/` 的一员），包含：

| 文件 | 用途 |
| --- | --- |
| [`godot-mcp-unified.plugin`](godot-mcp-unified.plugin) | 上传安装用的包快照（zip 格式；由真源 `plugin/godot-mcp-unified` 生成） |
| [`link-claude-plugin.ps1`](link-claude-plugin.ps1) | 把桌面端安装副本中的 `skills/`、`addons/` 替换为指向仓库真源的目录联接(junction；macOS 为符号链接)，并把 `.mcp.json` 的 shim 路径校正为本机路径（`-McpOnly` 只做后者） |

## 安装（首次）

1. 服务入口已发布：`<仓库>/plugin/godot-mcp-unified/server-dotnet/publish/win-x64/godot-mcp-shim.exe`（与 `godot-mcp-daemon.exe` 同目录）。
2. 在 Claude 桌面端设置里上传 `adapters/claude/godot-mcp-unified.plugin` 安装。桌面端把包解压到
   `%APPDATA%\Claude\local-agent-mode-sessions\<账户>\<组织>\rpm\plugin_<id>\`，
   并在同级 `manifest.json` 登记元数据（不校验内容哈希）。
3. 运行链接脚本（需 PowerShell 7+）：

       pwsh adapters/claude/link-claude-plugin.ps1

   `-Unlink` 可解除链接（不恢复副本；需要副本模式时在桌面端重新上传安装包）。

## macOS

流程同上，差异只有三处：

1. **在本机发布服务入口。** 包内 `.mcp.json` 写死的是打包机的 Windows shim 路径，Mac 上不存在，MCP 服务会一直"连接失败"。shim 为 NativeAOT，需 Xcode 命令行工具；Intel Mac 把 `osx-arm64` 换成 `osx-x64`：

       dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-daemon -c Release -p:PublishProfile=osx-arm64
       dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim   -c Release -p:PublishProfile=osx-arm64

2. 桌面端把包解压到 `~/Library/Application Support/Claude/local-agent-mode-sessions/<账户>/<组织>/rpm/plugin_<id>/`。
3. 把 `.mcp.json` 的 `command` 校正为 `<仓库>/plugin/godot-mcp-unified/server-dotnet/publish/osx-arm64/godot-mcp-shim`：

       pwsh adapters/claude/link-claude-plugin.ps1 -McpOnly

   不带 `-McpOnly` 时同时把 `skills/`、`addons/` 换成指向真源的符号链接（与 Windows 的联接等价）。脚本每次运行都按"本机仓库 + 本平台"重写该 `command`，可重复执行。

Godot 项目侧同样使用 `pwsh plugin/godot-mcp-unified/scripts/install-godot-project.ps1`；macOS 上 `-GodotExecutable` 可直接给 `.app` 包路径，缺省读仓库根 `.env` 的 `GODOT_EXECUTABLE`。

## 链接后的形态

插件目录内只有两类东西：

- **复制体（小文件）**：`.claude-plugin/plugin.json`、`.mcp.json`、`README.md`、`LICENSE`。其中 `.mcp.json` 的 `command` 在打包时已写死仓库内 shim 绝对路径，MCP 服务面本就直连真源。
- **联接（随仓库演进的大目录）**：`skills/`、`addons/` → `<仓库>/plugin/godot-mcp-unified/{skills,addons}`。

因此在仓库真源里改技能 Markdown 或 addon 脚本，**重启桌面端（新开会话）即生效，无需重新上传安装**。

## 注意

- 桌面端里重新上传/更新该插件会重建整个 `plugin_<id>` 目录（联接与 `.mcp.json` 的 shim 路径校正被覆盖），届时重跑链接脚本即可。
- 脚本按 `manifest.json` 中 `name = godot-mcp-unified` 自动定位安装目录，可跨账户/组织目录处理多份安装。
- 上传的包是某一时点的快照（技能换行为 LF，不含 `agents/` 等仓库专属目录）；已链接的安装不受包内容新旧影响。真源插件根另有 `.claude-plugin/plugin.json` 清单，供未来 Claude Code（CLI）侧的安装形态复用。
- daemon 是机器级单例：无论由桌面端的 shim、VS Code 的 shim 还是 Godot 编辑器边车拉起，都只有一份，经 `127.0.0.1:6590` 提供 MCP 服务，仅监听回环地址。
