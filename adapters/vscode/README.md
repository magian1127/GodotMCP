# VS Code 适配

VS Code 没有插件市场/插件包概念：它通过**项目级 `.vscode/mcp.json`** 配置 MCP 服务器。本目录是 Godot MCP Unified 的 VS Code 适配器（客户端接入层 `adapters/` 的一员），`mcp.json.example` 是可手改的模板；推荐直接用安装脚本生成最终配置。

## 前置条件

1. godot-mcp-daemon 已在机器上部署并监听（`install-http-face.ps1` 完成；默认 `127.0.0.1:6590`，Bearer token 见注册表目录 `daemon-token`）。
2. 目标 Godot 项目已安装 toolkit addon 且 Godot 编辑器已启动监听：

       & "<repo>\plugin\godot-mcp-unified\scripts\install-godot-project.ps1" -ProjectPath "D:\Games\MyProject" -GodotExecutable "<Godot 可执行文件>"

## 安装

在仓库所在机器运行（PowerShell）：

    & "<repo>\adapters\vscode\install-vscode-mcp.ps1" -ProjectPath "D:\Games\MyProject"

脚本会在目标项目写入 `.vscode/mcp.json`（若已存在先备份为 `.vscode/mcp.json.bak`），配置为 http 型指向 daemon HTTP 面（`127.0.0.1:6590` + Bearer token）。

然后：

1. 用 VS Code 打开该 Godot 项目；
2. 首次会提示“是否信任此工作区”，信任即可；
3. 重载窗口（`Ctrl+Shift+P` → Developer: Reload Window），MCP 服务器才会启动。

会话中应能看到 `godot` 服务器及其工具（工具名 `mcp__godot__<tool>`，启动面 20 个，`discover_tools` 按需激活其余工具）。

## 手动配置

如果不想运行脚本，把 [`mcp.json.example`](mcp.json.example) 复制为 `<项目>\.vscode\mcp.json`，并把 `Authorization` 头中的 `<daemon-token>` 替换为注册表目录 `daemon-token` 文件的内容。

## 说明

- 该配置只影响当前项目（工作区级），不会写入用户全局设置。
- 连接、鉴权与发现流程与其他客户端一致：daemon 通过 localhost WebSocket 连接本机已启动的 Godot 编辑器，仅监听回环地址。
