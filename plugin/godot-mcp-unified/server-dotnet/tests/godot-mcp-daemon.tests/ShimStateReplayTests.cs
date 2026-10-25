using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// daemon 重启后，shim 重放会话状态（spec §1.7）。
/// </summary>
/// <remarks>
/// <para><b>这条测试守的是"用户不会静默丢状态"</b>。会话键活在 daemon 内存里：
/// daemon 一重启，键表清空，连接状态与 <c>discover_tools</c> 激活的组全部消失。
/// 模型下一轮 <c>tools/list</c> 只会看到 core 9 —— 它会以为"工具变少了"，
/// 而没有任何线索指向"daemon 重启过"。</para>
///
/// <para>补偿手段是 shim 缓存最后一段 <c>godot_connect</c> 与 <c>discover_tools</c>，
/// 在新 daemon 上静默重放。<b>两段都要重放</b>：只重放 connect 会静默丢掉组。</para>
/// </remarks>
public sealed class ShimStateReplayTests
{
    [Fact]
    public async Task groups_survive_a_daemon_restart_through_shim_replay()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();

        var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = port,
            IdleSeconds = 120,
        });

        using var shim = ShimProcess.Start(new ShimSpawnOptions
        {
            Port = port,
            StateDir = stateDir,
            IdleSeconds = 30,
        });

        // ① 经 shim 连接并激活一个组。这两次调用会被 shim 记住。
        var connect = await CallAsync(shim, 1, "godot_connect",
            """{"groups":["editor_ops"],"project":"D:\\proj\\replay"}""");
        Assert.True(IsSuccess(connect), $"connect 应成功:{connect}");

        var discover = await CallAsync(shim, 2, "discover_tools",
            """{"request":"playtest"}""");
        Assert.True(IsSuccess(discover), $"discover_tools 应成功:{discover}");

        // 重启前:两个组都在。用 list_instances 之外的方式确认 —— 直接问 discover_tools 的
        // 状态会改写状态，所以改为看 tools/list 是否包含两组各一个代表工具。
        var before = await ListToolNamesAsync(shim);
        Assert.Contains("editor_sync", before);
        Assert.Contains("game_start", before);

        // ② 硬杀 daemon：键表随之消失。
        daemon.Dispose();

        // ③ 下一跳请求触发 shim 自举 → 重放会话状态 → 再发当前请求。
        var after = await ListToolNamesAsync(shim);

        // 两段状态都必须回来：只重放 connect 会让 playtest 消失。
        Assert.Contains("editor_sync", after);
        Assert.Contains("game_start", after);
        Assert.Contains("godot_connect", after);
    }

    [Fact]
    public async Task replay_does_not_leak_phantom_responses_to_the_host()
    {
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();

        var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = port,
            IdleSeconds = 120,
        });

        using var shim = ShimProcess.Start(new ShimSpawnOptions
        {
            Port = port,
            StateDir = stateDir,
            IdleSeconds = 30,
        });

        Assert.True(IsSuccess(await CallAsync(shim, 1, "godot_connect",
            """{"groups":["editor_ops"],"project":"D:\\proj\\replay2"}""")));

        daemon.Dispose();

        // 重启后第一跳：shim 会先静默重放两段，再回这一条。
        // 宿主必须**只**收到 id=9 这一条响应 —— 重放的响应若泄漏到 stdout，
        // 宿主会按 id 配对失败并报协议错误（或把重放结果当成这次调用的结果）。
        var raw = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"list_instances","arguments":{}}}""",
            TimeSpan.FromSeconds(60));

        using var document = JsonDocument.Parse(raw);
        Assert.Equal(9, document.RootElement.GetProperty("id").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("result", out _), $"应拿到 result:{raw}");
    }

    /// <summary>经 shim 发一次工具调用。</summary>
    /// <param name="shim">shim 进程句柄。</param>
    /// <param name="id">JSON-RPC id。</param>
    /// <param name="tool">工具名。</param>
    /// <param name="argsJson">参数对象字面量；null 表示空参数。</param>
    /// <returns>原始响应行。</returns>
    private static Task<string> CallAsync(ShimProcess shim, int id, string tool, string argsJson)
        => shim.SendAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\""
            + tool + "\",\"arguments\":" + argsJson + "}}",
            TimeSpan.FromSeconds(90));

    /// <summary>经 shim 取一次工具名列表。</summary>
    /// <param name="shim">shim 进程句柄。</param>
    /// <returns>工具名集合。</returns>
    private static async Task<HashSet<string>> ListToolNamesAsync(ShimProcess shim)
    {
        var raw = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""",
            TimeSpan.FromSeconds(90));
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>判断工具调用响应是否为成功信封。</summary>
    /// <param name="raw">原始响应行。</param>
    /// <returns>成功时 true。</returns>
    private static bool IsSuccess(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("result", out var result))
            {
                return false;
            }

            if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            {
                return false;
            }

            // 工具层的 success 字段（daemon 的成功信封）。
            if (result.TryGetProperty("content", out var content) && content.GetArrayLength() > 0
                && content[0].TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return !text.GetString()!.Contains("\"success\":false", StringComparison.Ordinal);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
