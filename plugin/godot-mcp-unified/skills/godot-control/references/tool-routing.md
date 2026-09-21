语言：中文 | [英文版](tool-routing.en.md)

# 工具路由

从始终可见的工具开始，然后只加载拥有缺失操作的工具组。

| 需求 | 优先使用 |
|---|---|
| 当前场景、节点、属性 | scene_get_tree、scene_query、node_inspect；空间布局加载 scene_spatial |
| 场景/节点变更 | scene_create、scene_create_node、node_manage、node_set_property、node_set_script、scene_delete_node、editor_save_scene |
| 脚本 | Codex 工作区读取/补丁工具和 script_check；引擎诊断/符号加载 LSP 组 |
| 项目配置 | project_get_settings；写设置或管理 autoload 时加载 project_config，节点组加载 node_advanced，输入映射加载 input_map |
| 资源和文件 | resource_io、asset_ops、cleanup、user_data；resource_io 包含 folder_create |
| 2D 创作 | path_editing、tilemap、tileset、tileset_edit、spriteframes、particles、navigation、procedural |
| 3D 创作 | 3d_tools 中的 scene_create_3d、particles、navigation、procedural，以及普通的场景/节点/属性工具 |
| UI 和主题 | node_advanced、theme、普通的节点/属性工具；信号操作加载 signals |
| 动画和音频 | animation_authoring、runtime_advanced、audio |
| 游玩测试/运行时 | game_start、game_stop、capture_screenshot、runtime_inspect_node、input_simulate、node_set_property(channel="runtime")；动画/时间控制加载 runtime_advanced |
| 调试器 | log_read、script_check；需要时加载 debugger 使用 debug_inspect/断点控制，并加载 LSP 组 |
| 引擎 API 发现 | 在猜测类名、方法、属性、枚举或默认值之前，先加载 classdb 并使用 classdb_query |
| 占位资源 | 阅读 placeholder-assets.md 并运行随附脚本；对用户提供的资源使用 asset_import |

外部文件变更后加载 editor_advanced 并调用 editor_sync。

`execute_code` 和 `node_call_method` 位于默认关闭的 unsafe 组。它们是等同于 RCE 的逃生舱；只有服务器以 `GODOT_MCP_UNSAFE=1` 启动、用户明确授权且没有类型化工具能表达当前操作时才加载。
