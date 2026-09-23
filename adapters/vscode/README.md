# VS Code 适配

VS Code 没有插件市场/插件包概念：它通过**项目级 `.vscode/mcp.json`** 配置 MCP 服务器。本目录是 Godot MCP Unified 的 VS Code 适配器（客户端接入层 `adapters/` 的一员），`mcp.json.example` 是可手改的模板；推荐直接用安装脚本生成最终配置。

配置形态是 **stdio 型**：`command` 指向仓库内唯一的服务入口（随包 shim），由 shim 自举机器级 daemon 再把 stdio 转发到 daemon 的 HTTP 面。因此**不需要 Bearer token**，也不要求 daemon 先被别处拉起。键名按 VS Code 官方 schema：顶层是 `servers`（不是 `mcpServers`）。

## 前置条件

1. 服务入口已发布：`<仓库>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim[.exe]`（与 `godot-mcp-daemon[.exe]` 同目录）：

       dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=<rid>

2. 目标 Godot 项目已安装 toolkit addon，且 Godot 编辑器已启动监听：

       & "<repo>\plugin\godot-mcp-unified\scripts\install-godot-project.ps1" -ProjectPath "D:\Games\MyProject" -GodotExecutable "<Godot 可执行文件>"

## 安装

在仓库所在机器运行（PowerShell）：

    & "<repo>\adapters\vscode\install-vscode-mcp.ps1" -ProjectPath "D:\Games\MyProject"

脚本会在目标项目写入 `.vscode/mcp.json`（若已存在先备份为 `.vscode/mcp.json.bak`），内容为 stdio 型条目，`command` 即上面那条 shim 绝对路径。

然后：

1. 用 VS Code 打开该 Godot 项目；
2. 首次会提示“是否信任此工作区”，信任即可（首次启动该服务器时还会弹一次 MCP 服务器信任确认）；
3. 重载窗口（`Ctrl+Shift+P` → Developer: Reload Window），MCP 服务器才会启动。

会话中应能看到 `godot` 服务器及其工具（工具名 `mcp__godot__<tool>`，启动面 20 个，`discover_tools` 按需激活其余工具）。

## 手动配置

如果不想运行脚本，把 [`mcp.json.example`](mcp.json.example) 复制为 `<项目>\.vscode\mcp.json`，并把 `command` 换成本机 `publish/<rid>/godot-mcp-shim[.exe]` 的绝对路径即可。

## 说明

- 该配置只影响当前项目（工作区级），不会写入用户全局设置。
- daemon 是机器级单例：无论由 VS Code 的 shim、Codex 的 shim 还是 Godot 编辑器边车拉起，都只有一份，经 `127.0.0.1:6590` 提供 MCP 服务；daemon 通过 localhost WebSocket 连接本机已启动的 Godot 编辑器，仅监听回环地址。
- Agent Host 会话不读 `.vscode/mcp.json`，它读工作区 `.mcp.json`（即本插件在 Godot 工程根写入的那份，同为 stdio + shim 形态）或用户级 `~/.copilot/mcp-config.json`。
