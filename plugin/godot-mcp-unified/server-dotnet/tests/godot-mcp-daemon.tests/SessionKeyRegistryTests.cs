using GodotMcp.Daemon.Sessions;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// <see cref="SessionKeyRegistry"/> 的三态状态机、淘汰与过期(ADR-0006)。
/// </summary>
/// <remarks>
/// 全部用 <see cref="ManualTimeProvider"/> 推进时间,不 sleep 真实 30s/10min。
/// 断言只针对外部可见行为(状态、键数、删除结果),不锚定内部实现细节 ——
/// 淘汰顺序、"最旧"这类策略不是需求,换实现不应让测试红。
/// </remarks>
public sealed class SessionKeyRegistryTests
{
    private static readonly string KeyA = Guid.NewGuid().ToString();
    private static readonly string KeyB = Guid.NewGuid().ToString();

    private static (SessionKeyRegistry Registry, ManualTimeProvider Clock) Create(
        TimeSpan? grace = null,
        TimeSpan? ttl = null,
        int max = 1024)
    {
        var clock = new ManualTimeProvider();
        var policy = new SessionKeyPolicy(
            grace ?? TimeSpan.FromSeconds(30),
            ttl ?? TimeSpan.FromMinutes(10),
            ttl ?? TimeSpan.FromMinutes(10),
            max);
        return (new SessionKeyRegistry(clock, policy), clock);
    }

    [Fact]
    public void EnsureKey_creates_idle_no_stream_entry()
    {
        var (registry, _) = Create();

        var rejection = registry.EnsureKey(KeyA, out var entry);

        Assert.Equal(SessionKeyRejection.None, rejection);
        Assert.NotNull(entry);
        Assert.Equal(SessionKeyState.IdleNoStream, entry.State);
        Assert.Empty(entry.Streams);
    }

    [Fact]
    public void AddStream_moves_key_to_active_even_when_it_did_not_exist()
    {
        var (registry, _) = Create();

        // listen 必须能建键:shim 启动的第一条请求就是内部 subscriptions/listen。
        var rejection = registry.AddStream(KeyA, streamId: 1, out var entry);

        Assert.Equal(SessionKeyRejection.None, rejection);
        Assert.NotNull(entry);
        Assert.Equal(SessionKeyState.Active, entry.State);
        Assert.Single(entry.Streams);
    }

    [Fact]
    public void Removing_last_stream_enters_dormant()
    {
        var (registry, _) = Create();
        registry.AddStream(KeyA, 1, out _);

        registry.RemoveStream(KeyA, 1);

        Assert.True(registry.TryGetSnapshot(KeyA, out var snapshot));
        Assert.Equal(SessionKeyState.Dormant, snapshot.State);
        Assert.Equal(0, snapshot.StreamCount);
    }

    [Fact]
    public void Removing_one_of_two_streams_keeps_active()
    {
        var (registry, _) = Create();
        registry.AddStream(KeyA, 1, out _);
        registry.AddStream(KeyA, 2, out _);

        registry.RemoveStream(KeyA, 1);

        Assert.True(registry.TryGetSnapshot(KeyA, out var snapshot));
        Assert.Equal(SessionKeyState.Active, snapshot.State);
        Assert.Equal(1, snapshot.StreamCount);
    }

    [Fact]
    public void Non_listen_request_during_grace_moves_to_idle_no_stream()
    {
        var (registry, clock) = Create();
        registry.AddStream(KeyA, 1, out _);
        registry.RemoveStream(KeyA, 1);

        clock.Advance(TimeSpan.FromSeconds(5));
        registry.RequestStarted(KeyA);
        registry.RequestCompleted(KeyA, isListen: false);

        Assert.True(registry.TryGetSnapshot(KeyA, out var snapshot));
        Assert.Equal(SessionKeyState.IdleNoStream, snapshot.State);
    }

    [Fact]
    public void Listen_during_grace_returns_to_active_without_becoming_idle()
    {
        var (registry, _) = Create();
        registry.AddStream(KeyA, 1, out _);
        registry.RemoveStream(KeyA, 1);

        // 宿主重连:listen 是流,不是普通请求 —— 必须走加流分支,不能转 IdleNoStream。
        registry.RequestStarted(KeyA);
        registry.AddStream(KeyA, 2, out var entry);
        registry.RequestCompleted(KeyA, isListen: true);

        Assert.NotNull(entry);
        Assert.True(registry.TryGetSnapshot(KeyA, out var snapshot));
        Assert.Equal(SessionKeyState.Active, snapshot.State);
        Assert.Equal(1, snapshot.StreamCount);
    }

