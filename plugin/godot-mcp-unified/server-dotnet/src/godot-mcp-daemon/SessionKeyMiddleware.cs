using GodotMcp.Daemon.Sessions;

namespace GodotMcp.Daemon;

/// <summary>
/// 会话键中间件(ADR-0006):从 <c>x-godot-session</c> 头读出会话键,驱动注册表的在途计数,
/// 并把键写进 <see cref="HttpContext.Items"/> 供工具实现与工具面组装器读取。
/// </summary>
/// <remarks>
/// <para><b>为什么在中间件里</b>:<c>ConfigureSessionOptions</c> 只有"请求开始"这一侧,
/// 没有请求结束回调。判"无在途"必须另有一个成对入口 —— 这里的 finally 就是它。</para>
/// <para><b>为什么在 token 之后</b>:401 探测不该产生任何会话活动(与空闲计量的处理一致)。</para>
/// <para><b>识别 listen</b>:按 <c>Mcp-Method</c> 头判断(2026-07-28 要求该头必填,shim 对 listen
/// 一律强制携带)。缺头时按非 listen 处理 —— 最坏结果是多一次 <c>LastSeen</c> 刷新,不会误删键。</para>
/// <para><b>为什么是静态方法而非 <c>UseMiddleware&lt;T&gt;</c></b>:约定的中间件类只允许
/// 构造函数注入 + <c>InvokeAsync(HttpContext, RequestDelegate)</c>;<c>ILogger</c> 这类参数不能放在
/// InvokeAsync 上。用 <c>app.Use</c> 显式调用可以避免这个陷阱,也让依赖来源一目了然。</para>
/// </remarks>
internal static class SessionKeyMiddleware
{
    /// <summary>会话键头名。shim 对<b>所有</b>请求都带它,包括内部与转发的 <c>subscriptions/listen</c>。</summary>
    public const string HeaderName = "x-godot-session";

    /// <summary>shim 程序集版本头,用于识别版本混跑。</summary>
    public const string ShimVersionHeaderName = "x-godot-shim-version";

    /// <summary>shim 上报的会话工作目录,用于 <c>godot_connect</c> 空参时的候选发现。</summary>
    public const string WorkingDirectoryHeaderName = "x-godot-cwd";

