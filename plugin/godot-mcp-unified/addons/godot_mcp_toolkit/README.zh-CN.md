[英文原文](README.md)

# Godot MCP Unified

一个 Godot 4.7+ 编辑器插件。它运行本地主机 WebSocket 服务器，让 AI 编码助手或任何兼容 MCP 的客户端能够在编辑器中创建场景、编辑脚本、检查节点并运行试玩。插件只是整个技术栈的一半；机器级 daemon(`http://127.0.0.1:6590/`)才是助手实际连接的接入面——由编辑器的 auto-spawn 边车或宿主安装器拉起，并经机器注册表回连这个 WebSocket 服务器。

所有操作都在本地完成，不会有任何内容离开你的计算机。

## 快速开始

1. **启用插件：**Project Settings → Plugins → **Godot MCP Unified** → 勾选 **Active**。MCP 工具坞会出现在底部面板，输出日志会打印 `[MCPServer] listening on 127.0.0.1:6550`（端口可能位于 6550–6560；工具坞会显示实际端口）。这一行来自编辑器内插件自己的 WebSocket 服务器，daemon 经注册表发现后回连它。
2. **写入客户端配置：**运行整合包随附的 `scripts/install-godot-project.ps1`。它会在项目根目录写入 `.mcp.json`，把 MCP 客户端指向机器级 daemon 的回环 HTTP 接入面(`type: "http"`，`http://127.0.0.1:6590/`)。本机不再需要 Node.js（随包 Node 桥已随插件 1.1.0 退役）；各宿主的认证 token 由其安装器统一布线。
3. **连接：**从项目根目录启动 MCP 客户端。它会自动发现 daemon 并完成身份验证；连接后 dock 中的对等端计数会递增。

如果某一步没有按预期工作，请先查看随插件提供的[高级配置指南](docs/advanced_configuration.md)。

## 文件位置

- **dock**（底部面板中的 “MCP”）——服务器状态和绑定端口、已连接对等端、只读开关、审计日志查看器、响应限制，以及 `.mcp.json` 健康状态和一键修复。
- **Project → Tools → Godot MCP Unified**——快捷操作：写入或打开 `.mcp.json`、重新生成认证 token、显示审计日志、打开插件的 Project Settings。Command Palette（Ctrl+Shift+P）中也提供这些操作。
- **Info / Help**（dock 中的按钮）——连接详情、已注册工具列表、版本兼容性、多实例指导和相关链接，其中包括打开随附兼容性指南的按钮。

## 只读模式

对于受监督环境（课堂、CI、演示），打开 dock 的只读开关（或在 `.mcp.json` 的 `env` 块设置 `GODOT_MCP_READ_ONLY=1`）。所有变更工具都会对智能体隐藏。关闭开关并重新连接客户端即可恢复完整访问——工具列表在连接时决定。

## 文档

本插件随附以下文档，位于 `addons/godot_mcp_toolkit/docs/`：

- [compatibility.md](docs/compatibility.md)——支持的 Godot 版本、逐工具及无头矩阵、降级行为、C#（.NET 编辑器）要求、导出剥离，以及如何安全禁用插件。
- [security-recommendations.md](docs/security-recommendations.md)——安全模型和推荐的客户端权限规则。
- [extending.md](docs/extending.md)——在 GDScript 中注册自己的 MCP 工具（支持 C#），以及热重载、超时和取消。
- [multi-instance.md](docs/multi-instance.md)——多个编辑器或 git worktree 并行运行。
- [advanced_configuration.md](docs/advanced_configuration.md)——端口、限制、环境变量和 macOS 专项说明。

插件还随附[智能体技能](docs/companion-skills.md)——一个工作流技能和一个扩展创作技能——位于 `addons/godot_mcp_toolkit/CompanionSkills/`；将技能文件夹复制到客户端的技能目录即可使用。

完整的生成工具参考位于本地整合包的 `server/docs/tool-reference/README.zh-CN.md`。

## 卸载

通过 Project Settings → Plugins 禁用（对话框会提供清理 `.mcp.json` 的选项），或删除插件文件夹。如果插件仍处于启用状态时就删除文件夹，清理步骤无法运行——请自行从项目根目录删除 `.mcp.json`。随附的兼容性指南解释了为什么不应手动编辑 `project.godot` 来禁用插件。

## 许可证

MIT：完整文本随插件放在 [LICENSE](LICENSE)。第三方归属见 [ATTRIBUTIONS.md](ATTRIBUTIONS.md)。
