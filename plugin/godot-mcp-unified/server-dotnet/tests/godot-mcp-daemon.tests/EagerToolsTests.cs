using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 09 验收:eager 编辑器工具迁移——① 工具面完整(名称/schema 与 Node 同名工具对齐);
/// ② 每个工具对 fake 实例端到端通过(以 params 回声断言 wire 参数构造与省略语义);
/// ③ node_inspect / node_set_property 的复合路由与 Node handler 同形(runtime 分支如实报态)。
/// </summary>
public class EagerToolsTests
{
    /// <summary>eager 工具面覆盖的 18 个 wire 方法(替身逐一声明回声帧,用于验证 wire 参数构造)。</summary>
    private static readonly string[] WireMethods =
    {
        "scene.get_tree", "scene.create_node", "scene.delete_node", "scene.create", "scene.open",
        "scene.query", "script.check", "node.get_property", "node.get_property_list",
        "node.set_property", "node.set_script", "node.manage", "editor.save_scene", "project.get_settings",
        "editor.windows", "editor.answer_dialog",
        "editor.build_csharp", "editor.build_csharp_status",
    };

    /// <summary>
    /// eager 工具面完整:无实例在场时 tools/list 已含全部 21 个工具,抽查 schema 的必填与属性键
    /// 与 Node 同名工具定义对齐。
    /// <para>断言链:Arrange —— DaemonProcess 真实拉起 daemon 并接入会话(无 fake 实例,eager 工具
    /// 不依赖实例)。Act —— ListToolsAsync。Assert —— ① 21 个预期工具名(实例/操作/场景/脚本/节点/
    /// 保存/设置族)逐一 Contains;② AssertToolSchema 抽查 7 个工具:scene_create_node 必填
    /// class_name+parent_path、共 7 属性;scene_delete_node 必填 node_path;scene_query 全可选、
    /// 10 属性;node_manage 必填 action+node_path;node_set_property 全可选、7 属性;
    /// editor_save_scene 全可选;script_check 必填 file_path。</para>
    /// </summary>
    [Fact]
    public async Task daemon_exposes_eager_editor_tool_surface()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToList();
        foreach (var expected in new[]
                 {
                     "list_instances", "list_operations", "editor_sync", "editor_save_scene", "project_get_settings",
                     "scene_get_tree", "scene_create_node", "scene_delete_node", "scene_create", "scene_open",
                     "scene_query", "script_check", "node_inspect", "node_set_property", "node_set_script", "node_manage",
                     "editor_windows", "editor_answer_dialog",
                     "editor_build_csharp", "editor_build_csharp_status", "editor_build_csharp_wait",
                 })
        {
            Assert.Contains(expected, names);
        }

