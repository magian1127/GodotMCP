using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 10 验收:playtest 通道与混合路由——① game_start 后运行时工具经 Mode B 通道生效;
/// ② 运行时缺席时截图/检查/log_read 的回退链路(含崩溃上下文);③ game_stop 后运行时通道按序拆除。
/// ④ 真实 Godot playtest 手动冒烟另行留档。
/// </summary>
public class PlaytestToolsTests
{
    /// <summary>
    /// playtest 主链路:game_start 建立运行时通道后,输入注入/节点检查/截图三类运行时工具经
    /// Mode B 通道生效;game_stop 按序拆除通道,拆除后再调用得 GAME_NOT_RUNNING。
    /// <para>断言链:Arrange —— DaemonProcess 真实拉起 daemon 并接入 MCP 会话;FakeGodot 起编辑器替身
    /// (game.start/game.stop 脚本帧)与运行时替身(Mode B 裸 ack,复用编辑器会话令牌,不重复注册),
    /// 经 FakeGodotRegistry.UpdateRuntimeFields 发布运行时条目(游戏侧行为)。Act —— game_start →
    /// input_simulate(单事件对象)→ runtime_inspect_node → capture_screenshot(disk)→ game_stop →
    /// 清空运行时条目 → 再次 input_simulate。Assert —— ① game_start 无错:runtime_ready=true、
    /// runtime_port=运行时端口,且不含 runtime_discovery/hint 键(结果已合并);运行时通道已建立
    /// (AuthedPeerCount=1)。② input_simulate 的 params_seen.events 已把单事件对象规范化为数组,
    /// 首元素 event_type="key"。③ runtime_inspect_node 合并 engine/script 两分区,node_path 随行,
    /// visibility 默认 "all"。④ 截图仅文本信封:path="user://screenshots/shot.png"、width=800。
    /// ⑤ game_stop 后轮询等待 AuthedPeerCount 归 0(通道拆除无悬挂连接);⑥ 再次调用报错
    /// code="GAME_NOT_RUNNING"。</para>
    /// </summary>
    [Fact]
    public async Task playtest_flow_routes_runtime_tools_and_tears_down_on_stop()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = stateDir,
            Scripts =
            {
                new FakeScript
                {
                    Method = "game.start",
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"runtime_discovery":"bridge","was_running":false}""") } },
                },
                new FakeScript
                {
                    Method = "game.stop",
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"was_running":true}""") } },
                },
            },
        });
        // 运行时替身(Mode B,裸 ack,与编辑器共享会话令牌;不重复注册)。
        using var runtime = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = null,
            Token = editor.Token,
            AuthAckJson = """{"authed":true}""",
            Scripts =
            {
                new FakeScript { Method = "input.simulate", Reusable = true, Frames = { new FakeFrame { Json = SectionFrame("input") } } },
                new FakeScript { Method = "runtime.get_node_state", Reusable = true, Frames = { new FakeFrame { Json = SectionFrame("engine") } } },
                new FakeScript { Method = "runtime.get_script_vars", Reusable = true, Frames = { new FakeFrame { Json = SectionFrame("script") } } },
                new FakeScript
                {
                    Method = "runtime.screenshot",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"path":"user://screenshots/shot.png","width":800,"height":600}""") } },
                },
            },
        });
        await WaitConnectedAsync(client, @"D:\proj\alpha");

        // playtest 启动:发布运行时条目(游戏侧行为)。
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, @"D:\proj\alpha", runtime.Port, Environment.ProcessId);

        // game_start(wait_for_runtime 默认 true):daemon 侧等待运行时通道就绪并合并结果。
        var start = await client.CallToolAsync("game_start", new Dictionary<string, object?>());
        Assert.True(start.IsError is null or false, TextOf(start));
        using (var payload = JsonDocument.Parse(TextOf(start)))
        {
            Assert.True(
                payload.RootElement.TryGetProperty("runtime_ready", out var readyEl),
                $"game_start 响应缺少 runtime_ready:{TextOf(start)}\n-- daemon stderr --\n{daemon.StderrSnapshot()}");
            Assert.True(readyEl.GetBoolean());
            Assert.Equal(runtime.Port, payload.RootElement.GetProperty("runtime_port").GetInt32());
            Assert.False(payload.RootElement.TryGetProperty("runtime_discovery", out _));
            Assert.False(payload.RootElement.TryGetProperty("hint", out _));
        }
        Assert.Equal(1, runtime.AuthedPeerCount); // 运行时通道已建立

        // input_simulate:单事件对象规范化为数组,经 Mode B 通道。
        var input = await client.CallToolAsync("input_simulate", new Dictionary<string, object?>
        {
            ["events"] = new Dictionary<string, object?> { ["event_type"] = "key", ["event_data"] = new Dictionary<string, object?> { ["keycode"] = 65 } },
        });
        Assert.True(input.IsError is null or false, TextOf(input));
        using (var payload = JsonDocument.Parse(TextOf(input)))
        {
            var seen = payload.RootElement.GetProperty("params_seen");
            Assert.Equal(JsonValueKind.Array, seen.GetProperty("events").ValueKind);
            Assert.Equal("key", seen.GetProperty("events")[0].GetProperty("event_type").GetString());
        }

        // runtime_inspect_node:两分区合并;visibility 默认 "all" 随行。
        var inspect = await client.CallToolAsync("runtime_inspect_node", new Dictionary<string, object?>
        {
            ["node_path"] = "/root/Main",
        });
        Assert.True(inspect.IsError is null or false, TextOf(inspect));
        using (var payload = JsonDocument.Parse(TextOf(inspect)))
        {
            Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("engine", payload.RootElement.GetProperty("engine").GetProperty("section").GetString());
            Assert.Equal("script", payload.RootElement.GetProperty("script").GetProperty("section").GetString());
            Assert.Equal("/root/Main", payload.RootElement.GetProperty("engine").GetProperty("params_seen").GetProperty("node_path").GetString());
            Assert.Equal("all", payload.RootElement.GetProperty("script").GetProperty("params_seen").GetProperty("visibility").GetString());
        }

        // capture_screenshot(target=runtime,disk 语义):仅文本信封,无图像块。
        var shot = await client.CallToolAsync("capture_screenshot", new Dictionary<string, object?>
        {
            ["target"] = "runtime",
            ["image_response_mode"] = "disk",
        });
        Assert.True(shot.IsError is null or false, TextOf(shot));
        using (var payload = JsonDocument.Parse(TextOf(shot)))
        {
            Assert.Equal("user://screenshots/shot.png", payload.RootElement.GetProperty("path").GetString());
            Assert.Equal(800, payload.RootElement.GetProperty("width").GetInt32());
        }

        // game_stop:游戏结束 → 运行时条目清除 → 通道按序拆除(无悬挂连接)。
        var stop = await client.CallToolAsync("game_stop", new Dictionary<string, object?>());
        Assert.True(stop.IsError is null or false, TextOf(stop));
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, @"D:\proj\alpha", null, null);
        await WaitUntilAsync(() => runtime.AuthedPeerCount == 0, TimeSpan.FromSeconds(10));

        var afterStop = await client.CallToolAsync("input_simulate", new Dictionary<string, object?>
        {
            ["events"] = new Dictionary<string, object?> { ["event_type"] = "key" },
        });
        Assert.True(afterStop.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(afterStop)))
        {
            Assert.Equal("GAME_NOT_RUNNING", payload.RootElement.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// capture_screenshot 默认内联模式返回合法的图像内容块(ImageContentBlock)——
    /// 回归:块数据须是已编码 base64,误喂原始字节会让客户端拿到非法 base64 文本。
    /// <para>断言链:Arrange —— daemon+会话;编辑器替身(D:\proj\inline)与运行时替身(共享令牌),
    /// runtime.screenshot 回帧携带 PNG 头形状字节(非 UTF-8/ASCII,误传必非合法 base64)的 base64
    /// 与 8x8 元数据;发布运行时条目。Act —— capture_screenshot(target=runtime,默认内联),
    /// GAME_NOT_RUNNING 视作 watcher 尚未消化注册表变更而轮询重试(15s 上限)。Assert ——
    /// ① 首内容块为 ImageContentBlock,DecodedData 还原为原始字节(能反序列化到此处本身即证明
    /// wire 数据为合法 base64),MimeType="image/png";② 次内容块为元数据文本,width=8。</para>
    /// </summary>
    [Fact]
    public async Task inline_screenshot_returns_valid_base64_image_block()
    {
        // 回归:ImageContentBlock.Data 期望已编码 base64,误喂原始字节会让客户端拿到非法 base64。
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        // 非 UTF-8、非 ASCII 的字节模式(PNG 头形状),原始误传时必然不是合法 base64 文本。
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x42 };
        var imageBase64 = Convert.ToBase64String(imageBytes);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\inline",
            StateDir = stateDir,
        });
        using var runtime = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\inline",
            StateDir = null,
            Token = editor.Token,
            AuthAckJson = """{"authed":true}""",
            Scripts =
            {
                new FakeScript
                {
                    Method = "runtime.screenshot",
                    Reusable = true,
                    Frames =
                    {
                        new FakeFrame { Json = ResultFrame(
                            $$"""{"success":true,"image_base64":"{{imageBase64}}","width":8,"height":8,"mime_type":"image/png"}""") },
                    },
                },
            },
        });
        await WaitConnectedAsync(client, @"D:\proj\inline");
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, @"D:\proj\inline", runtime.Port, Environment.ProcessId);

        // 注册表变更经 daemon watcher 异步消化(Reconcile 后才拉起 runtime 通道),满载时
        // 会晚于本调用到达 —— GAME_NOT_RUNNING 视作"通道尚未就绪",轮询重试直至建立(15s 上限)。
        CallToolResult shot;
        var shotDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            shot = await client.CallToolAsync("capture_screenshot", new Dictionary<string, object?>
            {
                ["target"] = "runtime",
            });
            var shotText = shot.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
            if (shot.IsError is null or false
                || !shotText.Contains("GAME_NOT_RUNNING", StringComparison.Ordinal)
                || DateTime.UtcNow >= shotDeadline)
            {
                break;
            }
            await Task.Delay(250);
        }
        Assert.True(shot.IsError is null or false, shot.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no text)");
        var image = Assert.IsType<ImageContentBlock>(shot.Content[0]);
        // 客户端视角:解码数据必须还原为原始字节(wire data 为合法 base64,反序列化才能走到这里)。
        Assert.Equal(imageBytes, image.DecodedData.ToArray());
        Assert.Equal("image/png", image.MimeType);
        // 次块为元数据文本(width 随行)。
        var meta = Assert.IsType<TextContentBlock>(shot.Content[1]).Text;
        using (var payload = JsonDocument.Parse(meta))
        {
            Assert.Equal(8, payload.RootElement.GetProperty("width").GetInt32());
        }
    }

    /// <summary>
    /// 慢启动竞态回归(实测线上症:runtime 通道 noReconnect 躺平):游戏侧发布 runtime_port
    /// 早于 runtime 服务器可服务(真实场景:mono 装配/主场景加载期 transport pump 不跑,
    /// 首连被拒或 auth ack 超时),daemon 首连失败后不得把通道判死——runtime_pid 存活期间
    /// 按短退避重试,游戏就绪后通道自愈;否则游戏活着而 runtime 工具永久 GAME_NOT_RUNNING
    /// (game_start 的 runtime_ready 探活走编辑器临时连接,对此互不可见)。
    /// <para>断言链:Arrange —— daemon+会话+编辑器替身;预留空闲端口并经 UpdateRuntimeFields
    /// 发布运行时字段(端口无监听)。Act —— 等 1.5s 保证 daemon 首连至少失败一轮(修复前实现
    /// 在此已一次性永久躺平)→ 同端口启动 runtime 替身(游戏就绪)。Assert —— 15s 内
    /// AuthedPeerCount=1(通道自愈重连);capture_screenshot(target=runtime) 端到端成功。</para>
    /// </summary>
    [Fact]
    public async Task runtime_channel_self_heals_when_game_starts_listening_late()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\slowstart",
            StateDir = stateDir,
        });
        await WaitConnectedAsync(client, @"D:\proj\slowstart");

        // 游戏侧行为:发布 runtime_port/runtime_pid,但服务器尚不可服务(慢启动窗口)。
        var runtimePort = TestPorts.GetFreePort();
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, @"D:\proj\slowstart", runtimePort, Environment.ProcessId);

        // 让 daemon 的首连确定失败一轮(修复前实现:一次性 noReconnect,此刻已永久躺平)。
        await Task.Delay(1500);

        // 游戏就绪:runtime 替身在同一端口开始监听(Mode B 裸 ack,共享编辑器会话令牌)。
        using var runtime = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\slowstart",
            StateDir = null,
            Token = editor.Token,
            AuthAckJson = """{"authed":true}""",
            Port = runtimePort,
            Scripts =
            {
                new FakeScript
                {
                    Method = "runtime.screenshot",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"path":"user://screenshots/slow.png","width":800,"height":600}""") } },
                },
            },
        });

        // 通道自愈:runtime_pid 存活 → 短退避重试最终连上(修复前实现此处必然超时)。
        await WaitUntilAsync(() => runtime.AuthedPeerCount == 1, TimeSpan.FromSeconds(15));

        // 端到端:runtime 工具经自愈通道成功。
        var shot = await client.CallToolAsync("capture_screenshot", new Dictionary<string, object?>
        {
            ["target"] = "runtime",
        });
        Assert.True(shot.IsError is null or false, TextOf(shot));
    }

    /// <summary>
    /// 运行时缺席但**无任何错误**时,不得声称崩溃:debugger.get_log 只有普通日志行
    /// (error_buffer 为空)时不应触发 "Game crashed" 补全,应落回通用 GAME_NOT_RUNNING 提示。
    /// <para>断言链:Arrange —— daemon+会话;仅编辑器替身,debugger.get_log 返回
    /// error_buffer=[] 且 lines 为正常启动横幅(实测本机误报正是把这类行当成 errors)。Act/Assert ——
    /// runtime_inspect_node 报 GAME_NOT_RUNNING,其 hint **不含** "Game crashed or failed to compile",
    /// 也不含横幅文本;本修复前该路径会把启动横幅当错误证据列出。</para>
    /// </summary>
    [Fact]
    public async Task runtime_absent_without_errors_does_not_claim_crash()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\noerr",
            StateDir = stateDir,
            Scripts =
            {
                // 无错误证据:error_buffer 空,lines 只是普通启动日志。
                new FakeScript
                {
                    Method = "debugger.get_log",
                    Reusable = true,
                    Frames =
                    {
                        new FakeFrame { Json = ResultFrame("""{"success":true,"returned":0,"total_lines":2,"error_buffer":[],"lines":["Godot Engine v4.7.2.stable.mono.official","OpenGL API 3.3.0 - Compatibility"]}""") },
                    },
                },
                // 编辑器控制台同样无 error 级条目。
                new FakeScript
                {
                    Method = "editor.get_console",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"returned":0,"total_lines":9,"entries":""}""") } },
                },
            },
        });
        await WaitConnectedAsync(client, @"D:\proj\noerr");

        var inspect = await client.CallToolAsync("runtime_inspect_node", new Dictionary<string, object?>
        {
            ["node_path"] = "/root/Main",
        });
        Assert.True(inspect.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(inspect)))
        {
            Assert.Equal("GAME_NOT_RUNNING", payload.RootElement.GetProperty("code").GetString());
            var hint = payload.RootElement.GetProperty("hint").GetString()!;
            Assert.DoesNotContain("Game crashed or failed to compile", hint);
            Assert.DoesNotContain("Godot Engine v4.7.2", hint);
        }
    }

    /// <summary>
    /// 运行时缺席的回退链路:log_read 按通道回退编辑器缓存并附摘要,runtime_inspect_node 与
    /// capture_screenshot 报 GAME_NOT_RUNNING 并以 error_buffer 补全崩溃上下文(crash context)。
    /// <para>断言链:Arrange —— daemon+会话;仅编辑器替身(D:\proj\solo),声明 debugger.get_log
    /// (error_buffer 含 boom/res://x.gd:7 一行)与 editor.get_console 回声帧。Act/Assert ——
    /// ① log_read(auto) 无错:_summary="1 error from debugger bridge, 1 cached line from log file"
    /// (回退摘要)。② log_read(channel=editor) 无错:_summary="2 lines (of 10 total)"(条数摘要)。
    /// ③ runtime_inspect_node 报错:code="GAME_NOT_RUNNING",hint 同时含崩溃行 "boom (res://x.gd:7)"
    /// 与固定文案 "Game crashed or failed to compile"。④ capture_screenshot(target=runtime) 同报
    /// GAME_NOT_RUNNING 且 hint 含崩溃行。</para>
    /// </summary>
    [Fact]
    public async Task runtime_absent_falls_back_to_editor_and_enriches_with_crash_context()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\solo",
            StateDir = stateDir,
            Scripts =
            {
                // 编辑器缓存链:debugger.get_log(承载 error_buffer 供崩溃上下文与回退摘要)。
                new FakeScript
                {
                    Method = "debugger.get_log",
                    Reusable = true,
                    Frames =
                    {
                        new FakeFrame { Json = ResultFrame("""{"success":true,"returned":1,"total_lines":3,"error_buffer":[{"message":"boom","source":"res://x.gd","line":7}],"lines":["tail line"]}""") },
                    },
                },
                new FakeScript
                {
                    Method = "editor.get_console",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = ResultFrame("""{"success":true,"returned":2,"total_lines":10,"entries":"e1\ne2"}""") } },
                },
            },
        });
        await WaitConnectedAsync(client, @"D:\proj\solo");

        // auto:运行时缺席 → 编辑器 debugger.get_log 回退,附回退摘要。
        var auto = await client.CallToolAsync("log_read", new Dictionary<string, object?>());
        Assert.True(auto.IsError is null or false, TextOf(auto));
        using (var payload = JsonDocument.Parse(TextOf(auto)))
        {
            Assert.Equal("1 error from debugger bridge, 1 cached line from log file", payload.RootElement.GetProperty("_summary").GetString());
        }

        // channel=editor:editor.get_console + 行数摘要。
        var editorLog = await client.CallToolAsync("log_read", new Dictionary<string, object?>
        {
            ["channel"] = "editor",
        });
        Assert.True(editorLog.IsError is null or false, TextOf(editorLog));
        using (var payload = JsonDocument.Parse(TextOf(editorLog)))
        {
            Assert.Equal("2 lines (of 10 total)", payload.RootElement.GetProperty("_summary").GetString());
        }

        // runtime_inspect_node:运行时缺席 → GAME_NOT_RUNNING + 崩溃上下文补全。
        var inspect = await client.CallToolAsync("runtime_inspect_node", new Dictionary<string, object?>
        {
            ["node_path"] = "/root/Main",
        });
        Assert.True(inspect.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(inspect)))
        {
            Assert.Equal("GAME_NOT_RUNNING", payload.RootElement.GetProperty("code").GetString());
            Assert.Contains("boom (res://x.gd:7)", payload.RootElement.GetProperty("hint").GetString());
            Assert.Contains("Game crashed or failed to compile", payload.RootElement.GetProperty("hint").GetString());
        }

        // capture_screenshot(target=runtime):同样走崩溃上下文。
        var shot = await client.CallToolAsync("capture_screenshot", new Dictionary<string, object?>
        {
            ["target"] = "runtime",
        });
        Assert.True(shot.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(shot)))
        {
            Assert.Equal("GAME_NOT_RUNNING", payload.RootElement.GetProperty("code").GetString());
            Assert.Contains("boom (res://x.gd:7)", payload.RootElement.GetProperty("hint").GetString());
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>把 result JSON 包成标准 JSON-RPC 结果帧($request_id 占位符由替身回填为真实请求 id)。</summary>
    /// <param name="resultJson">工具 result 载荷的 JSON 文本。</param>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string ResultFrame(string resultJson)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":" + resultJson + "}";
    }

    /// <summary>运行时脚本的通用帧:回显 wire 参数并携带分区标记(断言参数构造与合并)。</summary>
    /// <param name="section">随行返回的分区标记(如 input/engine/script)。</param>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string SectionFrame(string section)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"params_seen\":\"$params\",\"section\":\"" +
               section + "\"}}";
    }

    /// <summary>取工具结果唯一文本内容块的文本(顺带断言内容块恰有一个)。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>文本内容块承载的字符串。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
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

    /// <summary>每 100ms 轮询一次条件直至满足;超时抛 TimeoutException(用于通道拆除等异步收敛的等待)。</summary>
    /// <param name="condition">轮询谓词,返回 true 即通过。</param>
    /// <param name="timeout">最长等待时长。</param>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"条件在 {timeout.TotalSeconds}s 内未满足");
    }
}
