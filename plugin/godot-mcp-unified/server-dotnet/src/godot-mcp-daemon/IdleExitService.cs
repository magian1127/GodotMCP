namespace GodotMcp.Daemon;

/// <summary>
/// 周期检查空闲状态;达到阈值即请求整进程退出(退出原因同时落 stderr 与 daemon.log)。
/// 托管为 BackgroundService,与宿主同生命周期:宿主关机时轮询被取消,服务随之结束。
/// </summary>
/// <param name="monitor">空闲状态来源,由 Program 注册为单例。</param>
/// <param name="lifetime">宿主生命周期,用于在空闲时触发整进程退出。</param>
/// <param name="logger">结构化日志,经 stderr 与 daemon.log 落地。</param>
public sealed class IdleExitService(
    IdleMonitor monitor,
    IHostApplicationLifetime lifetime,
    ILogger<IdleExitService> logger)
    : BackgroundService
{
    /// <summary>
    /// 空闲轮询主循环,直至触发退出或宿主关机。
    /// </summary>
    /// <param name="stoppingToken">宿主关机令牌,同时用作轮询取消令牌。</param>
    /// <para>逻辑链:检查间隔取 min(1s, 阈值/4),保证退出延迟至多为阈值的约四分之一 →
    /// 每个周期先看 <see cref="IdleMonitor.IsIdle"/>,再复核 <see cref="IdleMonitor.HasNoRequestsInFlight"/>,
    /// 收窄与请求进入的竞态窗口;任一不满足则继续下一周期 → 双条件成立:记日志、
    /// StopApplication 请求整进程退出并返回(在途请求由 Kestrel 优雅关机 drain)→
    /// stoppingToken 取消(WaitForNextTickAsync 抛 OperationCanceledException):宿主正常关机,静默结束。</para>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromMilliseconds(
            Math.Min(1000, monitor.Timeout.TotalMilliseconds / 4));
        using var timer = new PeriodicTimer(checkInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // 判真后复核在途计数,收窄与请求进入的竞态窗口;
                // 即便如此仍可能有请求在 StopApplication 后到达 —— Kestrel 优雅关机会 drain 它们。
                if (!monitor.IsIdle || !monitor.HasNoRequestsInFlight)
                {
                    continue;
                }

                logger.LogInformation(
                    "空闲 {IdleSeconds:F0}s 且全部连接已断开,进程退出。",
                    monitor.Timeout.TotalSeconds);
                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主正常关机 —— 非错误。
        }
    }
}
