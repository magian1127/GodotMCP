using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 会话生命期的端到端断言(ADR-0006):listen 长流必须<b>登记为流</b>,否则
/// reaper 会删掉仍有长流的键;无键流必须被拒绝。
/// </summary>
/// <remarks>
/// <para>这些是代码审核发现的真实缺口,修复后补上的守护测试:</para>
/// <list type="number">
/// <item>修复前 <c>AddStream</c>/<c>RemoveStream</c> 在生产代码里<b>零调用</b> ——
/// <c>Active</c>/<c>Dormant</c> 永不出现,而 <c>Sweep</c> 的跳过条件
/// (<c>InFlight&gt;0 || Streams&gt;0</c>)恒假:一条开着的 listen 长流也会让键在
/// <c>NoStreamTtl</c> 后被静默回收,下一次 <c>tools/list</c> 塌回只有 <c>godot_connect</c>。</item>
/// <item>修复前无 header 的 listen 被直接放行 —— 免配额、不进注册表的无限长流,
/// 违反 spec §1.6「绝不开无键流」。</item>
/// </list>
/// </remarks>
public sealed class SessionLifetimeTests
{
    [Fact]
    public async Task connected_session_survives_past_the_no_stream_ttl()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            // 孤儿键 1 秒过期、已连接键 60 秒过期 —— 这个**差异**就是被测的本质:
            // 同一个 3 秒等待里,孤儿键该消失、已连接键该还在(reaper 是 1s 周期,1s TTL 足够)。
            // (两个都设 2 秒的话,已连接键也过期是正确行为,测不出任何东西。)
            Environment =
            {
                ["GODOT_MCP_SESSION_NO_STREAM_TTL_SECONDS"] = "1",
                ["GODOT_MCP_SESSION_CONNECTED_TTL_SECONDS"] = "60",
            },
        });

        // 对照键:只建键、不 connect(模拟孤儿键)—— 它必须在 2 秒后被回收。
        await using var orphan = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await orphan.Client.ListToolsAsync();  // 建键(首次 tools/list 会 EnsureKey? 不会 —— 见下)

        // 真会话:connect 过。
        await using var session = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await session.ConnectGodotAsync(project: @"D:\proj\alpha");
        Assert.Contains(await session.Client.ListToolsAsync(), t => t.Name == "list_instances");

        // 睡过 2× TTL,期间**不发任何请求**(这正是"用户去看代码"的形态)。
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 已连接的会话必须还在:core 9 一个不少。
        var after = (await session.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("list_instances", after);
        Assert.Contains("discover_tools", after);

        // 反向断言:未连接的键必须已被回收 —— 否则这条测试无法区分"Connected 生效"
        // 与"reaper 根本没工作"(两者都会让上面两条恒绿)。
        var orphanTools = (await orphan.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Equal(["godot_connect"], orphanTools);
    }

    [Fact]
    public async Task listen_without_session_header_is_rejected()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        // 无键 listen 必须 400:否则任何持 token 的本机进程都能开无限长流(不进注册表、不吃配额)。
        var status = await RawListenStatusAsync(daemon.Port, daemon.Token, sessionKey: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task listen_with_invalid_session_header_is_rejected()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        var status = await RawListenStatusAsync(daemon.Port, daemon.Token, sessionKey: "not-a-uuid");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
    }

    /// <summary>发一条 subscriptions/listen,只读状态码(不读体,长流不结束)。</summary>
    /// <param name="port">daemon 端口。</param>
    /// <param name="token">令牌。</param>
    /// <param name="sessionKey">会话键;null 表示故意不带。</param>
    /// <returns>HTTP 状态码。</returns>
    private static async Task<System.Net.HttpStatusCode> RawListenStatusAsync(
        int port,
        string token,
        string? sessionKey)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":"l1","method":"subscriptions/listen","params":{"notifications":{"toolsListChanged":true},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"tools":{"listChanged":true}},"io.modelcontextprotocol/clientInfo":{"name":"raw","version":"1"}}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", "subscriptions/listen");
        if (sessionKey is not null)
        {
            request.Headers.TryAddWithoutValidation("x-godot-session", sessionKey);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode;
    }
}
