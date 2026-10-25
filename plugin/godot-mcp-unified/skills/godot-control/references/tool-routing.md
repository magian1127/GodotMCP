语言：中文 | [英文版](tool-routing.en.md)

# 工具路由

## 连接当前会话

未连接时只有 `godot_connect`。首次调用 `godot_connect(project="<Godot 项目绝对路径>", groups=[...])` 后，从 9 个核心工具开始按需扩面。省略项目时依次使用本会话默认目标、cwd 下的 `project.godot`、唯一活跃实例；仅检查 cwd 本层，不递归扫描。常规工具寻址优先级是显式 `instance`、本会话默认目标、唯一活跃实例。

原生名称以最新工具列表为准：入口叫 `godot_connect`，其余如 `scene_get_tree` 没有统一的 `godot_` 前缀。宿主可添加命名空间；DSH 默认显示 `mcp__godot__godot_connect`、`mcp__godot__scene_get_tree`。

## 按任务加载

通过 `discover_tools(request=[组名], include_schemas=true)` 激活；`activate=false` 仅浏览，`reset=[组名]` 停用指定组，`reset=true` 停用全部组。只传 `include_schemas=true` 不会激活任何组。记录本阶段加载的组，在阶段结束时释放不再需要的组。

| 需求 | 核心工具或先加载的组 |
| --- | --- |
| 场景/节点只读检查 | 核心 `scene_get_tree`、`scene_query`、`node_inspect` |
| 场景/节点创建、属性、脚本挂载、保存、空间布局 | `scene_edit` |
| 外部文件修改后刷新/等待导入，编辑器窗口/对话框 | `editor_ops` |
| GDScript 检查、LSP、引擎 API 查询 | `code_intel`：`script_check`、`lsp_*`、`classdb_query` |
| C# 构建、状态与等待 | `build_csharp` |
| 项目设置、autoload、层名、输入映射 | `project_config` |
| 资源读写、素材查询/导入、建目录 | `assets` |
| 关闭场景、删除项目文件 | `cleanup` |
| TileMap 与 TileSet 操作 | `tilemap` |
| 动画关键帧、AnimationTree、SpriteFrames | `animation` |
| 粒子、渐变/曲线/噪声、Theme、Path2D、纹理碰撞 | `art_fx` |
| 3D 场景与导航 | `world_3d`；普通节点编辑仍用 `scene_edit` |
| 信号查询、连接、发射 | `signals`；运行时发射也属于 `playtest` |
| 启停游戏、输入、运行时检查、时间/动画控制 | `playtest`；截图与日志是核心工具 |
| 调试器、断点、继续运行 | `debugger` |
| 音频总线 | `audio` |
| 读取用户数据、写入/删除存档 | `user_data` |
| 任意代码或方法调用 | `unsafe`：`execute_code`、`node_call_method` |
| 占位资源 | [placeholder-assets.md](placeholder-assets.md)；导入现有素材用 `assets` |

组名以 `discover_tools` 静态描述和目录为准。工具名与组名可能不同：例如 `tileset_edit`、`input_map_edit`、`theme_edit`、`classdb_query` 是工具，分别属于 `tilemap`、`project_config`、`art_fx`、`code_intel`。

## 参数与恢复

- 每个工具的参数以宿主最新工具列表中的 schema 为准。当前 `include_schemas=true` 对部分常驻组工具只返回名称；此时读取激活后的 `tools/list`，不要猜参数。
- 连接/激活后确认宿主工具列表已更新。`ttlMs=0` 不保证所有宿主自动重拉；程序客户端调用成功也不等于当前聊天已挂载工具。工具面仍旧时使用宿主实际提供的重连方式，必要时新建会话。
- daemon 重启后核对目标和组。shim 当前仅重放最近的 connect/discover 请求，不能保证恢复多次增量激活的最终集合；宿主 listen 也未自动恢复。重新连接并按当前任务激活所需组。
- 调整现有组使用 `discover_tools(reset=...)`。当前 `godot_connect(groups=...)` 替换后旧组桶可能被一次浏览写回；停用后重新核对实际列表。
- 多项目交错任务显式传 `instance`；同一会话的目标切换与复合工具调用顺序完成，避免在途调用跨项目。
- `SESSION_HEADER_MISSING` 等会话头错误应检查 stdio shim 配置；重复 connect 不能修复缺失的传输头。`TARGET_NOT_RUNNING` 时启动提示路径对应的编辑器，再重试只读查询。
- 超时或传输中断后先读取状态，再决定是否重新提交变更。`unsafe` 需要 daemon 以 `GODOT_MCP_UNSAFE=1` 启动，并且当前操作已经获得用户授权、专用工具无法表达。