    [Fact]
    public void Listen_close_does_not_bypass_grace()
    {
        var (registry, clock) = Create();
        registry.AddStream(KeyA, 1, out _);

        // listen 关闭的时序:流移除 → Active→Dormant。listen 是长流,中间件不对它登记在途
        // (在途由 Streams 表达),所以这里也不调 RequestStarted。
        registry.RemoveStream(KeyA, 1);

        // 即使有人对 listen 误调 RequestCompleted,也不能把 Dormant 推成 IdleNoStream ——
        // 否则 30s grace 被完全绕过,退化成 10min TTL。
        registry.RequestCompleted(KeyA, isListen: true);

        Assert.True(registry.TryGetSnapshot(KeyA, out var snapshot));
        Assert.Equal(SessionKeyState.Dormant, snapshot.State);

        // 宽限期到期就删,而不是等到 10 分钟 TTL。
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, registry.Sweep());
    }

    [Fact]
    public void Sweep_removes_dormant_after_grace()
    {
        var (registry, clock) = Create(grace: TimeSpan.FromSeconds(30));
        registry.AddStream(KeyA, 1, out _);
        registry.RemoveStream(KeyA, 1);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, registry.Sweep());

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, registry.Sweep());
        Assert.False(registry.Contains(KeyA));
    }

    [Fact]
    public void Sweep_removes_idle_no_stream_after_ttl()
    {
        var (registry, clock) = Create(ttl: TimeSpan.FromMinutes(10));
        registry.EnsureKey(KeyA, out _);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, registry.Sweep());

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, registry.Sweep());
        Assert.False(registry.Contains(KeyA));
    }

    [Fact]
    public void Request_refreshes_ttl()
    {
        var (registry, clock) = Create(ttl: TimeSpan.FromMinutes(10));
        registry.EnsureKey(KeyA, out _);

        clock.Advance(TimeSpan.FromMinutes(9));
        registry.RequestStarted(KeyA);
        registry.RequestCompleted(KeyA, isListen: false);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, registry.Sweep());
        Assert.True(registry.Contains(KeyA));
    }

    [Fact]
    public void Active_key_is_never_swept()
    {
        var (registry, clock) = Create(grace: TimeSpan.FromSeconds(1), ttl: TimeSpan.FromSeconds(1));
        registry.AddStream(KeyA, 1, out _);

        clock.Advance(TimeSpan.FromDays(7));

        Assert.Equal(0, registry.Sweep());
        Assert.True(registry.Contains(KeyA));
    }

    [Fact]
    public void In_flight_key_is_not_swept()
    {
        var (registry, clock) = Create(ttl: TimeSpan.FromSeconds(1));
        registry.EnsureKey(KeyA, out _);
        registry.RequestStarted(KeyA);

        // 长工具(如 editor_build_csharp_wait)超过 TTL 时 LastSeen 只在请求开始刷新;
        // 不等在途就会在请求进行中删键。
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, registry.Sweep());
        Assert.True(registry.Contains(KeyA));

        // 请求完成后 LastSeen 被刷新(刚用过),所以不是立刻回收,而是 TTL 重新计时。
        registry.RequestCompleted(KeyA, isListen: false);
        Assert.Equal(0, registry.Sweep());
        Assert.True(registry.Contains(KeyA));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, registry.Sweep());
        Assert.False(registry.Contains(KeyA));
    }

    [Fact]
    public void Invalid_key_is_rejected_and_creates_nothing()
    {
        var (registry, _) = Create();

        Assert.Equal(SessionKeyRejection.InvalidKey, registry.EnsureKey("not-a-uuid", out _));
        Assert.Equal(SessionKeyRejection.InvalidKey, registry.EnsureKey(string.Empty, out _));
        Assert.Equal(SessionKeyRejection.InvalidKey, registry.EnsureKey(new string('a', 65), out _));
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.CreatedCount);
    }

    [Fact]
    public void Capacity_evicts_an_evictable_key_instead_of_refusing()
    {
        var (registry, _) = Create(max: 2);
        string first = Guid.NewGuid().ToString();
        registry.EnsureKey(first, out _);
        registry.EnsureKey(KeyB, out _);
        Assert.Equal(2, registry.Count);

        // 第三个键必须能建出来。不断言"淘汰了哪一个"——「最久未用」是实现策略而不是需求,
        // 断言它会把实现锁死。要断言的是:总数守恒、新键在、被淘汰者来自可淘汰集合
        // (两个候选都是 IdleNoStream,天然满足)。
        Assert.Equal(SessionKeyRejection.None, registry.EnsureKey(KeyA, out _));
        Assert.Equal(2, registry.Count);
        Assert.True(registry.Contains(KeyA));
        Assert.True(registry.Contains(first) ^ registry.Contains(KeyB));
    }

    [Fact]
    public void Capacity_refuses_new_key_when_all_keys_are_active()
    {
        var (registry, _) = Create(max: 2);
        registry.AddStream(KeyA, 1, out _);
        registry.AddStream(KeyB, 2, out _);

        // 活跃会话永不被淘汰 —— 宁可拒绝新建,也不能让正在连的 shim 突然失去会话。
        Assert.Equal(SessionKeyRejection.CapacityExceeded, registry.EnsureKey(Guid.NewGuid().ToString(), out _));
        Assert.Equal(SessionKeyRejection.CapacityExceeded, registry.AddStream(Guid.NewGuid().ToString(), 3, out _));
        Assert.Equal(2, registry.Count);
        Assert.True(registry.Contains(KeyA));
        Assert.True(registry.Contains(KeyB));
    }

    [Fact]
    public void Groups_and_default_target_survive_until_key_is_swept()
    {
        var (registry, clock) = Create();
        registry.EnsureKey(KeyA, out _);

        registry.SetGroups(KeyA, ["playtest", "signals"]);
        registry.SetDefaultTarget(KeyA, "/project/a");
        registry.RemoveStream(KeyA, 1); // no-op: 没有流

        Assert.True(registry.TryGetSnapshot(KeyA, out var snapshot));
        Assert.Equal(new[] { "playtest", "signals" }, snapshot.Groups.OrderBy(g => g, StringComparer.Ordinal));
        Assert.Equal("/project/a", snapshot.DefaultTarget);

        clock.Advance(TimeSpan.FromMinutes(11));
        registry.Sweep();
        Assert.False(registry.Contains(KeyA));
    }

    [Fact]
    public void CreatedCount_tracks_only_actual_creations()
    {
        var (registry, _) = Create();

        registry.EnsureKey(KeyA, out _);
        registry.EnsureKey(KeyA, out _); // 已存在,不递增
        registry.AddStream(KeyB, 1, out _);

        // 这条断言是"无 header 请求不建键"的判据:CompareCreatedCount 前后必须相等。
        Assert.Equal(2, registry.CreatedCount);
    }

    [Fact]
    public void key_text_variants_normalize_to_the_same_session()
    {
        // 同一 GUID 的不同文本形态必须落到同一个键。
        // 不归一化的话:配额多占一个,而用户的组与默认目标留在旧键上 ——
        // 现场表现是"连接莫名消失、组也没了",且因为键仍然合法,一声不响。
        var (registry, _) = Create();

        const string canonical = "55555555-5555-5555-5555-555555555555";
        const string braced = "{55555555-5555-5555-5555-555555555555}";
        const string plain = "55555555555555555555555555555555";

        Assert.Equal(canonical, SessionKeyRegistry.Normalize(braced));
        Assert.Equal(canonical, SessionKeyRegistry.Normalize(plain));
        Assert.Equal(canonical, SessionKeyRegistry.Normalize(canonical));

        registry.EnsureKey(canonical, out _);
        registry.SetGroups(canonical, ["scene_edit"]);

        // 用另一种写法建键:必须命中同一个条目,而不是新建一个。
        Assert.Equal(SessionKeyRejection.None, registry.EnsureKey(braced, out _));
        Assert.Equal(1, registry.Count);

        // 查询侧由上游(中间件 / 工具面组装器)统一归一化;这里显式归一化以模拟真实路径。
        Assert.True(registry.TryGetSnapshot(SessionKeyRegistry.Normalize(braced), out var snapshot));
        Assert.Equal(["scene_edit"], snapshot.Groups);
    }

    [Fact]
    public void unparsable_key_is_returned_unchanged_so_validation_can_report_it()
    {
        // 非法键必须原样返回:归一化若把它改写成某个合法值,错误码就会从
        // INVALID_SESSION_KEY 变成"建了个新会话"。
        Assert.Equal("not-a-uuid", SessionKeyRegistry.Normalize("not-a-uuid"));
        Assert.False(SessionKeyRegistry.IsValidKey("not-a-uuid"));
    }

    [Fact]
    public void connected_key_survives_the_dormant_grace_period()
    {
        // 宿主暂时把 listen 关掉(SDK 重连间隙 / 用户取消订阅)时,键进入 Dormant。
        // 若按 30s 宽限期删键,用户看到"工具突然只剩 godot_connect",而且拿到的是
        // **新**短键、对不上号。已连接的键必须与"无流但已连接"同档(ConnectedTtl)。
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(
            clock,
            SessionKeyPolicy.Default with
            {
                HandoffGrace = TimeSpan.FromSeconds(30),
                ConnectedTtl = TimeSpan.FromHours(1),
            });

        const string key = "66666666-6666-6666-6666-666666666666";
        keys.EnsureKey(key, out _);
        keys.MarkConnected(key);
        keys.AddStream(key, 1, out _);
        keys.RemoveStream(key, 1);

        // 越过 30s 宽限期,但远小于 ConnectedTtl。
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, keys.Sweep());
        Assert.True(keys.Contains(key), "已连接的键不该在宽限期后被回收");

        // 超过 ConnectedTtl 才回收。
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, keys.Sweep());
        Assert.False(keys.Contains(key));
    }

    [Fact]
    public void never_connected_key_is_reclaimed_after_the_grace_period()
    {
        // 未 connect 的 Dormant 键仍走宽限期:那是真正的交接瞬间,
        // 不回收就是给"连不上还占配额"留口子。
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(
            clock,
            SessionKeyPolicy.Default with { HandoffGrace = TimeSpan.FromSeconds(30) });

        const string key = "77777777-7777-7777-7777-777777777777";
        keys.EnsureKey(key, out _);
        keys.AddStream(key, 1, out _);
        keys.RemoveStream(key, 1);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, keys.Sweep());
        Assert.False(keys.Contains(key));
    }

    [Fact]
    public void every_key_taking_method_normalizes_its_input()
    {
        // 入口(中间件 / 工具面组装器)已经归一化,这里是**纵深防御**:
        // 任何调用方都不该因为 GUID 写法不同而落到另一个桶 —— 那会静默分桶,
        // 组与默认目标留在旧键上,而键依然"合法",一声不响。
        var (registry, _) = Create();

        const string canonical = "99999999-9999-9999-9999-999999999999";
        const string braced = "{99999999-9999-9999-9999-999999999999}";

        registry.EnsureKey(braced, out _);
        Assert.Equal(1, registry.Count);

        // 写路径:用不同写法写,读路径必须看到同一份状态。
        Assert.True(registry.SetGroups(braced, ["scene_edit"]));
        Assert.True(registry.SetDefaultTarget(braced, "d:/proj/x"));
        Assert.True(registry.MarkConnected(braced));
        Assert.True(registry.SetWorkingDirectory(braced, "d:/work"));
        Assert.True(registry.SetShimVersion(braced, "1.0.0"));
        Assert.True(registry.Contains(braced));
        Assert.NotNull(registry.RequestStarted(braced));
        registry.RequestCompleted(braced, isListen: false);
        registry.AddStream(braced, 42, out _);
        registry.RemoveStream(braced, 42);

        // 用规范写法读回:所有写入都应落在同一个键上。
        Assert.True(registry.TryGetSnapshot(canonical, out var snapshot));
        Assert.Equal(["scene_edit"], snapshot.Groups);
        Assert.Equal("d:/proj/x", snapshot.DefaultTarget);
        Assert.True(snapshot.Connected);
        Assert.Equal("d:/work", snapshot.WorkingDirectory);
        Assert.Equal("1.0.0", snapshot.ShimVersion);
        Assert.Equal(1, registry.Count);
    }
}
