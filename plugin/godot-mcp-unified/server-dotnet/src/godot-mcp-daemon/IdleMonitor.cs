namespace GodotMcp.Daemon;

/// <summary>
/// 空闲状态监控(ADR-0004):无在途请求、无已连接 Godot 实例,且距最后一次活动超过阈值即视为空闲。
/// 每个已完成的请求都会重置计时;启动时刻即初始活动时刻 —— 从未有连接的
/// daemon 同样在阈值后退出,与"全部连接断开后"的语义一致。
/// spec US12 的另一半(所有 Godot 实例断开)经 <see cref="SetConnectedInstances"/> 计量:
/// 任一实例连接期间绝不空闲;最后一个实例断开的那一刻重置计时。
/// </summary>
public sealed class IdleMonitor
{
    /// <summary>空闲退出阈值,构造时固定。</summary>
    private readonly TimeSpan _timeout;

    /// <summary>在途请求数:中间件 Enter/Exit 维护,Interlocked 保证跨请求线程安全。</summary>
    private long _inFlight;

    /// <summary>已连接 Godot 实例数,由实例管理器经 SetConnectedInstances 上报。</summary>
    private long _connectedInstances;

    /// <summary>最后一次活动时刻(UTC ticks):请求 Exit 或实例归零时刷新。</summary>
    private long _lastActivityUtcTicks;

    /// <summary>
    /// 创建监控器,并把构造时刻记为初始活动时刻 —— 从未有连接的 daemon 同样会在阈值后判定空闲。
    /// </summary>
    /// <param name="timeout">空闲退出阈值,来自 <see cref="DaemonOptions.IdleTimeout"/>。</param>
    public IdleMonitor(TimeSpan timeout)
    {
        _timeout = timeout;
        _lastActivityUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    /// <summary>空闲退出阈值,供 IdleExitService 计算轮询间隔与日志文案。</summary>
    public TimeSpan Timeout => _timeout;

    /// <summary>是否没有任何在途请求(供退出前复核,收窄竞态窗口)。</summary>
    public bool HasNoRequestsInFlight => Interlocked.Read(ref _inFlight) == 0;

    /// <summary>请求进入:在途计数 +1。由 token 门禁之后的中间件调用。</summary>
    public void Enter()
    {
        Interlocked.Increment(ref _inFlight);
    }

    /// <summary>请求离开:在途计数 -1,并重置空闲计时。</summary>
    public void Exit()
    {
        Interlocked.Decrement(ref _inFlight);
        Volatile.Write(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>已连接的 Godot 实例数(由实例管理器在状态变化时上报)。</summary>
    public int ConnectedInstances => (int)Interlocked.Read(ref _connectedInstances);

    /// <summary>
    /// 更新已连接实例数。从"有实例"落到"零实例"的瞬间重置空闲计时 ——
    /// 空闲窗口从最后一个实例断开起算,而非从它连接期间的最后一次请求起算。
    /// </summary>
    public void SetConnectedInstances(int count)
    {
        var previous = Interlocked.Exchange(ref _connectedInstances, count);
        if (previous > 0 && count == 0)
        {
            Volatile.Write(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }

    /// <summary>
    /// 当前是否空闲,IdleExitService 每个轮询周期读取。
    /// </summary>
    /// <returns>在途请求为零、已连接实例为零、且距最后活动超过阈值三者同时成立时为 true。</returns>
    /// <para>逻辑链:在途计数非零 → 不空闲 → 已连接实例非零 → 不空闲 →
    /// 比较 UTC ticks 与阈值得出结论;三步全部无锁(Volatile/Interlocked 读)。</para>
    public bool IsIdle
    {
        get
        {
            if (Interlocked.Read(ref _inFlight) != 0)
            {
                return false;
            }

            if (Interlocked.Read(ref _connectedInstances) != 0)
            {
                return false;
            }

            var elapsedTicks = DateTimeOffset.UtcNow.UtcTicks - Volatile.Read(ref _lastActivityUtcTicks);
            return elapsedTicks >= _timeout.Ticks;
        }
    }
}
