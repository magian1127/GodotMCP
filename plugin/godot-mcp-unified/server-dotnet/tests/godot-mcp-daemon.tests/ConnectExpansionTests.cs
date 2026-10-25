using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 「<c>godot_connect</c> 之后，宿主是否真的会重新拉取工具列表」——这是阻断-2′ 的判定。
/// </summary>
/// <remarks>
/// <para><b>为什么这条必须有</b>：<c>godot_connect</c> 的响应会列出连接后的工具名，
/// 但那一份只是"下一请求会看到什么"——当前请求的 collection 是在 <c>Connected</c> 置位
/// **之前**组装好的。若宿主不重拉，模型看到的工具列表仍然只有 <c>godot_connect</c> 一个，
/// 而它手里已经有 9 个工具的清单：现场表现是"连上了但工具用不了"，
/// 比直接报错更难查。</para>
///
/// <para><b>两条可能的通路</b>：① <c>tools/list</c> 带 <c>ttlMs=0</c>，客户端不缓存、
/// 每次调用前重拉；② <c>tools/list_changed</c> 通知，客户端被动重拉。
/// legacy 宿主（initialize 握手）通常不开 <c>subscriptions/listen</c>，所以只能靠 ①。
/// 这条测试就是在验证 ① 是否真的成立。</para>
/// </remarks>
public sealed class ConnectExpansionTests
{
    [Fact]
    public async Task tools_list_reports_zero_ttl_so_clients_refetch_after_connect()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        // 走 legacy 路径(initialize 握手,与生产 shim 的 2025-11-25 形态一致)。
        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);

        // 未连接:恰好 1 个工具。
        Assert.Equal(["godot_connect"], (await connector.Client.ListToolsAsync()).Select(t => t.Name));

        // connect 之后再拉:必须展开。
        // 这一步能通过,说明客户端**确实**重新发了 tools/list —— 也就是 ttlMs=0 生效。
        await connector.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\alpha");
        var names = (await connector.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("godot_connect", names);
        Assert.Contains("list_instances", names);
        Assert.Contains("editor_sync", names);
        Assert.DoesNotContain("game_start", names);  // 只激活了 editor_ops
    }

    [Fact]
    public async Task connect_response_lists_exactly_what_the_face_will_expose()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);

        var connect = await connector.ConnectGodotAsync(groups: ["playtest"], project: @"D:\proj\alpha");
        Assert.True(connect.IsError is null or false, "connect 不应失败");

        // 响应里声明的工具集,必须与随后 tools/list 实际给出的一致。
        // 不一致会让模型相信"我有这些工具",而实际调用全部落到未命中兜底。
        var declared = ToolsFromConnectResponse(connect);
        var actual = (await connector.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.OrderBy(n => n, StringComparer.Ordinal),
            actual.OrderBy(n => n, StringComparer.Ordinal));

        // 而且必须真的不只是入口工具。
        Assert.True(actual.Count > 1, $"connect 后应展开,实际只有: {string.Join(", ", actual)}");
    }

    /// <summary>从 <c>godot_connect</c> 的响应 JSON 里取出它声明的工具名。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>声明的工具名集合。</returns>
    private static HashSet<string> ToolsFromConnectResponse(
        ModelContextProtocol.Protocol.CallToolResult result)
    {
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(
            Assert.Single(result.Content)).Text;
        using var payload = System.Text.Json.JsonDocument.Parse(text);
        return payload.RootElement.GetProperty("tools")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