    internal static string ReadWorkingDirectory(HttpContext context)
    {
        var value = context.Request.Headers[WorkingDirectoryHeaderName].ToString();
        return value.StartsWith("uri:", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(value["uri:".Length..])
            : value;
    }

    /// <summary>2026-07-28 的路由头;listen 判定依赖它。</summary>
    public const string MethodHeaderName = "Mcp-Method";

    /// <summary>
    /// 本 daemon 的版本(与 Program.cs 报给客户端的 serverInfo.version 同源)。
    /// </summary>
    /// <remarks>用于识别"旧 shim 拎着新 daemon 跑"的混跑现场 —— 那是最难查的一类问题:
    /// 请求能到、键也有,但头部语义对不上,工具面就是不展开。</remarks>
    public static readonly string DaemonVersion =
        typeof(SessionKeyMiddleware).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>约定值:订阅长请求的方法名。</summary>
    public const string ListenMethod = "subscriptions/listen";

    /// <summary>listen 流标识的发号器(进程内单调,仅用于 Streams 集合的键)。</summary>
    private static long _streamIdSeed;

    /// <summary>处理一个请求:按 header 登记在途 → 下沉 → 结束后注销。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <param name="registry">会话键注册表。</param>
    /// <param name="accessor">键的请求作用域载体。</param>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <returns>管道处理任务。</returns>
    public static async Task HandleAsync(
        HttpContext context,
        SessionKeyRegistry registry,
        SessionKeyAccessor accessor,
        SessionKeyDiagnostics diagnostics,
        RequestDelegate next)
    {
        // 归一化:同一 GUID 的 {...} / 32-hex 写法必须落到同一个键,否则"换个写法就换会话"
        // —— 用户会看到"连接莫名消失、组也没了",而键仍然合法、一声不响。
        var rawKey = context.Request.Headers[HeaderName].ToString();
        var key = string.IsNullOrEmpty(rawKey) ? rawKey : SessionKeyRegistry.Normalize(rawKey);
        var workingDirectory = ReadWorkingDirectory(context);
        var isListen = string.Equals(
            context.Request.Headers[MethodHeaderName].ToString(),
            ListenMethod,
            StringComparison.Ordinal);

        // 混跑检测:旧 shim 不带这个头,新 shim 每请求都带。
        // 这是"工具突然只剩 godot_connect"最可能的现场之一,必须在日志里留痕 ——
        // 否则唯一的线索是客户端行为异常,而服务端一条记录都没有。
        //
        // 注意:**此时键可能还不存在**(EnsureKey 在下面的分支里),所以版本只在这里
        // 判定与告警,真正写进注册表要等建键之后 —— 提前写会静默 return false。
        var shimVersion = context.Request.Headers[ShimVersionHeaderName].ToString();
        if (shimVersion.Length > 0 && !string.Equals(shimVersion, DaemonVersion, StringComparison.Ordinal))
        {
            diagnostics.VersionMismatchOnce(shimVersion);
        }

        if (string.IsNullOrEmpty(key) || !SessionKeyRegistry.IsValidKey(key))
        {
            // 无键与"非法键"是两种现场:前者多为旧配置/旧 shim,后者是转发器 bug。
            if (string.IsNullOrEmpty(key))
            {
                diagnostics.MissingHeaderOnce();
            }
            else
            {
                diagnostics.InvalidKeyOnce();
            }

            // 无 header 走"未连接"语义(只有 godot_connect),非法 header 也一视同仁 ——
            // 但两者都不建键:请求不该顺手创建会话。
            //
            // 唯一例外是 listen:spec §1.6 要求"绝不开无键流"。无键流既不进注册表、
            // 也不受 MaxSessionKeys 约束,还能订阅工具面变更 —— 等于一个免配额的无限资源。
            if (isListen)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        error = new
                        {
                            code = -32602,
                            message = "subscriptions/listen 必须携带合法的 x-godot-session 头。",
                        },
                    },
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        // listen 是长流:它的"在途"由注册表的 Streams 集合表达,不计入 InFlight ——
        // 若两边都记,listen 关闭时 InFlight 没有配对的递减,键就永远不会被回收。
        //
        // **必须登记流**:不登记的话 Active/Dormant 永不出现,而 Sweep 的跳过条件
        // (InFlight>0 || Streams>0)恒假 —— 一条开着的 listen 长流会让键在 10 分钟后被
        // 静默回收,下一次 tools/list 直接塌回只有 godot_connect。这是最难查的"工具看着在、
        // 一调就错"。建键与加流在同一临界区(spec §1.6)。
        if (isListen)
        {
            var streamId = Interlocked.Increment(ref _streamIdSeed);
            var rejection = registry.AddStream(key, streamId, out _);
            if (rejection == SessionKeyRejection.CapacityExceeded)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        error = new
                        {
                            code = -32603,
                            message = $"会话数已达上限({registry.Policy.MaxSessionKeys})且无可回收项，无法再开流。",
                        },
                    },
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // 键此刻已存在(AddStream 建好了),现在写版本才有意义。
            _ = registry.SetShimVersion(key, shimVersion);
            using var listenScope = accessor.Begin(key, workingDirectory);
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                registry.RemoveStream(key, streamId);
            }

            return;
        }

        // 注意:即使键尚不存在也要写会话态 —— godot_connect 正是靠它建键的。
        // 版本同理:键存在就刷新(建键发生在更下面,所以首次请求这里可能落空,
        // 下一次请求就会补上;shim 每请求都带这个头)。
        _ = registry.SetShimVersion(key, shimVersion);
        var entry = registry.RequestStarted(key);
        using var scope = accessor.Begin(key, workingDirectory);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            if (entry is not null)
            {
                registry.RequestCompleted(key, isListen: false);
            }
        }
    }
}
