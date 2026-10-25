using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 会话键分级工具面(ADR-0007)的验收:未连接只看到 <c>godot_connect</c>;连接后展开为
/// core 9 ∪ 所选组;两个会话键的工具面互不可见。
/// </summary>
/// <remarks>
/// 这些断言是本次改造最核心的外部行为,因此只断言"模型能看到的工具名集合",
/// 不锚定内部实现(快照、集合填充顺序、锁)。
/// </remarks>
public sealed class ToolFaceTests
{
    /// <summary>core 9 的工具名(与 <see cref="ToolFace.Core"/> 同源)。</summary>
    private static readonly string[] CoreTools =
    [
        "godot_connect", "discover_tools", "list_instances", "list_operations",
        "log_read", "capture_screenshot", "scene_query", "scene_get_tree", "node_inspect",
    ];

    [Fact]
    public async Task unconnected_session_sees_only_godot_connect()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        // autoConnect: false —— 只建连接、不 connect,模拟"模型刚连上 MCP、还没调入口工具"。
        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);

        var tools = await connector.Client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToList();

        // 断言"恰好只有 1 个",不是"包含 1 个" —— 后者在回归时会静默放过。
        Assert.Equal(["godot_connect"], names);
    }

    [Fact]
    public async Task connect_without_groups_exposes_exactly_core_nine()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        var connect = await connector.ConnectGodotAsync(project: @"D:\proj\alpha");
        Assert.True(connect.IsError is null or false, "connect 不应失败");

        var names = (await connector.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        // 新键省略 groups = 空组 → 只有 core 9(这就是需求里说的"第一批工具")。
        Assert.Equal(CoreTools.OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task connect_with_group_adds_exactly_that_group()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await connector.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\alpha");

        var names = (await connector.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var expected = CoreTools
            .Concat(GroupCatalogue.Find("editor_ops")!.ToolNames)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));

        // 明确断言未激活组的工具不在面 —— 否则"裁剪没生效"也会因为 core 在而通过。
        Assert.DoesNotContain("scene_create_node", names);
        Assert.DoesNotContain("game_start", names);
    }

    [Fact]
    public async Task two_sessions_with_different_groups_do_not_see_each_other()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var sessionA = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await using var sessionB = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);

        // 两个键必须用**不同组集**:同组集本来就得到相同工具面,那样的断言是假绿。
        await sessionA.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\alpha");
        await sessionB.ConnectGodotAsync(groups: ["playtest"], project: @"D:\proj\beta");

        var namesA = (await sessionA.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var namesB = (await sessionB.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("editor_sync", namesA);
        Assert.DoesNotContain("editor_sync", namesB);
        Assert.Contains("game_start", namesB);
        Assert.DoesNotContain("game_start", namesA);

        // 会话键本身也必须不同(否则上面的差异无从解释)。
        Assert.NotEqual(sessionA.SessionKey, sessionB.SessionKey);
    }

    [Fact]
    public async Task unactivated_group_tool_errors_instead_of_vanishing_silently()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await connector.ConnectGodotAsync(project: @"D:\proj\alpha");

        // 调用一个"从未创建过"的懒建组工具:它既不在快照里、也不在 _toolObjects 里,
        // 必须由 CallToolHandler 兜底成 isError,而不是 JSON-RPC "工具不存在"。
        var result = await connector.Client.CallToolAsync(
            "tileset_edit", new Dictionary<string, object?> { ["instance"] = @"D:\proj\alpha" });

        Assert.True(result.IsError == true, "未激活组的工具应返回 isError 结果");
    }
}
