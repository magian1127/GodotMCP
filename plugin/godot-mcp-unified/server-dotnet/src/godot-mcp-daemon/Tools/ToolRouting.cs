using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 已迁移工具的共享路由助手:构造 wire params(仅携带调用方提供的键——省略语义与
/// Node 桥一致)、经实例寻址发起调用、把结果映射为工具返回(toolkit 失败信封 →
/// isError;寻址/传输失败 → 错误信封)。每个工具的参数 schema 与 Node 桥同名工具对齐。
/// </summary>
internal static class ToolRouting
{
    /// <summary>
    /// eager 工具的默认单次调用上限 —— 取自统一台账 <see cref="ToolCallTimeouts.Default"/>。
    /// </summary>
    /// <remarks>
    /// <para>本层多数工具是<b>短操作</b>:<c>node.get_property</c>、<c>project.get_settings</c>、
    /// <c>editor.windows</c>、<c>game.stop</c>。把常量整体抬到 180 秒会让"编辑器卡住"的反馈
    /// 从 30 秒变成 3 分钟。</para>
    /// <para><b>但长工具也在这一层</b>——<c>editor_save_scene</c>、<c>editor_build_csharp</c>、
    /// <c>editor_build_csharp_status</c> 都是 eager 注册的(不走 <c>JsonSchemaGroupTool</c>,
    /// 因此拿不到 <c>GroupToolInvoker</c> 按名取超时的能力)。所以调用点必须显式传
    /// <c>toolName</c>,由 <see cref="CallTimeoutFor"/> 查台账;只把名字写进
    /// <see cref="ToolCallTimeouts.LongNames"/> 而不传,等于台账对实现静默漂移 ——
    /// 名字说 180 秒、实际给 30 秒,而大场景保存超 30 秒时文件可能已经写了一半。</para>
    /// </remarks>
    public static readonly TimeSpan CallTimeout = ToolCallTimeouts.Default;

    /// <summary>按工具名取单次调用上限(未登记的名字落到 <see cref="ToolCallTimeouts.Default"/>)。</summary>
    /// <param name="toolName">MCP 工具名;null 表示调用点未声明(按短操作处理)。</param>
    /// <returns>该工具的调用上限。</returns>
    public static TimeSpan CallTimeoutFor(string? toolName)
        => toolName is null ? CallTimeout : ToolCallTimeouts.For(toolName);

    /// <summary>eager 工具的统一转发入口:构造 params、发起实例调用、映射结果。</summary>
    /// <para>逻辑链:args 序列化为 wire params(仅携带调用方提供的键,省略语义与 Node 桥一致)
    /// → 按寻址规则调 CallInstanceAsync(超时取 <paramref name="toolName"/> 对应的台账值,
    /// 不响应取消)→ toolkit 失败信封转 isError、成功原样透传;InstanceCallException →
    /// 错误信封(含恢复提示映射)。</para>
    /// <param name="instances">实例管理器,负责实例寻址与 wire 调用。</param>
    /// <param name="instance">目标实例(规范化项目路径或短 id);恰好一个实例时可省略。</param>
    /// <param name="method">toolkit 侧 wire 方法名,如 "node.get_property"。</param>
    /// <param name="args">工具入参键值(仅含调用方提供的键)。</param>
    /// <param name="toolName">
    /// 本工具在 MCP 面上的名字;长工具<b>必须</b>传,否则拿不到台账里登记的 180 秒。
    /// </param>
    /// <returns>映射后的 CallToolResult;本方法不抛异常,失败一律以 isError 表达。</returns>
    public static async Task<CallToolResult> RouteAsync(
        InstanceManager instances,
        string? instance,
        string method,
        Dictionary<string, object?> args,
        string? toolName = null)
    {
        try
        {
            var paramsJson = JsonSerializer.Serialize(args);
            var result = await instances.CallInstanceAsync(
                instance, method, paramsJson, CallTimeoutFor(toolName), CancellationToken.None);
            return ToolResults.FromToolkitResult(result);
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }
    }
}
