using System.Text.Json.Nodes;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Sessions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon;

/// <summary>
/// <c>tools/call</c> 的未命中兜底(ADR-0007)。被隐藏的工具(未激活组的、从未创建的懒建工具、
/// 会话未就绪时的任何工具)都会落到这里,返回带原因文案的 <c>isError</c> 结果。
/// </summary>
/// <remarks>
/// <para><b>为什么必须有它</b>:SDK 的 <c>CallToolHandler</c> <b>只在工具不在集合里时</b>被调用;
/// 工具命中集合就直接跑它自己的 <c>InvokeAsync</c>。所以"裁剪 + 兜底"必须成对:
/// 只裁剪不兜底,模型看到的是 JSON-RPC "Unknown tool",而不是可行动的说明。</para>
/// <para><b>为什么不用 <c>CallToolFilters</c> 判权</b>:它对<b>命中</b>的调用也会执行,
/// 等于给正常路径多加一层拦截;判权集中在 handler 一处即可。</para>
/// <para><b>绝不抛异常</b>:非 <c>McpProtocolException</c> 的异常虽会被 SDK 转成 <c>IsError</c>,
/// 但 <c>McpProtocolException</c> 会变成 JSON-RPC 错误 —— 一律按数据分支返回最稳。</para>
/// </remarks>
/// <param name="keys">会话键注册表。</param>
/// <param name="groups">组服务(可见集、读键与版本门控)。</param>
internal sealed class ToolCallFallback(
    SessionKeyRegistry keys,
    GroupService groups,
    SessionRequestScope scope)
{
    /// <summary>会话未就绪时的错误码(带了键但还没 connect)。</summary>
    public const string SessionNotReadyCode = "SESSION_NOT_READY";

    /// <summary>
    /// 请求根本没带会话键时的错误码。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SessionNotReadyCode"/> 分开是因为**修复动作完全不同**:
    /// 缺失意味着客户端没过 shim(旧配置 / 直接 HTTP 调试 / 写坏的转发器),调多少次
    /// <c>godot_connect</c> 都不会好转;而"未就绪"只要调一次 <c>godot_connect</c>。
    /// 两者混成一个码,用户会照着提示反复 connect 而永远失败。
    /// </remarks>
    public const string SessionHeaderMissingCode = "SESSION_HEADER_MISSING";

    /// <summary>组未激活时的错误码。</summary>
    public const string GroupNotActivatedCode = "GROUP_NOT_ACTIVATED";

    /// <summary>引擎版本不提供该工具时的错误码。</summary>
    public const string ToolNotProvidableCode = "TOOL_NOT_PROVIDABLE";

    /// <summary>工具名拼错/不存在时的错误码。</summary>
    public const string UnknownToolCode = "UNKNOWN_TOOL";

    /// <summary>
    /// 处理一次未命中的工具调用。
    /// </summary>
    /// <param name="request">调用请求(读工具名与参数)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恒为 <c>IsError = true</c> 的结果。</returns>
    public ValueTask<CallToolResult> HandleAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var toolName = request.Params?.Name ?? "(unknown)";

        // 读键用 `groups.CurrentKey`:它有三段回落(`_sessionKey` AsyncLocal → accessor 的
        // HttpContext → DefaultKey)。**不能只用 `CurrentSessionKey`** —— 那个只有第一段,
        // 读不到时返回 null,于是所有未命中都报"未连接",另外两个分支永不可达。
        // DefaultKey 是空串,用 IsNullOrEmpty 把它也判成"无键"。
        var key = groups.CurrentKey;

        // ① 没有键:细分"根本没带 header"与"带了但键不存在/未连接"。
        if (string.IsNullOrEmpty(key) || !keys.TryGetSnapshot(key, out var snapshot))
        {
            if (string.IsNullOrEmpty(key) && scope.KeyShape == RequestKeyShape.Missing)
            {
                return ValueTask.FromResult(Error(SessionHeaderMissingCode,
                    $"请求未携带 x-godot-session 头,工具 {toolName} 当前不可用。" +
                    "这通常意味着客户端没有走 godot-mcp-shim(旧配置 / 直接 HTTP 调试)," +
                    "此时调用 godot_connect 也不会生效 —— 请检查客户端配置里的 MCP 服务器入口。"));
            }

            return ValueTask.FromResult(Error(SessionNotReadyCode,
                $"会话尚未就绪,工具 {toolName} 当前不可用。请先调用 godot_connect 建立连接。"));
        }
        if (!snapshot.Connected)
        {
            return ValueTask.FromResult(Error(SessionNotReadyCode,
                $"会话还没有连接项目,工具 {toolName} 当前不可用。请先调用 godot_connect(可带 project 绝对路径)。"));
        }

        // ② 已连接:判断该工具是否属于"本键可见集"。
        //    **必须与 ToolFaceComposer 同源**:两处都调 groups.VisibleToolNamesFor,
        //    自己拼一遍会让扩展工具落进"不存在"分支,把"版本不支持"或"扩展组未激活"
        //    误报成"工具名拼错",把模型引向错误的修复动作。
        var visible = groups.VisibleToolNamesFor(snapshot.Groups);
        if (!visible.Contains(toolName))
        {
            // ②a 在某个未激活的内置组里 → 明确告诉模型激活哪个组。
            var owningGroup = GroupCatalogue.Groups
                .FirstOrDefault(g => g.ToolNames.Contains(toolName, StringComparer.Ordinal));
            if (owningGroup is not null)
            {
                return ValueTask.FromResult(Error(GroupNotActivatedCode,
                    $"工具 {toolName} 属于组 {owningGroup.Name},该组当前未激活。" +
                    $"调用 discover_tools(request: \"{owningGroup.Name}\") 或 godot_connect(groups: [\"{owningGroup.Name}\"]) 后可用。"));
            }

            // ②b 扩展组:内置目录查不到,但可能属于某个未激活的扩展组。
            //     只在该组**确实未激活**时才报"未激活" —— 已激活却仍不可见,说明它是被
            //     版本门控摘掉的,那种情况要落到 ③(否则会给"去激活"这种无效处方,
            //     模型会陷入"激活→没出现→再激活"的循环)。
            var extensionGroup = groups.ExtensionGroupOf(toolName);
            if (extensionGroup is not null
                && !snapshot.Groups.Contains(extensionGroup, StringComparer.Ordinal))
            {
                return ValueTask.FromResult(Error(GroupNotActivatedCode,
                    $"工具 {toolName} 属于扩展组 {extensionGroup},该组当前未激活。" +
                    $"调用 discover_tools(request: \"{extensionGroup}\") 激活后可用。"));
            }

            return ValueTask.FromResult(Error(UnknownToolCode,
                $"不存在名为 {toolName} 的工具。工具名不带 godot_ 前缀;调用 discover_tools 查看完整目录。"));
        }

        // ③ 在可见集里却落到兜底 → 只可能是版本门控把它摘掉了。
        //    这一支不能漏:不区分的话会把"引擎版本不支持"误报成"组未激活",把模型引向错误的修复动作。
        if (!groups.IsToolProvidable(toolName))
        {
            var gate = NodeToolTable.GateForTool(toolName);
            var need = gate?.Min is { } min ? $"需要 Godot ≥ {min}" : "当前引擎版本不支持";
            return ValueTask.FromResult(Error(ToolNotProvidableCode,
                $"工具 {toolName} 在当前引擎版本下不可用({need})。"));
        }

        // ④ 其余不应发生(可见集与集合不一致)→ 按未知工具返回。
        return ValueTask.FromResult(Error(UnknownToolCode,
            $"工具 {toolName} 暂时不可用。请重试;若持续失败,调用 godot_connect 重新连接。"));
    }

    /// <summary>构造 <c>isError</c> 结果。</summary>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">可行动文案。</param>
    /// <returns>错误结果。</returns>
    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = new JsonObject
                {
                    ["success"] = false,
                    ["code"] = code,
                    ["message"] = message,
                }.ToJsonString(),
            },
        ],
    };
}
