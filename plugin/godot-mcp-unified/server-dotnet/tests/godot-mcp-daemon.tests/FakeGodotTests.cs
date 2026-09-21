using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 04 验收:fake Godot 替身的四条外部行为——① 02 号语料全量回放(认证/往返/通知),
/// ② 多实例并发拉起时注册表条目互不干扰,③ 可编程脚本(延迟/错误信封/未匹配 -32601),
/// ④ 未认证连接的契约关闭语义(1008 invalid token / auth timeout)。
/// 只断言线上可观察行为,不触碰内部结构。
/// </summary>
public class FakeGodotTests
{
    /// <summary>读帧与等连接关闭的统一超时上限(留足余量,防机器满载误报超时)。</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    /// <summary>语料回放请求 id 的起始基数(原子递增,防跨用例撞 id)。</summary>
    private static int _nextId = 1000;

    // ── ① 语料回放 ──────────────────────────────────────────────

    /// <summary>
    /// 02 号语料全量回放冒烟:加载 wire-fixtures 目录全部夹具(≥10 条),逐条对 fake Godot
    /// 完成"鉴权 → 请求/响应 → 通知"的完整往返,零偏差即通过。
    /// <para>断言链:WireFixture.LoadAll 读入全部夹具并断言数量 ≥10 → 逐条 ReplayFixtureAsync
    /// 回放,记录已回放的夹具 id → 断言三条代表性语料都在列:auth-editor-happy(正确 token
    /// 放行)、mutation-serialized-queued-executing(变更排队/执行进度帧)、
    /// notify-extensions-changed(onConnect 定时推送)。</para>
    /// </summary>
    [Fact]
    public async Task wire_fixture_corpus_replays_green_against_fake_godot()
    {
        var fixtures = WireFixture.LoadAll(TestPaths.WireFixturesDir);
        Assert.True(fixtures.Count >= 10, $"语料应至少 10 条,实际 {fixtures.Count}");
        var replayed = new List<string>();
        foreach (var fx in fixtures)
        {
            using (fx)
            {
                await ReplayFixtureAsync(fx);
                replayed.Add(fx.Id);
            }
        }
        Assert.Contains("auth-editor-happy", replayed);
        Assert.Contains("mutation-serialized-queued-executing", replayed);
        Assert.Contains("notify-extensions-changed", replayed);
    }

    /// <summary>
    /// 单条语料的完整回放:拉起替身并完成鉴权握手,再按 steps 顺序驱动 call / concurrentCalls
    /// / 客户端取消场景,最后补读并断言 expectNotifications。
    /// <para>断言链:发首帧 {auth, version}(C2)→ auth 含 close 时等 Closed 并断言关闭码与
    /// 原因后返回;否则读 ack 帧按 DeepMatch 断言 → 逐步遍历 steps(sleepMs 跳过;CANCELLED
    /// 步骤改发 _cancel 并验证连接仍健康)逐条经 AssertResponseAsync 按 expect 断言 →
    /// expectNotifications 非空时在 20s 宽限窗口内补读广播帧,断言条数一致,并把 notification
    /// + params 组装成 {type, params} 形状后逐条 DeepMatch。</para>
    /// </summary>
    /// <param name="fx">待回放的语料夹具(wire-fixture/1 schema 已由加载器校验)。</param>
    private static async Task ReplayFixtureAsync(WireFixture fx)
    {
        using var fake = FakeGodotInstance.Start(BuildOptions(fx));
        using var client = await WsTestClient.ConnectAsync(fake.Port);

        // 首帧 {auth, version}(C2)。
        await client.SendTextAsync($"{{\"auth\":\"{fake.Token}\",\"version\":\"1.0.0\"}}");

        var auth = fx.Auth;
        if (auth.ValueKind == JsonValueKind.Object && auth.TryGetProperty("close", out var closeSpec))
        {
            // 错误 token 场景:伪造服务器按 fixture 关闭,客户端应观察到对应关闭帧。
            await client.Closed.WaitAsync(FrameTimeout);
            Assert.Equal(closeSpec.GetProperty("code").GetInt32(), client.CloseStatus);
            Assert.Equal(closeSpec.GetProperty("reason").GetString(), client.CloseReason);
            return;
        }

        string ackRaw;
        try
        {
            ackRaw = await client.ReceiveFrameAsync(FrameTimeout);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"[{fx.Id}] 等待鉴权 ack 超时");
        }
        using (var ack = JsonDocument.Parse(ackRaw))
        {
            JsonMatch.DeepMatch(ack.RootElement, auth.GetProperty("ack"), $"[{fx.Id}].authAck");
        }

