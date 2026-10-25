using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 两个会话键**真正并发**时不串面（审核轮次 S6 指出的测试缺口）。
/// </summary>
/// <remarks>
/// <para><b>为什么顺序测试不够</b>：会话态走 <c>AsyncLocal</c>（<c>SessionRequestScope</c>），
/// 而 AsyncLocal 的正确性只在"多个请求同时在飞"时才被考验 —— 顺序调用两个客户端时，
/// 前一个请求已经完全结束、执行上下文已回收，即使实现把键存进了静态字段也照样通过。
/// 一旦并发，静态字段就会让 A 的键出现在 B 的请求里（跨会话越权，且极难查）。</para>
///
/// <para>这条测试让两个键**同时**反复发请求，并各自断言对方的组不可见、自己的组可见。
/// 任一方向的串面都会让断言变红。</para>
/// </remarks>
public sealed class ConcurrentSessionIsolationTests
{
    [Fact]
    public async Task two_sessions_hammering_in_parallel_never_see_each_others_groups()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 120,
        });

        await using var alpha = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);
        await using var beta = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);

        // 两个键激活**互不相交**的组。
        await alpha.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\alpha");
        await beta.ConnectGodotAsync(groups: ["playtest"], project: @"D:\proj\beta");

        // 真并发：各自连打 12 轮 tools/list，期间没有任何等待/同步点。
        var alphaTask = HammerAsync(alpha.Client, expect: "editor_sync", forbid: "game_start", rounds: 12);
        var betaTask = HammerAsync(beta.Client, expect: "game_start", forbid: "editor_sync", rounds: 12);

        await Task.WhenAll(alphaTask, betaTask);
    }

    /// <summary>反复拉取工具列表并断言可见性。</summary>
    /// <param name="client">该会话的客户端。</param>
    /// <param name="expect">必须可见的工具。</param>
    /// <param name="forbid">绝不能可见的工具（对方组的代表）。</param>
    /// <param name="rounds">轮数。</param>
    private static async Task HammerAsync(
        ModelContextProtocol.Client.McpClient client,
        string expect,
        string forbid,
        int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            // 串面的两种表现：看到对方的组工具，或看不到自己的。
            Assert.DoesNotContain(forbid, names);
            Assert.Contains(expect, names);
            Assert.Contains("godot_connect", names);
        }
    }

    [Fact]
    public async Task parallel_connect_responses_report_their_own_groups_only()
    {
        // connect 的响应里带 groups/tools —— 这是模型判断"我现在有什么"的直接依据。
        // 并发的两次 connect 若读到同一个键，就会出现"两边的响应都报同一组组名"。
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 120,
        });

        await using var alpha = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);
        await using var beta = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);

        var alphaConnect = alpha.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\alpha");
        var betaConnect = beta.ConnectGodotAsync(groups: ["playtest"], project: @"D:\proj\beta");
        var results = await Task.WhenAll(alphaConnect, betaConnect);

        using var alphaPayload = JsonDocument.Parse(TextOf(results[0]));
        using var betaPayload = JsonDocument.Parse(TextOf(results[1]));

        var alphaGroups = GroupsOf(alphaPayload.RootElement);
        var betaGroups = GroupsOf(betaPayload.RootElement);

        Assert.Equal(["editor_ops"], alphaGroups);
        Assert.Equal(["playtest"], betaGroups);

        // 短键必须不同 —— 同一短键意味着两次 connect 落到了同一个会话。
        Assert.NotEqual(
            alphaPayload.RootElement.GetProperty("session").GetString(),
            betaPayload.RootElement.GetProperty("session").GetString());
    }

    /// <summary>取出响应 content 里的第一段文本。</summary>
    /// <param name="result">工具结果。</param>
    /// <returns>文本。</returns>
    private static string TextOf(ModelContextProtocol.Protocol.CallToolResult result)
        => Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>取出响应里的 groups 数组。</summary>
    /// <param name="root">响应根元素。</param>
    /// <returns>组名列表。</returns>
    private static string[] GroupsOf(JsonElement root)
        => root.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).ToArray();
}
