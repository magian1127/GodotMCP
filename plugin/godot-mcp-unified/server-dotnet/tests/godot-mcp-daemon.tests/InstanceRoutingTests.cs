using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 06 验收:instance 参数寻址与多实例路由(ADR-0003)——
/// ① 双实例未指定时报错并列出清单;② 项目路径(规范化)与 12 位短 id 均可命中;
/// ③ A/B 交替调用零串台;④ 单实例隐式选中;⑤ 目标不存在时报错并附可用清单。
/// 载体工具:首批迁移的 editor_sync(editor.refresh → editor.wait_for_idle)。
/// </summary>
public class InstanceRoutingTests
{
    /// <summary>list_instances 返回行的强类型投影(仅测试关心的字段)。</summary>
    private sealed record Row(string Path, string Id, int Port, bool Connected);

    /// <summary>单实例时省略 instance 参数应隐式选中并成功。</summary>
    /// <para>
    /// Arrange:经 DaemonProcess 真实拉起 daemon、SessionConnector 接入 SDK 客户端,
    /// 起 alpha 假实例(FakeGodot)并等待其连接。
    /// Act/Assert:无参调用 editor_sync 非错误,success 为 true,refresh 与 idle 两跳
    /// 返回的 project 均为 "alpha"(假实例按脚本回显 marker)。
    /// </para>
    [Fact]
    public async Task single_instance_is_implicitly_selected()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var alpha = StartFake(stateDir, @"D:\proj\alpha", "alpha");
        await WaitConnectedAsync(client, @"D:\proj\alpha");

