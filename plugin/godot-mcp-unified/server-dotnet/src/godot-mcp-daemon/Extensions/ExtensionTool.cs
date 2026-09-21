using System.Text.Json;
using GodotMcp.Daemon.Groups;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Extensions;

/// <summary>
/// 扩展工具的协议面:name/description/input_schema 原样取自插件声明
/// (仅注入 instance),注解三项默认 false(Node extensionAnnotations 同规);
/// 执行经 <see cref="ExtensionService"/> 直连声明实例。
/// </summary>
internal sealed class ExtensionTool : McpServerTool
{
    /// <summary>预构建的协议工具描述(name/description/schema/annotations 在构造期固化)。</summary>
    private readonly Tool _protocolTool;
    /// <summary>构造时的命令定义快照(登记表查不到时的兜底)。</summary>
    private readonly ExtensionCommand _command;
    /// <summary>扩展服务:提供最新定义查询与执行。</summary>
    private readonly ExtensionService _service;

    /// <summary>按命令定义构建协议面:注入 instance 参数,注解三项映射到 ToolAnnotations。</summary>
    /// <param name="command">扩展命令定义。</param>
    /// <param name="service">扩展服务(执行入口)。</param>
    public ExtensionTool(ExtensionCommand command, ExtensionService service)
    {
        Command = command;
        _command = command;
        _service = service;
        _protocolTool = new Tool
        {
            Name = command.ToolName,
            Description = command.Description,
            InputSchema = NodeToolTable.InjectInstance(command.InputSchema),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = command.Annotations.ReadOnly,
                DestructiveHint = command.Annotations.Destructive,
                IdempotentHint = command.Annotations.Idempotent,
            },
        };
    }

    /// <summary>构造时的命令引用(原位更新时由 GroupService 重建本对象)。</summary>
    public ExtensionCommand Command { get; }

    /// <summary>tools/list 暴露的协议工具(构造期固化的快照)。</summary>
    public override Tool ProtocolTool => _protocolTool;

    /// <summary>无附加元数据(空集)。</summary>
    public override IReadOnlyList<object> Metadata => [];

    /// <summary>
    /// 执行扩展工具。
    /// <para>逻辑链:从入参取字符串 instance(缺省 null,由实例管理器自选实例)→
    /// 按工具名取登记表最新定义(扩展热更新后参数/超时随新定义;查不到回退构造时快照)→
    /// 委托 ExtensionService.CallAsync,经 ValueTask 直通不额外调度。</para>
    /// </summary>
    /// <param name="request">调用请求(params.Arguments 为原始入参)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>工具执行结果(错误亦以 CallToolResult 表达,不抛出)。</returns>
    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        string? instance = null;
        if (request.Params.Arguments is { } args
            && args.TryGetValue("instance", out var instanceEl)
            && instanceEl.ValueKind == JsonValueKind.String)
        {
            instance = instanceEl.GetString();
        }
        // 执行取登记表最新定义(参数/超时随扩展热更新)。
        var command = _service.TryGet(_protocolTool.Name) ?? _command;
        return new(_service.CallAsync(instance, command, request.Params.Arguments, cancellationToken));
    }
}
