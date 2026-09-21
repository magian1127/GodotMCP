using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 15 验收:只读状态面——① 多会话并发变异时可查询到排队/执行中的操作;
/// ② 租约不可查询之处如实标注;③ 只读无副作用、instance 过滤严格、多实例未指定为全局视图。
/// </summary>
public class StateToolsTests
{
    /// <summary>list_operations 返回行的强类型投影(instance/method/status)。</summary>
    private sealed record Operation(string Instance, string Method, string Status);

    /// <summary>多会话并发变异期间,第三个会话经 list_operations 可同时观察到执行中与排队中的操作。</summary>
    /// <para>
    /// Arrange:拉起 daemon,接入 A/B/observer 三个会话;假实例编排两次 editor.refresh ——
    /// 第一次先发 _executing 通知、600ms 后完成,第二次先发 _queued 通知(被串行化)、500ms 后完成,
    /// editor.wait_for_idle 可复用即回。Act:A/B 间隔 80ms 并发调用 editor_sync,observer 轮询读在途表。
    /// Assert:轮询至 operations 同时含 status 为 "executing" 与 "queued" 的行(10s 上限,慢 CI 宽容),
    /// instance 均为 alpha 的规范化项目路径;两次调用完成后在途表清空(空数组)。
    /// </para>
    [Fact]
    public async Task operations_visible_while_queued_and_executing()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var sessionA = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        await using var sessionB = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        await using var observer = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = stateDir,
            Scripts =
            {
                // editor_sync 的第一次 editor.refresh:先 _executing,600ms 后完成。
                new FakeScript
                {
                    Method = "editor.refresh",
                    Frames =
                    {
                        new FakeFrame { Json = NotificationFrame("_executing") },
                        new FakeFrame { AfterMs = 600, Json = ResultFrame("""{"success":true,"ok":true}""") },
                    },
                },
                // 第二次 editor.refresh:先 _queued(被串行化),500ms 后完成。
                new FakeScript
                {
                    Method = "editor.refresh",
                    Frames =
                    {
                        new FakeFrame { Json = NotificationFrame("_queued") },
                        new FakeFrame { AfterMs = 500, Json = ResultFrame("""{"success":true,"ok":true}""") },
                    },
                },
                new FakeScript
                {
                    Method = "editor.wait_for_idle",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"idle":true}""") } },
                },
            },
        });
        await WaitConnectedAsync(observer, @"D:\proj\alpha");

        // 两个会话并发发起 editor_sync(其 editor.refresh 进入执行/排队状态)。
        var callA = sessionA.CallToolAsync("editor_sync", new Dictionary<string, object?>
        {
            ["instance"] = @"D:\proj\alpha",
        });
        await Task.Delay(80);
        var callB = sessionB.CallToolAsync("editor_sync", new Dictionary<string, object?>
        {
            ["instance"] = @"D:\proj\alpha",
        });

        // 轮询观察:等 _executing/_queued 通知真正送达 daemon(单次定时观察在慢 CI 上会偶发落空),
        // 两态同现即停;10s 上限耗尽后以下方断言给出明确失败。
        List<Operation> operations = [];
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
             DateTime.UtcNow < deadline;
             await Task.Delay(100))
        {
            operations = await ReadOperationsAsync(observer);
            if (operations.Any(op => op.Status == "executing") && operations.Any(op => op.Status == "queued"))
            {
                break;
            }
        }
        Assert.Contains(operations, op => op.Status == "executing");
        Assert.Contains(operations, op => op.Status == "queued");
        Assert.All(operations, op => Assert.Equal(JsonMatch.CanonicalProjectKey(@"D:\proj\alpha"), op.Instance));

        await Task.WhenAll(callA.AsTask(), callB.AsTask());
        // 完成后在途表清空。
        await Task.Delay(100);
        Assert.Empty(await ReadOperationsAsync(observer));
    }

    /// <summary>list_operations 只读无副作用、租约(lease)不可查询之处如实标注、instance 过滤严格。</summary>
    /// <para>
    /// Arrange:拉起 daemon,接入 observer 会话,起 alpha/beta 两实例并等待均连接(无在途操作)。
    /// Assert 全局视图:多实例未指定不报 AMBIGUOUS,operations 为空,lease.queryable 为 false
    /// 且 note 含 "not queryable";紧接着的第二次无参查询输出与第一次逐字一致(只读无副作用)。
    /// Assert 过滤:instance 传未命中的 "d:/proj/ghost" 报 INSTANCE_NOT_FOUND 且 instances 清单
    /// 恰 2 项;instance 传命中的 beta 返回非错误(实例视图正常)。
    /// </para>
    [Fact]
    public async Task operations_readonly_with_lease_annotation_and_strict_filter()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var observer = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var alpha = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = @"D:\proj\alpha", StateDir = stateDir });
        using var beta = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = @"D:\proj\beta", StateDir = stateDir });
        await WaitConnectedAsync(observer, @"D:\proj\alpha");
        await WaitConnectedAsync(observer, @"D:\proj\beta");

        // 无在途操作:多实例未指定 → daemon 全局视图(不报 AMBIGUOUS),lease 如实标注。
        var first = await CallOperationsAsync(observer, args: new Dictionary<string, object?>());
        Assert.True(first.IsError is null or false, "全局视图不应因多实例报错");
        using (var payload = JsonDocument.Parse(TextOf(first)))
        {
            Assert.Empty(payload.RootElement.GetProperty("operations").EnumerateArray());
            var lease = payload.RootElement.GetProperty("lease");
            Assert.False(lease.GetProperty("queryable").GetBoolean());
            Assert.Contains("not queryable", lease.GetProperty("note").GetString());
        }

        // 只读无副作用:无活动期间的两次查询输出一致。
        var second = await CallOperationsAsync(observer, args: new Dictionary<string, object?>());
        Assert.Equal(TextOf(first), TextOf(second));

        // instance 过滤:未命中 → INSTANCE_NOT_FOUND(附清单)。
        var missing = await CallOperationsAsync(observer, new Dictionary<string, object?> { ["instance"] = "d:/proj/ghost" });
        Assert.True(missing.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(missing)))
        {
            Assert.Equal("INSTANCE_NOT_FOUND", payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("instances").GetArrayLength());
        }

        // instance 过滤:命中的实例视图正常。
        var scoped = await CallOperationsAsync(observer, new Dictionary<string, object?> { ["instance"] = @"D:\proj\beta" });
        Assert.True(scoped.IsError is null or false);
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>调用 list_operations 的薄封装。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <param name="args">工具实参(空字典表示全局视图)。</param>
    /// <returns>原始工具调用结果。</returns>
    private static async Task<CallToolResult> CallOperationsAsync(McpClient client, Dictionary<string, object?> args)
    {
        return await client.CallToolAsync("list_operations", args);
    }

    /// <summary>取工具响应唯一文本块的内容。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>文本内容(由调用方自行解析)。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    /// <summary>无参调用 list_operations(全局视图)并把 operations 数组解析为 Operation 列表。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <returns>在途操作行(instance/method/status);响应必须是成功结果。</returns>
    private static async Task<List<Operation>> ReadOperationsAsync(McpClient client)
    {
        var result = await CallOperationsAsync(client, new Dictionary<string, object?>());
        Assert.True(result.IsError is null or false);
        using var payload = JsonDocument.Parse(TextOf(result));
        return payload.RootElement.GetProperty("operations").EnumerateArray()
            .Select(op => new Operation(
                op.GetProperty("instance").GetString()!,
                op.GetProperty("method").GetString()!,
                op.GetProperty("status").GetString()!))
            .ToList();
    }

    /// <summary>构造无 id 的 JSON-RPC 通知帧(模拟编辑器的执行状态通知,"$request_id" 占位由假实例在发送前替换)。</summary>
    /// <param name="method">通知方法名(如 "_executing"/"_queued")。</param>
    /// <returns>通知帧字符串。</returns>
    private static string NotificationFrame(string method) =>
        $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{{\"request_id\":\"$request_id\"}}}}";

    /// <summary>构造带 result 的 JSON-RPC 响应帧。</summary>
    /// <param name="resultJson">result 字段的原始 JSON 文本。</param>
    /// <returns>响应帧字符串("$request_id" 占位由假实例在发送前替换)。</returns>
    private static string ResultFrame(string resultJson) =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":" + resultJson + "}";

    /// <summary>轮询 list_instances 直至指定项目出现已连接行;20 秒未连接抛 TimeoutException。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <param name="projectPath">项目路径(内部先规范化再与行 path 比对)。</param>
    private static async Task WaitConnectedAsync(McpClient client, string projectPath)
    {
        var canonical = JsonMatch.CanonicalProjectKey(projectPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
            var text = TextOf(result);
            using var payload = JsonDocument.Parse(text);
            var connected = payload.RootElement.GetProperty("instances").EnumerateArray()
                .Any(i => i.GetProperty("path").GetString() == canonical && i.GetProperty("connected").GetBoolean());
            if (connected)
            {
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"实例 {canonical} 未在 20s 内连接");
    }
}
