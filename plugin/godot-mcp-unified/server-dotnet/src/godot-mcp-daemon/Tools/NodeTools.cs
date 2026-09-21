using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 节点类 eager 工具(issue 09):node_inspect / node_set_property / node_set_script / node_manage。
/// 名称、描述与参数 schema 对齐 Node 桥同名工具(server/src/tools/node.ts、nodeManagement.ts);
/// node_inspect 与 node_set_property 的复合路由逻辑与 Node 侧 handler 同形。
/// </summary>
/// <param name="instances">实例管理器:按 ADR-0003 寻址规则把调用路由到目标 Godot 实例。</param>
[McpServerToolType]
public sealed class NodeTools(InstanceManager instances)
{
    /// <summary>读取节点属性:指定 property 时取单项值,否则返回按 mask/visibility 筛选的属性视图。</summary>
    /// <para>逻辑链:property 非空 → 单项路由 node.get_property(params 仅 node_path 与 property 两键);
    /// property 为空 → 列表路由 node.get_property(mask 仅在提供时携带,visibility 缺省补 Node zod 默认值 "all")。
    /// 寻址/传输失败经 ToolRouting 统一映射为 isError 错误信封。</para>
    /// <param name="node_path">目标节点的场景树绝对路径。</param>
    /// <param name="property">要读取的单个属性名;非空时走单项路由。</param>
    /// <param name="mask">属性视图筛选(省略 property 时生效),如 common/all/script。</param>
    /// <param name="visibility">可见性筛选(省略 property 时生效);缺省为 "all"。</param>
    /// <param name="instance">目标实例(规范化项目路径或 12 位短 id);恰好一个实例时可省略。</param>
    /// <returns>节点属性 JSON 或错误信封的 CallToolResult。</returns>
    [McpServerTool(Name = "node_inspect", ReadOnly = true, OpenWorld = false)]
    [Description("检查当前编辑场景中的节点。传入 property 时读取单个属性值；否则按 mask 和 visibility 返回经过筛选的属性视图。")]
    public Task<CallToolResult> NodeInspect(
        string node_path,
        string? property = null,
        string? mask = null,
        string? visibility = null,
        string? instance = null)
    {
        if (!string.IsNullOrEmpty(property))
        {
            // 单项路由(nodeSetPropertyHandler 同形):property 分支 → node.get_property,仅携两键。
            return ToolRouting.RouteAsync(instances, instance, "node.get_property", new Dictionary<string, object?>
            {
                ["node_path"] = node_path,
                ["property"] = property,
            });
        }

        var args = new Dictionary<string, object?> { ["node_path"] = node_path };
        if (mask is not null) args["mask"] = mask;
        // Node zod default("all"):visibility 省略时也随行携带。
        args["visibility"] = visibility ?? "all";
        return ToolRouting.RouteAsync(instances, instance, "node.get_property_list", args);
    }

    /// <summary>设置节点属性:编辑器通道支持批量与 make_unique,运行时通道为单次有界变更。</summary>
    /// <para>逻辑链:channel 缺省视为 editor;channel='runtime' 时 → 携带 batch 或 make_unique 即回
    /// INVALID_PARAMS(提示改用 editor 或拆成单次有界变更),缺 node_path/property/value 任一即回
    /// INVALID_PARAMS,否则把单组键组装成 params 经运行时通道调 runtime.set_property(经由
    /// <see cref="RuntimeErrors.WithCrashContextAsync"/> 补崩溃上下文);editor 通道 →
    /// 仅把调用方提供的键组装进 params,经 node.set_property 转发。</para>
    /// <param name="node_path">目标节点的场景树绝对路径(runtime 单项模式必填)。</param>
    /// <param name="property">属性名,支持含 "/" 的复合路径与 ":" 子资源访问。</param>
    /// <param name="value">带类型的 Godot 属性值(JSON 原样透传)。</param>
    /// <param name="make_unique">复合路径指向外部 .tres 子资源时先复制为内嵌副本(仅 editor 通道)。</param>
    /// <param name="batch">批量条目数组(仅 editor 通道)。</param>
    /// <param name="channel">目标通道:editor(默认)或 runtime。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>工具执行结果;参数不合法或通道不可用时为 isError 错误信封。</returns>
    [McpServerTool(Name = "node_set_property", ReadOnly = false, OpenWorld = false, Destructive = false)]
    [Description("设置当前编辑场景中的一个或多个节点属性；channel='runtime' 时设置运行中游戏的单个属性。支持带类型的 Godot 值。")]
    public async Task<CallToolResult> NodeSetProperty(
        string? node_path = null,
        string? property = null,
        JsonElement? value = null,
        bool? make_unique = null,
        JsonElement? batch = null,
        string? channel = null,
        string? instance = null)
    {
        var effectiveChannel = channel ?? "editor";
        if (effectiveChannel == "runtime")
        {
            if (batch is not null || make_unique is not null)
            {
                return ToolResults.Error(
                    "INVALID_PARAMS",
                    "runtime node_set_property accepts one node_path/property/value and does not support batch or make_unique",
                    "Use channel='editor' for batch/make_unique, or issue one bounded runtime change.");
            }
            if (node_path is null || property is null || value is null)
            {
                return ToolResults.Error(
                    "INVALID_PARAMS",
                    "runtime node_set_property requires node_path, property, and value",
                    "Use an absolute runtime node path such as /root/Main/Player.");
            }
            // 运行时通道:单组有界变更经 runtime.set_property 落到游戏进程。
            // 运行时缺席(GAME_NOT_RUNNING)等错误按运行时语义补崩溃上下文。
            var runtimeArgs = new Dictionary<string, object?>
            {
                ["node_path"] = node_path,
                ["property"] = property,
                ["value"] = value.Value,
            };
            try
            {
                var result = await instances.CallRuntimeAsync(
                    instance, "runtime.set_property", JsonSerializer.Serialize(runtimeArgs),
                    ToolRouting.CallTimeout, CancellationToken.None);
                return ToolResults.FromToolkitResult(result);
            }
            catch (InstanceCallException ex)
            {
                return await RuntimeErrors.WithCrashContextAsync(instances, ex, instance);
            }
        }

        var args = new Dictionary<string, object?>();
        if (node_path is not null) args["node_path"] = node_path;
        if (property is not null) args["property"] = property;
        if (value is not null) args["value"] = value;
        if (make_unique is not null) args["make_unique"] = make_unique;
        if (batch is not null) args["batch"] = batch;
        return await ToolRouting.RouteAsync(instances, instance, "node.set_property", args);
    }

