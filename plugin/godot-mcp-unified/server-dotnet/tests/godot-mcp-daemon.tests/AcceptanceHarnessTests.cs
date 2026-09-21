using System.Collections.Concurrent;
using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 16 验收:M×N 端到端验收 harness(一条命令跑全量,可作回归入口——
/// <c>dotnet test tests/godot-mcp-daemon.tests</c>;仅跑本 harness:
/// <c>dotnet test --filter FullyQualifiedName~AcceptanceHarnessTests</c>)。
///
/// 覆盖:① 3 实例 × 3 并发 MCP 客户端 × 交错变更/读取的矩阵——每实例变更严格串行
/// (替身按 addon MutationLane 语义串行化并记录窗口)、寻址零串台、排队事件(_queued)真实发生;
/// ② 在途租约可见(list_operations 按实例呈现 executing/queued,完成后清空);
/// ③ 生命周期四场景:addon/shim 双路拉起竞态(单例必然)、单例防双开、空闲自退、
/// daemon 崩溃经 shim 自愈。失败输出带 (client, round, instance, tool) 定位信息。
/// </summary>
public class AcceptanceHarnessTests
{
    // ── ① 3×3 矩阵:交错变更串行 + 零串台 ─────────────────────────

    /// <summary>
    /// 3 实例 × 3 MCP 客户端 × 3 轮交错矩阵:变更与读取同连接交错下发,替身把变更方法
    /// scene.create_node 纳入 MutationLane 式串行车道(250ms 执行窗口),一次验收串行、
    /// 零串台、排队事件三重语义。
    /// <para>断言链:经 DaemonProcess 真实拉起 daemon,3 个 SessionConnector 客户端连入,
    /// 3 个串行化替身按各自项目路径注册并等 connected → 轮次编排(r0 各打各的、r1 三客户端
    /// 并发变更 mx0、r2 环形错位),每客户端同轮同时发 scene_create_node + scene_get_tree →
    /// 逐调用断言非错误,且替身回显 params_seen 逐字等于发出的 node_name/parent_path(参数级
    /// 零串台)→ 事后按实例核对:按开始序排列的变更窗口 StartedUtc 严格不重叠(无交错半写)、
    /// 收到的变更集合与编排重算结果完全一致(参数含本实例父路径、不含他实例)、读取计数一致、
    /// _executing 次数等于变更次数、mx0 存在 ≥2 次 _queued 而其余实例为 0。</para>
    /// </summary>
    [Fact]
    public async Task matrix_3x3_interleaved_mutations_serialized_and_zero_crosstalk()
    {
        const int instanceCount = 3;
        const int clientCount = 3;
        const int rounds = 3;

        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 180 });
        var clients = new McpClient[clientCount];
        var fakes = new FakeGodotInstance[instanceCount];
        var projects = Enumerable.Range(0, instanceCount).Select(j => $@"D:\proj\mx{j}").ToArray();
        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                clients[i] = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
            }
            for (var j = 0; j < instanceCount; j++)
            {
                fakes[j] = FakeGodotInstance.Start(new FakeGodotOptions
                {
                    ProjectPath = projects[j],
                    StateDir = stateDir,
                    // 变更串行化(镜像 addon MutationLane):变更方法 250ms 执行窗口,便于排队事件真实发生。
                    SerializedMutationMethods = ["scene.create_node"],
                    Scripts =
                    {
                        new FakeScript
                        {
                            Method = "scene.create_node",
                            Reusable = true,
                            Frames = { new FakeFrame { AfterMs = 250, Json = EchoFrame() } },
                        },
                        new FakeScript { Method = "scene.get_tree", Reusable = true, Frames = { new FakeFrame { Json = EchoFrame() } } },
                    },
                });
            }
            for (var j = 0; j < instanceCount; j++)
            {
                await WaitConnectedAsync(clients[0], projects[j]);
            }

            // 轮次编排:r0 各打各的;r1 三客户端并发变更同一实例(强制排队);r2 环形错位。
            // 各客户端在轮内同时发变更 + 读取(同连接交错)。
            for (var round = 0; round < rounds; round++)
            {
                var waves = new List<Task>();
                for (var clientIndex = 0; clientIndex < clientCount; clientIndex++)
                {
                    var i = clientIndex;
                    var r = round;
                    var target = r switch
                    {
                        0 => i,               // 各打各的
                        1 => 0,               // 三路并发变更同一实例
                        _ => (i + 1) % 3,     // 环形错位
                    };
                    waves.Add(Task.Run(async () =>
                    {
                        var nodeName = $"n_c{i}r{r}";
                        var parentPath = $"/root/Main/acc_m{target}";
                        var create = clients[i].CallToolAsync("scene_create_node", new Dictionary<string, object?>
                        {
                            ["class_name"] = "Node",
                            ["parent_path"] = parentPath,
                            ["node_name"] = nodeName,
                            ["instance"] = projects[target],
                        });
                        var read = clients[i].CallToolAsync("scene_get_tree", new Dictionary<string, object?>
                        {
                            ["max_depth"] = 1,
                            ["instance"] = projects[target],
                        });
                        await Task.WhenAll(create.AsTask(), read.AsTask());
                        var createResult = await create;
                        var readResult = await read;
                        var where = $"(client={i}, round={r}, target=mx{target})";
                        Assert.True(createResult.IsError is null or false, $"{where} scene_create_node 失败:{TextOf(createResult)}");
                        Assert.True(readResult.IsError is null or false, $"{where} scene_get_tree 失败:{TextOf(readResult)}");
                        // 回显参数逐字等于发出值(参数级零串台)。
                        using var payload = JsonDocument.Parse(TextOf(createResult));
                        var seen = payload.RootElement.GetProperty("params_seen");
                        Assert.Equal(nodeName, seen.GetProperty("node_name").GetString());
                        Assert.Equal(parentPath, seen.GetProperty("parent_path").GetString());
                    }));
                }
                await Task.WhenAll(waves);
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }
            }
            foreach (var fake in fakes)
            {
                fake?.Dispose();
            }
        }

        // 期望编排(3 会话 × 3 轮,target 规则:round0 打本位 i、round1 全打 mx0、round2 打下一位):
        // mx0 收 5 次变更(自打 1 + 被打 3 + 下位轮 1)、mx1/mx2 各收 2 次;读取数与变更数同目标。
        var expectedMutations = new Dictionary<int, HashSet<string>>();
        var expectedReads = new Dictionary<int, int>();
        for (var j = 0; j < instanceCount; j++)
        {
            expectedMutations[j] = new HashSet<string>();
            expectedReads[j] = 0;
        }
        for (var round = 0; round < rounds; round++)
        {
            for (var i = 0; i < clientCount; i++)
            {
                var target = round switch { 0 => i, 1 => 0, _ => (i + 1) % 3 };
                expectedMutations[target].Add($"n_c{i}r{round}");
                expectedReads[target]++;
            }
        }

        for (var j = 0; j < instanceCount; j++)
        {
            var ops = fakes[j].Ops;
            var mutations = ops.Where(o => o.Method == "scene.create_node")
                .OrderBy(o => o.StartedUtc)
                .ToList();
            var reads = ops.Count(o => o.Method == "scene.get_tree");

            // 变更串行:窗口严格不重叠(终到序 = 开始序,无交错半写)。
            for (var k = 1; k < mutations.Count; k++)
            {
                Assert.True(
                    mutations[k].StartedUtc >= mutations[k - 1].EndedUtc,
                    $"mx{j} 变更窗口重叠(未串行):#{k - 1} [{mutations[k - 1].StartedUtc:O}, " +
                    $"{mutations[k - 1].EndedUtc:O}] 与 #{k} [{mutations[k].StartedUtc:O}, {mutations[k].EndedUtc:O}]");
            }

            // 零串台:本实例只收到本实例标记的变更,节点名集合与编排完全一致。
            Assert.Equal(expectedMutations[j].Count, mutations.Count);
            var seenNames = new HashSet<string>();
            foreach (var op in mutations)
            {
                Assert.Contains($"/root/Main/acc_m{j}", op.ParamsRaw);
                for (var other = 0; other < instanceCount; other++)
                {
                    if (other != j)
                    {
                        Assert.DoesNotContain($"/root/Main/acc_m{other}", op.ParamsRaw);
                    }
                }
                using var paramsDoc = JsonDocument.Parse(op.ParamsRaw);
                seenNames.Add(paramsDoc.RootElement.GetProperty("node_name").GetString()!);
            }
            Assert.True(
                seenNames.SetEquals(expectedMutations[j]),
                $"mx{j} 变更集合不一致:seen=[{string.Join(",", seenNames)}] want=[{string.Join(",", expectedMutations[j])}]");

            // 读取计数一致(错路由会在实例间造成计数失衡)。
            Assert.Equal(expectedReads[j], reads);

            // 进度事件:每变更一次 _executing;r1 的并发仅本实例产生排队(≥2)。
            Assert.Equal(mutations.Count, fakes[j].ExecutingProgressCount);
            if (j == 0)
            {
                Assert.True(
                    fakes[j].QueuedProgressCount >= 2,
                    $"mx0 应出现排队事件(_queued),实际 {fakes[j].QueuedProgressCount}");
            }
            else
            {
                Assert.Equal(0, fakes[j].QueuedProgressCount);
            }
        }
    }

    // ── ② 在途租约可见(list_operations)────────────────────────

    /// <summary>
    /// 在途租约可见:A/B 两会话先后对同一实例发起变更(A 在飞、B 排队),观察者经
    /// list_operations 能同时看到 executing 与 queued 两条记录且严格归属该实例,完成后清空。
    /// <para>断言链:拉起 daemon 与 900ms 延迟变更脚本的串行化替身 → A 先发、150ms 后 B 发
    /// (同车道必然排队)→ 观察者在 8s 窗口轮询 list_operations,断言同时存在 executing 与
    /// queued,且每条记录的 instance 均为该实例 canonical 键、method 均为 scene.create_node →
    /// 两个调用均成功返回 → 再在 5s 窗口轮询,断言 operations 清空。</para>
    /// </summary>
    [Fact]
    public async Task in_flight_mutations_visible_via_list_operations_scoped_to_instance()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 180 });
        await using var sessionA = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        await using var sessionB = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        await using var observer = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\lease",
            StateDir = stateDir,
            SerializedMutationMethods = ["scene.create_node"],
            Scripts =
            {
                new FakeScript
                {
                    Method = "scene.create_node",
                    Reusable = true,
                    Frames = { new FakeFrame { AfterMs = 900, Json = EchoFrame() } },
                },
            },
        });
        await WaitConnectedAsync(observer, @"D:\proj\lease");

        // A 在飞、B 排队(同一实例,变更车道串行)。
        var callA = sessionA.CallToolAsync("scene_create_node", new Dictionary<string, object?>
        {
            ["class_name"] = "Node",
            ["parent_path"] = "/root/Main",
            ["node_name"] = "lease_a",
            ["instance"] = @"D:\proj\lease",
        });
        await Task.Delay(150);
        var callB = sessionB.CallToolAsync("scene_create_node", new Dictionary<string, object?>
        {
            ["class_name"] = "Node",
            ["parent_path"] = "/root/Main",
            ["node_name"] = "lease_b",
            ["instance"] = @"D:\proj\lease",
        });

        // 观察窗口:executing(A)与 queued(B)都应在册且严格归属该实例。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        List<(string Instance, string Method, string Status)> ops = [];
        while (DateTime.UtcNow < deadline)
        {
            ops = await ReadOperationsAsync(observer);
            if (ops.Any(o => o.Status == "executing") && ops.Any(o => o.Status == "queued"))
            {
                break;
            }
            await Task.Delay(80);
        }
        Assert.Contains(ops, o => o.Status == "executing");
        Assert.Contains(ops, o => o.Status == "queued");
        Assert.All(ops, o => Assert.Equal(JsonMatch.CanonicalProjectKey(@"D:\proj\lease"), o.Instance));
        Assert.All(ops, o => Assert.Equal("scene.create_node", o.Method));

        await Task.WhenAll(callA.AsTask(), callB.AsTask());
        var resultA = await callA;
        var resultB = await callB;
        Assert.True(resultA.IsError is null or false, TextOf(resultA));
        Assert.True(resultB.IsError is null or false, TextOf(resultB));

        // 完成后在途表清空。
        var clearDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < clearDeadline)
        {
            if ((await ReadOperationsAsync(observer)).Count == 0)
            {
                break;
            }
            await Task.Delay(80);
        }
        Assert.Empty(await ReadOperationsAsync(observer));
    }

    // ── ③ 生命周期四场景 ────────────────────────────────────────

    /// <summary>
    /// 生命周期①:直启(等价 addon 边车拉起路径)与 shim 自举同状态目录同端口竞态 ——
    /// 落定后服务可用,且单例不变式成立(竞态后再直启必被锁拒绝)。
    /// <para>断言链:两路同时拉起 → 经 shim 发 list_instances,断言拿到 result(自举成功或
    /// 复用赢家均可,只此一个)→ 竞态后再 Start 第三个 daemon,断言其 20s 内退出且退出码为
    /// 单例码(Windows 2 / Unix 3)→ 收尾分叉:直启方若已退出,断言以同一单例码干净让位;
    /// 若仍存活,用状态目录稳定 token 直连并断言 ListTools 含 list_instances。</para>
    /// </summary>
    [Fact]
    public async Task lifecycle_spawn_race_between_direct_and_shim_yields_single_daemon()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();

        // 双路同时拉起:直启(等价 addon 边车拉起路径)与 shim 自举,同状态目录同端口竞态。
        var direct = DaemonProcess.Start(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = port,
            IdleSeconds = 120,
        });
        using var shim = ShimProcess.Start(new ShimSpawnOptions { Port = port, StateDir = stateDir, IdleSeconds = 30 });

        // 竞态落定:服务可用(shim 首条消息走通 —— 自举的或复用的,只此一个)。
        var viaShim = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(60));
        using (var doc = JsonDocument.Parse(viaShim))
        {
            Assert.True(doc.RootElement.TryGetProperty("result", out _), $"竞态后 shim 路径应可用:{viaShim}");
        }

        // 单例不变式:竞态后再直启一个 → 必被锁拒绝(Windows 2 / Unix 3),不产生双监听。
        using var third = DaemonProcess.Start(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = TestPorts.GetFreePort(),
            IdleSeconds = 30,
        });
        Assert.True(third.WaitForExit(TimeSpan.FromSeconds(20)), "竞态后的第三实例应被单例锁快速拒绝");
        Assert.Equal(ExpectedSingletonExitCode(), third.ExitCode);

        // 直启方:赢家继续服务;输家必须以单例码干净让位(绝不半死不活)。
        if (direct.HasExited)
        {
            Assert.Equal(ExpectedSingletonExitCode(), direct.ExitCode);
        }
        else
        {
            await using var client = await SessionConnector.ConnectAsync(port, ReadToken(stateDir));
            (await client.ListToolsAsync()).AssertContainsTool("list_instances");
        }
    }

    /// <summary>
    /// 生命周期②:单例防双开 —— 同状态目录的第二个 daemon 因单例锁快速退出,持锁实例
    /// 不受影响并继续服务。
    /// <para>断言链:StartReady 拉起首实例 → 同状态目录、不同端口再 Start 第二实例 → 断言
    /// 第二实例 15s 内退出且退出码为单例码 → 断言首实例未退出,并经 SessionConnector 直连,
    /// ListTools 含 list_instances。</para>
    /// </summary>
    [Fact]
    public async Task lifecycle_singleton_refuses_second_daemon_and_first_keeps_serving()
    {
        var stateDir = TestPaths.NewStateDir();
        using var first = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        using var second = DaemonProcess.Start(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = TestPorts.GetFreePort(),
            IdleSeconds = 30,
        });
        Assert.True(second.WaitForExit(TimeSpan.FromSeconds(15)), "第二实例应因单例锁退出");
        Assert.Equal(ExpectedSingletonExitCode(), second.ExitCode);

        Assert.False(first.HasExited, "持锁实例不应受影响");
        await using var client = await SessionConnector.ConnectAsync(first.Port, first.Token);
        (await client.ListToolsAsync()).AssertContainsTool("list_instances");
    }

    /// <summary>
    /// 生命周期③:空闲自退 —— 全部客户端断开后,daemon 在空闲阈值(3s)到期时以退出码 0
    /// 自行退出。
    /// <para>断言链:IdleSeconds=3 拉起 daemon → 客户端连入并 ListTools 一次 → using 块
    /// 结束主动断开 → 断言 daemon 20s 内退出且 ExitCode 为 0。</para>
    /// </summary>
    [Fact]
    public async Task lifecycle_idle_exit_after_clients_leave()
    {
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { IdleSeconds = 3 });
        await using (var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token))
        {
            (await client.ListToolsAsync()).AssertContainsTool("list_instances");
        }

        Assert.True(
            daemon.WaitForExit(TimeSpan.FromSeconds(20)),
            "全部客户端断开且空闲超阈值后,daemon 应自行退出");
        Assert.Equal(0, daemon.ExitCode);
    }

    /// <summary>
    /// 生命周期④:daemon 崩溃经 shim 自愈 —— 下一跳请求触发 shim 重试 + 重新自举,复活的
    /// daemon 沿用同一 token 与端口,拉起方静态配置无需变更。
    /// <para>断言链:daemon + shim 先走通一次(id=7,响应回显同 id)→ Dispose 硬杀 daemon
    /// (不等优雅退出,锁残留、token 稳定不变)→ shim 再发 id=8,断言拿到 result(残留锁
    /// 必须自愈)→ 用状态目录里的稳定 token 直连复活实例,断言 ListTools 含 list_instances。</para>
    /// </summary>
    [Fact]
    public async Task lifecycle_daemon_crash_is_self_healed_through_shim()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();
        var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, Port = port, IdleSeconds = 120 });
        using var shim = ShimProcess.Start(new ShimSpawnOptions { Port = port, StateDir = stateDir, IdleSeconds = 30 });

        var first = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(60));
        using (var firstDoc = JsonDocument.Parse(first))
        {
            Assert.Equal(7, firstDoc.RootElement.GetProperty("id").GetInt32());
        }

        // 硬杀 daemon(不等优雅退出):锁残留、token 稳定保持不变。
        daemon.Dispose();

        // 下一跳:shim 重试 + 重新自举(同状态目录,残留锁必须自愈)。
        var second = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(60));
        using (var secondDoc = JsonDocument.Parse(second))
        {
            Assert.Equal(8, secondDoc.RootElement.GetProperty("id").GetInt32());
            Assert.True(secondDoc.RootElement.TryGetProperty("result", out _), $"崩溃自愈失败:{second}");
        }

        // 复活的 daemon 直连可用,且沿用同一 token(拉起方静态配置无需变更)。
        await using var client = await SessionConnector.ConnectAsync(port, ReadToken(stateDir));
        (await client.ListToolsAsync()).AssertContainsTool("list_instances");
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>按平台取单例拒绝的期望退出码:Windows 精确归因为 SingletonLockUnavailable(2),
    /// Unix 归入 LockStateUnclear(3)。</summary>
    /// <returns>当前平台下多余的 daemon 实例应有的退出码。</returns>
    private static int ExpectedSingletonExitCode() =>
        OperatingSystem.IsWindows() ? ExitCodes.SingletonLockUnavailable : ExitCodes.LockStateUnclear;

    /// <summary>从状态目录读稳定 token(竞态/自愈场景下无法从进程实例取)。</summary>
    private static string ReadToken(string stateDir)
    {
        var path = Path.Combine(stateDir, "daemon-token");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var token = File.ReadAllText(path).Trim();
                if (token.Length > 0)
                {
                    return token;
                }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"状态目录 {stateDir} 的 daemon-token 未在 15s 内就绪");
    }

    /// <summary>调 list_operations 并把 operations 数组折叠为 (instance, method, status) 快照(调用失败即断言失败)。</summary>
    /// <param name="client">观察者 MCP 客户端。</param>
    /// <returns>当前在途操作的三元组列表。</returns>
    private static async Task<List<(string Instance, string Method, string Status)>> ReadOperationsAsync(McpClient client)
    {
        var result = await client.CallToolAsync("list_operations", new Dictionary<string, object?>());
        Assert.True(result.IsError is null or false, TextOf(result));
        using var payload = JsonDocument.Parse(TextOf(result));
        return payload.RootElement.GetProperty("operations").EnumerateArray()
            .Select(op => (
                op.GetProperty("instance").GetString()!,
                op.GetProperty("method").GetString()!,
                op.GetProperty("status").GetString()!))
            .ToList();
    }

    /// <summary>构造替身回显帧:成功信封,params_seen 占位符原样带回请求 params(参数级零串台的证据)。</summary>
    /// <returns>可填入 FakeFrame.Json 的原始 JSON。</returns>
    private static string EchoFrame() =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"params_seen\":\"$params\"}}";

    /// <summary>取工具结果的唯一文本内容块(内容块非单、非文本即断言失败)。</summary>
    /// <param name="result">MCP 工具调用结果。</param>
    /// <returns>唯一文本块的内容。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    /// <summary>轮询 list_instances 直至指定项目路径的实例 connected 为 true(20s 超时抛 TimeoutException)。</summary>
    /// <param name="client">MCP 客户端。</param>
    /// <param name="projectPath">项目路径(按 canonical 键比对)。</param>
    private static async Task WaitConnectedAsync(McpClient client, string projectPath)
    {
        var canonical = JsonMatch.CanonicalProjectKey(projectPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
            using var payload = JsonDocument.Parse(TextOf(result));
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
