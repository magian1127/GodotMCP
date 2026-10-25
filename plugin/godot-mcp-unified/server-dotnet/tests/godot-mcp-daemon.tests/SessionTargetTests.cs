using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 按会话键的默认目标寻址(ADR-0008 / spec §8)。
/// </summary>
/// <remarks>
/// <para>需求来源:同一台机器常同时开多个 Godot 项目,而一个 MCP 会话通常只服务其中一个。
/// 没有这一层时,模型刚 connect 过的项目反而会让不带 <c>instance</c> 的调用报
/// <c>AMBIGUOUS_INSTANCE</c> —— 它当然知道自己在哪个项目上,这个错误是纯噪声。</para>
/// <para>优先级:<b>显式 instance &gt; 本键默认目标 &gt; 唯一活跃实例 &gt; 报错列清单</b>。
/// 后两级是 ADR-0003 的既有行为,由 <c>InstanceRoutingTests</c> 守护。</para>
/// <para>接线位置很关键:这一层必须作用在 <c>InstanceManager.ResolveConnection</c> 上,
/// 而不是某个调用器上 —— core 常驻工具走 <c>ToolRouting</c>、组工具走
/// <c>GroupToolInvoker</c>,只接一处会得到"有的工具能用、有的不能"这种极难归因的现场。</para>
/// </remarks>
public sealed class SessionTargetTests
{
    [Fact]
    public async Task explicit_instance_wins_over_the_default_target()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        using var alpha = StartFake(stateDir, @"D:\proj\alpha", "alpha");
        await using var session = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);
        await WaitConnectedAsync(session.Client, @"D:\proj\alpha");
        await session.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\alpha");

        // 显式传另一个未运行项目的路径:必须**优先于**默认目标,并因此报 INSTANCE_NOT_FOUND,
        // 而不是静默回落到 alpha(静默回落会让"我要操作 beta"变成"改了 alpha",是数据事故)。
        var result = await session.Client.CallToolAsync(
            "editor_sync", new Dictionary<string, object?> { ["instance"] = @"D:\proj\nowhere" });

        Assert.True(result.IsError == true, "显式指定未运行的项目应报错而不是回落默认目标");
        Assert.Contains("INSTANCE_NOT_FOUND", TextOf(result));
    }

    [Fact]
    public async Task default_target_that_is_not_running_reports_target_not_running()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });

        // connect 到一个没有实例的项目:默认目标写进键,但此刻没在线。
        await using var session = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);
        await session.ConnectGodotAsync(groups: ["editor_ops"], project: @"D:\proj\ghost");

        var result = await session.Client.CallToolAsync("editor_sync", new Dictionary<string, object?>());

        Assert.True(result.IsError == true);
        var text = TextOf(result);

        // 必须是 TARGET_NOT_RUNNING(可行动:去启动编辑器),而不是 INSTANCE_NOT_FOUND
        // (后者会让模型以为 instance 参数写错了,于是反复改参数却永远连不上)。
        Assert.Contains("TARGET_NOT_RUNNING", text);
        Assert.Contains("godot --headless --editor --path", text);
    }

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>起一个假 Godot 实例,声明 editor_sync 需要的两个方法。</summary>
    /// <param name="stateDir">状态目录(亦注册表目录)。</param>
    /// <param name="projectPath">项目路径。</param>
    /// <param name="marker">回显进响应的项目标记。</param>
    /// <returns>假实例句柄。</returns>
    private static FakeGodotInstance StartFake(string stateDir, string projectPath, string marker)
    {
        return FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = projectPath,
            StateDir = stateDir,
            Scripts =
            {
                new FakeScript
                {
                    Method = "editor.refresh",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = Marker(marker) } },
                },
                new FakeScript
                {
                    Method = "editor.wait_for_idle",
                    Reusable = true,
                    Frames = { new FakeFrame { Json = Marker(marker) } },
                },
            },
        });
    }

    /// <summary>构造带项目标记的成功响应帧。</summary>
    /// <param name="marker">项目标记。</param>
    /// <returns>响应帧字符串。</returns>
    private static string Marker(string marker) =>
        $"{{\"success\":true,\"project\":\"{marker}\"}}";

    /// <summary>等某个项目被 daemon 认成已连接实例。</summary>
    /// <param name="client">测试客户端。</param>
    /// <param name="projectPath">项目路径。</param>
    /// <returns>连接出现即返回。</returns>
    private static async Task WaitConnectedAsync(McpClient client, string projectPath)
    {
        var canonical = projectPath.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            using var payload = JsonDocument.Parse(text);
            if (payload.RootElement.TryGetProperty("instances", out var rows))
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.TryGetProperty("path", out var path)
                        && string.Equals(path.GetString(), canonical, StringComparison.Ordinal)
                        && row.TryGetProperty("connected", out var connected)
                        && connected.GetBoolean())
                    {
                        return;
                    }
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"实例 {canonical} 未在 20s 内连接");
    }
}
