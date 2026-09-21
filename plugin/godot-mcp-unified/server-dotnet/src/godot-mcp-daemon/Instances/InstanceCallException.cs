namespace GodotMcp.Daemon.Instances;

/// <summary>实例表的对外摘要(list_instances 行的子集,供寻址错误携带可用实例清单)。</summary>
/// <param name="Path">规范化项目路径(实例键,寻址主标识)。</param>
/// <param name="Id">12 位短 id(项目键哈希,寻址别名)。</param>
/// <param name="Port">编辑器 WS 端口。</param>
/// <param name="Connected">daemon 与该实例的编辑器通道当前是否已连接。</param>
public sealed record InstanceSummary(string Path, string Id, int Port, bool Connected);

/// <summary>
/// daemon 侧实例寻址/调用的工具层错误(ADR-0003:寻址错误形 + Node 桥 transport 错误码同族)。
/// Code 语义:NO_INSTANCE(无活跃实例)/ AMBIGUOUS_INSTANCE(多实例未指定)/
/// INSTANCE_NOT_FOUND(目标不存在)/ TIMEOUT / DISCONNECTED / RPC_ERROR / CANCELLED。
/// </summary>
/// <param name="code">机器可读错误码(语义见上;另有路由期扩展码 GAME_NOT_RUNNING /
/// UNSUPPORTED / LSP_UNAVAILABLE / LSP_PORT_CONFLICT)。</param>
/// <param name="message">人读错误信息(作为 Exception.Message 上抛)。</param>
/// <param name="hint">可选修复提示(如缺实例时的启动指引、LSP 端口排查建议)。</param>
/// <param name="instances">可选可用实例清单(寻址类错误附带,帮助调用方改写 instance 参数)。</param>
public sealed class InstanceCallException(string code, string message, string? hint = null, List<InstanceSummary>? instances = null)
    : Exception(message)
{
    /// <summary>机器可读错误码(与工具错误响应的 code 同名)。</summary>
    public string Code { get; } = code;

    /// <summary>可选修复提示;未附带为 null。</summary>
    public string? Hint { get; } = hint;

    /// <summary>寻址类错误附带的可用实例清单;其余错误为 null。</summary>
    public List<InstanceSummary>? Instances { get; } = instances;
}
