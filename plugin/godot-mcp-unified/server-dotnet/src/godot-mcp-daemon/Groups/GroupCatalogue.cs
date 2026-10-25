namespace GodotMcp.Daemon.Groups;

/// <summary>内置工具组定义(数据)——名称/描述/成员工具/关键词。</summary>
/// <param name="Name">组名(discover_tools 的激活目标标识)。</param>
/// <param name="Description">组描述,透出在目录与 discover_tools 描述里。</param>
/// <param name="ToolNames">组内成员工具名(与 NodeToolTable 组工具对应)。</param>
/// <param name="Keywords">模糊匹配关键词(discover_tools 按关键词打分激活)。</param>
public sealed record GroupDef(string Name, string Description, IReadOnlyList<string> ToolNames, IReadOnlyList<string> Keywords);

/// <summary>
/// 17 个内置组的静态目录(ADR-0007)。这是组划分的<b>唯一真源</b>。
/// </summary>
/// <remarks>
/// <para><b>为什么是 17 组而不是原来的 31 组</b>:31 组里有大量"一个工具一组"的碎片
/// (如 <c>tileset_edit</c>、<c>input_map</c>、<c>classdb</c>),它们让目录变长而没有帮助;
/// 17 组按<b>工作流</b>合并(实测使用率见 <c>.scratch/godot-toolface/stats.md</c>)。</para>
/// <para><b>两处有意重叠</b>:<c>node_set_property</c> ∈ {scene_edit, playtest},
/// <c>signal_emit</c> ∈ {playtest, signals}。因此可见集必须按<b>活跃组并集</b>重算,
/// 不能对单个组做增量增删 —— 否则停用一个组会摘掉另一个组还在用的工具。</para>
/// <para><b>组名硬改、不留别名</b>:旧组名统一判 unknown-group,由 P1 的全仓改名保证
/// skills / addons / 适配器文档同步更新。</para>
/// </remarks>
public static class GroupCatalogue
{
    /// <summary>全部内置组定义(目录按此序展示)。</summary>
    public static readonly IReadOnlyList<GroupDef> Groups =
    [
        new("scene_edit", "创建/打开/保存场景与节点，设置属性与脚本，管理节点组和 Control 布局，比较并实例化场景。",
            ["scene_create", "scene_create_node", "scene_delete_node", "scene_open", "scene_create_inherited",
                "scene_diff", "scene_instantiate", "node_manage", "node_set_property", "node_set_script",
                "node_groups", "control_set_layout", "editor_save_scene", "scene_spatial_map"],
            ["scene", "node", "create node", "delete node", "open scene", "save scene", "property", "script",
                "node group", "control layout", "anchors", "offsets", "ui layout", "instantiate", "prefab",
                "inheritance", "inherited scene", "variant", "base scene", "scene diff", "compare",
                "spatial", "bounds", "overlap", "gap", "containment", "position"]),

        new("world_3d", "创建 3D 基本形状、灯光、摄像机，以及配置导航区域、网格和避障。",
            ["scene_create_3d", "navigation_edit"],
            ["3d", "mesh", "meshinstance", "primitive", "camera3d", "light", "environment",
                "directional light", "world environment", "sky", "nav", "navigation", "navmesh",
                "pathfinding", "obstacle", "avoidance", "navigation region", "nav polygon"]),

        new("playtest", "确定性游玩测试：启动/停止游戏、模拟输入、检查运行时状态、推进时间、控制动画播放。",
            ["game_start", "game_stop", "input_simulate", "runtime_inspect_node", "runtime_time_control",
                "animation_player_control", "node_set_property", "signal_emit"],
            ["playtest", "run game", "start game", "stop game", "input", "simulate input", "runtime",
                "inspect runtime", "animation playback", "animationplayer", "play animation", "freeze game",
                "step frames", "deterministic playtest", "emit", "emit signal"]),

        new("signals", "在编辑器或运行时检查、连接、断开和发出信号。",
            ["signal_list", "signal_manage", "signal_emit"],
            ["signal", "connect", "disconnect", "emit", "observer", "event", "handler", "callback"]),

        new("code_intel", "GDScript/C# 静态分析：诊断、符号、悬停、补全、跳转、类层级查询、整项目检查。",
            ["lsp_diagnostics", "lsp_symbols", "lsp_hover", "lsp_completion", "lsp_navigate",
                "classdb_query", "script_check"],
            ["lsp", "diagnostics", "symbols", "hover", "type", "gdscript", "errors", "warnings",
                "completion", "definition", "references", "go to definition", "find references",
                "autocomplete", "navigate", "cross-file", "class", "classdb", "api", "inheritance",
                "introspection", "validate", "analyze", "project", "all scripts", "compile"]),

        new("assets", "浏览与导入资产、创建目录、加载或写入 Godot 资源（.tres/.res）、查询依赖关系。",
            ["asset_query", "asset_import", "folder_create", "resource_load", "resource_write"],
            ["asset", "import", "dependencies", "dependents", "referenced by", "who references", "uid",
                "texture", "image", "list assets", "files", "browse", "uid_to_path", "path_to_uid",
                "folder", "directory", "resource", "load", "write", "save resource", "tres", "res"]),

        new("cleanup", "安全删除项目路径，或关闭已打开的场景标签页。",
            ["project_delete", "scene_close"],
            ["delete", "cleanup", "close", "remove", "delete file", "delete scene", "delete script"]),

        new("project_config", "项目设置、自动加载单例、物理/渲染/导航层名、输入动作与按键绑定。",
            ["project_get_settings", "project_set_setting", "autoload_manage", "layer_names_get",
                "layer_names_set", "input_map_edit"],
            ["project settings", "configuration", "autoload", "singleton", "project.godot",
                "layer", "layer name", "physics layer", "render layer", "collision layer",
                "collision mask", "mask", "input", "input map", "action", "key binding", "keybind",
                "keyboard", "controller", "gamepad", "joystick"]),

        new("editor_ops", "编辑器自身操作：刷新外部改动、管理编辑器窗口、应答编辑器对话框。",
            ["editor_sync", "editor_windows", "editor_answer_dialog"],
            ["refresh", "reload scripts", "rescan", "filesystem", "reimport", "wait idle",
                "sync external files", "editor window", "dialog", "popup", "confirm"]),

        new("build_csharp", "构建 C# 项目：发起构建、查询进度、等待构建结束。",
            ["editor_build_csharp", "editor_build_csharp_status", "editor_build_csharp_wait"],
            ["csharp", "c#", "build", "compile", "dotnet", "msbuild", "build status", "wait build"]),

        new("tilemap", "读取和绘制 TileMap 单元格，创建 TileSet、添加图集源、配置层与替代图块、编辑逐图块属性。",
            ["tilemap_read_cells", "tilemap_set_cells", "tileset_create", "tileset_add_source",
                "tileset_remove_source", "tileset_add_alternative", "tileset_remove_alternative",
                "tileset_setup_layers", "tileset_edit"],
            ["tilemap", "tile", "grid", "cell", "read cells", "paint cells", "2d",
                "tileset", "atlas", "tile source", "tile layer", "terrain set", "tile alternative",
                "tile variant", "create tileset", "tileset collision", "tile physics", "tile terrain",
                "tile navigation", "tile occlusion", "tile animation", "tile custom data", "peering bits"]),

        new("animation", "关键帧与轨道编辑、AnimationTree 状态机、SpriteFrames 逐帧动画。",
            ["animation_keyframe", "animation_get_keys", "animationtree_edit", "animationtree_list",
                "spriteframes_create", "spriteframes_edit"],
            ["animation", "keyframe", "track", "animate", "animationtree", "state machine", "blend tree",
                "transition", "blend", "sprite", "spriteframes", "animated sprite", "frame", "flipbook",
                "2d animation", "spritesheet", "atlas"]),

        new("art_fx", "程序化资源（渐变/曲线/噪声）、粒子系统、主题样式、Path2D 曲线、从纹理生成碰撞。",
            ["particles_create", "procedural_edit_gradient", "procedural_edit_curve", "procedural_edit_noise",
                "theme_edit", "path2d_edit_curve", "collision_from_texture"],
            ["procedural", "generate", "gradient", "noise", "curve", "resource create", "fastnoiselite",
                "easing", "particle", "particles", "gpu particles", "vfx", "visual effect", "effects",
                "fire", "smoke", "sparks", "rain", "snow", "explosion", "emitter", "particle system",
                "theme", "style", "stylebox", "font", "color", "ui style", "control theme",
                "path", "path2d", "bezier", "spline", "follow path", "pathfollow", "curve2d",
                "collision", "collision polygon", "bitmap", "alpha", "shape from texture"]),

        new("audio", "列出和配置音频总线、效果及音量。",
            ["audiobus_edit", "audiobus_list"],
            ["audio", "audiobus", "sound", "music", "volume", "bus", "effect", "reverb", "sfx"]),

        new("debugger", "检查调试器状态，管理断点，控制执行流程。",
            ["debug_inspect", "debug_set_breakpoint", "debug_continue"],
            ["debug", "breakpoint", "pause", "continue", "step", "debugger", "state", "breaked"]),

        new("user_data", "读取、写入、删除和列出 user:// 存档文件。",
            ["user_data_read", "save_write", "save_delete"],
            ["save", "save file", "user data", "persistence", "save game", "load game", "savegame"]),

        new("unsafe", "显式执行任意表达式或当前编辑场景的方法；需要设置 GODOT_MCP_UNSAFE=1。",
            ["execute_code", "node_call_method"],
            ["unsafe", "arbitrary expression", "execute code", "call method", "escape hatch", "rce"]),
    ];

    /// <summary>全部内置组名(与 Groups 同序)。</summary>
    public static readonly IReadOnlyList<string> GroupNames = Groups.Select(g => g.Name).ToList();

    /// <summary>组名 → 定义索引(序数比较;O(1) 精确查找)。</summary>
    private static readonly Dictionary<string, GroupDef> ByName =
        Groups.ToDictionary(g => g.Name, StringComparer.Ordinal);

    /// <summary>按名称精确查找内置组。</summary>
    /// <param name="name">组名。</param>
    /// <returns>命中的组定义;未命中为 null(调用方再走扩展组分发)。</returns>
    public static GroupDef? Find(string name) => ByName.GetValueOrDefault(name);
}
