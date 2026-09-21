using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Groups;

/// <summary>
/// 表驱动的组工具:协议面(name/description/schema/annotations)在构造期
/// 一次性固化(schema 原样取自 GroupToolTable,保证与 Node tools/list parity),
/// 调用经 <see cref="GroupToolInvoker"/> 路由。
/// </summary>
internal sealed class JsonSchemaGroupTool : McpServerTool
{
    /// <summary>构造期固化的协议面(name/description/schema/annotations 不可变)。</summary>
    private readonly Tool _protocolTool;
    /// <summary>工具表定义(调用期转交给调用器)。</summary>
    private readonly NodeToolDef _def;
    /// <summary>共享调用器(所有组工具实例复用同一份)。</summary>
    private readonly GroupToolInvoker _invoker;

    /// <summary>以工具表定义构建协议面;schema 经 NodeToolTable.SchemaFor 注入 instance 寻址参数。</summary>
    /// <param name="def">工具表定义(name/description/schema/annotations 数据源)。</param>
    /// <param name="invoker">组工具调用器。</param>
    public JsonSchemaGroupTool(NodeToolDef def, GroupToolInvoker invoker)
    {
        _def = def;
        _invoker = invoker;
        _protocolTool = new Tool
        {
            Name = def.Name,
            Description = def.Description,
            InputSchema = NodeToolTable.SchemaFor(def),
            Annotations = def.Annotations,
        };
    }

    /// <summary>MCP 协议工具描述(构造期固化的同一实例)。</summary>
    public override Tool ProtocolTool => _protocolTool;

    /// <summary>无附加元数据(组工具不参与 ADR-0003 描述注入)。</summary>
    public override IReadOnlyList<object> Metadata => [];

    /// <summary>调用入口:委托 GroupToolInvoker 按定义路由,zod 剥离后的入参原样上送。</summary>
    /// <param name="request">tools/call 请求上下文(Arguments 为入参字典)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用器的结果包装任务。</returns>
    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        new(_invoker.InvokeAsync(_def, request.Params.Arguments, cancellationToken));
}