        var notifications = new List<JsonElement>();
        var lateResponses = new Dictionary<string, JsonElement>();
        foreach (var step in fx.Steps.EnumerateArray())
        {
            if (step.TryGetProperty("sleepMs", out _))
            {
                continue;
            }
            if (step.TryGetProperty("call", out var call))
            {
                var id = NextId();
                await SendRequestAsync(client, 0, id, call);
                if (IsCancelStep(step))
                {
                    // 客户端取消场景:服务器按契约永不响应;回放"客户端随后发出 _cancel"并验证
                    // 连接仍健康(CANCELLED 的客户端侧语义由 Node harness 断言)。
                    await Task.Delay(100);
                    await client.SendTextAsync(
                        $"{{\"jsonrpc\":\"2.0\",\"method\":\"_cancel\",\"params\":{{\"request_id\":\"{id}\"}}}}");
                    await Task.Delay(50);
                    continue;
                }
                await AssertResponseAsync(fx.Id, client, id, step, notifications, lateResponses);
                continue;
            }
            if (step.TryGetProperty("concurrentCalls", out var concurrent))
            {
                var entries = concurrent.EnumerateArray().ToArray();
                var ids = new List<string>();
                var sends = new List<Task>();
                foreach (var entry in entries)
                {
                    var afterMs = entry.TryGetProperty("afterMs", out var am) ? am.GetInt32() : 0;
                    var id = NextId();
                    ids.Add(id);
                    sends.Add(SendRequestAsync(client, afterMs, id, entry.GetProperty("call")));
                }
                await Task.WhenAll(sends);
                for (var i = 0; i < entries.Length; i++)
                {
                    await AssertResponseAsync(fx.Id, client, ids[i], entries[i], notifications, lateResponses);
                }
            }
        }

