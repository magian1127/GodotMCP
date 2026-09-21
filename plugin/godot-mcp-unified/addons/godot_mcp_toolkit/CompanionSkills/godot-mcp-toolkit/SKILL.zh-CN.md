---
name: godot-mcp-unified
description: >
  通过 Godot MCP Unified 构建、检查、调试和游玩测试 Godot 4 项目。
  覆盖当前合并后的工具面、按需组生命周期、安全规则和证据闭环。
when_to_use: >
  当 MCP 智能体通过 scene_get_tree、node_inspect、node_set_property、
  game_start 或 discover_tools 等工具处理 Godot 项目时使用。
---

[英文原文](SKILL.md)

# Godot MCP Unified

通过足够小的工具面驱动实时编辑器，并让项目停留在已验证状态。

## 工具面生命周期

启动面包含 19 个业务工具和 `discover_tools`。只为当前工作阶段加载工具组，记录本次加载的组，普通工作同时保持不超过 3 个；阶段结束后使用 `discover_tools(reset=[...])` 释放下一阶段不再需要的组。跨领域任务可临时达到 5 个组。

新增、编辑或删除项目扩展后使用 `discover_tools(refresh_extensions:true)`。Godot 4.2 修改已有扩展时可能需要重启编辑器；检查 `extension_refresh.hint`。

服务器只有以 `GODOT_MCP_UNSAFE=1` 启动时才会暴露 `unsafe` 组。仅在用户明确授权且类型化工具无法表达当前操作时启用。

## 当前路由

| 目标 | 工具或工具组 |
|---|---|
| 检查场景 | `scene_get_tree`、`scene_query`、`node_inspect`；几何布局加载 `scene_spatial` |
| 编辑节点 | `scene_create_node`、`node_manage`、`node_set_property`、`node_set_script`、`scene_delete_node` |
| 编辑项目文件 | 使用宿主的文件读取/补丁工具，然后加载 `editor_advanced` 并调用 `editor_sync` |
| 项目配置 | `project_get_settings`；按需加载 `project_config`、`node_advanced`、`input_map` 或 `layer_naming` |
| 素材与资源 | 加载 `asset_ops`、`resource_io` 或 `cleanup`；`project_delete` 统一处理项目路径删除 |
| 运行时与游玩测试 | `game_start`、`runtime_inspect_node`、`input_simulate`、`capture_screenshot`、`log_read`；时间/动画控制加载 `runtime_advanced` |
| GDScript 智能 | `script_check`；按需加载 `lsp_code_analysis`、`lsp_code_navigation` 或 `classdb` |
| 调试器 | `log_read`；加载 `debugger` 使用 `debug_inspect`、断点和继续执行 |
| 3D、Tile 与动画 | 加载最窄的领域组；使用 `scene_create_3d`、`tileset_edit` 和 `spriteframes_create`，不再使用旧的逐动作工具 |

只有需要详细的 `input_simulate` 事件载荷时才阅读 [input-events.md](references/input-events.md)。多个智能体或编辑器实例共享项目时阅读 [parallel-sessions.md](references/parallel-sessions.md)。发送 Godot 引擎值或嵌套资源时阅读 [type-wrappers.md](references/type-wrappers.md)。

## 创作闭环

1. 解析准确项目，并检查当前场景和项目状态。
2. 只加载缺失的工具组，执行最小的类型化修改；重复编辑使用批量输入。
3. 显式保存编辑场景；宿主侧文件变更后调用 `editor_sync`。
4. 对变更后的 GDScript 运行 `script_check`，跨文件修改使用 `lsp_diagnostics(scope="project")`。
5. 行为发生变化时运行或游玩测试。逻辑使用结构化状态/日志证据，外观使用截图。
6. 恢复运行时状态，只停止本次启动的会话，并释放不再需要的工具组。

## 确定性游玩测试

从 `game_start(wait_for_runtime=true)` 开始。使用 `runtime_inspect_node` 和 `log_read` 捕获结构化基线。时间敏感行为使用 `runtime_time_control` 冻结和推进；尽量把输入作为一个有界的 `input_simulate` 序列发送。`capture_screenshot(target="runtime")` 只用于视觉证据。清理时始终解除冻结并释放按住的输入。

## 安全与错误

- 保留无关的用户编辑和活动场景。
- 将 `project_delete`、递归删除、项目设置、外部素材导入、`node_call_method` 和 `execute_code` 视为重要后果操作。
- 删除类型或范围不明显时，先调用 `project_delete(dry_run=true)`。
- 文件路径保持在 `res://` 或预期的 `user://` 范围内，并保留服务端守卫。
- `GAME_NOT_RUNNING`：检查 `log_read(channel="auto")`，修复错误后再启动。
- `NOT_FOUND`：重试前检查 `scene_get_tree`、`scene_query`、`asset_query` 或工作区。
- `COMPILATION_FAILED`：调用 `editor_sync`，再运行 `script_check` 和 `log_read(channel="editor")`。
- 串行修改排队后超时可能表示修改已经执行；重试前先检查状态。

完成条件是：编辑器状态已保存、相关诊断干净、观察到的编辑器/运行时行为符合请求，并已清理临时会话和加载的工具组。
