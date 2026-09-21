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
    /// <summary>Node 桥 bridge.call 的默认单次调用上限。</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>eager 工具的统一转发入口:构造 params、发起实例调用、映射结果。</summary>
    /// <para>逻辑链:args 序列化为 wire params(仅携带调用方提供的键,省略语义与 Node 桥一致)
    /// → 按寻址规则调 CallInstanceAsync(固定 30 秒超时,不响应取消)→ toolkit 失败信封转
    /// isError、成功原样透传;InstanceCallException → 错误信封(含恢复提示映射)。</para>
    /// <param name="instances">实例管理器,负责实例寻址与 wire 调用。</param>
    /// <param name="instance">目标实例(规范化项目路径或短 id);恰好一个实例时可省略。</param>
    /// <param name="method">toolkit 侧 wire 方法名,如 "node.get_property"。</param>
    /// <param name="args">工具入参键值(仅含调用方提供的键)。</param>
    /// <returns>映射后的 CallToolResult;本方法不抛异常,失败一律以 isError 表达。</returns>
    public static async Task<CallToolResult> RouteAsync(
        InstanceManager instances,
        string? instance,
        string method,
        Dictionary<string, object?> args)
    {
        try
        {
            var paramsJson = JsonSerializer.Serialize(args);
            var result = await instances.CallInstanceAsync(
                instance, method, paramsJson, CallTimeout, CancellationToken.None);
            return ToolResults.FromToolkitResult(result);
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }
    }
}
