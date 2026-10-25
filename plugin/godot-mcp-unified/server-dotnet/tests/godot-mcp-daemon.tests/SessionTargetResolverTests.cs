using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tests.Infrastructure;
using GodotMcp.Daemon.Sessions;
using GodotMcp.Daemon.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// <see cref="SessionTargetResolver"/> 的读键与默认目标解析(ADR-0008)。
/// </summary>
/// <remarks>
/// 单独测这里而不只测端到端,是因为端到端把"键没读到"和"默认目标没写入"两种原因
/// 混成同一个 <c>NO_INSTANCE</c>,定位成本极高。这些测试直接锚定 resolver 的输入输出。
/// </remarks>
public sealed class SessionTargetResolverTests
{
    [Fact]
    public void default_target_is_read_from_the_current_session_key()
    {
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(clock, SessionKeyPolicy.Default);
        var scope = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var resolver = new SessionTargetResolver(
            instances: null!,
            keys,
            scope);

        const string key = "11111111-1111-1111-1111-111111111111";
        keys.EnsureKey(key, out _);
        keys.SetDefaultTarget(key, @"d:/proj/alpha");

        // 模拟中间件:把会话态放进请求作用域。
        using (scope.Begin(key, @"D:\proj\alpha"))
        {
            Assert.Equal("11111111-1111-1111-1111-111111111111", scope.CurrentKey);
            Assert.Equal(@"d:/proj/alpha", resolver.DefaultTargetOfCurrentSession());
        }
    }

    [Fact]
    public void default_target_takes_precedence_over_the_single_active_instance()
    {
        // 优先级语义的纯逻辑验证:有唯一活跃实例时,若本键还有默认目标,
        // 解析器必须把默认目标交给 InstanceManager(而不是让它走"唯一实例"捷径)。
        // 端到端的那条断言已由 resolve 层覆盖,这里只锚定"解析器把哪个值交出去"。
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(clock, SessionKeyPolicy.Default);
        var scope = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var resolver = new SessionTargetResolver(instances: null!, keys, scope);

        const string key = "44444444-4444-4444-4444-444444444444";
        keys.EnsureKey(key, out _);
        keys.SetDefaultTarget(key, @"d:/proj/delta");

        using (scope.Begin(key, workingDirectory: null))
        {
            // 返回的就是"要交给 InstanceManager 的 defaultTarget"。
            Assert.Equal(@"d:/proj/delta", resolver.DefaultTargetOfCurrentSession());
        }
    }

    [Fact]
    public void no_key_means_no_default_target()
    {
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(clock, SessionKeyPolicy.Default);
        var scope = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var resolver = new SessionTargetResolver(instances: null!, keys, scope);

        // 没有 Begin:无键请求。绝不能因此读到别人的默认目标。
        Assert.Null(scope.CurrentKey);
        Assert.Null(resolver.DefaultTargetOfCurrentSession());
    }

    [Fact]
    public void key_without_default_target_returns_null()
    {
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(clock, SessionKeyPolicy.Default);
        var scope = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var resolver = new SessionTargetResolver(instances: null!, keys, scope);

        const string key = "22222222-2222-2222-2222-222222222222";
        keys.EnsureKey(key, out _);

        using (scope.Begin(key, workingDirectory: null))
        {
            Assert.Null(resolver.DefaultTargetOfCurrentSession());
        }
    }

    [Fact]
    public void recycled_key_no_longer_yields_a_default_target()
    {
        var clock = new ManualTimeProvider();
        var keys = new SessionKeyRegistry(
            clock,
            SessionKeyPolicy.Default with { NoStreamTtl = TimeSpan.FromSeconds(1) });
        var scope = new SessionKeyAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var resolver = new SessionTargetResolver(instances: null!, keys, scope);

        const string key = "33333333-3333-3333-3333-333333333333";
        keys.EnsureKey(key, out _);
        keys.SetDefaultTarget(key, @"d:/proj/gamma");

        using (scope.Begin(key, workingDirectory: null))
        {
            Assert.Equal(@"d:/proj/gamma", resolver.DefaultTargetOfCurrentSession());
        }

        // 过 TTL 后被回收:同键不能再给出默认目标(否则会把调用路由到一个已失效的会话)。
        clock.Advance(TimeSpan.FromSeconds(2));
        keys.Sweep();

        using (scope.Begin(key, workingDirectory: null))
        {
            Assert.Null(resolver.DefaultTargetOfCurrentSession());
        }
    }
}
