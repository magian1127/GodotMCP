using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// <c>state/sessions.json</c> 与 <c>--status</c> 的守护（spec §1.4 / §10）。
/// </summary>
/// <remarks>
/// <para><b>为什么这条必须有</b>：这两个东西是"工具突然只剩 <c>godot_connect</c>"时
/// 唯一能回答"现在有哪些会话、各自什么状态、连的哪个项目、哪个版本的 shim"的面。
/// 它们一旦悄悄失效（字段改名、服务没注册、写盘路径算错），平时完全无感 ——
/// 直到真的需要排查那一刻才发现没有数据。</para>
///
/// <para><b>为什么断言短键而不只是"文件存在"</b>：完整 UUID 是会话凭据的一部分，
/// 带上它就能冒充该会话。文件里只允许出现前 8 位 —— 这条也一并守住。</para>
/// </remarks>
public sealed class SessionSnapshotFileTests
{
    [Fact]
    public async Task snapshot_file_reports_live_sessions_without_leaking_full_keys()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 120,
        });

        await using var session = await SessionConnector.ConnectKeyedAsync(daemon.Port, daemon.Token);

        var connect = await session.ConnectGodotAsync(
            groups: ["editor_ops"],
            project: @"D:\proj\snapshot");

        // connect 响应给出短键 —— 它就是 --status 里用来对号的那个。
        var shortKey = ShortKeyOf(connect);
        var snapshotPath = Path.Combine(stateDir, "sessions.json");

        // 会话变化会**立即**触发落盘(KeyRegistry.Changed → WriteOnce),所以正常情况
        // 毫秒级就有文件。10s 只是并行满载时的安全余量,不是在等定时器周期。
        var payload = await WaitForSnapshotAsync(snapshotPath, shortKey, TimeSpan.FromSeconds(10));

        Assert.True(payload.TryGetProperty("policy", out _), "快照必须带生效策略,否则排查时不知道当时用的是哪套 TTL");

        var session0 = payload.GetProperty("sessions").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == shortKey);

        Assert.True(session0.GetProperty("connected").GetBoolean(), "已 connect 的会话在快照里必须是 connected");
        Assert.Equal("IdleNoStream", session0.GetProperty("state").GetString());
        Assert.Contains("editor_ops", session0.GetProperty("groups").EnumerateArray().Select(g => g.GetString()));

        // defaultTarget 与 connect 响应的 is_default **同源**:没有实例在跑时解析不出项目,
        // is_default=false 且快照里就该是 null。硬编码期望值会把"目标解析失败"错判成缺陷
        // ——这里要守的是"快照忠实反映注册表",不是"connect 一定成功设上目标"。
        var isDefault = IsDefaultOf(connect);
        Assert.Equal(
            isDefault,
            session0.GetProperty("defaultTarget").ValueKind == JsonValueKind.String);

        // 认证明文绝不落盘:文件里不应出现任何 36 字符的 UUID。
        var raw = await File.ReadAllTextAsync(snapshotPath);
        Assert.DoesNotMatch("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", raw);
    }

    [Fact]
    public void status_command_succeeds_against_a_live_daemon()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 120,
        });

        // --status 是只读诊断,靠读状态目录工作 —— 它不该因为"daemon 正持有单例锁"而失败。
        var result = DaemonProcess.RunStatus(stateDir, daemon.Port);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("godot-mcp-daemon 状态", result.Output);
        Assert.Contains("已被持有（daemon 正在运行）", result.Output);
        Assert.Contains("活动会话", result.Output);
    }

    [Fact]
    public void status_command_reports_a_missing_state_directory_without_crashing()
    {
        // 状态目录根本不存在(daemon 从未启动)时,给可行动的说明而不是堆栈。
        var stateDir = Path.Combine(Path.GetTempPath(), $"gmc-absent-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(stateDir));

        var result = DaemonProcess.RunStatus(stateDir, 6590);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("状态目录不存在", result.Output);
    }

    /// <summary>从 connect 响应里取出 is_default。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>是否已设置本会话默认目标。</returns>
    private static bool IsDefaultOf(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(
            Assert.Single(result.Content)).Text;
        using var payload = JsonDocument.Parse(text);
        return payload.RootElement.TryGetProperty("is_default", out var flag) && flag.GetBoolean();
    }

    /// <summary>从 connect 响应里取出短键。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>短键。</returns>
    private static string ShortKeyOf(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(
            Assert.Single(result.Content)).Text;
        using var payload = JsonDocument.Parse(text);
        return payload.RootElement.GetProperty("session").GetString()!;
    }

    /// <summary>等快照文件里出现指定短键的会话。</summary>
    /// <param name="path">快照路径。</param>
    /// <param name="shortKey">期望出现的短键。</param>
    /// <param name="timeout">最长等待。</param>
    /// <returns>解析后的快照根元素。</returns>
    private static async Task<JsonElement> WaitForSnapshotAsync(string path, string shortKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var raw = await File.ReadAllTextAsync(path);
                try
                {
                    var document = JsonDocument.Parse(raw);
                    if (document.RootElement.TryGetProperty("sessions", out var sessions)
                        && sessions.EnumerateArray().Any(s => s.GetProperty("key").GetString() == shortKey))
                    {
                        // 克隆一份脱离 document 生命周期(调用方用完即弃,不 Dispose)。
                        return document.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    // 原子替换窗口内的半文件不该出现(先写 .tmp 再 Move),出现就再等一轮。
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"sessions.json 未在 {timeout.TotalSeconds}s 内出现短键 {shortKey}");
    }
}
