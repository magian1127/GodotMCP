# Godot MCP Unified 工作区

中文 | [English](README.en.md)

为 Godot 4 提供 MCP（Model Context Protocol）控制面的插件生态：AI 编码助手会话（ZCode、Codex、DSH、VS Code）通过机器级单例 daemon 同时操控多个 Godot 项目与实例——场景、节点、脚本、资源的编辑与查询，确定性游玩测试，截图验证与 LSP 诊断。

## 核心能力

- **83 个内置 MCP 工具**：启动面 19 常驻 + 1 个 `discover_tools` 元工具；另有 31 组 64 个按需工具组，按需激活以控制上下文成本。
- **确定性游玩测试**：运行游戏、输入模拟、运行时状态检查、时间控制与逐帧步进、截图内联返回。
- **多实例并行**：机器级注册表自动发现所有运行中的 Godot 实例，daemon 多路复用，一台机器可同时开发多个项目。
- **本地优先安全**：仅监听回环地址 + Bearer token 鉴权；游戏导出构建自动剥离插件。
- **多客户端接入**：Codex、ZCode、DSH、VS Code 各有适配层；MCP 工具统一以 `godot` 服务器/key 提供（`mcp__godot__<tool>`，DSH 门面为 `godot_<tool>`）。

## 快速开始

1. **安装插件到 Godot 项目**（构建桥接、备份并替换 addon、启用插件、headless 验证）：

       & "<仓库>\plugin\godot-mcp-unified\scripts\install-godot-project.ps1" -ProjectPath "<Godot 项目>" -GodotExecutable "<Godot 可执行文件>"

2. **接入你的 AI 客户端**：通道总览见 [`adapters/README.md`](adapters/README.md)（Codex / ZCode / DSH / VS Code 各一行入口与专用文档）。
3. **可选**：复制 `.env.example` 为 `.env` 填入本机 Godot 路径，脚本即可免参数使用（该文件不入版本管理）。

前提：Godot 4.x（.NET/mono 版），目标项目的编辑器处于运行状态。完整前置与故障排查见各客户端适配文档。

## 仓库地图

| 目录 / 文件 | 职责 | 详情 |
| --- | --- | --- |
| `plugin/godot-mcp-unified/` | 唯一插件真源：编辑器/运行时 addon、C# daemon 与 shim、6 个技能、项目模板 | [README](plugin/godot-mcp-unified/README.md) |
| `adapters/` | 各 AI 客户端的接入管理层（通道总览、安装与链接脚本） | [README](adapters/README.md) |
| `docs/` | 维护文档、架构决策记录（ADR）、客户端安装指南 | [导航](docs/README.md) |
| `test-project/` | Godot 4.7.2 Mono 验收项目（addon 经 junction 指向真源） | [README](test-project/README.md) |
| `scripts/` | 仓库级开发/验证工具（文档一致性、配置发现回归） | [README](scripts/README.md) |
| `marketplace.json` | 本地插件市场清单（市场根 = 仓库根） | — |

## 文档

- **文档总导航**：[`docs/README.md`](docs/README.md)
- **代理/贡献者须知**：[`AGENTS.md`](AGENTS.md)——仓库布局细节、构建与测试命令、文档与脚本归属规则、发布前检查清单
- **领域词汇表**：[`CONTEXT.md`](CONTEXT.md)
- **架构决策记录**：[`docs/adr/`](docs/adr/)
## 许可证

见 [LICENSE](LICENSE)。