        if (fx.ExpectNotifications is { } expected)
        {
            // 补读:onConnect 定时推送可能落在最后一次响应之后(steps 的 sleepMs 已给足到达时间;
            // 与生产 daemon/编辑器共存时机器满载,给足 20s 宽限)。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (notifications.Count < expected.GetArrayLength() && DateTime.UtcNow < deadline)
            {
                string raw;
                try
                {
                    raw = await client.ReceiveFrameAsync(TimeSpan.FromMilliseconds(300));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("notification", out _))
                {
                    notifications.Add(doc.RootElement.Clone());
                }
                doc.Dispose();
            }
            Assert.Equal(expected.GetArrayLength(), notifications.Count);
            for (var i = 0; i < expected.GetArrayLength(); i++)
            {
                var frame = notifications[i];
                var paramsRaw = frame.TryGetProperty("params", out var p) ? p.GetRawText() : "null";
                using var actual = JsonDocument.Parse(
                    $"{{\"type\":{JsonSerializer.Serialize(frame.GetProperty("notification").GetString())},\"params\":{paramsRaw}}}");
                JsonMatch.DeepMatch(actual.RootElement, expected[i], $"[{fx.Id}].notifications[{i}]");
            }
        }
    }

    /// <summary>
    /// 把语料夹具翻译成替身启动选项:auth.ack / auth.close 分别映射为鉴权应答与关单语义,
    /// steps 的 call / concurrentCalls 展开为按方法脚本,onConnect 展开为鉴权后定时推送。
    /// </summary>
    /// <param name="fx">源语料夹具。</param>
    /// <returns>对应该夹具的替身配置(项目路径形如 D:/proj/wire-{id})。</returns>
    private static FakeGodotOptions BuildOptions(WireFixture fx)
    {
        var auth = fx.Auth;
        string? ackJson = null;
        string? closeJson = null;
        if (auth.ValueKind == JsonValueKind.Object)
        {
            if (auth.TryGetProperty("close", out var close))
            {
                closeJson = close.GetRawText();
            }
            else if (auth.TryGetProperty("ack", out var ack))
            {
                ackJson = ack.GetRawText();
            }
        }

        var scripts = new List<FakeScript>();
        foreach (var step in fx.Steps.EnumerateArray())
        {
            if (step.TryGetProperty("call", out var call))
            {
                scripts.Add(ScriptFrom(call, step));
            }
            else if (step.TryGetProperty("concurrentCalls", out var concurrent))
            {
                foreach (var entry in concurrent.EnumerateArray())
                {
                    scripts.Add(ScriptFrom(entry.GetProperty("call"), entry));
                }
            }
        }

        var onConnect = new List<FakePush>();
        if (fx.OnConnect is { } pushes)
        {
            foreach (var push in pushes.EnumerateArray())
            {
                onConnect.Add(new FakePush
                {
                    AfterMs = push.TryGetProperty("afterMs", out var am) ? am.GetInt32() : 0,
                    Json = push.GetProperty("send").GetRawText(),
                });
            }
        }

        return new FakeGodotOptions
        {
            ProjectPath = $"D:/proj/wire-{fx.Id}",
            AuthAckJson = ackJson,
            CloseOnAuthJson = closeJson,
            Scripts = scripts,
            OnConnect = onConnect,
        };
    }

    /// <summary>从 call 步骤构造按方法脚本:script 数组逐帧映射为 AfterMs + 原始 JSON 的回放帧。</summary>
    /// <param name="call">步骤内的 call 对象(取 method 作脚本键)。</param>
    /// <param name="container">承载该 call 的步骤或并发条目(其 script 数组为帧来源)。</param>
    /// <returns>匹配到该方法时按序回放的替身脚本。</returns>
    private static FakeScript ScriptFrom(JsonElement call, JsonElement container)
    {
        var frames = new List<FakeFrame>();
        if (container.TryGetProperty("script", out var script))
        {
            foreach (var frame in script.EnumerateArray())
            {
                frames.Add(new FakeFrame
                {
                    AfterMs = frame.TryGetProperty("afterMs", out var am) ? am.GetInt32() : 0,
                    Json = frame.GetProperty("send").GetRawText(),
                });
            }
        }
        return new FakeScript { Method = call.GetProperty("method").GetString()!, Frames = frames };
    }

    /// <summary>生成全局唯一请求 id(原子递增,形如 wire-1001)。</summary>
    /// <returns>形如 wire-{n} 的请求 id。</returns>
    private static string NextId() => $"wire-{Interlocked.Increment(ref _nextId)}";

    /// <summary>判断步骤是否为客户端取消场景(expect.rejects.code == "CANCELLED")。</summary>
    /// <param name="stepLike">步骤或并发条目节点。</param>
    /// <returns>true 表示该步骤按契约永不响应,回放时需补发 _cancel。</returns>
    private static bool IsCancelStep(JsonElement stepLike) =>
        stepLike.TryGetProperty("expect", out var expect)
        && expect.TryGetProperty("rejects", out var rejects)
        && rejects.TryGetProperty("code", out var code)
        && code.GetString() == "CANCELLED";

    /// <summary>按 JSON-RPC 信封发送一条请求(afterMs 先延迟,供并发条目错峰到达)。</summary>
    /// <param name="client">已鉴权的测试 WS 客户端。</param>
    /// <param name="afterMs">发送前的延迟毫秒数(0 立即发)。</param>
    /// <param name="id">请求 id(替身侧 $request_id 占位符以它替换)。</param>
    /// <param name="call">语料中的 call 对象(method + params)。</param>
    private static async Task SendRequestAsync(WsTestClient client, int afterMs, string id, JsonElement call)
    {
        if (afterMs > 0)
        {
            await Task.Delay(afterMs);
        }
        var method = call.GetProperty("method").GetString();
        var paramsRaw = call.TryGetProperty("params", out var p) ? p.GetRawText() : "null";
        await client.SendTextAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"{method}\",\"params\":{paramsRaw}}}");
    }

    /// <summary>
    /// 读取直到本 id 的响应帧(记录广播通知、跳过 _queued/_executing 进度帧),并按 expect 断言。
    /// 并发场景下其它 id 的响应可能先到:存入 lateResponses 供其断言时取用,绝不丢弃。
    /// </summary>
    private static async Task AssertResponseAsync(
        string fxId,
        WsTestClient client,
        string id,
        JsonElement stepLike,
        List<JsonElement> notifications,
        Dictionary<string, JsonElement> lateResponses)
    {
        JsonDocument? response = null;
        if (lateResponses.Remove(id, out var buffered))
        {
            response = JsonDocument.Parse(buffered.GetRawText());
        }
        while (response is null)
        {
            string raw;
            try
            {
                raw = await client.ReceiveFrameAsync(FrameTimeout);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"[{fxId}] 等待 id={id} 的响应帧超时({FrameTimeout.TotalSeconds}s)");
            }
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String
                && idEl.GetString() == id)
            {
                response = doc;
                break;
            }
            if (doc.RootElement.TryGetProperty("notification", out _))
            {
                notifications.Add(doc.RootElement.Clone());
            }
            else if (doc.RootElement.TryGetProperty("id", out var otherId)
                && otherId.ValueKind == JsonValueKind.String)
            {
                // 其它请求的响应先到——缓存待其断言者取用。
                lateResponses[otherId.GetString()!] = doc.RootElement.Clone();
            }
            // 其余(_queued/_executing 进度帧)按契约跳过。
            doc.Dispose();
        }

        using (response)
        {
            if (!stepLike.TryGetProperty("expect", out var expect))
            {
                return;
            }
            if (expect.TryGetProperty("resolves", out var resolves))
            {
                Assert.True(
                    response.RootElement.TryGetProperty("result", out var result),
                    $"[{fxId}] 期望 result,实际 {response.RootElement.GetRawText()}");
                JsonMatch.DeepMatch(result, resolves, $"[{fxId}].result");
                return;
            }
            if (expect.TryGetProperty("rejects", out var rejects))
            {
                var code = rejects.GetProperty("code").GetString();
                if (code != "RPC_ERROR")
                {
                    throw new InvalidOperationException($"原始回放不覆盖 reject 码 {code}(属 daemon 通道层场景)");
                }
                Assert.True(
                    response.RootElement.TryGetProperty("error", out var error),
                    $"[{fxId}] 期望 error 帧,实际 {response.RootElement.GetRawText()}");
                // messageIncludes 形如 "-32601: Method not found: …" —— 线路侧 error.message 为冒号后半段。
                var includes = rejects.GetProperty("messageIncludes").GetString()!;
                var separator = includes.IndexOf(": ", StringComparison.Ordinal);
                Assert.Equal(-32601, error.GetProperty("code").GetInt32());
                Assert.Equal(includes[(separator + 2)..], error.GetProperty("message").GetString());
            }
        }
    }

    // ── ② 多实例注册表隔离 ──────────────────────────────────────

    /// <summary>
    /// 多实例注册表隔离:三个大小写/斜杠风格各异的项目路径同时拉起替身,注册表条目互不干扰,
    /// 落盘键可与独立重算的 canonical 键(反斜杠→正斜杠、去尾斜杠、小写)+ sha256 前 12 位对上。
    /// <para>断言链:独立重算每条路径的哈希键 → 断言 entries/{hash}.json 与
    /// project_instance_{hash}/mcp_token 均落盘 → projects.json 的 by_path 恰 3 条 → 逐实例
    /// 断言 port/token_path/godot_version("4.5")/runtime_port(null)/lsp_host("127.0.0.1")
    /// → 每实例各完成一次鉴权往返,ack.authed 为 true(M 实例同时在线互不影响)。</para>
    /// </summary>
    [Fact]
    public async Task registry_entries_for_multiple_instances_are_isolated()
    {
        var stateDir = TestPaths.NewStateDir();
        var paths = new[] { @"D:\Proj\Alpha", @"D:\proj\beta/", "D:/Proj/GAMMA" };
        var fakes = paths
            .Select(p => FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = p, StateDir = stateDir }))
            .ToList();
        try
        {
            foreach (var path in paths)
            {
                // 独立重算:canonical(反斜杠→正斜杠、去尾斜杠、小写)+ sha256 前 12 位。
                var key = path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
                    .ToLowerInvariant()[..12];
                Assert.True(
                    File.Exists(Path.Combine(stateDir, "entries", hash + ".json")),
                    $"缺条目文件 entries/{hash}.json");
                Assert.True(
                    File.Exists(Path.Combine(stateDir, $"project_instance_{hash}", "mcp_token")),
                    $"缺 token 文件 project_instance_{hash}/mcp_token");
            }

            using var projects = JsonDocument.Parse(File.ReadAllText(Path.Combine(stateDir, "projects.json")));
            var byPath = projects.RootElement.GetProperty("by_path");
            Assert.Equal(3, byPath.EnumerateObject().Count());

            foreach (var fake in fakes)
            {
                var row = byPath.GetProperty(fake.ProjectKey);
                Assert.Equal(fake.Port, row.GetProperty("port").GetInt32());
                Assert.Equal(fake.TokenFilePath, row.GetProperty("token_path").GetString());
                Assert.Equal("4.5", row.GetProperty("godot_version").GetString());
                Assert.Equal(JsonValueKind.Null, row.GetProperty("runtime_port").ValueKind);
                Assert.Equal("127.0.0.1", row.GetProperty("lsp_host").GetString());

                // M 实例同时在线:各自完成一次鉴权 + ack 往返。
                using var client = await WsTestClient.ConnectAsync(fake.Port);
                await client.SendTextAsync($"{{\"auth\":\"{fake.Token}\",\"version\":\"1.0.0\"}}");
                using var ack = JsonDocument.Parse(await client.ReceiveFrameAsync(FrameTimeout));
                Assert.True(ack.RootElement.GetProperty("authed").GetBoolean());
            }
        }
        finally
        {
            foreach (var fake in fakes)
            {
                fake.Dispose();
            }
        }
    }

    // ── ③ 可编程脚本 ────────────────────────────────────────────

    /// <summary>
    /// 可编程脚本三合一:延迟帧按 AfterMs 真实等待、错误信封按业务码原样回传、未匹配方法
    /// 回 JSON-RPC -32601。
    /// <para>断言链:注册 demo.slow(200ms 延迟)与 demo.err(FAILED 业务信封)脚本并完成
    /// 鉴权 → 发 demo.slow,断言耗时 ≥180ms 且 result.ok 为 true → 发 demo.err,断言
    /// result.code 为 "FAILED" → 发未注册的 no.such.verb,断言 error.code 为 -32601 且
    /// message 逐字为 "Method not found: no.such.verb"。</para>
    /// </summary>
    [Fact]
    public async Task programmable_script_delays_errors_and_unmatched_methods()
    {
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = "D:/proj/scripted",
            Scripts =
            {
                new FakeScript
                {
                    Method = "demo.slow",
                    Frames =
                    {
                        new FakeFrame { AfterMs = 200, Json = """{"jsonrpc":"2.0","id":"$request_id","result":{"ok":true}}""" },
                    },
                },
                new FakeScript
                {
                    Method = "demo.err",
                    Frames =
                    {
                        new FakeFrame { Json = """{"jsonrpc":"2.0","id":"$request_id","result":{"success":false,"error":"boom","code":"FAILED"}}""" },
                    },
                },
            },
        });
        using var client = await WsTestClient.ConnectAsync(fake.Port);
        await client.SendTextAsync($"{{\"auth\":\"{fake.Token}\",\"version\":\"1.0.0\"}}");
        await client.ReceiveFrameAsync(FrameTimeout); // ack

        var sw = Stopwatch.StartNew();
        await client.SendTextAsync("""{"jsonrpc":"2.0","id":"p1","method":"demo.slow","params":{}}""");
        using (var r1 = JsonDocument.Parse(await client.ReceiveFrameAsync(FrameTimeout)))
        {
            Assert.True(sw.ElapsedMilliseconds >= 180, "200ms 延迟脚本应生效");
            Assert.True(r1.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        }

        await client.SendTextAsync("""{"jsonrpc":"2.0","id":"p2","method":"demo.err","params":{}}""");
        using (var r2 = JsonDocument.Parse(await client.ReceiveFrameAsync(FrameTimeout)))
        {
            Assert.Equal("FAILED", r2.RootElement.GetProperty("result").GetProperty("code").GetString());
        }

        await client.SendTextAsync("""{"jsonrpc":"2.0","id":"p3","method":"no.such.verb","params":{}}""");
        using (var r3 = JsonDocument.Parse(await client.ReceiveFrameAsync(FrameTimeout)))
        {
            Assert.Equal(-32601, r3.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("Method not found: no.such.verb", r3.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
    }

    // ── ④ 未认证连接的契约关闭语义 ──────────────────────────────

    /// <summary>
    /// 未认证连接的契约关闭语义:错误 token 立即按 1008 "invalid token" 关闭;静默对端在
    /// 鉴权超时后按 1008 "auth timeout" 关闭(契约值 2s,经 AuthTimeoutMs 缩短为 300ms)。
    /// <para>断言链:场景一发错误 token,等 Closed 后断言 CloseStatus 为 1008、CloseReason
    /// 为 "invalid token" → 场景二连接后不发任何帧,等 Closed 后断言 1008 与
    /// "auth timeout"。</para>
    /// </summary>
    [Fact]
    public async Task unauthenticated_peers_get_contract_close_semantics()
    {
        // 错误 token → WS close 1008 "invalid token"。
        using (var fake = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = "D:/proj/auth-bad" }))
        using (var client = await WsTestClient.ConnectAsync(fake.Port))
        {
            await client.SendTextAsync("""{"auth":"wrong-token","version":"1.0.0"}""");
            await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1008, client.CloseStatus);
            Assert.Equal("invalid token", client.CloseReason);
        }

        // 静默对端(无任何帧)→ 鉴权超时后 WS close 1008 "auth timeout"(契约值 2s,此处缩短)。
        using (var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = "D:/proj/auth-silent",
            AuthTimeoutMs = 300,
        }))
        using (var client = await WsTestClient.ConnectAsync(fake.Port))
        {
            await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1008, client.CloseStatus);
            Assert.Equal("auth timeout", client.CloseReason);
        }
    }
}