        var result = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>());
        Assert.True(result.IsError is null or false, "单实例时 editor_sync 应隐式选中并成功");
        using var payload = ParseText(result);
        Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("alpha", payload.RootElement.GetProperty("refresh").GetProperty("project").GetString());
        Assert.Equal("alpha", payload.RootElement.GetProperty("idle").GetProperty("project").GetString());
    }

    /// <summary>多实例且未指定 instance 参数时报 AMBIGUOUS_INSTANCE 并附实例清单。</summary>
    /// <para>
    /// Arrange:拉起 daemon 接入,起 alpha/beta 两个假实例并等待均连接。
    /// Act/Assert:无参调用 editor_sync 必须是错误响应,code 为 "AMBIGUOUS_INSTANCE",
    /// instances 数组恰 2 项且包含 alpha/beta 的规范化项目路径。
    /// </para>
    [Fact]
    public async Task multiple_instances_without_selector_errors_and_lists_them()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var alpha = StartFake(stateDir, @"D:\proj\alpha", "alpha");
        using var beta = StartFake(stateDir, @"D:\proj\beta", "beta");
        await WaitConnectedAsync(client, @"D:\proj\alpha");
        await WaitConnectedAsync(client, @"D:\proj\beta");

        var result = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>());
        Assert.True(result.IsError == true, "多实例未指定时必须是错误响应");
        using var payload = ParseText(result);
        Assert.Equal("AMBIGUOUS_INSTANCE", payload.RootElement.GetProperty("code").GetString());
        var instances = payload.RootElement.GetProperty("instances");
        Assert.Equal(2, instances.GetArrayLength());
        var paths = instances.EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToList();
        Assert.Contains(JsonMatch.CanonicalProjectKey(@"D:\proj\alpha"), paths);
        Assert.Contains(JsonMatch.CanonicalProjectKey(@"D:\proj\beta"), paths);
    }

    /// <summary>instance 参数按规范化项目路径(主标识)与 12 位短 id(别名)均可命中。</summary>
    /// <para>
    /// Arrange:拉起 daemon 接入,起 alpha/beta 假实例并等待均连接,从 list_instances 取 beta 的短 id。
    /// Act/Assert:instance 传 "D:\Proj\ALPHA\"(大写+反斜杠+尾斜杠)应被规范化吃掉并命中 alpha;
    /// instance 传 beta 短 id 的大写形式(大小写不敏感)命中 beta —— 两次调用的
    /// refresh.project 分别为 "alpha"/"beta",均非错误。
    /// </para>
    [Fact]
    public async Task instance_resolves_by_normalized_path_and_short_id()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var alpha = StartFake(stateDir, @"D:\proj\alpha", "alpha");
        using var beta = StartFake(stateDir, @"D:\proj\beta", "beta");
        await WaitConnectedAsync(client, @"D:\proj\alpha");
        await WaitConnectedAsync(client, @"D:\proj\beta");
        var rows = await GetRowsAsync(client);
        var betaId = rows.Single(r => r.Path.EndsWith("/beta", StringComparison.Ordinal)).Id;

        // 主标识:规范化项目路径(反斜杠/大写/尾斜杠都应被吃掉)。
        var byPath = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>
        {
            ["instance"] = @"D:\Proj\ALPHA\",
        });
        Assert.True(byPath.IsError is null or false);
        using (var payload = ParseText(byPath))
        {
            Assert.Equal("alpha", payload.RootElement.GetProperty("refresh").GetProperty("project").GetString());
        }

        // 别名:12 位短 id,大小写不敏感。
        var byId = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>
        {
            ["instance"] = betaId.ToUpperInvariant(),
        });
        Assert.True(byId.IsError is null or false);
        using (var payload = ParseText(byId))
        {
            Assert.Equal("beta", payload.RootElement.GetProperty("refresh").GetProperty("project").GetString());
        }
    }

    /// <summary>A/B 实例交替调用 3 轮零串台:每轮响应都来自被寻址的实例。</summary>
    /// <para>
    /// Arrange:拉起 daemon 接入,起 alpha/beta 假实例并等待均连接。
    /// Act/Assert:循环 3 轮,交替以项目路径寻址调用 editor_sync,每轮断言非错误且
    /// refresh/idle 的 project 与所寻址实例一致(alpha 轮得 "alpha",beta 轮得 "beta")。
    /// </para>
    [Fact]
    public async Task alternating_instances_route_without_crosstalk()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var alpha = StartFake(stateDir, @"D:\proj\alpha", "alpha");
        using var beta = StartFake(stateDir, @"D:\proj\beta", "beta");
        await WaitConnectedAsync(client, @"D:\proj\alpha");
        await WaitConnectedAsync(client, @"D:\proj\beta");

        for (var round = 1; round <= 3; round++)
        {
            var a = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>
            {
                ["instance"] = @"D:\proj\alpha",
            });
            Assert.True(a.IsError is null or false, $"第 {round} 轮 alpha 调用失败");
            using (var payload = ParseText(a))
            {
                Assert.Equal("alpha", payload.RootElement.GetProperty("refresh").GetProperty("project").GetString());
                Assert.Equal("alpha", payload.RootElement.GetProperty("idle").GetProperty("project").GetString());
            }

            var b = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>
            {
                ["instance"] = @"D:\proj\beta",
            });
            Assert.True(b.IsError is null or false, $"第 {round} 轮 beta 调用失败");
            using (var payload = ParseText(b))
            {
                Assert.Equal("beta", payload.RootElement.GetProperty("refresh").GetProperty("project").GetString());
                Assert.Equal("beta", payload.RootElement.GetProperty("idle").GetProperty("project").GetString());
            }
        }
    }

    /// <summary>寻址不存在的实例时报 INSTANCE_NOT_FOUND 并附可用实例清单。</summary>
    /// <para>
    /// Arrange:拉起 daemon 接入,起 alpha/beta 假实例并等待均连接。
    /// Act/Assert:instance 传 "D:/proj/ghost" 调用 editor_sync 为错误,code 为 "INSTANCE_NOT_FOUND",
    /// instances 数组恰 2 项(alpha/beta 组成的可用清单)。
    /// </para>
    [Fact]
    public async Task unknown_instance_errors_with_available_list()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var alpha = StartFake(stateDir, @"D:\proj\alpha", "alpha");
        using var beta = StartFake(stateDir, @"D:\proj\beta", "beta");
        await WaitConnectedAsync(client, @"D:\proj\alpha");
        await WaitConnectedAsync(client, @"D:\proj\beta");

        var result = await client.CallToolAsync("editor_sync", new Dictionary<string, object?>
        {
            ["instance"] = "D:/proj/ghost",
        });
        Assert.True(result.IsError == true);
        using var payload = ParseText(result);
        Assert.Equal("INSTANCE_NOT_FOUND", payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("instances").GetArrayLength());
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>起一个预置 editor.refresh/editor.wait_for_idle 脚本的假实例,脚本以 marker 回显 project。</summary>
    /// <param name="stateDir">注入注册表的状态目录。</param>
    /// <param name="projectPath">项目路径(决定注册表键与条目文件名)。</param>
    /// <param name="marker">回显进响应 project 字段的标记(用于区分实例)。</param>
    /// <returns>已启动的假实例。</returns>
    private static FakeGodotInstance StartFake(string stateDir, string projectPath, string marker)
    {
        return FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = projectPath,
            StateDir = stateDir,
            Scripts =
            {
                new FakeScript { Method = "editor.refresh", Reusable = true, Frames = { new FakeFrame { Json = Frame(marker) } } },
                new FakeScript { Method = "editor.wait_for_idle", Reusable = true, Frames = { new FakeFrame { Json = Frame(marker) } } },
            },
        });
    }

    /// <summary>构造 success:true 且 project 为 marker 的 JSON-RPC 响应帧("$request_id" 占位由假实例在发送前替换)。</summary>
    /// <param name="marker">回显进 project 字段的标记。</param>
    /// <returns>响应帧字符串。</returns>
    private static string Frame(string marker)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"project\":\"" +
               marker + "\"}}";
    }

    /// <summary>取工具响应唯一文本块并解析为 JSON 文档;非法 JSON 时抛出带前 300 字符的诊断异常。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>解析出的 JSON 文档(调用方负责释放)。</returns>
    private static JsonDocument ParseText(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"工具返回文本不是合法 JSON(前 300 字符):{text[..Math.Min(300, text.Length)]}");
        }
    }

    /// <summary>调用 list_instances 并把 instances 数组解析为 Row 列表。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <returns>实例行列表(每行含 path/id/port/connected)。</returns>
    private static async Task<List<Row>> GetRowsAsync(McpClient client)
    {
        var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
        using var payload = ParseText(result);
        return payload.RootElement.GetProperty("instances").EnumerateArray()
            .Select(el => new Row(
                el.GetProperty("path").GetString()!,
                el.GetProperty("id").GetString()!,
                el.GetProperty("port").GetInt32(),
                el.GetProperty("connected").GetBoolean()))
            .ToList();
    }

    /// <summary>轮询 list_instances 直至指定项目出现已连接行;20 秒未连接抛 TimeoutException。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <param name="projectPath">项目路径(内部先规范化再与行 path 比对)。</param>
    private static async Task WaitConnectedAsync(McpClient client, string projectPath)
    {
        var canonical = JsonMatch.CanonicalProjectKey(projectPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await GetRowsAsync(client);
            if (rows.Any(r => r.Path == canonical && r.Connected))
            {
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"实例 {canonical} 未在 20s 内连接");
    }
}
