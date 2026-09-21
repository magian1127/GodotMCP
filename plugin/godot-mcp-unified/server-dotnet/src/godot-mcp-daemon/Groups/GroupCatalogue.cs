namespace GodotMcp.Daemon.Groups;

/// <summary>内置工具组定义(数据)——名称/描述/成员工具/关键词。真源:
/// Node 桥 src/groups/defs/*.ts(31 组,一文件一定义)。</summary>
/// <param name="Name">组名(discover_tools 的激活目标标识)。</param>
/// <param name="Description">组描述,透出在目录与 discover_tools 描述里。</param>
/// <param name="ToolNames">组内成员工具名(与 NodeToolTable 组工具对应)。</param>
/// <param name="Keywords">模糊匹配关键词(discover_tools 按关键词打分激活)。</param>
public sealed record GroupDef(string Name, string Description, IReadOnlyList<string> ToolNames, IReadOnlyList<string> Keywords);

/// <summary>31 个内置组的静态目录(与 Node builtinGroups 装配顺序一致)。</summary>
public static class GroupCatalogue
{
    /// <summary>全部内置组定义(装配顺序与 Node builtinGroups 一致,目录按此序展示)。</summary>
    public static readonly IReadOnlyList<GroupDef> Groups =
    [
        new("3d_tools", "创建 3D 基本形状、灯光、摄像机和环境配置。", ["scene_create_3d"],
            ["3d", "mesh", "meshinstance", "primitive", "camera3d", "light", "environment", "directional light", "world environment", "sky"]),
        new("animation_authoring", "检查和制作关键帧，编辑轨道，配置 AnimationTree 状态机。",
            ["animation_keyframe", "animation_get_keys", "animationtree_edit", "animationtree_list"],
            ["animation", "keyframe", "track", "animate", "animationtree", "state machine", "blend tree", "transition", "blend"]),
        new("asset_ops", "列出资产、查询正向/反向依赖，并在 uid:// 与路径之间互转。", ["asset_query", "asset_import"],
            ["asset", "import", "dependencies", "dependents", "referenced by", "who references", "uid", "texture", "image", "list assets", "files", "browse", "uid_to_path", "path_to_uid"]),
        new("audio", "列出和配置音频总线、效果及音量。", ["audiobus_edit", "audiobus_list"],
            ["audio", "audiobus", "sound", "music", "volume", "bus", "effect", "reverb", "sfx"]),
        new("classdb", "搜索和检查 Godot 类层级，包括属性、方法、信号和继承关系。", ["classdb_query"],
            ["class", "classdb", "api", "inheritance", "introspection"]),
        new("cleanup", "安全删除项目路径，或关闭已打开的场景标签页。", ["project_delete", "scene_close"],
            ["delete", "cleanup", "close", "remove", "delete file", "delete scene", "delete script"]),
        new("debugger", "检查调试器状态，管理断点，控制执行流程。",
            ["debug_inspect", "debug_set_breakpoint", "debug_continue"],
            ["debug", "breakpoint", "pause", "continue", "step", "debugger", "state", "breaked"]),
        new("editor_advanced", "刷新外部修改的文件，等待编辑器完成导入并进入空闲状态。", ["editor_sync"],
            ["refresh", "reload scripts", "rescan", "filesystem", "reimport", "wait idle", "sync external files"]),
        new("input_map", "列出、创建和编辑输入动作，以及键盘和手柄绑定。", ["input_map_edit"],
            ["input", "input map", "action", "key binding", "keybind", "keyboard", "controller", "gamepad", "joystick"]),
        new("layer_naming", "获取和设置物理层、渲染层及导航层的名称。", ["layer_names_set", "layer_names_get"],
            ["layer", "layer name", "physics layer", "render layer", "collision layer", "collision mask", "mask"]),
        new("lsp_code_analysis", "通过语言服务器获取 GDScript 诊断、符号和悬停信息，并检查整个项目的编译情况。",
            ["lsp_diagnostics", "lsp_symbols", "lsp_hover"],
            ["lsp", "diagnostics", "symbols", "hover", "type", "gdscript", "shader", "gdshader", "errors", "warnings",
                "validate", "analyze", "project", "whole project", "all scripts", "compile"]),
        new("lsp_code_navigation", "通过语言服务器进行代码补全、跳转到定义和查找引用。",
            ["lsp_completion", "lsp_navigate"],
            ["completion", "definition", "references", "go to definition", "find references", "autocomplete", "navigate", "cross-file"]),
        new("navigation", "配置导航区域、网格和避障。", ["navigation_edit"],
            ["nav", "navigation", "navmesh", "pathfinding", "navigate", "obstacle", "avoidance", "navigation region", "nav polygon", "ai pathfinding"]),
        new("node_advanced", "管理节点组，应用 Control 布局预设。", ["node_groups", "control_set_layout"],
            ["node group", "tag node", "control layout", "anchors", "offsets", "ui layout"]),
        new("particles", "创建和配置用于视觉效果的 GPU 粒子系统。", ["particles_create"],
            ["particle", "particles", "gpu particles", "vfx", "visual effect", "effects", "fire", "smoke", "sparks",
                "rain", "snow", "explosion", "emitter", "particle system"]),
        new("path_editing", "编辑 Path2D 曲线，根据精灵纹理生成碰撞形状。", ["path2d_edit_curve", "collision_from_texture"],
            ["path", "path2d", "curve", "bezier", "spline", "follow path", "pathfollow", "curve2d", "2d", "collision",
                "collision polygon", "sprite", "bitmap", "alpha", "shape from texture"]),
        new("procedural", "编辑用于程序化生成的渐变、曲线和 FastNoiseLite 资源。",
            ["procedural_edit_gradient", "procedural_edit_curve", "procedural_edit_noise"],
            ["procedural", "generate", "gradient", "noise", "curve", "resource create", "fastnoiselite", "easing"]),
        new("project_config", "修改项目设置，管理自动加载单例。", ["project_set_setting", "autoload_manage"],
            ["project settings", "configuration", "autoload", "singleton", "project.godot"]),
        new("resource_io", "创建项目目录，加载或写入 Godot 资源（.tres/.res）。",
            ["folder_create", "resource_load", "resource_write"],
            ["folder", "directory", "resource", "load", "write", "save resource", "tres", "res"]),
        new("runtime_advanced", "在确定性游玩测试中控制 AnimationPlayer，并逐帧推进游戏时间。",
            ["animation_player_control", "runtime_time_control"],
            ["runtime", "animation playback", "animationplayer", "play animation", "stop animation", "animation control",
                "freeze game", "step frames", "deterministic playtest"]),
        new("scene_advanced", "比较场景差异，从打包场景批量实例化节点。", ["scene_diff", "scene_instantiate"],
            ["instantiate", "instance", "scene diff", "compare", "prefab", "spawn", "batch instantiate"]),
        new("scene_inheritance", "从基础场景创建继承场景（变体）。", ["scene_create_inherited"],
            ["inheritance", "inherited scene", "prefab", "variant", "base scene", "scene extend", "inherit"]),
        new("scene_spatial", "测量场景中的位置、边界、重叠、间距和包含关系。", ["scene_spatial_map"],
            ["spatial", "layout", "bounds", "overlap", "gap", "containment", "clear space", "position"]),
        new("signals", "在编辑器或运行时检查、连接、断开和发出信号。", ["signal_list", "signal_manage", "signal_emit"],
            ["signal", "connect", "disconnect", "emit", "observer", "event", "handler", "callback"]),
        new("spriteframes", "列出、创建和编辑 SpriteFrames 动画，从精灵表导入帧。",
            ["spriteframes_create", "spriteframes_edit"],
            ["sprite", "spriteframes", "animated sprite", "frame", "flipbook", "2d animation", "spritesheet", "atlas", "2d"]),
        new("theme", "编辑界面主题覆盖，包括样式框、字体、颜色和常量。", ["theme_edit"],
            ["theme", "style", "stylebox", "font", "color", "ui style", "control theme"]),
        new("tilemap", "读取和绘制 TileMap/TileMapLayer 单元格，支持单元格查询、批量填充和区域操作。",
            ["tilemap_read_cells", "tilemap_set_cells"],
            ["tilemap", "tile", "grid", "cell", "read cells", "paint cells", "2d"]),
        new("tileset", "创建 TileSet 资源，添加图集源，配置层，管理替代图块。",
            ["tileset_create", "tileset_add_source", "tileset_remove_source", "tileset_add_alternative",
                "tileset_remove_alternative", "tileset_setup_layers"],
            ["tileset", "atlas", "tile source", "tile layer", "terrain set", "tile alternative", "tile variant", "create tileset"]),
        new("tileset_edit", "编辑逐图块属性，包括物理、地形、导航、视觉和自定义数据。", ["tileset_edit"],
            ["tileset collision", "tile physics", "tile terrain", "tile navigation", "tile occlusion", "tile animation",
                "tile custom data", "peering bits"]),
        new("unsafe", "显式执行任意表达式或当前编辑场景的方法；需要设置 GODOT_MCP_UNSAFE=1。",
            ["execute_code", "node_call_method"],
            ["unsafe", "arbitrary expression", "execute code", "call method", "escape hatch", "rce"]),
        new("user_data", "读取、写入、删除和列出 user:// 存档文件。", ["user_data_read", "save_write", "save_delete"],
            ["save", "save file", "user data", "persistence", "save game", "load game", "savegame"]),
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
