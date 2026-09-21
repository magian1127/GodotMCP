# Godot MCP Unified 文档导航

这里是本项目中文维护文档的总入口。阅读时优先使用中文主文档；英文版本用于对照、追溯和与上游同步。

## 文档约定

- 项目的维护文档以中文主文档 `.md` 为准，并保留对应的 `.en.md` 英文版本。日常维护和决策应优先查阅中文主文档。
- 上游生成文档或 API 参考保留英文 `.md` 原文，并在同目录提供 `.zh-CN.md` 中文译本。译本用于阅读，不改变上游生成文件的结构、标识或事实来源。
- 英文 `LICENSE` 是唯一具有法律效力的许可证文本；任何中文许可证译本都仅供参考，不能替代英文原文。
- **单一出处 + 索引**：同一事实只在一处维护，其他位置一律链接过去；发现重复内容应合并到职责所在文档并在原处留索引。

## 项目入口

- [项目根 README](../README.md) —— 项目范围、快速开始与仓库地图。
- [代理/贡献者须知（AGENTS.md）](../AGENTS.md) —— 仓库布局细节、构建与测试命令、文档与脚本归属规则、发布前检查清单。
- [接入层总览](../adapters/README.md) —— Codex / ZCode / DSH / VS Code 四条通道的接入机制与脚本入口。
- [验收项目 README](../test-project/README.md) —— Godot 回归脚本的承载项目与运行方式。

## 插件与服务器

- [统一插件 README](../plugin/godot-mcp-unified/README.md) —— Godot MCP Unified 插件的安装和使用入口。
- [daemon README](../plugin/godot-mcp-unified/server-dotnet/README.md) —— C# 单例 daemon 与 stdio shim 的构建、发布和运行说明（Node 桥已退役，退役前全量状态见 git tag `node-bridge-final`）。
- [架构决策记录](adr/) —— ADR-0001 ~ 0004：单例 daemon、监听面、实例寻址与生命周期/本地安全。
- [Toolkit 插件 README](../plugin/godot-mcp-unified/addons/godot_mcp_toolkit/README.zh-CN.md) —— Godot MCP Toolkit 插件本体的配置、文档和卸载说明。

## 六个配套技能

以下技能位于插件仓库的 `skills/` 目录；各技能的 `SKILL.md` 是对应技能的使用入口：

- [godot-control](../plugin/godot-mcp-unified/skills/godot-control/SKILL.md) —— Godot 编辑器控制与工具路由。
- [godot-debug](../plugin/godot-mcp-unified/skills/godot-debug/SKILL.md) —— 调试、诊断和错误恢复。
- [godot-mcp-extension](../plugin/godot-mcp-unified/skills/godot-mcp-extension/SKILL.md) —— MCP 扩展创作与注册。
- [godot-playtest](../plugin/godot-mcp-unified/skills/godot-playtest/SKILL.md) —— 运行游戏、输入模拟和试玩验证。
- [godot-project-setup](../plugin/godot-mcp-unified/skills/godot-project-setup/SKILL.md) —— Godot 项目初始化与基础设置。
- [godot-ui](../plugin/godot-mcp-unified/skills/godot-ui/SKILL.md) —— Control 节点、主题定制与常见游戏 UI 模式。

## 阅读顺序建议

第一次了解项目时，先读项目根 README，再按需读接入层总览与插件 README；需要理解实现时阅读架构决策记录（ADR）与 daemon 源码。回归与验收类测试由仓库测试脚本与 test-project 承载，运行方式见各测试文件头部说明与 [`AGENTS.md`](../AGENTS.md) 的命令表。
