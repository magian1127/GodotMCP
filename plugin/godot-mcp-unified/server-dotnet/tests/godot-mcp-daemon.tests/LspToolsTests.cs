using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 11 验收:LSP 通道工具——① 五工具端到端(诊断/符号/悬停/补全/导航,经 daemon 自持
/// LSP 客户端端点发现);② 端口冲突与缺席时与 Node 桥一致的报错/降级;③ 多实例零串台;
/// ④ 输入校验与着色器短路;⑤ 项目级扫描聚合。
/// </summary>
public class LspToolsTests
{
    /// <summary>
    /// 五个 LSP 工具端到端 + 多实例零串台:两个临时项目各挂一台 FakeLspServer(端点写入实例
    /// 注册表,由 daemon 自持 LSP 客户端按端点发现接入),逐个验收诊断、悬停、补全、
    /// 定义/引用导航与符号树,并以 HOVER-A/HOVER-B 互斥证明路由不串台。
    /// <para>断言链:双替身 + 双 LSP 拉起并等 connected → lsp_diagnostics 断言行列 0-based →
    /// 1-based 换算(LSP line 2 → 显示 3)、severity 映射为 "Error"、message 与 code 原样透出
    /// → lsp_hover 对 A 断言含 HOVER-A、项目内 file:// 链接已转 res://、含 &lt;untrusted-
    /// 信封标记且不含 HOVER-B,对 B 断言返回 HOVER-B → lsp_completion 断言 limit=1 截断
    /// (count=1、total=2)、补全 kind 数字 2 映射为 "Method" → lsp_navigate(definition)断言
    /// uri 转 res:// 且行列 1-based(LSP 9,1 → 显示 10,2)→ lsp_navigate(references)对 B
    /// 实例断言 2 条引用 → lsp_symbols 断言符号名、kind 类别标签(Method)、start_line 换算与
    /// 嵌套 children(kind 13 → Variable)。</para>
    /// </summary>
    [Fact]
    public async Task five_lsp_tools_route_per_instance_without_crosstalk()
    {
        var stateDir = TestPaths.NewStateDir();
        var projA = CreateTempProject("lsp-a", ("main.gd", "func a():\n\tpass\n"));
        var projB = CreateTempProject("lsp-b", ("main.gd", "func b():\n\tpass\n"));
        using var lspA = new FakeLspServer
        {
            // 真实 Godot LSP 按自身解析回显 uri(大小写可能与 daemon 发送的规范化小写不同)。
            DiagnosticsUriMutator = uri => uri.Replace("a", "A"),
            MethodResults =
            {
                ["textDocument/hover"] = """{"contents":{"kind":"markdown","value":"HOVER-A def at $uri end"}}""",
                ["textDocument/completion"] = """{"items":[{"label":"alpha","kind":2,"detail":"fn"},{"label":"beta","kind":6}]}""",
                ["textDocument/definition"] = """[{"uri":"$uri","range":{"start":{"line":9,"character":1}}}]""",
                ["textDocument/references"] = """[{"uri":"$uri","range":{"start":{"line":0,"character":0}}},{"uri":"$uri","range":{"start":{"line":4,"character":2}}}]""",
                ["textDocument/documentSymbol"] = """[{"name":"a","kind":6,"range":{"start":{"line":0,"character":0},"end":{"line":1,"character":0}},"children":[{"name":"x","kind":13,"range":{"start":{"line":1,"character":1},"end":{"line":1,"character":5}}}]}]""",
            },
        };
        using var lspB = new FakeLspServer
        {
            DefaultDiagnosticsJson = "[]",
            MethodResults =
            {
                ["textDocument/hover"] = """{"contents":{"kind":"markdown","value":"HOVER-B def at $uri end"}}""",
                ["textDocument/references"] = """[{"uri":"$uri","range":{"start":{"line":1,"character":1}}},{"uri":"$uri","range":{"start":{"line":2,"character":2}}}]""",
            },
        };

        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var godotA = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projA, StateDir = stateDir, LspPort = lspA.Port });
        using var godotB = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projB, StateDir = stateDir, LspPort = lspB.Port });
        await WaitConnectedAsync(client, projA);
        await WaitConnectedAsync(client, projB);

        // ① 单文件诊断:0-based → 1-based 换算 + 严重级别标签 + code。
        var diagnostics = await client.CallToolAsync("lsp_diagnostics", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["instance"] = projA,
        });
        Assert.True(diagnostics.IsError is null or false, TextOf(diagnostics));
        using (var payload = JsonDocument.Parse(TextOf(diagnostics)))
        {
            Assert.Equal(1, payload.RootElement.GetProperty("count").GetInt32());
            var entry = payload.RootElement.GetProperty("diagnostics")[0];
            Assert.Equal(3, entry.GetProperty("line").GetInt32());   // LSP line 2 → 显示 3
            Assert.Equal(5, entry.GetProperty("character").GetInt32());
            Assert.Equal("Error", entry.GetProperty("severity").GetString());
            Assert.Equal("boom error", entry.GetProperty("message").GetString());
            Assert.Equal("UNDEFINED", entry.GetProperty("code").GetString());
        }

        // ② 悬停:untrusted 信封 + 项目内 file:// 链接转 res://;双实例零串台。
        var hoverA = await client.CallToolAsync("lsp_hover", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["line"] = 2,
            ["column"] = 1,
            ["instance"] = projA,
        });
        Assert.True(hoverA.IsError is null or false, TextOf(hoverA));
        using (var payload = JsonDocument.Parse(TextOf(hoverA)))
        {
            var contents = payload.RootElement.GetProperty("contents").GetString()!;
            Assert.Contains("HOVER-A", contents);
            Assert.Contains("res://main.gd", contents);
            Assert.Contains("<untrusted-", contents);
            Assert.DoesNotContain("HOVER-B", contents);
        }
        var hoverB = await client.CallToolAsync("lsp_hover", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["line"] = 1,
            ["column"] = 0,
            ["instance"] = projB,
        });
        using (var payload = JsonDocument.Parse(TextOf(hoverB)))
        {
            Assert.Contains("HOVER-B", payload.RootElement.GetProperty("contents").GetString()!);
        }

        // ③ 补全:limit 截断 + 类别标签 + total。
        var completion = await client.CallToolAsync("lsp_completion", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["line"] = 1,
            ["column"] = 1,
            ["limit"] = 1,
            ["instance"] = projA,
        });
        using (var payload = JsonDocument.Parse(TextOf(completion)))
        {
            Assert.Equal(1, payload.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(2, payload.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("alpha", payload.RootElement.GetProperty("completions")[0].GetProperty("label").GetString());
            Assert.Equal("Method", payload.RootElement.GetProperty("completions")[0].GetProperty("kind").GetString());
        }

        // ④ 定义导航:file:// → res:// + 1-based。
        var definition = await client.CallToolAsync("lsp_navigate", new Dictionary<string, object?>
        {
            ["mode"] = "definition",
            ["file_path"] = "res://main.gd",
            ["line"] = 1,
            ["column"] = 0,
            ["instance"] = projA,
        });
        using (var payload = JsonDocument.Parse(TextOf(definition)))
        {
            var def = payload.RootElement.GetProperty("definition");
            Assert.Equal("res://main.gd", def.GetProperty("file_path").GetString());
            Assert.Equal(10, def.GetProperty("line").GetInt32());
            Assert.Equal(2, def.GetProperty("column").GetInt32());
        }

        // ⑤ 引用导航(另一实例)。
        var references = await client.CallToolAsync("lsp_navigate", new Dictionary<string, object?>
        {
            ["mode"] = "references",
            ["file_path"] = "res://main.gd",
            ["line"] = 1,
            ["column"] = 0,
            ["instance"] = projB,
        });
        using (var payload = JsonDocument.Parse(TextOf(references)))
        {
            Assert.Equal(2, payload.RootElement.GetProperty("count").GetInt32());
        }

        // ⑥ 符号树:嵌套 children + 类别标签 + 行号换算。
        var symbols = await client.CallToolAsync("lsp_symbols", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["instance"] = projA,
        });
        using (var payload = JsonDocument.Parse(TextOf(symbols)))
        {
            var first = payload.RootElement.GetProperty("symbols")[0];
            Assert.Equal("a", first.GetProperty("name").GetString());
            Assert.Equal("Method", first.GetProperty("kind").GetString());
            Assert.Equal(1, first.GetProperty("start_line").GetInt32());
            Assert.Equal("Variable", first.GetProperty("children")[0].GetProperty("kind").GetString());
        }
    }

    /// <summary>
    /// 输入校验与降级:着色器文件(.gdshader)短路——不触 LSP、直接附说明返回;.cs 拒绝并
    /// 指向 .NET 语言服务器;非 res:// 路径拒绝;项目级扫描把多文件结果聚合为计数与问题文件列表。
    /// <para>断言链:临时项目含 main.gd(默认诊断 1 错)与 other.gd(按后缀配置为干净)→
    /// lsp_diagnostics 请求 .gdshader,断言 note 含 "does not validate shader files" → 请求
    /// .cs,断言整体错误且 code 为 UNSUPPORTED_FILE_TYPE、error 提及 ".NET language server"
    /// → lsp_hover 传 C:/ 绝对路径,断言 code 为 INVALID_PATH → scope=project 扫描,断言
    /// scanned=2、clean=1、total_diagnostics=1、files_with_diagnostics[0].file_path 为
    /// res://main.gd。</para>
    /// </summary>
    [Fact]
    public async Task lsp_input_validation_shader_short_circuit_and_project_scan()
    {
        var stateDir = TestPaths.NewStateDir();
        var projA = CreateTempProject("lsp-scan",
            ("main.gd", "func a():\n\tpass\n"),
            ("other.gd", "func b():\n\tpass\n"));
        using var lspA = new FakeLspServer
        {
            DiagnosticsBySuffix = { ["other.gd"] = "[]" },
        };
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var godotA = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projA, StateDir = stateDir, LspPort = lspA.Port });
        await WaitConnectedAsync(client, projA);

        // 着色器短路:不触 LSP,附说明。
        var shader = await client.CallToolAsync("lsp_diagnostics", new Dictionary<string, object?>
        {
            ["file_path"] = "res://shader.gdshader",
            ["instance"] = projA,
        });
        Assert.True(shader.IsError is null or false);
        using (var payload = JsonDocument.Parse(TextOf(shader)))
        {
            Assert.Contains("does not validate shader files", payload.RootElement.GetProperty("note").GetString());
        }

        // .cs 拒绝(指向 IDE 的 .NET LSP)。
        var cs = await client.CallToolAsync("lsp_diagnostics", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.cs",
            ["instance"] = projA,
        });
        Assert.True(cs.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(cs)))
        {
            Assert.Equal("UNSUPPORTED_FILE_TYPE", payload.RootElement.GetProperty("code").GetString());
            Assert.Contains(".NET language server", payload.RootElement.GetProperty("error").GetString());
        }

        // 非 res:// 路径拒绝。
        var notRes = await client.CallToolAsync("lsp_hover", new Dictionary<string, object?>
        {
            ["file_path"] = "C:/x/main.gd",
            ["line"] = 0,
            ["column"] = 0,
            ["instance"] = projA,
        });
        Assert.True(notRes.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(notRes)))
        {
            Assert.Equal("INVALID_PATH", payload.RootElement.GetProperty("code").GetString());
        }

        // 项目级扫描:main.gd 带 1 个错误;other.gd 干净。
        var project = await client.CallToolAsync("lsp_diagnostics", new Dictionary<string, object?>
        {
            ["scope"] = "project",
            ["instance"] = projA,
        });
        Assert.True(
            project.IsError is null or false,
            TextOf(project) + "\n-- fake received --\n" + string.Join("\n", lspA.ReceivedLog) +
            "\n-- daemon stderr --\n" + daemon.StderrSnapshot());
        using (var payload = JsonDocument.Parse(TextOf(project)))
        {
            Assert.Equal(2, payload.RootElement.GetProperty("scanned").GetInt32());
            Assert.Equal(1, payload.RootElement.GetProperty("clean").GetInt32());
            Assert.Equal(1, payload.RootElement.GetProperty("total_diagnostics").GetInt32());
            var fileWith = payload.RootElement.GetProperty("files_with_diagnostics")[0];
            Assert.Equal("res://main.gd", fileWith.GetProperty("file_path").GetString());
        }
    }

    /// <summary>
    /// LSP 端点异常的报错/降级与 Node 桥同码同文:双实例声称同一存活端口报 LSP_PORT_CONFLICT;
    /// 无 lsp_port 且受保护的默认端口 6005 被占报 LSP_UNAVAILABLE(错误文本指明 port 6005);
    /// 连到无监听端口报 LSP_UNAVAILABLE 并带 "Could not reach" 提示。
    /// <para>断言链:A/B 两实例注册同一 FakeLspServer 端口并对 A 调 lsp_hover,断言 code 为
    /// LSP_PORT_CONFLICT → C 不带 lsp_port、E 占用 6005,对 C 调 lsp_hover,断言 code 为
    /// LSP_UNAVAILABLE 且 error 含 "port 6005" → D 注册无监听者的空闲端口(与 A/B 端口不同),
    /// 调 lsp_hover,断言 code 为 LSP_UNAVAILABLE 且 hint 含
    /// "Could not reach the GDScript LSP on port"。</para>
    /// </summary>
    [Fact]
    public async Task lsp_port_conflict_and_unavailable_match_node_semantics()
    {
        var stateDir = TestPaths.NewStateDir();
        var projA = CreateTempProject("lsp-ca", ("main.gd", "func a():\n\tpass\n"));
        var projB = CreateTempProject("lsp-cb", ("main.gd", "func b():\n\tpass\n"));
        var projC = CreateTempProject("lsp-cc", ("main.gd", "func c():\n\tpass\n"));
        var projE = CreateTempProject("lsp-ce", ("main.gd", "func e():\n\tpass\n"));
        using var lspShared = new FakeLspServer();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        // A 与 B 声称同一 LSP 端口(且都存活)→ 冲突。
        using var godotA = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projA, StateDir = stateDir, LspPort = lspShared.Port });
        using var godotB = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projB, StateDir = stateDir, LspPort = lspShared.Port });
        // C 无 lsp_port,且有存活实例 E 占 6005 默认位 → 受保护的 6005 拒用。
        using var godotC = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projC, StateDir = stateDir, LspPort = null });
        using var godotE = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = projE, StateDir = stateDir, LspPort = 6005 });
        await WaitConnectedAsync(client, projA);
        await WaitConnectedAsync(client, projB);
        await WaitConnectedAsync(client, projC);
        await WaitConnectedAsync(client, projE);

        var conflict = await client.CallToolAsync("lsp_hover", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["line"] = 0,
            ["column"] = 0,
            ["instance"] = projA,
        });
        Assert.True(conflict.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(conflict)))
        {
            Assert.Equal("LSP_PORT_CONFLICT", payload.RootElement.GetProperty("code").GetString());
        }

        var unavailable = await client.CallToolAsync("lsp_hover", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["line"] = 0,
            ["column"] = 0,
            ["instance"] = projC,
        });
        Assert.True(unavailable.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(unavailable)))
        {
            Assert.Equal("LSP_UNAVAILABLE", payload.RootElement.GetProperty("code").GetString());
            Assert.Contains("port 6005", payload.RootElement.GetProperty("error").GetString());
        }

        // 连接拒绝(nothing listening)→ LSP_UNAVAILABLE + Node 同文提示。
        var projD = CreateTempProject("lsp-cd", ("main.gd", "func d():\n\tpass\n"));
        using var godotD = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = projD,
            StateDir = stateDir,
            LspPort = TestPorts.GetFreePort(), // 无监听者的端口(注:与 A/B 端口不同)。
        });
        await WaitConnectedAsync(client, projD);
        var refused = await client.CallToolAsync("lsp_hover", new Dictionary<string, object?>
        {
            ["file_path"] = "res://main.gd",
            ["line"] = 0,
            ["column"] = 0,
            ["instance"] = projD,
        });
        Assert.True(refused.IsError == true);
        using (var payload = JsonDocument.Parse(TextOf(refused)))
        {
            Assert.Equal("LSP_UNAVAILABLE", payload.RootElement.GetProperty("code").GetString());
            Assert.Contains("Could not reach the GDScript LSP on port", payload.RootElement.GetProperty("hint").GetString());
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>在临时目录(guid 隔离)创建假项目根并写入给定文件;路径仅用于注册表寻址,内容不被读取。</summary>
    /// <param name="name">项目子目录名。</param>
    /// <param name="files">(文件名, 内容)列表。</param>
    /// <returns>项目根目录绝对路径。</returns>
    private static string CreateTempProject(string name, params (string File, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "godot-mcp-daemon-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(root);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(root, file), content);
        }
        return root;
    }

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
