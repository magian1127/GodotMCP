using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 14 验收:① fake 实例声明扩展工具后,经 refresh_extensions 与 extensions.changed
/// 两条变更路径投影进 tools/list 并可调用(未分组即时可见;分组经 discover_tools 激活);
/// ② 双实例(新旧版本替身)并存时 tools/list 为并集;③ 对旧实例调用高版本门控工具得到
/// 含引擎版本信息的明确错误(Node 调用期门控同文案);④ 单实例版本过滤与 Node 一致(无回退)。
/// </summary>
public class ExtensionAndGateTests
{
    /// <summary>gamma 项目:扩展投影场景的编辑器替身路径(未分组与分组扩展工具)。</summary>
    private const string ProjectGamma = @"D:\proj\gamma";
    /// <summary>delta 项目:旧版替身路径(auth 声明 godot_version=4.4.2,用于版本门控反向用例)。</summary>
    private const string ProjectDelta = @"D:\proj\delta";
    /// <summary>epsilon 项目:新版替身路径(auth 声明 godot_version=4.7.0,用于版本门控正向用例)。</summary>
    private const string ProjectEpsilon = @"D:\proj\epsilon";

    /// <summary>
    /// 扩展投影双路径:refresh_extensions 即时投影未分组工具,extensions.changed 通知完成
    /// "移除 + 分组上架",分组工具经 discover_tools 激活后可见可调用,reset 一并停用。
    /// <para>断言链:Arrange —— daemon+会话;gamma 编辑器替身声明 extensions.refresh(返回未分组
    /// demo.hello,含 input_schema 与 annotations)及 demo.hello/demo.world 回声帧。Act/Assert ——
    /// ① discover_tools(refresh_extensions=true) 无错:extensions_refreshed=true,摘要 registered=1、
    /// deferred=0、commands=1。② tools/list 出现 demo_hello:description="Say hello",schema 含
    /// name:string 且带 instance 键。③ 调用 demo_hello 无错:params_seen.name="Zed" 且无 instance 键
    /// (wire 参数仅 schema 声明键,点号法名经下划线反转成工具名)。④ 广播 extensions.changed
    /// (removed=[demo.hello],新增分组 demo_tools 携带 demo.world)后轮询:demo_hello 已移除,
    /// demo_world 未激活不可见。⑤ 目录出现 demo_tools 行:status="available"、description="Demo tools"。
    /// ⑥ request="demo_tools" 激活:status="activated"、match="exact_name";tools/list 出现
    /// demo_world,discover_tools 描述追加 "扩展:demo_tools [已加载] — Demo tools";调用 demo_world
    /// 无错,params_seen.target="hud"。⑦ reset=true:deactivated 含 demo_tools、deactivated_tools 含
    /// demo_world,tools/list 不再有 demo_world。</para>
    /// </summary>
    [Fact]
    public async Task extensions_project_into_tools_list_via_refresh_and_changed()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ProjectGamma,
            StateDir = stateDir,
            Scripts =
            {
                new FakeScript
                {
                    Method = "extensions.refresh",
                    Frames =
                    {
                        new FakeFrame
                        {
                            Json = ResultFrame(
                                """{"success":true,"commands":[{"method":"demo.hello","description":"Say hello","input_schema":{"type":"object","properties":{"name":{"type":"string"}}},"annotations":{"readOnlyHint":true,"destructiveHint":false,"idempotentHint":true}}]}"""),
                        },
                    },
                },
                new FakeScript { Method = "demo.hello", Reusable = true, Frames = { new FakeFrame { Json = EchoFrame() } } },
                new FakeScript { Method = "demo.world", Reusable = true, Frames = { new FakeFrame { Json = EchoFrame() } } },
            },
        });
        await WaitConnectedAsync(client, ProjectGamma);

        // ── refresh_extensions:未分组扩展即时投影进 tools/list。──
        var refresh = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["refresh_extensions"] = true,
        });
        Assert.True(refresh.IsError is null or false, TextOf(refresh));
        using (var payload = JsonDocument.Parse(TextOf(refresh)))
        {
            Assert.True(payload.RootElement.GetProperty("extensions_refreshed").GetBoolean());
            var summary = payload.RootElement.GetProperty("extension_refresh");
            Assert.Equal(1, summary.GetProperty("registered").GetInt32());
            Assert.Equal(0, summary.GetProperty("deferred").GetInt32());
            Assert.Equal(1, summary.GetProperty("commands").GetInt32());
        }

        var afterRefresh = await client.ListToolsAsync();
        var hello = Assert.Single(afterRefresh, t => t.Name == "demo_hello");
        Assert.Equal("Say hello", hello.Description);
        using (var schema = JsonDocument.Parse(hello.JsonSchema.GetRawText()))
        {
            Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
            Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("instance", out _));
        }

        // 未分组扩展工具可调用(wire 方法 = 点号法名的下划线反转;params 仅 schema 声明键)。
        var call = await client.CallToolAsync("demo_hello", new Dictionary<string, object?>
        {
            ["name"] = "Zed",
            ["instance"] = ProjectGamma,
        });
        Assert.True(call.IsError is null or false, TextOf(call));
        using (var payload = JsonDocument.Parse(TextOf(call)))
        {
            Assert.Equal("Zed", payload.RootElement.GetProperty("params_seen").GetProperty("name").GetString());
            Assert.False(payload.RootElement.GetProperty("params_seen").TryGetProperty("instance", out _));
        }

        // ── extensions.changed:removed 移除 + 分组命令进入扩展组(激活后可见可调用)。──
        editor.Broadcast(
            "extensions.changed",
            """{"commands":[{"method":"demo.world","description":"World ops","input_schema":{"type":"object","properties":{"target":{"type":"string"}}},"annotations":{"readOnlyHint":false,"destructiveHint":false,"idempotentHint":true},"group":{"name":"demo_tools","description":"Demo tools","keywords":["demo","widgets"]}}],"removed":["demo.hello"]}""");

        // 移除生效(轮询容忍通知到达间隙);分组工具在激活前不可见。
        await PollAsync(async () =>
        {
            var tools = await client.ListToolsAsync();
            return tools.All(t => t.Name != "demo_hello") && tools.All(t => t.Name != "demo_world");
        }, "demo_hello 移除且 demo_world 未激活不可见");

        // 目录:扩展组行出现(available)。
        var catalog = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>());
        using (var payload = JsonDocument.Parse(TextOf(catalog)))
        {
            var row = payload.RootElement.GetProperty("groups").EnumerateArray()
                .Single(g => g.GetProperty("name").GetString() == "demo_tools");
            Assert.Equal("available", row.GetProperty("status").GetString());
            Assert.Equal("Demo tools", row.GetProperty("description").GetString());
        }

        // 激活扩展组 → 工具上线、可调用;discover_tools 描述带扩展节。
        var activate = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "demo_tools",
        });
        Assert.True(activate.IsError is null or false, TextOf(activate));
        using (var payload = JsonDocument.Parse(TextOf(activate)))
        {
            var row = payload.RootElement.GetProperty("groups").EnumerateArray()
                .Single(g => g.GetProperty("name").GetString() == "demo_tools");
            Assert.Equal("activated", row.GetProperty("status").GetString());
            Assert.Equal("exact_name", row.GetProperty("match").GetString());
        }

        var toolsAfter = await client.ListToolsAsync();
        Assert.Contains(toolsAfter, t => t.Name == "demo_world");
        var discoverTool = toolsAfter.Single(t => t.Name == "discover_tools");
        Assert.Contains("扩展：demo_tools [已加载] — Demo tools", discoverTool.Description);

        var world = await client.CallToolAsync("demo_world", new Dictionary<string, object?>
        {
            ["target"] = "hud",
            ["instance"] = ProjectGamma,
        });
        Assert.True(world.IsError is null or false, TextOf(world));
        using (var payload = JsonDocument.Parse(TextOf(world)))
        {
            Assert.Equal("hud", payload.RootElement.GetProperty("params_seen").GetProperty("target").GetString());
        }

        // reset:true 同时停用扩展组(Node deactivateGroups 覆盖扩展组)。
        var reset = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["reset"] = true,
        });
        using (var payload = JsonDocument.Parse(TextOf(reset)))
        {
            var deactivated = payload.RootElement.GetProperty("deactivated").EnumerateArray()
                .Select(e => e.GetString()!).ToList();
            Assert.Contains("demo_tools", deactivated);
            Assert.Contains("demo_world", payload.RootElement.GetProperty("deactivated_tools").EnumerateArray()
                .Select(e => e.GetString()!).ToList());
        }
        var afterReset = await client.ListToolsAsync();
        Assert.DoesNotContain(afterReset, t => t.Name == "demo_world");
    }

    /// <summary>
    /// 版本门控跨实例取并集:tools/list 按"任一在场实例满足最低版本"放行,调用期按目标实例版本
    /// 门控 —— 内置工具与扩展 min_godot_version 同语义、同错误文案。
    /// <para>断言链:阶段 A —— 仅 4.4.2 旧替身(delta)在场:激活 cleanup 组后 tools/list 不含
    /// scene_close(最低 4.5 不满足即隐藏,与 Node 注册门控一致、无回退),project_delete(无门控)
    /// 照常在列。阶段 B —— 加入 4.7.0 替身(epsilon,extensions.refresh 声明 min_godot_version=4.6 的
    /// demo.future)后轮询至 scene_close 经并集可见;对 delta 调 scene_close 报错:code="UNSUPPORTED"、
    /// error="scene_close is not supported on this Godot version (connected: 4.4)"、hint=
    /// "Requires Godot 4.5 or newer. Use classdb.get_info for alternatives.";对 epsilon 调用无错。
    /// 阶段 C —— refresh_extensions 后 demo_future 经并集可见;对 delta 调用报 UNSUPPORTED
    /// (error 含 "connected: 4.4",hint 含 "Requires Godot 4.6 or newer."),对 epsilon 调用无错。</para>
    /// </summary>
    [Fact]
    public async Task version_gating_is_union_across_instances_and_errors_on_old_instance()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        // ── 阶段 A:单实例(4.4.2)→ 门控工具隐藏,与 Node 注册门控一致(无回退)。──
        using var old = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ProjectDelta,
            StateDir = stateDir,
            AuthAckJson = """{"authed":true,"godot_version":"4.4.2","version":"1.0.0","headless":false}""",
            Scripts =
            {
                new FakeScript { Method = "scene.close", Reusable = true, Frames = { new FakeFrame { Json = EchoFrame() } } },
                new FakeScript { Method = "extensions.refresh", Reusable = true, Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"commands":[]}""") } } },
            },
        });
        await WaitConnectedAsync(client, ProjectDelta);

        await client.CallToolAsync("discover_tools", new Dictionary<string, object?> { ["request"] = "cleanup" });
        var singleOld = await client.ListToolsAsync();
        Assert.DoesNotContain(singleOld, t => t.Name == "scene_close");     // min 4.5 不满足 → 隐藏
        Assert.Contains(singleOld, t => t.Name == "project_delete");        // 无门控 → 照常

        // ── 阶段 B:加入 4.7.0 实例 → tools/list 为并集;旧实例调用得版本错误。──
        using var modern = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ProjectEpsilon,
            StateDir = stateDir,
            AuthAckJson = """{"authed":true,"godot_version":"4.7.0","version":"1.0.0","headless":false}""",
            Scripts =
            {
                new FakeScript { Method = "scene.close", Reusable = true, Frames = { new FakeFrame { Json = EchoFrame() } } },
                new FakeScript { Method = "demo.future", Reusable = true, Frames = { new FakeFrame { Json = EchoFrame() } } },
                new FakeScript
                {
                    Method = "extensions.refresh",
                    Reusable = true,
                    Frames =
                    {
                        new FakeFrame
                        {
                            Json = ResultFrame(
                                """{"success":true,"commands":[{"method":"demo.future","description":"Future only","input_schema":{"type":"object","properties":{}},"min_godot_version":"4.6"}]}"""),
                        },
                    },
                },
            },
        });
        await WaitConnectedAsync(client, ProjectEpsilon);
        await PollAsync(async () =>
        {
            var tools = await client.ListToolsAsync();
            return tools.Any(t => t.Name == "scene_close");
        }, "4.7.0 实例加入后 scene_close 经并集可见");

        // 旧实例调用:明确错误含引擎版本信息(Node 调用期门控同文案)。
        var onOld = await client.CallToolAsync("scene_close", new Dictionary<string, object?>
        {
            ["file_path"] = "res://Main.tscn",
            ["instance"] = ProjectDelta,
        });
        Assert.True(onOld.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(onOld)))
        {
            Assert.Equal("UNSUPPORTED", payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "scene_close is not supported on this Godot version (connected: 4.4)",
                payload.RootElement.GetProperty("error").GetString());
            Assert.Equal(
                "Requires Godot 4.5 or newer. Use classdb.get_info for alternatives.",
                payload.RootElement.GetProperty("hint").GetString());
        }

        // 新实例调用:照常执行。
        var onModern = await client.CallToolAsync("scene_close", new Dictionary<string, object?>
        {
            ["file_path"] = "res://Main.tscn",
            ["instance"] = ProjectEpsilon,
        });
        Assert.True(onModern.IsError is null or false, TextOf(onModern));

        // ── 阶段 C:扩展版本门控 —— 新实例声明的 min 4.6 扩展可见(并集),旧实例调用同类错误。──
        var refresh = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["refresh_extensions"] = true,
        });
        Assert.True(refresh.IsError is null or false, TextOf(refresh));
        await PollAsync(async () =>
        {
            var tools = await client.ListToolsAsync();
            return tools.Any(t => t.Name == "demo_future");
        }, "min 4.6 扩展经新实例并集可见");

        var extOnOld = await client.CallToolAsync("demo_future", new Dictionary<string, object?>
        {
            ["instance"] = ProjectDelta,
        });
        Assert.True(extOnOld.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(extOnOld)))
        {
            Assert.Equal("UNSUPPORTED", payload.RootElement.GetProperty("code").GetString());
            Assert.Contains("connected: 4.4", payload.RootElement.GetProperty("error").GetString());
            Assert.Contains("Requires Godot 4.6 or newer.", payload.RootElement.GetProperty("hint").GetString());
        }

        var extOnModern = await client.CallToolAsync("demo_future", new Dictionary<string, object?>
        {
            ["instance"] = ProjectEpsilon,
        });
        Assert.True(extOnModern.IsError is null or false, TextOf(extOnModern));
    }

    // ── 辅助 ─────────────────────────────────────────────────

    /// <summary>通用回声帧:success=true 且 params_seen 原样回显 wire 参数($params 占位)。</summary>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string EchoFrame() =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"params_seen\":\"$params\"}}";

    /// <summary>把 result JSON 包成标准 JSON-RPC 结果帧($request_id 占位符由替身回填为真实请求 id)。</summary>
    /// <param name="resultJson">工具 result 载荷的 JSON 文本。</param>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string ResultFrame(string resultJson) =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":" + resultJson + "}";

    /// <summary>取工具结果唯一文本内容块的文本(顺带断言内容块恰有一个)。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>文本内容块承载的字符串。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    /// <summary>每 100ms 轮询一次异步条件直至满足;15s 超时抛 TimeoutException(容忍通知到达间隙)。</summary>
    /// <param name="condition">轮询谓词,返回 true 即通过。</param>
    /// <param name="description">超时消息里的人类可读描述。</param>
    private static async Task PollAsync(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"等待超时:{description}");
    }

    /// <summary>轮询 list_instances 直到目标项目(规范化路径)显示 connected;20s 超时抛 TimeoutException。</summary>
    /// <param name="client">MCP 会话客户端。</param>
    /// <param name="projectPath">目标项目路径(内部先做规范化)。</param>
    /// <returns>实例连接就绪后完成的任务。</returns>
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
