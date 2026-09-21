using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 场景类 eager 工具(issue 09):scene_get_tree / scene_create_node / scene_delete_node /
/// scene_create / scene_open / scene_query / script_check。名称、描述与参数 schema 对齐
/// Node 桥同名工具(server/src/tools/scene.ts、sceneQuery.ts、script.ts)。
/// </summary>
/// <param name="instances">实例管理器:按 ADR-0003 寻址规则把调用路由到目标 Godot 实例。</param>
[McpServerToolType]
public sealed class SceneTools(InstanceManager instances)
{
    /// <summary>以嵌套 JSON 返回当前编辑场景的节点树(name/class/path/children)。</summary>
    /// <param name="max_depth">树深度限制,默认 2;-1 表示不限制。</param>
    /// <param name="include_properties">是否为每个节点内嵌属性快照,默认 false。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>节点树 JSON 或错误信封。</returns>
    [McpServerTool(Name = "scene_get_tree", ReadOnly = true, OpenWorld = false)]
    [Description(
        "以嵌套 JSON { name, class, path, children } 返回当前编辑场景的节点树。根节点路径为 \".\"，返回的路径可直接传给其他编辑器命令。")]
    public Task<CallToolResult> SceneGetTree(
        double? max_depth = null,
        bool? include_properties = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>();
        if (max_depth is not null) args["max_depth"] = max_depth;
        if (include_properties is not null) args["include_properties"] = include_properties;
        return ToolRouting.RouteAsync(instances, instance, "scene.get_tree", args);
    }

