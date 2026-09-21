---
name: godot-playtest
description: 通过 MCP 驱动的确定性游玩测试验证 Godot 游戏玩法、输入、时序、动画、UI 交互、运行时状态或视觉行为。当用户要求测试、游玩、观察、比较前后状态、复现运行时行为，或证明游戏变更有效时使用。
---

语言：中文 | [英文版](SKILL.en.md)

# Godot 游玩测试

建立有界且可重复的证据循环。

1. 记录游戏是否已经在运行。仅在需要时使用 `game_start(wait_for_runtime=true)` 启动；只停止由你启动的会话。
2. 使用 `discover_tools(include_schemas=true)` 加载 runtime_advanced，并记录该组是否由本次测试加载。
3. 使用 `runtime_inspect_node`、信号或 `log_read(channel="runtime")` 捕获结构化基线。将 `capture_screenshot(target="runtime")` 保留给外观验证。
4. 对时间敏感的行为：
   - `runtime_time_control(action="freeze")`。
   - 尽可能将输入作为一个 `input_simulate` 事件序列发送。按下的动作可能跨越一个步骤；之后要显式释放它。
   - 使用 `runtime_time_control(action="step", frames=N, report=[...])` 推进，或者使用 `action="step_until"`、`until` 和 `max_frames` 在有界条件下停止。
   - 再次推进前，检查冻结停止帧的状态。
5. 将观察到的状态与明确的预期值比较。使用截图验证布局/外观，使用结构化状态/日志验证行为。
6. 清理时始终解除冻结。释放按住的输入；如果本次运行启动了游戏，则停止游戏；如果 runtime_advanced 是本次测试加载且后续阶段不再需要，则使用 `discover_tools(reset=["runtime_advanced"])` 释放。

避免盲目的墙上时钟休眠；`click_node` 可用时不要猜测坐标；对于有状态行为不要仅凭截图下结论。通过的游玩测试应说明初始条件、输入序列、帧数/条件边界和最终观察值。
