namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 当前请求的会话态载体。优先从 <see cref="HttpContext.Items"/> 读(同请求内一定可见),
/// 回退到 <see cref="AsyncLocal{T}"/>。
/// </summary>
/// <remarks>
/// <para><b>为什么优先用 <c>HttpContext.Items</c></b>:实测在 stateless HTTP 下,
/// 中间件写入的 <c>AsyncLocal</c> 到工具实现里读不到 —— SDK 处理请求时建立了新的执行上下文,
/// 中间件那层的 AsyncLocal 值没有流过去(工具里拿到 null)。而 <c>HttpContext</c> 本身随请求传递,
/// 通过 <see cref="IHttpContextAccessor"/> 在工具里取得到。AsyncLocal 保留为兜底
/// (非 HTTP 路径,例如直接调用工具的单元测试)。</para>
/// <para><b>为什么不用"每请求闭包捕获 key"</b>:一旦 <c>serverOptions</c> 共享,
/// A 会话的键就会出现在 B 的请求里 —— 跨会话越权,且极难查。</para>
/// <para><b>使用纪律</b>:只在请求的 <c>await</c> 链里读;不要 <c>Task.Run</c> 到后台再读;
/// 不要缓存进静态字段。</para>
/// </remarks>
/// <param name="httpContextAccessor">HTTP 上下文访问器(取当前请求的 Items)。</param>
public sealed class SessionKeyAccessor(IHttpContextAccessor httpContextAccessor)
{
    /// <summary><see cref="HttpContext.Items"/> 里的会话态键。</summary>
    internal const string ItemsKey = "godot-mcp.session";

    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    /// <summary>当前请求的会话态;无键请求时为 null。</summary>
    public SessionContext? Current
    {
        get
        {
            if (httpContextAccessor.HttpContext?.Items[ItemsKey] is SessionContext fromHttp)
            {
                return fromHttp;
            }

            return CurrentScope.Value?.Context;
        }
    }

    /// <summary>当前请求的会话键;无键请求时为 null。</summary>
    public string? CurrentKey => Current?.Key;

    /// <summary>
    /// 开启一次请求范围:同时写入 <see cref="HttpContext.Items"/> 与 <c>AsyncLocal</c>。
    /// 返回的 <see cref="Scope"/> 必须 <c>using</c> 释放。
    /// </summary>
    /// <param name="key">会话键(已校验)。</param>
    /// <param name="workingDirectory">shim 上报的会话工作目录;可为空。</param>
    /// <returns>可释放的范围句柄。</returns>
    public Scope Begin(string key, string? workingDirectory)
    {
        var context = new SessionContext(
            key,
            string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory);

        if (httpContextAccessor.HttpContext is { } http)
        {
            http.Items[ItemsKey] = context;
        }

        var previous = CurrentScope.Value;
        CurrentScope.Value = new Scope(context, previous);
        return CurrentScope.Value;
    }

    /// <summary>请求范围内的会话信息。</summary>
    /// <param name="Key">会话键。</param>
    /// <param name="WorkingDirectory">shim 上报的 cwd,用于空参候选发现。</param>
    public readonly record struct SessionContext(string Key, string? WorkingDirectory);

    /// <summary>可释放的作用域:释放时恢复 AsyncLocal 进入前的值。</summary>
    public sealed class Scope(SessionContext context, Scope? previous) : IDisposable
    {
        /// <summary>本次范围承载的会话信息。</summary>
        public SessionContext Context { get; } = context;

        /// <inheritdoc />
        public void Dispose() => CurrentScope.Value = previous;
    }
}