    /// <summary>在父节点下创建 class_name 指定类型的节点(幂等:重名返回 returned,新建返回 created)。</summary>
    /// <para>逻辑链:class_name 与 parent_path 必填,其余键仅在提供时随行,
    /// 经 scene.create_node 转发;引擎类与用户 class_name 均支持,具体校验在 toolkit 侧。</para>
    /// <param name="class_name">节点类型(引擎类或用户定义 class_name 类)。</param>
    /// <param name="parent_path">父节点路径,场景根为 "."。</param>
    /// <param name="node_name">节点名;缺省用类名。</param>
    /// <param name="layout_mode">Control 节点布局模式:0 自由布局,1 锚点布局(父为 Container 时自动 1)。</param>
    /// <param name="unique_name">标记为场景唯一节点(脚本内可用 %Name 访问)。</param>
    /// <param name="properties">创建后立即设置的属性值。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>创建结果(created/returned 与节点信息)或错误信封。</returns>
    [McpServerTool(Name = "scene_create_node", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "在父节点下创建 class_name 指定类型的节点，支持引擎类和用户定义的 class_name 类。保持幂等：名称冲突时返回 'returned'，新建时返回 'created'。\n\n示例：class_name: \"CharacterBody2D\", parent_path: \".\", node_name: \"Player\"")]
    public Task<CallToolResult> SceneCreateNode(
        string class_name,
        string parent_path,
        string? node_name = null,
        double? layout_mode = null,
        bool? unique_name = null,
        JsonElement? properties = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["class_name"] = class_name,
            ["parent_path"] = parent_path,
        };
        if (node_name is not null) args["node_name"] = node_name;
        if (layout_mode is not null) args["layout_mode"] = layout_mode;
        if (unique_name is not null) args["unique_name"] = unique_name;
        if (properties is not null) args["properties"] = properties;
        return ToolRouting.RouteAsync(instances, instance, "scene.create_node", args);
    }

    /// <summary>删除 node_path 指定的节点(toolkit 拒绝删除当前编辑场景根节点)。</summary>
    /// <param name="node_path">要删除的节点 NodePath。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>删除确认或错误信封。</returns>
    [McpServerTool(Name = "scene_delete_node", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("删除 node_path（NodePath）指定的节点。拒绝删除当前编辑场景的根节点。")]
    public Task<CallToolResult> SceneDeleteNode(
        string node_path,
        string? instance = null)
    {
        return ToolRouting.RouteAsync(instances, instance, "scene.delete_node", new Dictionary<string, object?>
        {
            ["node_path"] = node_path,
        });
    }

    /// <summary>在 file_path 创建 .tscn(幂等,状态为 created/returned/replaced)。</summary>
    /// <param name="file_path">目标 res:// 路径;根节点默认名为不含扩展名的文件名。</param>
    /// <param name="root_type">根节点类型,默认 Node。</param>
    /// <param name="root_name">覆盖根节点名。</param>
    /// <param name="if_exists">已存在时的行为:return / fail / replace。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>创建结果(created/returned/replaced)或错误信封。</returns>
    [McpServerTool(Name = "scene_create", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description(
        "在 file_path 创建 .tscn。根节点位于 '.'，默认名称为不含扩展名的文件名，root_type 默认为 Node。保持幂等，状态为 created、returned 或 replaced。if_exists 可取 return、fail、replace。创建后使用 scene_open 打开编辑。")]
    public Task<CallToolResult> SceneCreate(
        string file_path,
        string? root_type = null,
        string? root_name = null,
        string? if_exists = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?> { ["file_path"] = file_path };
        if (root_type is not null) args["root_type"] = root_type;
        if (root_name is not null) args["root_name"] = root_name;
        if (if_exists is not null) args["if_exists"] = if_exists;
        return ToolRouting.RouteAsync(instances, instance, "scene.create", args);
    }

    /// <summary>打开场景(.tscn/.scn)并设为当前编辑场景;文件不存在时返回 NOT_FOUND。</summary>
    /// <param name="file_path">res:// 场景路径(仅支持 res://)。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>打开确认或错误信封。</returns>
    [McpServerTool(Name = "scene_open", ReadOnly = true, OpenWorld = false)]
    [Description("打开场景（.tscn / .scn）并将其设为当前编辑场景。仅支持 res://；文件不存在时返回 NOT_FOUND。")]
    public Task<CallToolResult> SceneOpen(
        string file_path,
        string? instance = null)
    {
        return ToolRouting.RouteAsync(instances, instance, "scene.open", new Dictionary<string, object?>
        {
            ["file_path"] = file_path,
        });
    }

    /// <summary>按类/组/名称通配符/属性条件搜索场景树,深度优先顺序返回并支持分页。</summary>
    /// <para>逻辑链:全部筛选键仅在提供时随行,经 scene.query 转发;toolkit 侧按条件过滤、
    /// 按 offset/limit 分页,返回 returned/total_matches/has_more/next_offset。
    /// 分页仅在两次调用间场景树未变时稳定;树变化(增删/重排)后需从 offset=0 重查。</para>
    /// <param name="class_filter">类名筛选(含继承),如 "CollisionShape2D"、"Control"。</param>
    /// <param name="group_filter">按节点所属组筛选。</param>
    /// <param name="name_pattern">节点名通配符模式,如 "Enemy*"、"*Collision*"。</param>
    /// <param name="property_filters">属性值条件数组,多个条件按逻辑与组合。</param>
    /// <param name="root_path">子树根路径,默认场景根节点。</param>
    /// <param name="max_depth">最大遍历深度;-1 不限制,默认 -1。</param>
    /// <param name="include_properties">结果中要包含的属性名列表。</param>
    /// <param name="offset">跳过前 N 项,默认 0。</param>
    /// <param name="limit">每页条数,默认 50,上限 200(超出截断)。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>匹配节点与分页字段(has_more 为 true 时以 next_offset 续读)或错误信封。</returns>
    [McpServerTool(Name = "scene_query", ReadOnly = true, OpenWorld = false)]
    [Description(
        "按类、组、名称通配符和属性条件搜索场景树，返回匹配节点，比 scene_get_tree 后手动筛选更快。分页字段：returned、total_matches、has_more。has_more 为 true 时，通过 next_offset 继续读取，直到 has_more 为 false。仅在各次调用之间数据源未发生变化时，分页结果才保持稳定。结果按确定的深度优先顺序返回，nodes 同时返回 offset/limit。limit 范围为 1–200，默认 50，超过 200 时限制为 200。分页读取之间若场景树发生变化（添加、删除或重排节点），结果可能遗漏或重复，请从 offset=0 重新查询。")]
    public Task<CallToolResult> SceneQuery(
        string? class_filter = null,
        string? group_filter = null,
        string? name_pattern = null,
        JsonElement? property_filters = null,
        string? root_path = null,
        int? max_depth = null,
        string[]? include_properties = null,
        int? offset = null,
        int? limit = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>();
        if (class_filter is not null) args["class_filter"] = class_filter;
        if (group_filter is not null) args["group_filter"] = group_filter;
        if (name_pattern is not null) args["name_pattern"] = name_pattern;
        if (property_filters is not null) args["property_filters"] = property_filters;
        if (root_path is not null) args["root_path"] = root_path;
        if (max_depth is not null) args["max_depth"] = max_depth;
        if (include_properties is not null) args["include_properties"] = include_properties;
        if (offset is not null) args["offset"] = offset;
        if (limit is not null) args["limit"] = limit;
        return ToolRouting.RouteAsync(instances, instance, "scene.query", args);
    }

    /// <summary>离线验证 GDScript(不运行编辑器),返回通过/失败状态与诊断列表。</summary>
    /// <param name="file_path">待检查的 .gd 文件 res:// 路径。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>校验状态与诊断(4.5+ 带行号;4.2-4.4 省略行号)或错误信封。</returns>
    [McpServerTool(Name = "script_check", ReadOnly = true, OpenWorld = false)]
    [Description(
        "离线验证 GDScript，返回通过/失败状态及诊断。Godot 4.5+ 的错误诊断包含真实行号（从 1 开始）；4.2–4.4 省略行号。列号请通过 lsp_diagnostics 获取。无需运行编辑器。")]
    public Task<CallToolResult> ScriptCheck(
        string file_path,
        string? instance = null)
    {
        return ToolRouting.RouteAsync(instances, instance, "script.check", new Dictionary<string, object?>
        {
            ["file_path"] = file_path,
        });
    }
}
