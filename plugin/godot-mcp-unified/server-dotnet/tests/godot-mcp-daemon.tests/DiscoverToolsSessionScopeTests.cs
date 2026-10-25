using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// <c>discover_tools</c> 的激活/停用/目录/模糊匹配语义,以及"激活状态<b>按会话键隔离</b>"
/// (ADR-0007 推翻了旧版"daemon 全局"语义)。
/// </summary>
/// <remarks>
/// 旧测试 <c>activation_is_daemon_global_across_sessions_and_reset_restores_surface</c>
/// 断言的正是被推翻的那条:会话 A 激活后会话 B 立即可见。现在要求相反 —— A 激活只影响 A。
/// 本文件把那条测试改写为"按键隔离",其余断言(目录、模糊匹配、reset 两种形态)保留。
/// </remarks>
public sealed class DiscoverToolsSessionScopeTests
{
    private static readonly string[] CleanupMethods = ["scene.close"];

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task activation_is_scoped_to_session_key_and_reset_restores_surface()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        // 两个独立会话键。用 autoConnect + 空组起步,避免默认激活全部组掩盖差异。
        await using var sessionA = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await using var sessionB = await SessionConnector.ConnectKeyedAsync(
            daemon.Port, daemon.Token, autoConnect: false);
        await sessionA.ConnectGodotAsync(project: @"D:\proj\beta");
        await sessionB.ConnectGodotAsync(project: @"D:\proj\beta");

        // cleanup 组里的 scene_close 有 godotMinVersion 4.5 门控:没有活跃实例时会被保守隐藏。
        // 所以这里必须起一个 4.7 的替身,否则测的就不是"激活语义"而是"版本门控"。
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\beta",
            StateDir = stateDir,
            Scripts =
            [
                new FakeScript
                {
                    Method = "scene.close",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = "{\"success\":true}" } },
                },
            ],
        });

        // ① A 激活 cleanup —— **只影响 A**;B 仍看不到 scene_close。
        var activateA = await sessionA.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "cleanup",
        });
        Assert.True(activateA.IsError is null or false, TextOf(activateA));

        Assert.Contains(await sessionA.Client.ListToolsAsync(), t => t.Name == "scene_close");
        Assert.DoesNotContain(await sessionB.Client.ListToolsAsync(), t => t.Name == "scene_close");

        // ② A 的目录里 cleanup 是 already_loaded;B 的目录里仍是 available。
        using (var payloadA = JsonDocument.Parse(TextOf(
                   await sessionA.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>()))))
        {
            var groups = payloadA.RootElement.GetProperty("groups");
            Assert.Equal(17, groups.GetArrayLength());
            var cleanup = groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "cleanup");
            Assert.Equal("already_loaded", cleanup.GetProperty("status").GetString());
        }
        using (var payloadB = JsonDocument.Parse(TextOf(
                   await sessionB.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>()))))
        {
            var cleanup = payloadB.RootElement.GetProperty("groups").EnumerateArray()
                .Single(g => g.GetProperty("name").GetString() == "cleanup");
            Assert.Equal("available", cleanup.GetProperty("status").GetString());
        }

        // ③ 模糊匹配:关键词 "delete" 命中 cleanup,match 标记为 loose_keyword。
        var fuzzy = await sessionB.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "delete",
        });
        using (var payload = JsonDocument.Parse(TextOf(fuzzy)))
        {
            var cleanup = payload.RootElement.GetProperty("groups").EnumerateArray()
                .First(g => g.GetProperty("name").GetString() == "cleanup");
            Assert.Equal("loose_keyword", cleanup.GetProperty("match").GetString());
        }

        // ④ A reset:true → A 的工具面回落;B 本来就看不到 scene_close,不受影响。
        var reset = await sessionA.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["reset"] = true,
        });
        using (var payload = JsonDocument.Parse(TextOf(reset)))
        {
            Assert.True(payload.RootElement.GetProperty("reset_all").GetBoolean());
            Assert.Contains("cleanup", payload.RootElement.GetProperty("deactivated").EnumerateArray()
                .Select(e => e.GetString()).ToList());
        }
        Assert.DoesNotContain(await sessionA.Client.ListToolsAsync(), t => t.Name == "scene_close");

        // ⑤ 数组形态 reset:先重新激活,再仅停用 cleanup;响应无 reset_all 键。
        await sessionA.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "cleanup",
        });
        var resetOne = await sessionA.Client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["reset"] = new List<string> { "cleanup" },
        });
        using (var payload = JsonDocument.Parse(TextOf(resetOne)))
        {
            Assert.False(payload.RootElement.TryGetProperty("reset_all", out _));
            var deactivated = payload.RootElement.GetProperty("deactivated").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Equal(["cleanup"], deactivated);
        }
    }
}
