namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 当前请求的会话态(<b>唯一真源</b>):会话键 + shim 上报的 cwd。
/// </summary>
/// <remarks>
/// <para><b>为什么独立成类,而不是放在 <c>GroupService</c> 里</b>:<c>GroupService</c> 构造时
/// <c>new GroupToolInvoker</c>,而 invoker 需要读本键默认目标(<see cref="Tools.SessionTargetResolver"/>);
/// 若会话态挂在 GroupService 上,这条依赖就成环。提到独立单例后,
/// <c>GroupService</c>、<c>ConnectTool</c>、<c>ToolCallFallback</c>、<c>SessionTargetResolver</c>
/// 都只依赖这一个类 —— 顺带消灭"同一个问题三种读法"的漂移。</para>
///
/// <para><b>为什么不用 <c>IHttpContextAccessor</c></b>:实测 MCP 的工具 handler 与中间件
/// 不在同一执行上下文,工具里读 <c>HttpContext.Items</c> 拿到 null。而
/// <c>ConfigureSessionOptions</c> 与工具 handler <b>共享同一个 AsyncLocal 流</b>
/// (现有代码长期依赖这一点,<c>_sessionCollection</c> 就是这个模式)。</para>
///
/// <para><b>为什么 cwd 与键必须同载体</b>:空参 <c>godot_connect</c> 要靠 cwd 找
/// <c>&lt;cwd&gt;/project.godot</c>,而首次 connect 时注册表里还没这个键 ——
/// 从注册表读会静默拿到 null,让这条自救路径恰好在最该生效时失效。</para>
///
/// <para><b>使用纪律</b>:只在请求的 <c>await</c> 链里读;不要 <c>Task.Run</c> 到后台再读;
/// 不要缓存进静态字段。</para>
/// </remarks>
public sealed class SessionRequestScope
{
    private readonly AsyncLocal<Context?> _current = new();

    /// <summary>当前请求的会话键;无键请求为 null。</summary>
    public string? CurrentKey => _current.Value?.Key;

    /// <summary>当前请求 shim 上报的 cwd;未知为 null。</summary>
    public string? CurrentWorkingDirectory => _current.Value?.WorkingDirectory;

    /// <summary>当前请求的会话键形态(用于把未命中分成可行动的三个错误码)。</summary>
    public RequestKeyShape KeyShape => _current.Value?.KeyShape ?? RequestKeyShape.Missing;

    /// <summary>
    /// 写入本请求的会话态。必须在 <c>ConfigureSessionOptions</c> 里调用一次(每请求一次)。
    /// </summary>
    /// <param name="sessionKey">会话键;无键请求传 null。</param>
    /// <param name="workingDirectory">shim 上报的 cwd;未知传 null。</param>
    /// <param name="keyShape">会话键形态;默认按"有键/无键"推断。</param>
    public void Set(string? sessionKey, string? workingDirectory, RequestKeyShape? keyShape = null)
        => _current.Value = new Context(
            string.IsNullOrEmpty(sessionKey) ? null : sessionKey,
            string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            keyShape ?? (string.IsNullOrEmpty(sessionKey) ? RequestKeyShape.Missing : RequestKeyShape.Present));

    /// <summary>请求范围内的会话信息。</summary>
    /// <param name="Key">会话键。</param>
    /// <param name="WorkingDirectory">shim 上报的 cwd。</param>
    /// <param name="KeyShape">会话键形态。</param>
    private readonly record struct Context(string? Key, string? WorkingDirectory, RequestKeyShape KeyShape);
}

/// <summary>
/// 请求所带会话键的形态(与 <see cref="SessionKeyState"/> 的生命周期状态是两件事)。
/// </summary>
/// <remarks>
/// 这三种形态对应**三种不同的修复动作**,所以必须分开报:
/// 缺失 → 客户端配置/旧 shim;非法 → 转发器改写了 header;存在但未连接 → 调 <c>godot_connect</c>。
/// 全归成"会话尚未就绪,请先 godot_connect"会让前两种情形照着提示做还是同样的错。
/// </remarks>
public enum RequestKeyShape
{
    /// <summary>请求没带 <c>x-godot-session</c>。</summary>
    Missing,

    /// <summary>带了,但不是合法的 GUID 文本。</summary>
    Invalid,

    /// <summary>带了且合法。</summary>
    Present,
}
