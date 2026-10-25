using Microsoft.Extensions.Logging;

namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 会话键生命周期的日志(ADR-0006 §1.4)。
/// </summary>
/// <remarks>
/// <para><b>这类日志为什么必须存在</b>:用户报"工具突然消失"时,能拿到的唯一线索是
/// <c>godot_connect</c> 返回的 8 位键前缀,而排查时最需要回答的是三个问题:
/// 这个键还在吗?它是什么状态?它为什么被删?没有这些日志,唯一相关的一行是
/// "回收 N 个过期键" —— 既对不上号、也说不出原因,工单基本无法复盘。</para>
/// <para><b>为什么单独成类</b>:<see cref="SessionKeyRegistry"/> 是被高频调用的纯数据结构,
/// 给它塞 <c>ILogger</c> 会把日志依赖压进最内层;<see cref="SessionKeyRegistry"/>
/// 通过 <see cref="SessionKeyRegistry.KeyRecycled"/> 与建键返回值把事实抛出来,
/// 由本类(挂在 <see cref="SessionKeyReaper"/> 与中间件旁)决定怎么记。</para>
/// </remarks>
/// <param name="logger">日志。</param>
internal sealed class SessionKeyDiagnostics(ILogger<SessionKeyDiagnostics> logger)
{
    /// <summary>同一类告警只记一次(避免每次请求刷屏)。</summary>
    private int _missingHeaderWarned;
    private int _invalidKeyWarned;
    private int _versionMismatchWarned;

    /// <summary>
    /// 记录一次"请求没带会话键" —— 通常是旧配置或旧 shim,和"键被回收"是完全不同的现场。
    /// </summary>
    /// <remarks>只记第一次:未带键的请求在混跑期可能出现很多次,每次都写会把日志冲掉。</remarks>
    public void MissingHeaderOnce()
    {
        if (Interlocked.Exchange(ref _missingHeaderWarned, 1) == 0)
        {
            logger.LogWarning(
                "收到未携带 x-godot-session 的请求。该会话只会看到 godot_connect 一个工具。" +
                "若这是升级后的首次运行,请确认客户端走的是最新 shim;若是调试用途,可设 GODOT_MCP_DEBUG_UNKEYED=1。");
        }
    }

    /// <summary>
    /// 记录一次"带了 header 但不是合法会话键" —— 转发器 bug,与"没带 header"是两种现场。
    /// </summary>
    /// <remarks>只记第一次:非法键通常意味着某个转发环节在改写或截断 header,会持续发生。</remarks>
    public void InvalidKeyOnce()
    {
        if (Interlocked.Exchange(ref _invalidKeyWarned, 1) == 0)
        {
            logger.LogWarning(
                "收到无法解析为 GUID 的 x-godot-session 头。这通常是转发器缺陷(改写/截断 header)," +
                "而不是配置问题;该会话只会看到 godot_connect 一个工具。");
        }
    }

    /// <summary>记录一次"shim 版本不符"(升级混跑)。</summary>
    /// <param name="version">shim 上报的版本;未知为 null。</param>
    public void VersionMismatchOnce(string? version)
    {
        if (Interlocked.Exchange(ref _versionMismatchWarned, 1) == 0)
        {
            logger.LogWarning(
                "检测到与当前 daemon 不同版本的 shim(上报版本 {Version});混跑可能导致工具名或头部语义不一致。建议重启客户端会话以加载新 shim。",
                version ?? "(未上报)");
        }
    }

    /// <summary>记录键被回收,<b>带短键与原因</b>。</summary>
    /// <param name="shortKey">键的前 8 位(与 godot_connect 返回的一致)。</param>
    /// <param name="reason">回收原因(过期/容量淘汰)。</param>
    public void KeyRecycled(string shortKey, string reason)
        => logger.LogInformation("会话键回收:{ShortKey}({Reason})。该键的组状态与默认目标已清除。", shortKey, reason);

    /// <summary>记录工具面尺寸(Debug 级):让"面塌了"在日志里留痕。</summary>
    /// <param name="shortKey">键前缀;无键为 null。</param>
    /// <param name="hasSession">是否已连接。</param>
    /// <param name="visibleCount">本请求可见工具数。</param>
    public void ToolFace(string? shortKey, bool hasSession, int visibleCount)
        => logger.LogDebug(
            "工具面:key={ShortKey} connected={Connected} visible={Visible}",
            shortKey ?? "(none)",
            hasSession,
            visibleCount);

    /// <summary>启动时打印一次生效策略,便于事后核对"当时用的是哪套 TTL"。</summary>
    /// <param name="policy">生效的会话键策略。</param>
    public void PolicyInEffect(SessionKeyPolicy policy)
        => logger.LogInformation(
            "会话键策略:grace={Grace}s noStream={NoStream}s connected={Connected} maxKeys={Max}。无键请求只会看到 godot_connect。",
            policy.HandoffGrace.TotalSeconds,
            policy.NoStreamTtl.TotalSeconds,
            policy.ConnectedTtl.TotalSeconds,
            policy.MaxSessionKeys);
}
