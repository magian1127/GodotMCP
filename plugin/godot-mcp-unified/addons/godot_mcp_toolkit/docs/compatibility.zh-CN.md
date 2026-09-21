[英文原文](compatibility.md)

# Godot 版本兼容性

## 版本层级

| Godot 版本 | 支持级别 | 说明 |
|---------------|---------------|-------|
| 4.0 - 4.1 | 不支持 | `EditorInterface` 不是全局单例，需要包装 70 多个调用点 |
| **4.2** | 核心 | 所有工具可用；部分 UI 降级（见下文） |
| **4.3** | 核心 | 新增 TileMapLayer 支持（tilemap 工具自动检测） |
| **4.4** | 完整 UI | 新增 Toast 通知（`EditorInterface.get_editor_toaster()` 从 4.4 起可用） |
| **4.5+** | 完整 | 所有工具和 UI 功能可用 |
| 4.8+（未来） | 预计 | `has_method()` 保护向前兼容；启动时仅警告 |

## 工具兼容性矩阵

| 工具 | 最低版本 | 较旧 Godot 上的行为 |
|------|-------------|------------------------|
| `scene_close` | 4.5 | 以带版本信息的 `UNSUPPORTED` 错误返回。4.5+ 可关闭活动或非活动标签页；关闭最后一个标签页会自动创建空场景 |
| `script_check` | 4.2 | 仅限 GDScript（`.gd`）；`.cs` 以 `INVALID_PARAMS` 拒绝。错误消息使用 `gdscript://` URI；通过剥离 `class_name` 修复误报 |
| `editor_sync` | 4.2 | 对指定 `file_paths` 逐文件刷新；省略时执行完整 `scan()`，随后等待导入管线空闲。两个阶段在所有版本有效 |
| `discover_tools(refresh_extensions:true)` | 4.2 | 4.2 上**编辑已有**扩展不会在会话内应用（缓存读取可避免引擎重导入崩溃，见下文）；`extension_refresh.hint` 会指出扩展并要求重启。增删扩展即时应用；4.3+ 所有变更即时应用 |
| `node_call_method` | 4.2 | **< 4.4** 上，若方法存在于节点磁盘上的 `.gd`（但活动实例过旧），`INVALID_METHOD` 会带重启 `hint`；真正拼写错误不会。4.4+ 有显示器的编辑器支持热重载，调用直接成功；但 **4.4+ 无头**编辑器不会重新实例化重载节点，因此也会触发旧代码 `hint`（无头提示：重新创建节点或重启有显示器的编辑器） |
| `lsp_diagnostics`、`lsp_symbols`、`lsp_hover`、`lsp_completion`、`lsp_navigate` | 4.2 | 4.2+ 可用。**< 4.5 的多编辑器冲突检测降级**：跨项目根不匹配检查需要 4.5+，所以 4.2–4.4 为每个编辑器提供不同的 `--lsp-port` + `GODOT_MCP_LSP_PORT`（见 [multi-instance.md](multi-instance.md)） |
| `log_read(channel:"editor")` | 4.2 | 所有版本捕获运行时输出（错误 / 警告 / 打印）。**编辑器解析错误**（`editor_sync` 重新编译脚本产生）只在 **4.5+** 出现（Logger API 会挂接编辑器诊断）；4.2–4.4 不写入 `godot.log`，因此 `source="buffer"` 与 `source="file"` 都无法返回——改用 `script_check` 或 `lsp_diagnostics`。能够捕获的多行错误（`SCRIPT ERROR: …` + `   at: <script>.gd:LINE`）按整体分级，因此用文件名 + `level_filter:["error"]` 查询可找到位置行。4.5+ 使用同步 Logger（每个错误一条，零延迟） |
| `animationtree_edit` | 4.2 | 4.2+ 所有操作有效（set_root、add/remove_node、add/remove_transition、set_property；所有版本都可枚举 transitions） |
| `animationtree_list` | 4.2 | 4.2+ 可列出，但**节点枚举需要 4.5+**：`AnimationNodeStateMachine.get_node_list()` 是 4.5 的脚本 API，4.2–4.4 不存在，因此返回 `nodes: []`、`nodes_count` 为 0；通过 `animationtree_edit` 的增删 / has 操作仍可用 |
| `input_simulate`（`send_text` 事件） | 4.2 | 每个 codepoint 合成一个 `InputEventKey.unicode`，经 `Viewport.push_input` 发送；所有 ≥4.0 的引擎 API 均相同，不受版本门控。非 ASCII 使用 `String.unicode_at`；`secret` `LineEdit` 的 `text_after` 会脱敏 |
| `debug_set_breakpoint` | 4.2 | 仅使用 Godot 内置脚本编辑器时所有版本可用。若编辑器配置了**外部编辑器**（Editor Settings → Text Editor → External，例如 VS Code），返回 `EXTERNAL_EDITOR_ACTIVE` 及引导提示——这是 **100% 的引擎限制**，与版本无关（见[外部脚本编辑器](#external-script-editor-engine-limitation)） |
| 其他所有工具 | 4.2 | 完整可用。变更操作在所有支持版本注册 UndoRedo 历史（Edit > Undo 撤销）；Toolkit 通过 `EditorPlugin.get_undo_redo()` 访问 `EditorUndoRedoManager`，该 API 自 4.0 稳定 |

### 外部脚本编辑器（引擎限制）

`debug_set_breakpoint` 必须使用内置脚本编辑器。外部编辑器无法接收引擎调试器断点，因此会返回 `EXTERNAL_EDITOR_ACTIVE` 信号。

### 按版本划分的降级行为

Godot 4.2–4.4 的许多限制是引擎 API 缺失或编辑器缓存行为；使用精确版本门控并在响应中提供说明。4.5+ 是完整兼容基线。通过工作区文件工具修改脚本后应调用 `editor_sync`；若 4.4 以下活动实例仍在运行旧代码，`node_call_method` 会提供重启提示。无头的 4.4+ 仍需重建节点或重启显示编辑器。

### EditorFileSystem 索引（所有版本）

文件和资源变更 MCP 工具（`resource_write`、`scene_create`、`project_delete` 等）会调用 `EditorFileSystem.update_file()`，并轮询 `get_file_type()` 确认索引完成后再返回。响应中的 `indexed` 或 `deindexed` 字段表明 EditorFileSystem 是否已完成索引。通过工作区文件工具修改源码后，请对变更路径调用 `editor_sync`。

### 幽灵标签页清理（场景 / 文件 / 文件夹删除）

`project_delete` 的 `kind:"scene"`、`kind:"file"`（`.tscn` / `.scn`）和 `kind:"folder"` 都会处理仍打开的标签页。若删除后存在 `stale_tabs` 数组，请随后对每个条目调用 `scene_close`（每次 MCP 往返都会带来相应成本）。文件夹删除使用切离策略，并返回 `stale_tabs` + `warnings`。

### C#（.NET）编辑器要求

C# 扩展需要 .NET 编辑器版本，并且构建后才会更新全局脚本类缓存。详见 `CompanionSkills/mcp-extension-creator/references/csharp-extensions.md`。并行编辑器设置另见 [multi-instance.md](multi-instance.md)。扩展接口的完整说明见 [extending.md](extending.md)。

### `script_check` 限制（所有版本）

`script_check` 使用 `GDScript.new().reload()` 验证。曾评估用 `ResourceLoader.load()` + `CACHE_MODE_IGNORE`，但校验前会处理 `class_name` 声明；因此同一名称可能已经存在于缓存。请将它视为脚本语法 / 解析检查，而不是完整项目编译。

### 退出挂起保存期间——一次性控制台噪音（所有版本）

如果退出发生在保存操作等待期间，控制台可能出现一次延迟队列相关消息。这是引擎清理路径的正常噪音，不表示保存失败；重启后再次读取文件即可确认。

### `editor_description` tooltip 计时器（Godot 4.3 引擎问题）

Godot 4.3 在每次节点添加 / 删除 / 移动时重建整个 scene dock（`tree->clear()`），并可能保留 `SceneTreeEditor` 节点缓存中的 `TreeItem`。在 `scene_create_node` 的内联属性路径中，处理器会断开节点上的 `editor_description_changed` 连接，防止写入时启动悬浮提示计时器；`Modules.CommandHelpers.disarm_tooltip_uaf(node, "editor_description")` 会立即解除它。

## UI 界面兼容性矩阵

| UI 界面 | 4.3 | 4.4 | 4.5+ | 较旧版本回退 |
|------------|-----|-----|------|-------------------|
| 底部面板 dock | 正常 | 正常 | 正常 | 4.2–4.5：`add_control_to_bottom_panel()`；4.6+：`add_dock()`（`EditorDock`），能力门控在 4.6 翻转 |
| 服务器状态、审计日志 | 正常 | 正常 | 正常 | 标准 Control 节点 |
| 引导式 onboarding 向导（3 步） | 正常 | 正常 | 正常 | `AcceptDialog` + `add_button()` 自 4.0 稳定；中文编辑器语言显示中文，其余语言显示英文 |
| Toast 通知 | 降级 | 正常 | 正常 | 静默跳过；向 Output 面板 `push_warning()` |
| 菜单项（Project > Tools） | 正常 | 正常 | 正常 | `add_tool_menu_item()` 自 4.0 稳定 |
| Command Palette 条目 | 正常 | 正常 | 正常 | 保护 `get_command_palette()`；不可用时跳过 |
| 信息 / 帮助面板 | 正常 | 正常 | 正常 | 标准 Control 节点 |
| 禁用插件清理对话框 | 正常 | 正常 | 正常 | 保护 `popup_dialog_centered()` 并回退 |
| 导出剥离（非脚本 + Text 模式） | 正常 | 正常 | 正常 | 4.2 所有模式都剥离（无二进制 token）；见下文 |
| 二进制 token 脚本泄漏警告 | 输出日志 | 导出对话框 | 导出对话框 | 4.2：不适用（无二进制 token 模式） |
| Inspector 插件 | 正常 | 正常 | 正常 | `EditorInspectorPlugin` API 自 4.0 稳定 |
| 响应上限配置 | 正常 | 正常 | 正常 | `SpinBox` / `LineEdit` 自 4.0 稳定 |

## 导出剥离（二进制 token 脚本缺口）

导出插件会在所有模式中剥离 `res://.mcp.json`，并将运行时自动加载置空。Godot 内置 GDScript 导出插件会先编译 `.gd` 为 `.gdc`，然后 Toolkit 才能运行剥离；由于 `EditorExportPreset.set_exclude_filter` 未绑定给 GDScript（4.6 仍如此），孤立 `.gdc` 可能保留。

| Godot | 是否泄漏二进制 token | 警告投递 |
|-------|--------------------|------------------|
| 4.2 | 否——没有二进制 token 模式；脚本以文本发布并被剥离 | 无（不会触发） |
| 4.3 | 是 | `push_warning()` → **Output / stderr 日志**（`add_message` 直到 4.4 才有绑定） |
| 4.4 | 是 | `EditorExportPlatform.add_message()` → **导出对话框** |
| 4.5+ | 是 | `add_message()` → **导出对话框** |

`project.binary` 还会携带无作用的 `[mcp_toolkit]` 配置标志；`user://` 数据永远不会打包。导出插件会保留它，但运行时服务器会拒绝在导出游戏中启动。

### 导出中仍保留导入 sidecar（仅 Godot 4.2）

Godot 4.2 可能仍把 `addons/godot_mcp_toolkit/icon.svg.import` 及其烘焙的 `.ctex`（约 8 KB）打包。若要彻底清理，可将 `res://addons/godot_mcp_toolkit/*` 加入导出预设的 exclude filter。

## 安全禁用插件

禁用插件前先移除自动加载。否则 `[autoload] MCPRuntimeServer` 行会产生一个随导出包发布的**悬空自动加载**。重新启用后，运行 `editor_sync` 并确认服务器状态，再进行游戏测试。

## 无头模式（`--headless`）

### 检测

使用 `DisplayServer.get_name() == "headless"`。需要显示器的工具应返回 `HEADLESS_UNSUPPORTED`，而不是空结果。

### 按工具的无头矩阵

| 工具 | 无头 | 说明 |
|------|----------|-------|
| `script_check`、`folder_create`、`project_delete` | ✅ | 删除通过文件、目录、场景、脚本和资源各自的安全路径 |
| `scene_create` | ✅ | 基于文件 |
| `scene_open` | ✅ | `EditorInterface.open_scene_from_path()` 在无头可用 |
| `scene_close` | ✅ | 需要 4.5+（与 GUI 相同） |
| `scene_get_tree`、`scene_create_node`、`scene_delete_node`、`scene_instantiate`、`scene_diff` | ✅ | 场景打开后可用 |
| `node_inspect`、`node_set_property`、`node_set_script`、`node_call_method` | ✅ | `node_call_method` 在 4.4+ 无头重载后不会重实例化；新方法会返回 `INVALID_METHOD` + 无头旧代码提示（重建或重启） |
| `signal_list`、`signal_manage`、`signal_emit` | ✅ | |
| `resource_load`、`resource_write`、`asset_query`、`asset_import` | ✅ | |
| `user_data_read`、`save_write`、`save_delete` | ✅ | |
| `classdb_query`、`project_get_settings`、`project_set_setting` | ✅ | |
| `input_map_edit`、`animation_keyframe`、`animation_get_keys`、`tilemap_set_cells`、`scene_create_3d`、`tileset_edit` | ✅ | |
| `editor_save_scene`、`editor_sync` | ✅ | |
| `log_read(channel="editor")` | ✅ | 可捕获可用的编辑器/运行时输出；编辑器解析错误的捕获仍随版本不同 |
| `game_start` | ❌ | 返回 `HEADLESS_UNSUPPORTED`；改用 `script_check`、场景检查和 editor-channel `log_read` |
| `game_stop` | ✅ | |
| `capture_screenshot` | ❌ | editor/runtime 两个 target 都需要渲染视口 |
| `runtime_inspect_node`、runtime-channel `log_read`、`animation_player_control`、`execute_code` | ⚠️ | 需要带运行时服务器的游戏 |
| `input_simulate` | ❌ | 输入事件需要显示器 |

✅ = 可用 &nbsp; ⚠️ = 取决于运行时服务器是否可用 &nbsp; ❌ = 需要显示器

### CI / 流水线使用

无头模式支持 CI 流水线和仅 SSH 工作流。典型 CI 设置：

```bash
godot --headless --editor --path /path/to/project &
# The addon's auto-spawn sidecar brings up the machine-level daemon;
# MCP clients connect via loopback HTTP (http://127.0.0.1:6590/) —
# there is no per-session Node server to launch anymore.
```

文件工具（脚本、资源、场景、文件夹）、ClassDB 内省和项目设置均无需显示器。场景树操作也可用——`scene_open` 以编程方式加载场景，完整节点 / 信号工具链从此可用。

## 客户端连接（daemon HTTP）

自插件 1.1.0 起 Node 桥退役，所有平台都让 MCP 客户端经回环 HTTP 连接常驻 daemon——随包 Node 服务器已不存在。如果无法连接：从终端启动客户端以查看真实错误；确认项目根存在 `.mcp.json`；确认 daemon 正在监听 `127.0.0.1:6590`（编辑器边车或宿主安装器会拉起）。完整排查见 [advanced_configuration.zh-CN.md](advanced_configuration.zh-CN.md)。

## 向前兼容

未来 Godot 版本由 `has_method()` 保护和能力检查覆盖；未知能力会在启动时警告，而不会阻止其余工具加载。新版本仍应使用完整验证矩阵进行回归测试。

## 数据格式说明

工具响应使用 JSON 可编码的 Dictionary / Array；引擎类型采用显式 `{type: "...", ...}` 包装。大型 `user_data_read` 响应遵循分页契约；源码则通过工作区文件读取器按范围读取。见 `advanced_configuration.md`。常见问题还可查阅 `bundled local documentation`。
