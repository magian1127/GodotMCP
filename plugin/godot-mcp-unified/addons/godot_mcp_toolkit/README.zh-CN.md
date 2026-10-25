[英文原文](README.md)

# Godot MCP Unified

一个 Godot 4.2+ 编辑器插件。它运行本地主机 WebSocket 服务器，让 AI 编码助手或任何兼容 MCP 的客户端能够在编辑器中创建场景、编辑脚本、检查节点并运行试玩。插件只是整个技术栈的一半：真正干活的是机器级 daemon，而助手是通过 MCP 客户端自己 spawn 的**随包 stdio shim**（`server-dotnet/publish/<rid>/godot-mcp-shim`）连上它的。daemon 尚未运行时由 shim 自举拉起，daemon 再经机器注册表回连这个 WebSocket 服务器。

桥接监听在本地；工具输入输出是否发送给模型服务，由宿主控制。


先通过 stdio shim 握手，再调用 godot_connect(project) 选择会话目标。初始只有入口，连接后是 core 9，更多能力通过 discover_tools(request=[...]) 激活。通知与恢复的当前限制见随附的多实例与会话指南。

## 快速开始

1. **启用插件：**Project Settings → Plugins → **Godot MCP Unified** → 勾选 **Active**。MCP 工具坞会出现在底部面板，输出日志会打印 `[MCPServer] listening on 127.0.0.1:6550`（端口可能位于 6550–6560；工具坞会显示实际端口）。这一行来自编辑器内插件自己的 WebSocket 服务器，daemon 经注册表发现后回连它。
2. **写入客户端配置：**运行整合包随附的 `scripts/install-godot-project.ps1`。它会在项目根目录写入 `.mcp.json`，条目为 `type: "stdio"`，指向随包 shim（`server-dotnet/publish/<rid>/godot-mcp-shim`），首次使用时由 shim 自举拉起 daemon。本机不再需要 Node.js（随包 Node 桥已随插件 1.1.0 退役），也不再需要宿主侧认证 token——token 由 shim 自己从机器级注册表读取。
3. **连接：**从项目根目录启动 MCP 客户端。它会 spawn shim，由 shim 拉起 daemon 并完成鉴权；连接后 dock 中的对等端计数会递增。

如果某一步没有按预期工作，请先查看随插件提供的[高级配置指南](docs/advanced_configuration.md)。

## 文件位置

- **dock**（底部面板中的 “MCP”）——服务器状态和绑定端口、已连接对等端、只读开关、审计日志查看器、响应限制，以及 `.mcp.json` 健康状态和一键修复。
- **Project → Tools → Godot MCP Unified**——快捷操作：写入或打开 `.mcp.json`、重新生成认证 token、显示审计日志、打开插件的 Project Settings。Command Palette（Ctrl+Shift+P）中也提供这些操作。
- **Info / Help**（dock 中的按钮）——连接详情、已注册工具列表、版本兼容性、多实例指导和相关链接，其中包括打开随附兼容性指南的按钮。

## 只读模式

addon UI 保留旧只读配置/状态，但当前 C# daemon 不读取 GODOT_MCP_READ_ONLY。不能把开关视为强制拦截，应配置宿主工具策略并验证。工具缺失也可能是未连接、组未激活或版本门控。

## 文档

本插件随附以下文档，位于 `addons/godot_mcp_toolkit/docs/`：

- [compatibility.md](docs/compatibility.md)——支持的 Godot 版本、逐工具及无头矩阵、降级行为、C#（.NET 编辑器）要求、导出剥离，以及如何安全禁用插件。
- [security-recommendations.md](docs/security-recommendations.md)——安全模型和推荐的客户端权限规则。
- [extending.md](docs/extending.md)——在 GDScript 中注册自己的 MCP 工具（支持 C#），以及热重载、超时和取消。
- [multi-instance.md](docs/multi-instance.md)——多个编辑器或 git worktree 并行运行。
- [advanced_configuration.md](docs/advanced_configuration.md)——端口、限制、环境变量和 macOS 专项说明。

插件还随附[智能体技能](docs/companion-skills.md)——一个工作流技能和一个扩展创作技能——位于 `addons/godot_mcp_toolkit/CompanionSkills/`；将技能文件夹复制到客户端的技能目录即可使用。

工具/schema 以连接后的 tools/list 为准，组通过 discover_tools 查询；旧 Node 参考路径已退役。

## 卸载

通过 Project Settings → Plugins 禁用（对话框会提供清理 `.mcp.json` 的选项），或删除插件文件夹。如果插件仍处于启用状态时就删除文件夹，清理步骤无法运行——请自行从项目根目录删除 `.mcp.json`。随附的兼容性指南解释了为什么不应手动编辑 `project.godot` 来禁用插件。

## 许可证

MIT：完整文本随插件放在 [LICENSE](LICENSE)。第三方归属见 [ATTRIBUTIONS.md](ATTRIBUTIONS.md)。
