using System.Diagnostics;
using System.Text.Json;
using GodotMcp.Daemon;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 17 真实环境验收(仅在 GODOT_MCP_REAL_ACCEPTANCE=1 时执行;默认跳过):
/// 真实 Godot 4.7.2 编辑器 + 真实 addon(注册表自注册),经 daemon 的 HTTP 面驱动——
/// ① 双 MCP 会话并发操作同一真实实例(addon 变更车道串行,真实编辑器场景树为真值);
/// ② 单会话交错操作两个真实实例(双编辑器,零串台);
/// ③ 全量工具面真机抽查(代表性组;全 31 组的逐组调用证据见 issue 13 的 65 行 fake 矩阵)。
/// 证据写入 references/test-artifacts/daemon-acceptance-*/(归档验收文档引用)。
/// </summary>
public class RealAcceptanceTests
{
    /// <summary>真机 Godot 编辑器可执行文件路径(本机固定安装位)。</summary>
    private const string GodotExe = @"C:\Soft\Godot\Godot.exe";
    /// <summary>主验收项目(test-project,含全部夹具场景)。</summary>
    private const string MainProject = @"D:\Projects\My\Godot\GodotMCP\test-project";
    /// <summary>验收证据归档目录(references/test-artifacts 下,供验收文档引用)。</summary>
    private const string EvidenceDir = @"D:\Projects\My\Godot\GodotMCP\references\test-artifacts\daemon-acceptance-20260914";
    /// <summary>第二实例项目(临时目录下自动生成的最小场景,独立注册表键)。</summary>
    private static readonly string SecondProject =
        Path.Combine(Path.GetTempPath(), "godot-mcp-real-acceptance", "second-project");

    /// <summary>环境开关:仅 GODOT_MCP_REAL_ACCEPTANCE=1 时执行真机测试,否则默认跳过。</summary>
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("GODOT_MCP_REAL_ACCEPTANCE") == "1";

