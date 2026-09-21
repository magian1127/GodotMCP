---
name: godot-debug
description: 通过 Godot MCP Unified 诊断并修复已报告的 Godot 错误、崩溃、损坏的场景、无效脚本、调试器故障或运行时回归。当某项内容已经失败或行为不正确时使用；普通功能开发使用 godot-control，计划中的游戏玩法验证使用 godot-playtest。
---

语言：中文 | [英文版](SKILL.en.md)

# Godot 调试

运行以证据为先的修复循环。

1. 确定确切的项目、场景、复现操作、预期行为和观察到的失败。不要根据过时的注册表条目或单行日志进行推测。
2. 编辑前收集两个通道的信息：
   - 对变更文件使用 `script_check`，对跨文件 GDScript 错误使用 `lsp_diagnostics(scope="project")`。
   - 使用 `log_read(channel="editor")` 获取编辑器/导入警告。
   - 使用 `log_read(channel="runtime")` 获取运行中游戏的输出和崩溃上下文。
   - 加载 debugger 工具组以使用断点、暂停/继续和 `debug_inspect`。
3. 检查相关的场景树、节点属性、脚本、信号和资源。区分解析/导入错误、编辑器状态和运行时状态。
4. 通过类型化的场景/脚本/资源工具进行最小修正。保留无关编辑，避免将 `execute_code` 用作猜测性探针。
5. 重复原始复现步骤。重新运行诊断，并比较具体的状态/日志值。

记录本次诊断加载的 debugger、LSP 或其他按需组；修复验证结束后，使用 `discover_tools(reset=[...])` 释放后续阶段不再需要的组。

对于时间或输入回归，调用 godot-playtest，并使用 `runtime_time_control`，不要使用按墙上时钟计时的休眠。原始失败已复现为红色、修复使同一检查变为绿色且没有出现新的诊断信息时，停止。
