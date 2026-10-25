using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tests.Infrastructure;
using GodotMcp.Daemon.Sessions;
using Microsoft.AspNetCore.Http;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 键回收 → 组桶清理(<c>KeyRecycled</c> → <see cref="GroupService.ForgetKey"/>)。
/// </summary>
/// <remarks>
/// <para>不清桶有两个后果,两个都很难在端到端里归因:</para>
/// <list type="number">
/// <item>每个历史键永久留两个 <c>HashSet</c>,而 daemon 是常驻单例、shim 每次启动换 UUID
/// —— 桶无界增长;</item>
/// <item>同一 shim 用同一 UUID 重连时,一次**无参** <c>discover_tools()</c>(规范里那是"只浏览")
/// 会把桶里的旧组写回注册表并静默展开 —— 模型从未激活过的工具自己出现。</item>
/// </list>
/// </remarks>
public sealed class SessionKeyRecyclingTests
{
    [Fact]
    public void sweeping_a_connected_key_notifies_subscribers()
    {
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(
            clock,
            SessionKeyPolicy.Default with { ConnectedTtl = TimeSpan.FromSeconds(1) });

        var recycled = new List<string>();
        keys.KeyRecycled += recycled.Add;

        const string key = "aaaaaaaa-0000-0000-0000-000000000001";
        keys.EnsureKey(key, out _);
        keys.MarkConnected(key);

        clock.Advance(TimeSpan.FromSeconds(2));
        var removed = keys.Sweep();

        Assert.Equal(1, removed);
        Assert.Equal([key], recycled);
    }

    [Fact]
    public void capacity_eviction_also_notifies_after_draining()
    {
        var clock = new ManualTimeProvider();
        // 上限 1:第二个键必须淘汰第一个。
        var keys = new SessionKeyRegistry(
            clock,
            new SessionKeyPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), 1));

        var recycled = new List<string>();
        keys.KeyRecycled += recycled.Add;

        const string first = "bbbbbbbb-0000-0000-0000-000000000001";
        const string second = "bbbbbbbb-0000-0000-0000-000000000002";
        keys.EnsureKey(first, out _);

        // 未连接的键可被容量淘汰。EnsureKey 在出锁后**直接**回调,不经过 pending 队列
        // (pending 只服务 AddStream 那条"仍在锁内"的路径)。
        keys.EnsureKey(second, out _);

        Assert.Equal([first], recycled);
        Assert.True(keys.Contains(second));

        // pending 队列此刻应为空 —— 直接回调路径不该再产生一次重复通知。
        Assert.Equal(0, keys.DrainPendingRecycled());
        Assert.Single(recycled);
    }

    [Fact]
    public void forget_key_clears_the_group_buckets()
    {
        var (groups, scope, keys) = BuildGroupService();

        const string key = "cccccccc-0000-0000-0000-000000000001";
        keys.EnsureKey(key, out _);

        // 让该键的桶真实建立。
        scope.Set(key, null);
        groups.Activate("editor_ops");
        Assert.Equal(["editor_ops"], groups.SnapshotGroupsForCurrentKey());

        groups.ForgetKey(key);

        // 清桶后同一键的激活集合必须为空 —— 这正是"重连后浏览不复活旧组"的依据。
        scope.Set(key, null);
        Assert.Empty(groups.SnapshotGroupsForCurrentKey());
    }

    [Fact]
    public void forget_key_never_touches_the_default_bucket()
    {
        var (groups, scope, _) = BuildGroupService();

        // DefaultKey 是无键请求与回滚面的公共桶,不归任何会话所有,绝不能被回收回调清掉。
        scope.Set(GroupService.DefaultKey, null);
        groups.Activate("editor_ops");
        Assert.Equal(["editor_ops"], groups.SnapshotGroupsForCurrentKey());

        groups.ForgetKey(GroupService.DefaultKey);
        groups.ForgetKey(string.Empty);

        scope.Set(GroupService.DefaultKey, null);
        Assert.Equal(["editor_ops"], groups.SnapshotGroupsForCurrentKey());
    }

    /// <summary>构造一个不碰网络的 <see cref="GroupService"/> 与其会话态载体。</summary>
    /// <returns>组服务 + 会话态载体(两者共用同一个 scope 实例)。</returns>
    private static (GroupService Groups, SessionRequestScope Scope, SessionKeyRegistry Keys) BuildGroupService()
    {
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(clock, SessionKeyPolicy.Default);
        var accessor = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var scope = new SessionRequestScope();

        var groups = new GroupService(
            instances: null!,
            extensions: null!,
            accessor,
            scope,
            new Tools.SessionTargetResolver(instances: null!, keys, accessor));

        return (groups, scope, keys);
    }

    [Fact]
    public void reconnecting_with_the_same_uuid_does_not_revive_old_groups()
    {
        // 这是 ForgetKey 存在的**唯一理由**,必须端到端守住。
        //
        // 场景:shim 用同一 UUID 重连(进程重启后 UUID 改变,但同一进程内重连、
        // 或宿主重放 connect 时会复用)。若桶没被清,一次**无参**的 discover_tools
        // (按规范那是"只浏览")就会把旧组写回注册表并静默展开 ——
        // 模型从未激活过的工具自己出现在面上。
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(
            clock,
            SessionKeyPolicy.Default with { ConnectedTtl = TimeSpan.FromSeconds(1) });
        var accessor = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var scope = new SessionRequestScope();
        var groups = new GroupService(
            instances: null!,
            extensions: null!,
            accessor,
            scope,
            new Tools.SessionTargetResolver(instances: null!, keys, accessor));

        keys.KeyRecycled += groups.ForgetKey;

        const string key = "88888888-8888-8888-8888-888888888888";

        // 第一次连接:激活两组。
        keys.EnsureKey(key, out _);
        keys.MarkConnected(key);
        scope.Set(key, null);
        groups.Activate("editor_ops");
        groups.Activate("playtest");
        Assert.Equal(["editor_ops", "playtest"], groups.SnapshotGroupsForCurrentKey());

        // 键被回收(订阅者 ForgetKey 同步清桶)。
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, keys.Sweep());
        Assert.False(keys.Contains(key));

        // 同一 UUID 重新 connect:组集必须为空 —— 旧组不得复活。
        keys.EnsureKey(key, out _);
        keys.MarkConnected(key);
        scope.Set(key, null);
        Assert.Empty(groups.SnapshotGroupsForCurrentKey());
    }
}