    /// <summary>
    /// 真机三合一验收:全量组代表工具抽查 + 运行时通道往返 + 双会话并发同实例 + 单会话交错双实例;
    /// 环境开关缺失时直接 return(xUnit 记为通过,即默认跳过)。
    /// <para>断言链:Arrange —— Enabled 关卡(=1 才继续);不覆盖状态目录(真实 addon 写机器级注册表,
    /// daemon 须读同一份);PrepareSecondProject 备好第二项目;LaunchEditor 拉起真实编辑器 #1,client1
    /// 接入并经 WaitRealInstanceAsync 等注册表连接。Act/Assert —— ① 真机抽查:discover_tools 请求全部
    /// 31 组名激活按需面,随后 12 个代表性组工具逐个真机调用,任一失败即断言失败,结果逐行记入证据日志。
    /// ② 运行时通道:game_start → runtime_time_control(status) 无错,finally 中 game_stop 收尾。
    /// ③ 编辑器 #2 上线、client2 接入后:双会话并发 4 个 scene_create_node 全部成功(addon 变更车道
    /// 串行,无交错半写),scene_get_tree 真值含全部 4 个节点名(无丢失);单会话 3 轮交错对两实例各建
    /// 1 节点,两棵场景树各自只含自己的节点、绝无串台;证据写入 real-acceptance.log。finally 按序
    /// Kill(entireProcessTree)/Dispose 两个编辑器进程。</para>
    /// </summary>
    [Fact]
    public async Task real_dual_sessions_concurrent_same_instance_and_two_instance_interleave()
    {
        // 默认跳过(会拉起真实 Godot 编辑器并占用机器级注册表/6005 LSP);
        // 复现:GODOT_MCP_REAL_ACCEPTANCE=1 dotnet test --filter FullyQualifiedName~RealAcceptanceTests
        if (!Enabled)
        {
            return;
        }
        Directory.CreateDirectory(EvidenceDir);
        var evidence = new List<string>();
        void Log(string line)
        {
            evidence.Add(line);
            Console.WriteLine(line);
        }

        // 不覆盖状态目录:真实 addon 把注册表写到机器级 %APPDATA%\godot-mcp-toolkit,
        // 验收 daemon 必须读同一注册表(机器上此时无其他 daemon,单例锁归本进程)。
        var stateDir = DaemonOptions.FromEnvironment().StateDir;
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 600,
        });
        Log($"daemon: port={daemon.Port} state={stateDir}");

        PrepareSecondProject();

        // 真实编辑器 #1(test-project,含全部夹具)与 #2(second-project,最小场景)。
        // 编辑器 #2 指定独立 LSP 端口:两个编辑器都默认 6005 时,daemon 的存活佐证
        // 冲突检测会如实拒绝(正确的服务端行为,Node ADR 0008/0025)。
        var editor1 = LaunchEditor(MainProject);
        try
        {
            await using var client1 = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
            await WaitRealInstanceAsync(client1, MainProject, "编辑器#1");
            Log("editors: 编辑器#1 经注册表连接");

            // ── ③ 真机抽查先行(单实例在场,无 LSP 6005 竞争;代表性组,全 31 组逐组证据见
            // issue 13 fake 矩阵)。先激活全部 31 组(按需面,组工具才会出现在 tools/list)。──
            var activate = await client1.CallToolAsync("discover_tools", new Dictionary<string, object?>
            {
                ["request"] = GroupCatalogue.Groups.Select(g => g.Name).ToList(),
            });
            Assert.True(activate.IsError is null or false, $"全组激活失败:{TextOf(activate)}");
            var spot = new List<(string Group, string Tool, Dictionary<string, object?> Args)>
            {
                ("editor_advanced", "editor_sync", new Dictionary<string, object?> { ["instance"] = MainProject }),
                ("signals", "signal_list", new Dictionary<string, object?> { ["node_path"] = "/root", ["instance"] = MainProject }),
                ("classdb", "classdb_query", new Dictionary<string, object?> { ["mode"] = "search", ["pattern"] = "Node2D", ["limit"] = 5, ["instance"] = MainProject }),
                ("input_map", "input_map_edit", new Dictionary<string, object?> { ["action"] = "add_action", ["name"] = "acc_test_action", ["instance"] = MainProject }),
                ("layer_naming", "layer_names_get", new Dictionary<string, object?> { ["category"] = "2d_physics", ["instance"] = MainProject }),
                ("resource_io", "folder_create", new Dictionary<string, object?> { ["path"] = "res://acc_tmp_dir", ["instance"] = MainProject }),
                ("user_data", "save_write", new Dictionary<string, object?> { ["path"] = "user://acc_save.dat", ["content"] = "daemon-acceptance", ["instance"] = MainProject }),
                ("audio", "audiobus_list", new Dictionary<string, object?> { ["instance"] = MainProject }),
                ("3d_tools", "scene_create_3d", new Dictionary<string, object?> { ["kind"] = "primitive", ["parent_path"] = "/root/Main", ["primitive"] = "box", ["instance"] = MainProject }),
                ("scene_inheritance", "scene_create_inherited", new Dictionary<string, object?> { ["file_path"] = "res://acc_inherited.tscn", ["base_scene"] = "res://Main.tscn", ["instance"] = MainProject }),
                ("debugger", "debug_inspect", new Dictionary<string, object?> { ["mode"] = "breakpoints", ["instance"] = MainProject }),
                ("lsp_code_analysis", "lsp_diagnostics", new Dictionary<string, object?> { ["file_path"] = "res://runtime_counter.gd", ["instance"] = MainProject }),
            };
            var spotResults = new List<string>();
            foreach (var (group, tool, args) in spot)
            {
                var result = await client1.CallToolAsync(tool, args);
                var ok = result.IsError is null or false;
                spotResults.Add($"{group}|{tool}|{(ok ? "PASS" : $"FAIL {TextOf(result)}")}");
                Assert.True(ok, $"真机抽查 {group}/{tool} 失败:{TextOf(result)}");
            }
            Log("③ 真机抽查:");
            foreach (var line in spotResults)
            {
                Log("  " + line);
            }

            // 运行时通道(playtest):game_start → runtime_time_control → game_stop。
            var gameStart = await client1.CallToolAsync("game_start", new Dictionary<string, object?> { ["instance"] = MainProject });
            Assert.True(gameStart.IsError is null or false, $"game_start 失败:{TextOf(gameStart)}");
            try
            {
                var status = await client1.CallToolAsync("runtime_time_control", new Dictionary<string, object?>
                {
                    ["action"] = "status",
                    ["instance"] = MainProject,
                });
                Assert.True(status.IsError is null or false, $"runtime_time_control 失败:{TextOf(status)}");
                Log("③ 运行时通道:game_start → runtime_time_control(status) → 成功");
            }
            finally
            {
                await client1.CallToolAsync("game_stop", new Dictionary<string, object?> { ["instance"] = MainProject });
                Log("③ 运行时通道:game_stop");
            }

            // ── 编辑器 #2(second-project,最小场景)上线后做 ①②。──
            // LSP 注记:GDScript LSP 默认端口 6005 是机器级单例,双编辑器按 docs/multi-instance.md
            // 配方为 #2 错开:--lsp-port 6015(步进 10,避开 6006 的 DAP)+ GODOT_MCP_LSP_PORT=6015。
            // daemon 对 #2 的 LSP 抽查不依赖(抽查安排在 #2 上线前完成,按 #1 的 6005 执行)。
            var editor2 = LaunchEditor(SecondProject, 6015);
            try
            {
                await using var client2 = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
                await WaitRealInstanceAsync(client1, SecondProject, "编辑器#2");
                Log("editors: 编辑器#2 经注册表连接");

                // ── ① 双会话并发操作同一真实实例:4 个变更交错,全部成功且场景树全量在册。──
                var names1 = new[] { "acc_s1_a", "acc_s1_b" };
                var names2 = new[] { "acc_s2_a", "acc_s2_b" };
                var wave = new[]
                {
                    client1.CallToolAsync("scene_create_node", NodeArgs(MainProject, names1[0])),
                    client1.CallToolAsync("scene_create_node", NodeArgs(MainProject, names1[1])),
                    client2.CallToolAsync("scene_create_node", NodeArgs(MainProject, names2[0])),
                    client2.CallToolAsync("scene_create_node", NodeArgs(MainProject, names2[1])),
                };
                await Task.WhenAll(wave.Select(t => t.AsTask()));
                foreach (var (call, index) in wave.Select((c, i) => (c, i)))
                {
                    var result = await call;
                    Assert.True(result.IsError is null or false, $"并发变更 #{index} 失败:{TextOf(result)}");
                }
                Log("① 双会话并发同实例:4/4 变更成功(addon 变更车道串行,无交错半写)");

                // 真值:场景树包含全部 4 个节点(无半写、无丢失)。
                var tree = await client1.CallToolAsync("scene_get_tree", new Dictionary<string, object?> { ["instance"] = MainProject });
                var treeText = TextOf(tree);
                foreach (var name in names1.Concat(names2))
                {
                    Assert.Contains(name, treeText);
                }
                Log("① 场景树真值:4 节点全部在册");

                // ── ② 单会话交错操作两个真实实例:零串台。──
                for (var round = 0; round < 3; round++)
                {
                    var create1 = await client1.CallToolAsync("scene_create_node", NodeArgs(MainProject, $"acc_x1_r{round}"));
                    var create2 = await client1.CallToolAsync("scene_create_node", NodeArgs(SecondProject, $"acc_x2_r{round}"));
                    Assert.True(create1.IsError is null or false, $"交错 r{round} 实例1 失败:{TextOf(create1)}");
                    Assert.True(create2.IsError is null or false, $"交错 r{round} 实例2 失败:{TextOf(create2)}");
                }
                var tree1 = TextOf(await client1.CallToolAsync("scene_get_tree", new Dictionary<string, object?> { ["instance"] = MainProject }));
                var tree2 = TextOf(await client1.CallToolAsync("scene_get_tree", new Dictionary<string, object?> { ["instance"] = SecondProject }));
                Assert.Contains("acc_x1_r0", tree1);
                Assert.Contains("acc_x1_r2", tree1);
                Assert.DoesNotContain("acc_x2", tree1);
                Assert.Contains("acc_x2_r0", tree2);
                Assert.Contains("acc_x2_r2", tree2);
                Assert.DoesNotContain("acc_x1", tree2);
                Log("② 单会话双实例交错:6/6 成功,两实例场景树各自只含自己的节点(零串台)");

                File.WriteAllLines(Path.Combine(EvidenceDir, "real-acceptance.log"), evidence);
                Log($"证据已写入 {EvidenceDir}");
            }
            finally
            {
                editor2.Kill(entireProcessTree: true);
                editor2.Dispose();
            }
        }
        finally
        {
            if (!editor1.HasExited)
            {
                editor1.Kill(entireProcessTree: true);
            }
            editor1.Dispose();
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>构造 scene_create_node 的调用参数:在 /root/Main 下新建指定名称的 Node。</summary>
    /// <param name="project">目标项目路径(instance 寻址参数)。</param>
    /// <param name="nodeName">新建节点名称。</param>
    /// <returns>参数字典。</returns>
    private static Dictionary<string, object?> NodeArgs(string project, string nodeName) => new()
    {
        ["class_name"] = "Node",
        ["parent_path"] = "/root/Main",
        ["node_name"] = nodeName,
        ["instance"] = project,
    };

    /// <summary>以 --path/--editor 拉起真实 Godot 编辑器进程;可选按 docs/multi-instance.md 配方错开 LSP 端口。</summary>
    /// <param name="projectPath">编辑器打开的项目路径。</param>
    /// <param name="lspPort">可选 LSP 端口(双编辑器场景第二个必须错开默认 6005;避开 6006 —— DAP 默认端口)。</param>
    /// <returns>已启动的编辑器进程。</returns>
    private static Process LaunchEditor(string projectPath, int? lspPort = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GodotExe,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--editor");
        if (lspPort is { } lsp)
        {
            // docs/multi-instance.md 配方,两者都要:引擎在插件可观察之前就消费 --lsp-port
            // (空格分隔,Godot ≥ 4.2);工具包发布到注册表的 lsp_port 则以 GODOT_MCP_LSP_PORT 为准,
            // daemon 按项目从注册表发现各自端点(该 env 属编辑器子进程,daemon 单例无需感知)。
            startInfo.ArgumentList.Add("--lsp-port");
            startInfo.ArgumentList.Add(lsp.ToString());
            startInfo.Environment["GODOT_MCP_LSP_PORT"] = lsp.ToString();
        }
        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    /// <summary>
    /// 最小第二实例项目:插件目录复制(避免 shell 拼接)+ 无 C# 特性的最小场景(独立注册表键)。
    /// </summary>
    private static void PrepareSecondProject()
    {
        Directory.CreateDirectory(SecondProject);
        Directory.CreateDirectory(Path.Combine(SecondProject, "addons"));
        var addonTarget = Path.Combine(SecondProject, "addons", "godot_mcp_toolkit");
        if (!Directory.Exists(addonTarget))
        {
            var pluginAddon = Path.GetFullPath(Path.Combine(
                MainProject, "..", "plugin", "godot-mcp-unified", "addons", "godot_mcp_toolkit"));
            foreach (var sourceFile in Directory.EnumerateFiles(pluginAddon, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(pluginAddon, sourceFile);
                var destination = Path.Combine(addonTarget, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceFile, destination, overwrite: true);
            }
        }
        var projectGodot = Path.Combine(SecondProject, "project.godot");
        if (!File.Exists(projectGodot))
        {
            File.WriteAllText(projectGodot,
                "; daemon 验收第二实例(自动生成)\n\nconfig_version=5\n\n[application]\n\n" +
                "config/name=\"Daemon Acceptance Second\"\nrun/main_scene=\"res://Main.tscn\"\n" +
                "config/features=PackedStringArray(\"4.7\", \"GL Compatibility\")\n\n" +
                "[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/godot_mcp_toolkit/plugin.cfg\")\n\n" +
                "[rendering]\n\nrenderer/rendering_method=\"gl_compatibility\"\n");
        }
        var mainTscn = Path.Combine(SecondProject, "Main.tscn");
        if (!File.Exists(mainTscn))
        {
            File.WriteAllText(mainTscn,
                "[gd_scene format=3]\n\n[node name=\"Main\" type=\"Node\"]\n\n" +
                "[node name=\"Child\" type=\"Node\" parent=\".\"]\n");
        }
    }

    /// <summary>轮询 list_instances 直到真实实例(规范化路径)显示 connected;120s 超时(真实编辑器启动慢)。</summary>
    /// <param name="client">MCP 会话客户端。</param>
    /// <param name="projectPath">目标项目路径。</param>
    /// <param name="label">超时消息里的人类可读标签(如 编辑器#1)。</param>
    /// <returns>实例连接就绪后完成的任务。</returns>
    private static async Task WaitRealInstanceAsync(McpClient client, string projectPath, string label)
    {
        var canonical = JsonMatch.CanonicalProjectKey(projectPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
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
            await Task.Delay(500);
        }
        throw new TimeoutException($"真实实例 {label}({canonical})未在 120s 内连接");
    }

    /// <summary>取工具结果唯一文本内容块的文本(顺带断言内容块恰有一个)。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>文本内容块承载的字符串。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }
}
