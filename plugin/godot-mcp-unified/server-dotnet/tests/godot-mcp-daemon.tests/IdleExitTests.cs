using GodotMcp.Daemon;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 空闲自退的语义单测(ADR-0004,2026-09-23 修订:默认不自退)。
/// 覆盖两个纯单元 seam:<see cref="DaemonOptions.ParseIdleTimeout"/> 的解析分支,
/// 与 <see cref="IdleMonitor"/> 的空闲判定 —— 都不触碰环境变量或进程,
/// 因此与其它测试并行安全(集成面另见 DaemonMcpFaceTests 的空闲退出两例)。
/// </summary>
public class IdleExitTests
{
    /// <summary>
    /// 未设置/空白 = 禁用自退(默认):全 HTTP 接入的宿主不会周期性发请求,
    /// 默认自退会让服务在无人察觉时消失。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void idle_timeout_is_disabled_when_unset(string? raw)
    {
        var timeout = DaemonOptions.ParseIdleTimeout(raw);

        Assert.Equal(DaemonOptions.NoIdleExit, timeout);
        Assert.True(timeout <= TimeSpan.Zero, "禁用哨兵值必须是非正值");
    }

    /// <summary>显式 0 = 禁用自退(与未设置同义,便于安装器写死"常驻")。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("0.000")]
    public void idle_timeout_is_disabled_when_explicit_zero(string raw)
    {
        Assert.Equal(DaemonOptions.NoIdleExit, DaemonOptions.ParseIdleTimeout(raw));
    }

    /// <summary>正数(含小数)= 启用自退,单位为秒。</summary>
    [Theory]
    [InlineData("0.5", 500)]
    [InlineData("1", 1000)]
    [InlineData("600", 600_000)]
    [InlineData("86400", 86_400_000)]
    public void idle_timeout_parses_positive_seconds(string raw, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), DaemonOptions.ParseIdleTimeout(raw));
    }

    /// <summary>负数与非法值按环境故障报错(由 Program 归因为退出码 1),绝不静默降级。</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("-0.5")]
    [InlineData("abc")]
    [InlineData("10s")]
    public void idle_timeout_rejects_negative_or_invalid(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => DaemonOptions.ParseIdleTimeout(raw));
    }

    /// <summary>
    /// 禁用态:即使无在途请求、无实例连接,也永不判定为空闲(进程常驻)。
    /// </summary>
    [Fact]
    public async Task monitor_never_idle_when_disabled()
    {
        var monitor = new IdleMonitor(DaemonOptions.NoIdleExit);

        Assert.False(monitor.IdleExitEnabled);
        Assert.False(monitor.IsIdle);

        await Task.Delay(120);
        Assert.False(monitor.IsIdle, "禁用自退后,时间流逝不应让它变空闲");
    }

    /// <summary>
    /// 启用态:三个条件(无在途请求 / 无实例连接 / 超过阈值)同时成立才算空闲;
    /// 在途请求与实例连接各自都能阻止空闲。
    /// </summary>
    [Fact]
    public async Task monitor_reports_idle_only_after_timeout_without_activity()
    {
        var monitor = new IdleMonitor(TimeSpan.FromMilliseconds(80));

        Assert.True(monitor.IdleExitEnabled);
        Assert.False(monitor.IsIdle, "刚构造时算作活动时刻,尚未超阈值");

        monitor.Enter();
        await Task.Delay(120);
        Assert.False(monitor.IsIdle, "有在途请求时不得空闲");

        monitor.Exit();
        await Task.Delay(120);
        Assert.True(monitor.IsIdle, "请求完成后超阈值即空闲(计时由 Exit 重置)");

        monitor.SetConnectedInstances(1);
        Assert.False(monitor.IsIdle, "有已连接实例时不得空闲");

        monitor.SetConnectedInstances(0);
        await Task.Delay(120);
        Assert.True(monitor.IsIdle, "实例归零后重新起算,超阈值即空闲");
    }
}
