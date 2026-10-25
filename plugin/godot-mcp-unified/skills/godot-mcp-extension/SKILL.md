---
name: godot-mcp-extension
description: 使用 Godot MCP Unified 扩展 API 添加或更新项目专用的 Godot MCP 工具。当用户要求新 MCP 工具、自定义项目操作、扩展包，或内置工具无法清晰表达某项行为时使用。
---

语言：中文 | [英文版](SKILL.en.md)

# Godot MCP 扩展

先确认本会话已通过 `godot_connect(project="<Godot 项目绝对路径>")` 选择目标。未连接时只有入口工具；连接后提供 9 个核心工具，其他能力通过 `discover_tools(request=[组名], include_schemas=true)` 激活。多项目交错调用显式传 `instance`。工具路由、参数缺失与连接恢复见 [工具路由](../godot-control/references/tool-routing.md)。

编辑前，阅读相对于本技能目录解析的 `../../addons/godot_mcp_toolkit/docs/extending.md`。

相比修改工具包核心，优先使用项目扩展：

1. 确认没有内置或按需工具已经提供该操作。
2. 将 GDScript 扩展放在目标项目自身的 `addons/<extension_name>/` 下，并置于 `addons/godot_mcp_toolkit/` 之外，使工具包更新无法将其删除。
3. 使用 `@tool`、继承 `MCPToolkitExtension` 的唯一 `class_name`，并通过 `MCPToolkitExtensionOptions` 注册。
4. 在 JSON schema 中声明每个接受的参数。真实标注只读、幂等和破坏性行为；未标注的扩展会被视为会产生变更。
5. 防护 LLM 提供的每一个 `res://` 或 `user://` 路径，将外部/项目内容封装为不可信内容，对有界读取进行分页，并限制长时间操作的上限。
6. 对编辑器变更和场景保存使用 UndoRedo 或 MCPToolkitSafeSceneOps。让随运行时发布的依赖不包含仅编辑器可用的类型。
7. 添加直接成功、边界、无效输入和安全测试。刷新扩展，检查已发布的 schema，通过 MCP 调用该工具，并验证其可观察效果。

只有当扩展无法触及所需的编辑器/运行时生命周期时，才修改核心协议；此时同时更新服务器和插件契约。