    /// <summary>为节点绑定脚本(.gd/.cs);script_path 为空字符串即解除绑定。</summary>
    /// <param name="node_path">目标节点的场景树绝对路径。</param>
    /// <param name="script_path">脚本 res:// 路径;空字符串表示解除脚本绑定。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>绑定结果(含脚本公开的 @export 属性)或错误信封。</returns>
    [McpServerTool(Name = "node_set_script", ReadOnly = false, OpenWorld = false, Destructive = false)]
    [Description("为节点附加脚本（.gd/.cs），返回脚本公开的 @export 属性。script_path 为空字符串时解除脚本绑定。")]
    public Task<CallToolResult> NodeSetScript(
        string node_path,
        string script_path,
        string? instance = null)
    {
        return ToolRouting.RouteAsync(instances, instance, "node.set_script", new Dictionary<string, object?>
        {
            ["node_path"] = node_path,
            ["script_path"] = script_path,
        });
    }

    /// <summary>节点结构操作:重命名 / 换父 / 调序 / 复制,按 action 分派到 node.manage。</summary>
    /// <para>逻辑链:action 与 node_path 必填,其余键仅在调用方提供时随行组装,
    /// 经 node.manage 转发到 toolkit(具体 action 的必填校验在 toolkit 侧执行);
    /// 寻址/传输失败映射为错误信封。</para>
    /// <param name="action">结构操作:rename / reparent / reorder / duplicate。</param>
    /// <param name="node_path">目标节点的场景树绝对路径。</param>
    /// <param name="new_name">rename 的新名;duplicate 的新名(可选)。</param>
    /// <param name="new_parent_path">reparent 的新父节点路径。</param>
    /// <param name="keep_global_transform">reparent 时是否保留全局变换,默认 true。</param>
    /// <param name="new_index">reorder 的同级索引,从 0 开始。</param>
    /// <param name="parent_path">duplicate 的目标父节点,默认与原节点相同。</param>
    /// <param name="properties">duplicate 的属性覆盖,如 {position:{x,y}}。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>结构操作结果或错误信封。</returns>
    [McpServerTool(Name = "node_manage", ReadOnly = false, OpenWorld = false, Destructive = false)]
    [Description(
        "对当前编辑场景树执行节点结构操作。\n\naction='rename'：重命名，必须提供 new_name。\naction='reparent'：更换父节点，必须提供 new_parent_path；keep_global_transform 可选，默认 true。\naction='reorder'：调整顺序，必须提供 new_index（从 0 开始的同级索引）。\naction='duplicate'：复制，new_name、parent_path、properties 均可选；properties 用于覆盖属性，例如 {position:{x,y}}。")]
    public Task<CallToolResult> NodeManage(
        string action,
        string node_path,
        string? new_name = null,
        string? new_parent_path = null,
        bool? keep_global_transform = null,
        int? new_index = null,
        string? parent_path = null,
        JsonElement? properties = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["node_path"] = node_path,
        };
        if (new_name is not null) args["new_name"] = new_name;
        if (new_parent_path is not null) args["new_parent_path"] = new_parent_path;
        if (keep_global_transform is not null) args["keep_global_transform"] = keep_global_transform;
        if (new_index is not null) args["new_index"] = new_index;
        if (parent_path is not null) args["parent_path"] = parent_path;
        if (properties is not null) args["properties"] = properties;
        return ToolRouting.RouteAsync(instances, instance, "node.manage", args);
    }
}
