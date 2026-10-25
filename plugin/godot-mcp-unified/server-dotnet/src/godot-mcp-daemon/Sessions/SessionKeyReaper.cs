using Microsoft.Extensions.Hosting;

namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 会话键的过期调度器(ADR-0006)。周期扫描 <see cref="SessionKeyRegistry"/>,回收宽限期已过的
/// <c>Dormant</c> 与超过 TTL 的 <c>IdleNoStream</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么必须有独立调度器</b>:过期事件不能靠"请求到达时顺手判定"—— 一个既无请求
/// 又无流的键永远不会再触发任何代码,那样 <c>HandoffGrace</c> 与 <c>NoStreamTtl</c> 就只是两个
/// 永不生效的常量,等于泄漏。</para>
/// <para><b>为什么不依赖 IdleExitService</b>:它默认禁用(ADR-0004),且语义是"进程退出",
/// 与"回收单个键"无关。</para>
/// <para><b>为什么现在不用最小堆</b>:1024 键 × 1s 全扫是微秒级;而 <c>IdleNoStream</c> 的
/// <c>LastSeen</c> 每个请求都刷新,堆需要惰性删除 + generation + 并发失效,代码量与 bug 面都更大。
/// 扫描已抽到本类后面,真到瓶颈时换时间轮/分桶即可,不动调用方。</para>
/// </remarks>
internal sealed class SessionKeyReaper(
    SessionKeyRegistry registry,
    TimeProvider timeProvider,
    ILogger<SessionKeyReaper> logger) : BackgroundService
{
    /// <summary>扫描周期。1 秒足够让 30s/10min 两档过期在肉眼不可感知的延迟内生效。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>单次扫描耗时告警阈值:超过它说明键数增长到该换调度结构了。</summary>
    /// <summary>
    /// 慢扫描告警阈值。<b>不能取 1ms</b>:1024 键全扫在高负载下会反复超过它,
    /// 而扫描是 1Hz —— 那就是每秒一条 WARN,把日志冲光,真正该看的会话回收记录反而被埋掉。
    /// 取 20ms:到这个量级已经说明调度设计需要改(例如换成时间轮),而不是偶发抖动。
    /// </summary>
    private static readonly TimeSpan SlowScanThreshold = TimeSpan.FromMilliseconds(20);

    /// <summary>执行一轮扫描;测试可直接调用它,不必等真实时钟。</summary>
    /// <returns>本次回收的键数。</returns>
    public int SweepOnce()
    {
        var start = timeProvider.GetTimestamp();

        // 先排空容量淘汰路径攒下的待通知键(AddStream 的容量分支在锁内,不能就地回调)。
        registry.DrainPendingRecycled();

        var removed = registry.Sweep();
        var elapsed = timeProvider.GetElapsedTime(start);
        if (removed > 0)
        {
            logger.LogInformation("会话键回收:{Removed} 个过期键,剩余 {Remaining} 个。", removed, registry.Count);
        }

        // 只在超阈值时记录:1Hz 全量打印是每天 8.6 万行。
        if (elapsed > SlowScanThreshold)
        {
            logger.LogWarning(
                "会话键扫描耗时 {ElapsedMs:F2}ms,超过 {ThresholdMs}ms;键数 {Count} —— 考虑改用时间轮/分桶调度。",
                elapsed.TotalMilliseconds,
                SlowScanThreshold.TotalMilliseconds,
                registry.Count);
        }

        return removed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // **每一轮都必须自己兜住异常**。未处理的异常会从 ExecuteAsync 逃出去,
                // .NET 默认 BackgroundServiceExceptionBehavior.StopHost 会让整个 daemon 退出
                // (退出码 0,宿主只看到"连接断了")。键回收是后台维护动作,它失败绝不该
                // 带走前台服务 —— 宁可这一轮不回收,下一轮再试。
                try
                {
                    SweepOnce();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "会话键扫描失败,本轮跳过;下一轮继续。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关机。
        }
    }
}
