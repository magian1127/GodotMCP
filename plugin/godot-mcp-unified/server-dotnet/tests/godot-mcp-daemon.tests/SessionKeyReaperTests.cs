using GodotMcp.Daemon.Sessions;
using GodotMcp.Daemon.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// <see cref="SessionKeyReaper"/> 的回收行为。直接调 <see cref="SessionKeyReaper.SweepOnce"/>,
/// 不启动后台循环 —— 断言的是"过期会被回收"这个行为,不是"每 1 秒扫一次"这个实现选择。
/// </summary>
public sealed class SessionKeyReaperTests
{
    private static readonly string KeyA = Guid.NewGuid().ToString();

    private static (SessionKeyReaper Reaper, SessionKeyRegistry Registry, ManualTimeProvider Clock) Create(
        TimeSpan? grace = null,
        TimeSpan? ttl = null)
    {
        var clock = new ManualTimeProvider();
        var registry = new SessionKeyRegistry(
            clock,
            new SessionKeyPolicy(
                grace ?? TimeSpan.FromSeconds(30),
                ttl ?? TimeSpan.FromMinutes(10),
                ttl ?? TimeSpan.FromMinutes(10),
                1024));
        var reaper = new SessionKeyReaper(registry, clock, NullLogger<SessionKeyReaper>.Instance);
        return (reaper, registry, clock);
    }

    [Fact]
    public void SweepOnce_reclaims_dormant_after_grace()
    {
        var (reaper, registry, clock) = Create();
        registry.AddStream(KeyA, 1, out _);
        registry.RemoveStream(KeyA, 1);

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(0, reaper.SweepOnce());

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, reaper.SweepOnce());
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void SweepOnce_reclaims_idle_no_stream_after_ttl()
    {
        var (reaper, registry, clock) = Create();
        registry.EnsureKey(KeyA, out _);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, reaper.SweepOnce());
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void SweepOnce_keeps_keys_with_traffic()
    {
        var (reaper, registry, clock) = Create(ttl: TimeSpan.FromMinutes(1));
        registry.EnsureKey(KeyA, out _);

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            registry.RequestStarted(KeyA);
            registry.RequestCompleted(KeyA, isListen: false);
            Assert.Equal(0, reaper.SweepOnce());
        }

        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void SweepOnce_never_removes_in_flight_key()
    {
        var (reaper, registry, clock) = Create(ttl: TimeSpan.FromSeconds(1));
        registry.EnsureKey(KeyA, out _);
        registry.RequestStarted(KeyA);

        clock.Advance(TimeSpan.FromMinutes(30));

        // 一个卡住的长工具不能因为超过 TTL 就被删掉 —— 否则请求进行到一半键就没了。
        Assert.Equal(0, reaper.SweepOnce());
        Assert.Equal(1, registry.Count);
    }
}
