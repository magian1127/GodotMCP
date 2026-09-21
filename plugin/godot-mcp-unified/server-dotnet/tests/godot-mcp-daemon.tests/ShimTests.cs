using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 08 验收:shim 自举器——① 缺席拉起且首个调用成功;② 已有实例时不重复拉起;
/// ③ stdio↔HTTP 转发对调用与通知透明;④ daemon 重启后恢复转发(不静默挂死);
/// ⑤ stdio 客户端(SDK,等价 mini client)经 shim 完成完整工具调用。
/// </summary>
public class ShimTests
{
    /// <summary>stdio 侧发出的 initialize 请求帧(钉协议版本 2025-11-25)。</summary>
    // 握手层最新可用版本为 2025-11-25(SDK 2.2.0 的 initialize 协商面;
    // 2026-07-28 修订为 SDK 原生无状态核心,不经经典握手)。
    private const string InitializeMessage =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"shim-test","version":"0.1.0"}}}""";

    /// <summary>initialize 之后的 notifications/initialized 通知帧(无 id,不应产生任何响应)。</summary>
    private const string InitializedNotification =
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    /// <summary>daemon 缺席时 shim 冷启动自举:先拉起 daemon,首条调用仍成功处理。</summary>
    /// <para>
    /// Arrange:全新状态目录 + 随机端口,直接以 ShimProcess 起 shim(无 daemon 在跑)。
    /// Act/Assert:发 initialize,响应 result.serverInfo.name 为 "godot-mcp-unified";
    /// 随后 tools/call list_instances 的响应 id 回配为 2,文本含 "instances";
    /// 稍后 stderr 快照含 "已拉起 daemon"(确认确为 shim 拉起而非复用)。
    /// </para>
    [Fact]
    public async Task shim_spawns_daemon_when_absent_and_first_call_succeeds()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();
        using var shim = ShimProcess.Start(new ShimSpawnOptions { Port = port, StateDir = stateDir, IdleSeconds = 30 });

        // 冷启动:shim 先拉起 daemon 再处理首条消息(30s 就绪上限之内)。
        var initializeResponse = await shim.SendAsync(InitializeMessage, TimeSpan.FromSeconds(60));
        using (var init = JsonDocument.Parse(initializeResponse))
        {
            Assert.Equal(
                "godot-mcp-unified",
                init.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        }

        var call = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(30));
        using (var callDoc = JsonDocument.Parse(call))
        {
            Assert.Equal(2, callDoc.RootElement.GetProperty("id").GetInt32());
            var text = callDoc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Contains("\"instances\"", text);
        }

        await Task.Delay(200);
        Assert.Contains("已拉起 daemon", shim.StderrSnapshot());
    }

    /// <summary>已有 daemon 在跑时 shim 复用而不重复拉起。</summary>
    /// <para>
    /// Arrange:先在指定端口拉起 daemon,再对同一端口/状态目录起 shim。
    /// Act/Assert:经 shim 调用 list_instances,响应 id 回配为 3;stderr 快照不含
    /// "已拉起 daemon",且既有 daemon 全程未退出 —— 未被 shim 干扰。
    /// </para>
    [Fact]
    public async Task shim_reuses_running_daemon_without_spawning_second()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, Port = port, IdleSeconds = 60 });
        using var shim = ShimProcess.Start(new ShimSpawnOptions { Port = port, StateDir = stateDir, IdleSeconds = 30 });

        var call = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(30));
        using (var callDoc = JsonDocument.Parse(call))
        {
            Assert.Equal(3, callDoc.RootElement.GetProperty("id").GetInt32());
        }

        await Task.Delay(300);
        Assert.DoesNotContain("已拉起 daemon", shim.StderrSnapshot());
        Assert.False(daemon.HasExited, "既有 daemon 不应被 shim 影响");
    }

    /// <summary>stdio↔HTTP 转发对调用与通知透明:通知不产生响应,id 按序正确回配。</summary>
    /// <para>
    /// Arrange:冷启动 shim,发 initialize 并断言响应 id 回配为 1。
    /// Act/Assert:写入 initialized 通知后紧读到的应是 tools/list 的响应(id=4),工具表含
    /// list_instances 与 editor_sync;再连续调用两次(id=5/6),响应 id 逐一对应 —— 顺序保持、双向透明。
    /// </para>
    [Fact]
    public async Task stdio_http_forwarding_is_transparent_for_calls_and_notifications()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();
        using var shim = ShimProcess.Start(new ShimSpawnOptions { Port = port, StateDir = stateDir, IdleSeconds = 30 });

        using (var init = JsonDocument.Parse(await shim.SendAsync(InitializeMessage, TimeSpan.FromSeconds(60))))
        {
            Assert.Equal(1, init.RootElement.GetProperty("id").GetInt32());
        }

        // 通知无响应:紧随其后的读应是 tools/list 的响应(id=4)。
        await shim.WriteLineAsync(InitializedNotification);
        var list = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{}}""",
            TimeSpan.FromSeconds(30));
        using (var listDoc = JsonDocument.Parse(list))
        {
            Assert.Equal(4, listDoc.RootElement.GetProperty("id").GetInt32());
            var names = listDoc.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Contains("list_instances", names);
            Assert.Contains("editor_sync", names);
        }

        // 连续两次调用,id 正确回配(双向透明的顺序保持)。
        foreach (var id in new[] { 5, 6 })
        {
            var response = await shim.SendAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"list_instances\",\"arguments\":{{}}}}}}",
                TimeSpan.FromSeconds(30));
            using var doc = JsonDocument.Parse(response);
            Assert.Equal(id, doc.RootElement.GetProperty("id").GetInt32());
        }
    }

    /// <summary>daemon 突然死亡后 shim 恢复转发,而不是让 stdio 侧 host 静默挂死。</summary>
    /// <para>
    /// Arrange:指定端口拉起 daemon 并起 shim,先完成一次调用(响应 id 回配为 7)。
    /// Act:强制终止 daemon,再发一次 tools/call(id=8)。
    /// Assert:响应 id 回配为 8 且含 result(转发已恢复);stderr 快照含 "已拉起 daemon"
    /// —— shim 对下一跳重试并经同状态目录重新自举。
    /// </para>
    [Fact]
    public async Task shim_recovers_forwarding_after_daemon_restart()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();
        var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, Port = port, IdleSeconds = 60 });
        using var shim = ShimProcess.Start(new ShimSpawnOptions { Port = port, StateDir = stateDir, IdleSeconds = 30 });

        var first = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(30));
        using (var firstDoc = JsonDocument.Parse(first))
        {
            Assert.Equal(7, firstDoc.RootElement.GetProperty("id").GetInt32());
        }

        // daemon 突然死亡:shim 的下一跳应重试并重新自举(同状态目录),而不是让 host 挂死。
        daemon.Dispose();
        var second = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(60));
        using (var secondDoc = JsonDocument.Parse(second))
        {
            Assert.Equal(8, secondDoc.RootElement.GetProperty("id").GetInt32());
            Assert.True(secondDoc.RootElement.TryGetProperty("result", out _), "daemon 重启后转发应恢复");
        }
        Assert.Contains("已拉起 daemon", shim.StderrSnapshot());
    }

    /// <summary>官方 SDK 的 stdio 客户端经 shim 完成完整工具调用(等价 mini client 的端到端面)。</summary>
    /// <para>
    /// Arrange:StdioClientTransport 以 dotnet exec 拉起 shim dll,环境变量注入端口/状态目录/
    /// daemon dll/空闲阈值,创建 McpClient 完成完整 MCP 握手。
    /// Act/Assert:tools/list 含 list_instances;调用 list_instances 非错误,唯一文本块
    /// 解析后含 instances 属性。
    /// </para>
    [Fact]
    public async Task stdio_client_completes_full_tool_call_through_shim()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();

        // mini client 等价:官方 SDK 的 stdio 客户端直连 shim(完整 MCP 握手 → 工具面 → 调用)。
        // 传输归 McpClient 拥有(随客户端释放关闭),此处不单独包裹 using。
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = ["exec", TestPaths.ShimDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["GODOT_MCP_DAEMON_PORT"] = port.ToString(),
                ["GODOT_MCP_DAEMON_STATE_DIR"] = stateDir,
                ["GODOT_MCP_DAEMON_EXE"] = TestPaths.DaemonDll,
                ["GODOT_MCP_DAEMON_IDLE_SECONDS"] = "30",
            },
        });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "list_instances");

        var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
        Assert.True(result.IsError is null or false);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var payload = JsonDocument.Parse(text);
        Assert.True(payload.RootElement.TryGetProperty("instances", out _));
    }
}