        // schema 抽查:必填与关键参数(name/required/properties 与 Node 定义对齐)。
        var byName = tools.ToDictionary(t => t.Name);
        AssertToolSchema(byName, "scene_create_node", required: ["class_name", "parent_path"], properties: ["class_name", "parent_path", "node_name", "layout_mode", "unique_name", "properties", "instance"]);
        AssertToolSchema(byName, "scene_delete_node", required: ["node_path"], properties: ["node_path", "instance"]);
        AssertToolSchema(byName, "scene_query", required: [], properties: ["class_filter", "group_filter", "name_pattern", "property_filters", "root_path", "max_depth", "include_properties", "offset", "limit", "instance"]);
        AssertToolSchema(byName, "node_manage", required: ["action", "node_path"], properties: ["action", "node_path", "new_name", "new_parent_path", "keep_global_transform", "new_index", "parent_path", "properties", "instance"]);
        AssertToolSchema(byName, "node_set_property", required: [], properties: ["node_path", "property", "value", "make_unique", "batch", "channel", "instance"]);
        AssertToolSchema(byName, "editor_save_scene", required: [], properties: ["file_path", "instance"]);
        AssertToolSchema(byName, "script_check", required: ["file_path"], properties: ["file_path", "instance"]);
    }

    /// <summary>
    /// eager 工具端到端转发:对 fake 实例的 wire params 与 Node 桥同形同省略语义;instance 仅寻址、
    /// 绝不转发,不存在目标报 INSTANCE_NOT_FOUND。
    /// <para>断言链:Arrange —— daemon+会话;fake(alpha)对 14 个 wire 方法回显 params_seen=$params。
    /// Act —— 21 组 (工具, 参数, 期望 JSON) 用例逐个调用(含 node_inspect 的 property 与 mask 两分支、
    /// editor_save_scene 无参与有参两形态)。Assert —— ① 每例无错且 success=true,params_seen 与期望
    /// JSON 深匹配(JsonMatch.DeepMatch,严格键集合 = 未传参数绝不出现);mask 分支补默认
    /// visibility:"all";无参保存 wire 为空对象。② instance 指向不存在的 d:/proj/ghost →
    /// code="INSTANCE_NOT_FOUND"(证明走路由);③ instance 指向合法目标时 params_seen 无 instance 键
    /// (寻址参数绝不进 wire)。</para>
    /// </summary>
    [Fact]
    public async Task eager_editor_tools_forward_wire_params_like_node_bridge()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = stateDir,
            Scripts = WireMethods.Select(m => new FakeScript
            {
                Method = m,
                Reusable = true,
                Frames = { new FakeFrame { Json = EchoParamsFrame() } },
            }).ToList(),
        });
        await WaitConnectedAsync(client, @"D:\proj\alpha");

        // 每行断言:工具调用 → wire params 的完整构造(严格键集合 = 省略语义与 Node 一致)。
        var cases = new (string Tool, Dictionary<string, object?> Args, string ExpectJson)[]
        {
            ("scene_get_tree", Args(("max_depth", 3), ("include_properties", true)),
                """{"max_depth":3,"include_properties":true}"""),
            ("scene_create_node",
                Args(("class_name", "CharacterBody2D"), ("parent_path", "."), ("node_name", "Player"),
                    ("properties", Obj(("position", Obj(("x", 1), ("y", 2)))))),
                """{"class_name":"CharacterBody2D","parent_path":".","node_name":"Player","properties":{"position":{"x":1,"y":2}}}"""),
            ("scene_delete_node", Args(("node_path", "Player")),
                """{"node_path":"Player"}"""),
            ("scene_create", Args(("file_path", "res://a.tscn"), ("if_exists", "return")),
                """{"file_path":"res://a.tscn","if_exists":"return"}"""),
            ("scene_open", Args(("file_path", "res://Main.tscn")),
                """{"file_path":"res://Main.tscn"}"""),
            ("scene_query", Args(("name_pattern", "Enemy*"), ("limit", 10)),
                """{"name_pattern":"Enemy*","limit":10}"""),
            ("script_check", Args(("file_path", "res://x.gd")),
                """{"file_path":"res://x.gd"}"""),
            ("node_inspect", Args(("node_path", "."), ("property", "position")),
                """{"node_path":".","property":"position"}"""),
            ("node_inspect", Args(("node_path", "."), ("mask", "script")),
                """{"node_path":".","mask":"script","visibility":"all"}"""),
            ("node_set_property",
                Args(("node_path", "."), ("property", "position"), ("value", Obj(("x", 2), ("y", 3))), ("make_unique", true)),
                """{"node_path":".","property":"position","value":{"x":2,"y":3},"make_unique":true}"""),
            ("node_set_script", Args(("node_path", "."), ("script_path", "res://p.gd")),
                """{"node_path":".","script_path":"res://p.gd"}"""),
            ("node_manage", Args(("action", "rename"), ("node_path", "."), ("new_name", "X")),
                """{"action":"rename","node_path":".","new_name":"X"}"""),
            ("editor_save_scene", Args(), "{}"),
            ("editor_save_scene", Args(("file_path", "res://b.tscn")),
                """{"file_path":"res://b.tscn"}"""),
            ("project_get_settings", Args(("prefix", "application")),
                """{"prefix":"application"}"""),
            ("editor_windows", Args(), "{}"),
            ("editor_answer_dialog", Args(("window", "w123"), ("button", "ok")),
                """{"window":"w123","button":"ok"}"""),
            ("editor_answer_dialog", Args(("window", "w123")),
                """{"window":"w123","button":"ok"}"""),
            ("editor_build_csharp", Args(("no_incremental", true)),
                """{"no_incremental":true}"""),
            ("editor_build_csharp_status", Args(), "{}"),
            ("editor_build_csharp_wait", Args(("timeout_ms", 5000)),
                "{}"),
        };

        foreach (var (tool, args, expectJson) in cases)
        {
            var result = await client.CallToolAsync(tool, args);
            Assert.True(result.IsError is null or false, $"{tool} 失败:{TextOf(result)}");
            var text = TextOf(result);
            using var payload = JsonDocument.Parse(text);
            Assert.True(payload.RootElement.GetProperty("success").GetBoolean(), $"{tool}: {text}");
            using var expected = JsonDocument.Parse(expectJson);
            JsonMatch.DeepMatch(payload.RootElement.GetProperty("params_seen"), expected.RootElement, $"{tool}.params_seen");
        }

        // instance 参数在该批工具上全部生效:不存在目标 → INSTANCE_NOT_FOUND(证明走路由);
        var missing = await client.CallToolAsync("scene_get_tree", Args(("instance", "d:/proj/ghost")));
        Assert.True(missing.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(missing)))
        {
            Assert.Equal("INSTANCE_NOT_FOUND", payload.RootElement.GetProperty("code").GetString());
        }

        // 且 instance 是寻址参数、绝不转发到 wire(合法目标 → params_seen 无 instance 键)。
        var routed = await client.CallToolAsync("scene_get_tree", Args(("instance", @"D:\proj\alpha"), ("max_depth", 1)));
        Assert.True(routed.IsError is null or false);
        using (var payload = JsonDocument.Parse(TextOf(routed)))
        using (var expected = JsonDocument.Parse("""{"max_depth":1}"""))
        {
            JsonMatch.DeepMatch(payload.RootElement.GetProperty("params_seen"), expected.RootElement, "routed.params_seen");
        }
    }

    /// <summary>
    /// node_set_property 的 channel=runtime 分支:参数校验报错文案与 Node handler 一致,
    /// 合法单发经运行时通道落到 runtime.set_property(运行时缺席时报 GAME_NOT_RUNNING)。
    /// <para>断言链:Act/Assert —— ① runtime+batch → code="INVALID_PARAMS",error/hint 为与 Node
    /// 一致的既定文案(runtime 单发不支持 batch/make_unique);② runtime 缺 property/value →
    /// INVALID_PARAMS,error="runtime node_set_property requires node_path, property, and value";
    /// ③ 运行时替身在场时 runtime 合法单发 → 抵达 runtime.set_property,params 为单组
    /// node_path/property/value;④ 运行时替身撤走后同调用 → GAME_NOT_RUNNING(证明确实走运行时通道,
    /// 而非静默改道编辑器通道)。</para>
    /// </summary>
    [Fact]
    public async Task node_set_property_runtime_branch_validates_like_node_bridge()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        // 编辑器替身(寻址锚点:runtime 调用的 instance 解析依赖编辑器实例在场)。
        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = stateDir,
        });
        // 运行时替身(Mode B,裸 ack,与编辑器共享会话令牌):回声 runtime.set_property 的 params。
        using var runtime = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = null,
            Token = editor.Token,
            AuthAckJson = """{"authed":true}""",
            Scripts =
            {
                new FakeScript
                {
                    Method = "runtime.set_property",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"params_seen\":\"$params\"}}" } },
                },
            },
        });
        await WaitConnectedAsync(client, @"D:\proj\alpha");

        // runtime + batch → INVALID_PARAMS(报错文案与 Node handler 一致)。
        var withBatch = await client.CallToolAsync("node_set_property", Args(
            ("channel", "runtime"), ("node_path", "."), ("property", "x"), ("value", 1),
            ("batch", new List<Dictionary<string, object?>> { Obj(("node_path", "."), ("property", "y"), ("value", 2)) })));
        Assert.True(withBatch.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(withBatch)))
        {
            Assert.Equal("INVALID_PARAMS", payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "runtime node_set_property accepts one node_path/property/value and does not support batch or make_unique",
                payload.RootElement.GetProperty("error").GetString());
            Assert.Equal(
                "Use channel='editor' for batch/make_unique, or issue one bounded runtime change.",
                payload.RootElement.GetProperty("hint").GetString());
        }

        // runtime 缺参 → INVALID_PARAMS(同 Node 文案)。
        var missing = await client.CallToolAsync("node_set_property", Args(("channel", "runtime"), ("node_path", ".")));
        Assert.True(missing.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(missing)))
        {
            Assert.Equal("INVALID_PARAMS", payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "runtime node_set_property requires node_path, property, and value",
                payload.RootElement.GetProperty("error").GetString());
        }

        // 运行时替身在位:合法单发抵达 runtime.set_property,且 params 恰为单组三键。
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, @"D:\proj\alpha", runtime.Port, Environment.ProcessId);
        await WaitUntilAsync(() => runtime.AuthedPeerCount == 1, TimeSpan.FromSeconds(15));
        var runtimeSingle = await client.CallToolAsync("node_set_property", Args(
            ("channel", "runtime"), ("node_path", "/root/Main"), ("property", "score"), ("value", 42)));
        Assert.True(runtimeSingle.IsError is null or false, TextOf(runtimeSingle));
        using (var payload = JsonDocument.Parse(TextOf(runtimeSingle)))
        {
            Assert.True(payload.RootElement.GetProperty("success").GetBoolean(), TextOf(runtimeSingle));
            using var expected = JsonDocument.Parse("""{"node_path":"/root/Main","property":"score","value":42}""");
            JsonMatch.DeepMatch(payload.RootElement.GetProperty("params_seen"), expected.RootElement, "runtime.set_property.params_seen");
        }

        // 运行时替身撤走(游戏停止):同调用改报 GAME_NOT_RUNNING —— 证明走的是运行时通道。
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, @"D:\proj\alpha", null, null);
        await WaitUntilAsync(() => runtime.AuthedPeerCount == 0, TimeSpan.FromSeconds(15));
        var withoutRuntime = await client.CallToolAsync("node_set_property", Args(
            ("channel", "runtime"), ("node_path", "/root/Main"), ("property", "score"), ("value", 1)));
        Assert.True(withoutRuntime.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(withoutRuntime)))
        {
            Assert.Equal("GAME_NOT_RUNNING", payload.RootElement.GetProperty("code").GetString());
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>断言工具 schema:required 与 properties 逐一包含期望键(与 Node 定义对齐的抽查)。</summary>
    /// <param name="tools">按名称索引的工具表。</param>
    /// <param name="name">目标工具名。</param>
    /// <param name="required">期望出现的必填参数名集合。</param>
    /// <param name="properties">期望出现的属性名集合。</param>
    private static void AssertToolSchema(
        Dictionary<string, McpClientTool> tools, string name, string[] required, string[] properties)
    {
        var tool = tools[name];
        var schema = tool.JsonSchema;
        var requiredNames = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();
        foreach (var r in required)
        {
            Assert.Contains(r, requiredNames);
        }
        var propertyNames = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        foreach (var p in properties)
        {
            Assert.Contains(p, propertyNames);
        }
    }

    /// <summary>把 (键, 值) 对打包成工具调用参数字典(用例表的紧凑写法)。</summary>
    /// <param name="pairs">键值对序列。</param>
    /// <returns>参数字典。</returns>
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
        {
            dict[key] = value;
        }
        return dict;
    }

    /// <summary>Args 的别名:构造嵌套对象字面量参数,读用例时更贴近 JSON 形状。</summary>
    /// <param name="pairs">键值对序列。</param>
    /// <returns>参数字典。</returns>
    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs) => Args(pairs);

    /// <summary>回声帧:success=true 且 params_seen 原样回显 wire 参数($params 占位)。</summary>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string EchoParamsFrame() =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"params_seen\":\"$params\"}}";

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

    /// <summary>每 100ms 轮询一次条件直至满足;超时抛 TimeoutException(等待通道拆装的异步收敛)。</summary>
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
