using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

public sealed class ShimLegacyNotificationsTests
{
    [Fact]
    public async Task legacy_host_gets_one_initial_tool_then_notifications_for_expansion()
    {
        using var shim = ShimProcess.Start(new ShimSpawnOptions
        {
            Port = TestPorts.GetFreePort(),
            StateDir = TestPaths.NewStateDir(),
            IdleSeconds = 30,
        });
        using var init = JsonDocument.Parse(await shim.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"legacy-face-test","version":"1"}}}""",
            TimeSpan.FromSeconds(45)));
        Assert.True(init.RootElement.GetProperty("result").GetProperty("capabilities")
            .GetProperty("tools").GetProperty("listChanged").GetBoolean());

        using var initial = JsonDocument.Parse(await shim.SendAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            TimeSpan.FromSeconds(15)));
        Assert.Single(initial.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());

        var project = Path.Combine(Path.GetTempPath(), "godot-legacy-face");
        await AssertCallAndNotification(shim, 3, "godot_connect",
            JsonSerializer.Serialize(new { project, groups = Array.Empty<string>() }));
        await AssertCount(shim, 4, 9);
        await AssertCallAndNotification(shim, 5, "discover_tools", """{"request":"editor_ops"}""");
        await AssertCount(shim, 6, 12);
    }

    [Fact]
    public async Task legacy_notification_subscription_recovers_after_daemon_restart()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();
        var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir, Port = port, IdleSeconds = 120,
        });
        using var shim = ShimProcess.Start(new ShimSpawnOptions
        {
            Port = port, StateDir = stateDir, IdleSeconds = 30,
        });
        try
        {
            _ = await shim.SendAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"legacy-restart-test","version":"1"}}}""",
                TimeSpan.FromSeconds(30));
            await AssertCallAndNotification(shim, 2, "godot_connect",
                JsonSerializer.Serialize(new { project = stateDir, groups = new[] { "editor_ops" } }));
        }
        finally
        {
            daemon.Dispose();
        }

        using var changed = JsonDocument.Parse(await shim.ReadLineAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("notifications/tools/list_changed", changed.RootElement.GetProperty("method").GetString());
        await AssertCount(shim, 3, 12);
    }

    private static async Task AssertCallAndNotification(ShimProcess shim, int id, string name, string arguments)
    {
        await shim.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\""
            + name + "\",\"arguments\":" + arguments + "}}");
        var responseSeen = false;
        var changedSeen = false;
        for (var i = 0; i < 2; i++)
        {
            using var item = JsonDocument.Parse(await shim.ReadLineAsync(TimeSpan.FromSeconds(15)));
            var root = item.RootElement;
            if (root.TryGetProperty("id", out var responseId))
            {
                Assert.Equal(id, responseId.GetInt32());
                Assert.True(root.TryGetProperty("result", out _));
                responseSeen = true;
            }
            else
            {
                Assert.Equal("notifications/tools/list_changed", root.GetProperty("method").GetString());
                Assert.False(root.TryGetProperty("params", out var parameters)
                    && parameters.TryGetProperty("_meta", out _));
                changedSeen = true;
            }
        }
        Assert.True(responseSeen && changedSeen);
    }

    private static async Task AssertCount(ShimProcess shim, int id, int count)
    {
        using var item = JsonDocument.Parse(await shim.SendAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/list\",\"params\":{}}",
            TimeSpan.FromSeconds(15)));
        Assert.Equal(count, item.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }
}
