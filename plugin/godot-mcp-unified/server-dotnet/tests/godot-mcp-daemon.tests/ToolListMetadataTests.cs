using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// <c>tools/list</c> 的缓存语义与目录顺序(2026-07-28 §2.4 / spec §2.3)。
/// </summary>
/// <remarks>
/// <para><b>为什么这些断言必须单独写</b>:SDK 对缺失的 <c>ttlMs</c>/<c>cacheScope</c> 只 warn
/// 不 fail,漏写不会让任何功能测试变红 —— 但客户端会按默认值缓存,于是"工具面该变的时候不变"。
/// 这类缺陷只在真实会话里表现为"模型看不见刚激活的工具",极难定位。</para>
/// <para><b>顺序</b>:<c>godot_connect</c> 必须恒第一(模型第一眼就要看到入口),其余按 Node 表序。
/// </para>
/// </remarks>
public sealed class ToolListMetadataTests
{
    /// <summary>core 9 的完整名单(未连接时只给 godot_connect,连接后给这 9 个)。</summary>
    private static readonly string[] CoreTools =
    [
        "godot_connect", "discover_tools", "list_instances", "list_operations",
        "log_read", "capture_screenshot", "scene_query", "scene_get_tree", "node_inspect",
    ];

    [Fact]
    public async Task tools_list_declares_zero_ttl_and_private_cache_scope()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);

        // 用 SDK 的底层重载拿原始 ListToolsResult(高层 ListToolsAsync 只回工具列表)。
        var result = await connector.Client.ListToolsAsync();

        // 工具面本身必须非空(否则下面的断言是空转)。
        Assert.NotEmpty(result);

        // 缓存语义经裸 HTTP 断言(高层 API 不暴露 ttlMs/cacheScope)。
        var raw = await RawToolsListAsync(daemon.Port, daemon.Token, connector.SessionKey);
        using var doc = JsonDocument.Parse(raw);
        var payload = doc.RootElement.GetProperty("result");
        Assert.True(payload.TryGetProperty("ttlMs", out var ttl), $"tools/list 必须带 ttlMs;实际: {raw[..Math.Min(400, raw.Length)]}");
        Assert.Equal(0, ttl.GetInt32());
        Assert.True(payload.TryGetProperty("cacheScope", out var scope), "tools/list 必须带 cacheScope");
        Assert.Equal("private", scope.GetString());
    }

    [Fact]
    public async Task unconnected_tools_list_is_exactly_godot_connect_and_ordered_first()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);

        var names = (await connector.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        // 恰好 1 个,不是"包含 1 个" —— 后者在回归时会静默放过。
        Assert.Equal(["godot_connect"], names);
    }

    [Fact]
    public async Task connected_tools_list_puts_godot_connect_first_then_core()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        await using var connector = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await connector.ConnectGodotAsync(project: @"D:\proj\alpha");

        var names = (await connector.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        // 入口工具恒第一。
        Assert.Equal("godot_connect", names[0]);

        // core 9 齐全,且都排在组工具之前(组工具此刻没有,因为没激活任何组)。
        Assert.Equal(CoreTools.OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>用裸 HTTP 取一次 tools/list 的完整 JSON(含 ttlMs/cacheScope 等协议字段)。</summary>
    /// <param name="port">daemon 端口。</param>
    /// <param name="token">令牌。</param>
    /// <param name="sessionKey">会话键。</param>
    /// <returns>SSE 里第一条 data 行的 JSON 文本。</returns>
    private static async Task<string> RawToolsListAsync(int port, string token, string sessionKey)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("x-godot-session", sessionKey);
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"raw","version":"1"}}}}""",
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();

        // SSE:取第一条 data: 行;纯 JSON 则原样返回。
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line[6..].Trim();
            }
        }

        return text.Trim();
    }
}
