[英文原文](companion-skills.md)

# 随附的智能体技能

此插件随 Toolkit 附带两个**智能体技能**——它们是小型、自包含的指令包，用来帮助 AI 编码智能体更好地驱动 MCP 接口。它们位于 `addons/godot_mcp_toolkit/CompanionSkills/`，每个技能对应一个文件夹。技能是可选的：不安装也不影响 Toolkit 工作，但安装后能改善智能体的使用方式。

**安装技能**——将 `addons/godot_mcp_toolkit/CompanionSkills/<skill>/` 中该技能的**整个文件夹**（包括其 `references/` 子文件夹）复制到客户端的技能目录，例如 `.claude/skills/`。请复制整个文件夹，而不是只复制顶层文件——`references/` 中的材料也是技能的一部分。

随附的两个技能：

> Godot UI 领域知识（`godot-ui`）不是配套技能：它作为插件技能位于 `plugin/godot-mcp-unified/skills/godot-ui/`，由插件系统客户端（ZCode、Codex、DSH）原生加载，其他客户端可从该目录复制安装。

## `godot-mcp-unified` — Toolkit 工作流技能

**它是什么。** 工作流技能教智能体如何正确使用 Toolkit：该选用哪个工具、如何批量调用工具、如何从错误中恢复，以及通过 MCP 连接的编辑器工作时如何降低 token 用量。它适用于任何构建项目——安装后，智能体从第一次请求开始就能做出更好的工具选择。

**如何安装。** 将 `addons/godot_mcp_toolkit/CompanionSkills/godot-mcp-toolkit/` 整个文件夹（包括 `references/`）复制到客户端的技能目录（例如 `.claude/skills/`）。

**为什么安装。** 它能在典型构建中**以可测量的方式减少智能体的工具调用、token 和耗时**。在一次受控的两波测试中——同一个游戏、相同的 Toolkit 与服务器版本，分别在安装和未安装技能的情况下构建——安装技能的一组使用了更少的工具调用、更少的输出 token、更短的耗时和更少的费用，同时完成了相同的构建。测量数据、确切范围（游戏、模型、运行次数、版本）以及诚实的限制说明发布在这里：`bundled local documentation`。

## `mcp-extension-creator` — 扩展创作技能

**它是什么。** 该 Toolkit 允许项目用 GDScript 注册自己的 MCP 工具——智能体可以像调用内置工具一样调用这些项目专用辅助工具。扩展创作技能**简化了创建这些新工具的工作流**：它引导智能体端到端编写可分发的 MCP 扩展，并提供可选的**引导式创作模式**，将扩展创建转化为交互式设计对话。当你希望智能体为项目构建或扩展 Toolkit 的工具接口时，请安装此技能。

**如何安装。** 将 `addons/godot_mcp_toolkit/CompanionSkills/mcp-extension-creator/` 整个文件夹（包括 `references/`）复制到客户端的技能目录（例如 `.claude/skills/`）。

**了解更多。** 它引导你使用的扩展配置接口——工具注册、超时、取消、热重载以及 C# 支持——记录在 [extending.md](extending.md) 中；该文档是手工编写扩展时的事实来源。
