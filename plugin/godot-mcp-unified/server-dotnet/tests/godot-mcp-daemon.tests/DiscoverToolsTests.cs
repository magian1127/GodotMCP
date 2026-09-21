using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 12 验收:discover_tools 激活框架——① 激活后 host 经 subscriptions/listen 长流
/// 收到恰好一次 tools/list_changed(2026-07-28 修订下无状态 HTTP 的唯一投递通道)且新工具可调用;
/// ② 未激活的组工具不出现在 tools/list;③ cleanup 组全部工具对 fake 实例通过;
/// ④ 激活状态 daemon 全局(跨会话一致,无隐藏状态);⑤ 目录/模糊匹配/重置/reset 语义。
/// </summary>
public class DiscoverToolsTests
{
    /// <summary>cleanup 组覆盖的三个 wire 方法(scene.close/script.delete/file.delete),替身逐一声明回声帧。</summary>
    private static readonly string[] CleanupMethods = ["scene.close", "script.delete", "file.delete"];

    /// <summary>
    /// 激活 cleanup 组:listen 长流收到恰好一次 tools/list_changed(带订阅 id 标注),组工具上线可调用,
    /// 幂等重激活不重复推送。
    /// <para>断言链:Arrange —— daemon+会话;fake(alpha)声明 cleanup 三方法回声帧;激活前 tools/list
    /// 不含 scene_close/project_delete 而含 discover_tools;ListenStream.OpenAsync 打开长流并确认 ack 中
    /// toolsListChanged=true。Act —— request=["cleanup"] 按精确名称激活。Assert —— ① 响应 success=true,
    /// 组行 name="cleanup"、status="activated"、match="exact_name"、description 为既定中文文案,tools
    /// 恰为 [project_delete, scene_close] 有序;② WaitForListChangedAsync(1) 恰好一次通知,且 _meta 订阅
    /// id="listen-1"(SEP-2575 共享通道多路分解标注);③ tools/list 出现两工具;④ scene_close 调用无错,
    /// params_seen.file_path 回显;⑤ project_delete(auto)转发为 script.delete,wire 参数重写为
    /// file_path;⑥ dry_run=true 只返回计划(dry_run=true、kind="folder"、recursive=true,note 含
    /// "safety checks");⑦ 幂等重激活后等待 750ms,listen.ListChanged 仍恰一条(仅真实变化推送,
    /// 批语义 = 每批至多一次)。</para>
    /// </summary>
    [Fact]
    public async Task discover_activates_cleanup_group_with_one_list_changed_and_tools_are_callable()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = stateDir,
            Scripts = CleanupMethods.Select(m => new FakeScript
            {
                Method = m,
                Reusable = true,
                Frames = { new FakeFrame { Json = EchoParamsFrame() } },
            }).ToList(),
        });
        await WaitConnectedAsync(client, @"D:\proj\alpha");

        // 未激活:组工具不出现在 tools/list。
        var before = await client.ListToolsAsync();
        Assert.DoesNotContain(before, t => t.Name is "scene_close" or "project_delete");
        Assert.Contains(before, t => t.Name == "discover_tools");

        // listen 长流:无状态 HTTP 下 daemon 自持该流(SDK 无状态内建 listen 按设计不授予通知)。
        await using var listen = await ListenStream.OpenAsync(daemon.Port, daemon.Token);
        var ack = await listen.WaitForAckAsync(TimeSpan.FromSeconds(10));
        Assert.True(ack["params"]!["notifications"]!["toolsListChanged"]!.GetValue<bool>());

        // 激活 cleanup(精确名称)。
        var discover = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = new List<string> { "cleanup" },
        });
        Assert.True(discover.IsError is null or false, TextOf(discover));
        using (var payload = JsonDocument.Parse(TextOf(discover)))
        {
            Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
            var group = payload.RootElement.GetProperty("groups")[0];
            Assert.Equal("cleanup", group.GetProperty("name").GetString());
            Assert.Equal("activated", group.GetProperty("status").GetString());
            Assert.Equal("exact_name", group.GetProperty("match").GetString());
            Assert.Equal("安全删除项目路径，或关闭已打开的场景标签页。", group.GetProperty("description").GetString());
            var toolNames = group.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!).ToList();
            Assert.Equal(new List<string> { "project_delete", "scene_close" }, toolNames);
        }

        // 恰好一次 tools/list_changed(Node batchToolRegistration 同纪律),
        // 且按 SEP-2575 标注订阅 id(共享通道多路分解)。
        var changed = await listen.WaitForListChangedAsync(1, TimeSpan.FromSeconds(10));
        Assert.Equal("listen-1",
            changed["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>());

        // 激活后:tools/list 出现,且可调用。
        var after = await client.ListToolsAsync();
        Assert.Contains(after, t => t.Name == "scene_close");
        Assert.Contains(after, t => t.Name == "project_delete");

        var close = await client.CallToolAsync("scene_close", new Dictionary<string, object?>
        {
            ["file_path"] = "res://Main.tscn",
            ["instance"] = @"D:\proj\alpha",
        });
        Assert.True(close.IsError is null or false, TextOf(close));
        using (var payload = JsonDocument.Parse(TextOf(close)))
        {
            Assert.Equal("res://Main.tscn", payload.RootElement.GetProperty("params_seen").GetProperty("file_path").GetString());
        }

        // project_delete:auto → script.delete,wire 参数为 {file_path}。
        var deleteScript = await client.CallToolAsync("project_delete", new Dictionary<string, object?>
        {
            ["path"] = "res://scripts/x.gd",
            ["instance"] = @"D:\proj\alpha",
        });
        Assert.True(deleteScript.IsError is null or false, TextOf(deleteScript));
        using (var payload = JsonDocument.Parse(TextOf(deleteScript)))
        {
            Assert.Equal("res://scripts/x.gd", payload.RootElement.GetProperty("params_seen").GetProperty("file_path").GetString());
        }

        // project_delete:dry_run 只返回计划(folder + recursive)。
        var dryRun = await client.CallToolAsync("project_delete", new Dictionary<string, object?>
        {
            ["path"] = "res://tmp_cleanup_dir",
            ["kind"] = "folder",
            ["recursive"] = true,
            ["dry_run"] = true,
            ["instance"] = @"D:\proj\alpha",
        });
        Assert.True(dryRun.IsError is null or false, TextOf(dryRun));
        using (var payload = JsonDocument.Parse(TextOf(dryRun)))
        {
            Assert.True(payload.RootElement.GetProperty("dry_run").GetBoolean());
            Assert.Equal("folder", payload.RootElement.GetProperty("kind").GetString());
            Assert.True(payload.RootElement.GetProperty("recursive").GetBoolean());
            Assert.Contains("safety checks", payload.RootElement.GetProperty("note").GetString());
        }

        // 幂等重激活不产生第二条通知(仅真实变化推送;批语义 = 每批至多一次)。
        var reActivate = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = new List<string> { "cleanup" },
        });
        Assert.True(reActivate.IsError is null or false, TextOf(reActivate));
        await Task.Delay(750);
        Assert.Single(listen.ListChanged);
    }

    /// <summary>
    /// 激活状态 daemon 全局:会话 A 激活后会话 B 立即可见(跨会话一致、无隐藏状态);目录/模糊匹配/
    /// refresh_extensions 空摘要;reset 全量与数组形态各自回落工具面。
    /// <para>断言链:Arrange —— daemon+会话 A/B+fake(beta)声明 cleanup 三方法。Act/Assert ——
    /// ① A 激活 cleanup 后 B 的 tools/list 立刻含 scene_close。② 无参目录恰 31 组,cleanup 行
    /// status="already_loaded";③ request="delete" 模糊命中 cleanup,match="loose_keyword"。
    /// ④ refresh_extensions 对无扩展脚本的替身不算错误,结算为空摘要(registered/deferred/commands
    /// 全 0)。⑤ A reset:true:reset_all=true、deactivated 含 cleanup、deactivated_tools 含
    /// scene_close;B 的 tools/list 不再含 scene_close,再调用抛异常(工具面已回落)。
    /// ⑥ 数组形态 reset=["cleanup"](先重新激活):响应无 reset_all 键,deactivated 恰为 [cleanup]。</para>
    /// </summary>
    [Fact]
    public async Task activation_is_daemon_global_across_sessions_and_reset_restores_surface()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var sessionA = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        await using var sessionB = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\beta",
            StateDir = stateDir,
            Scripts = CleanupMethods.Select(m => new FakeScript
            {
                Method = m,
                Reusable = true,
                Frames = { new FakeFrame { Json = EchoParamsFrame() } },
            }).ToList(),
        });
        await WaitConnectedAsync(sessionA, @"D:\proj\beta");

        // 会话 A 激活 → 会话 B 立刻可见(daemon 全局,无隐藏状态)。
        var activateA = await sessionA.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "cleanup",
        });
        Assert.True(activateA.IsError is null or false, TextOf(activateA));
        var toolsB = await sessionB.ListToolsAsync();
        Assert.Contains(toolsB, t => t.Name == "scene_close");

        // 完整目录:31 个组;cleanup 已加载;模糊关键词 "delete" 命中 cleanup。
        var catalog = await sessionB.CallToolAsync("discover_tools", new Dictionary<string, object?>());
        using (var payload = JsonDocument.Parse(TextOf(catalog)))
        {
            var groups = payload.RootElement.GetProperty("groups");
            Assert.Equal(31, groups.GetArrayLength());
            var cleanup = groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "cleanup");
            Assert.Equal("already_loaded", cleanup.GetProperty("status").GetString());
        }
        var fuzzy = await sessionB.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "delete",
        });
        using (var payload = JsonDocument.Parse(TextOf(fuzzy)))
        {
            var cleanup = payload.RootElement.GetProperty("groups").EnumerateArray()
                .First(g => g.GetProperty("name").GetString() == "cleanup");
            Assert.Equal("loose_keyword", cleanup.GetProperty("match").GetString());
        }

        // refresh_extensions:对已连接实例拉取扩展(issue 14 起已接入;
        // 该替身无扩展脚本 —— 拉取失败不算错误,结算为空摘要)。
        var refresh = await sessionB.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["refresh_extensions"] = true,
        });
        Assert.True(refresh.IsError is null or false, TextOf(refresh));
        using (var payload = JsonDocument.Parse(TextOf(refresh)))
        {
            Assert.True(payload.RootElement.GetProperty("extensions_refreshed").GetBoolean());
            var summary = payload.RootElement.GetProperty("extension_refresh");
            Assert.Equal(0, summary.GetProperty("registered").GetInt32());
            Assert.Equal(0, summary.GetProperty("deferred").GetInt32());
            Assert.Equal(0, summary.GetProperty("commands").GetInt32());
        }

        // reset:true → 全部停用;工具面回落;组工具不可再调用。
        var reset = await sessionA.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["reset"] = true,
        });
        using (var payload = JsonDocument.Parse(TextOf(reset)))
        {
            Assert.True(payload.RootElement.GetProperty("reset_all").GetBoolean());
            var deactivated = payload.RootElement.GetProperty("deactivated").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Contains("cleanup", deactivated);
            Assert.Contains("scene_close", payload.RootElement.GetProperty("deactivated_tools").EnumerateArray()
                .Select(e => e.GetString()).ToList());
        }
        var afterReset = await sessionB.ListToolsAsync();
        Assert.DoesNotContain(afterReset, t => t.Name == "scene_close");
        await Assert.ThrowsAnyAsync<Exception>(() => sessionB.CallToolAsync(
            "scene_close", new Dictionary<string, object?> { ["file_path"] = "res://Main.tscn" }).AsTask());

        // 数组形态 reset:重新激活后仅停用指定组,无 reset_all。
        await sessionA.CallToolAsync("discover_tools", new Dictionary<string, object?> { ["request"] = "cleanup" });
        var resetOne = await sessionA.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["reset"] = new List<string> { "cleanup" },
        });
        using (var payload = JsonDocument.Parse(TextOf(resetOne)))
        {
            Assert.False(payload.RootElement.TryGetProperty("reset_all", out _));
            var deactivated = payload.RootElement.GetProperty("deactivated").EnumerateArray()
                .Select(e => e.GetString()!).ToList();
            Assert.Equal(new List<string> { "cleanup" }, deactivated);
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>回声帧:success=true 且 params_seen 原样回显 wire 参数($params 占位)。</summary>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string EchoParamsFrame()
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"params_seen\":\"$params\"}}";
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
}
