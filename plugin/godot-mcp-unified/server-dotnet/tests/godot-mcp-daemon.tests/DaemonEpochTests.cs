using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// daemon 重启检测与 shim 健壮性（第四轮审核 F1 / F5）。
/// </summary>
/// <remarks>
/// <para><b>F1 的现场</b>：token 跨重启稳定、端口固定，所以"新 daemon 先就绪、下一条请求返回 200"
/// 时，shim 从响应上看不出任何异常 —— 它只会看到工具面塌回 <c>godot_connect</c>。
/// 靠"请求失败"检测重启会漏掉这种情况，而这是真实世界最常见的时序（两个宿主共用一个
/// daemon、daemon 被另一个进程先拉起）。判据必须是 daemon 的进程身份。</para>
///
/// <para><b>F5 的现场</b>：JSON-RPC 允许位置参数（<c>"params": []</c>），而
/// <c>JsonElement.TryGetProperty</c> 对非对象抛 <c>InvalidOperationException</c>。
/// 若这个异常从"记录会话状态"里逃出去，整个 shim 进程就退出了 ——
/// 宿主侧表现为"MCP 服务器没了"，而一条合法请求就能触发。</para>
/// </remarks>
public sealed class DaemonEpochTests
{
    [Fact]
    public async Task groups_survive_a_restart_where_the_new_daemon_is_already_listening()
    {
        // 这条是 F1 的核心：**先**把新 daemon 起好（同端口、同 stateDir、同 token），
        // **再**发请求。修复前 shim 只会拿到 200 + 只剩 godot_connect，什么都不重放。
        var stateDir = TestPaths.NewStateDir();
        var port = TestPorts.GetFreePort();

        var first = DaemonProcess.StartReady(new DaemonSpawnOptions
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
            """{"groups":["editor_ops"],"project":"D:\\proj\\epoch"}""")));

        Assert.True(IsSuccess(await CallAsync(shim, 2, "discover_tools",
            """{"request":"playtest"}""")));

        var before = await ListToolNamesAsync(shim);
        Assert.Contains("editor_sync", before);
        Assert.Contains("game_start", before);

        // 硬杀 + **等新 daemon 完全就绪**，再发下一条请求。
        first.Dispose();
        using var second = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = port,
            IdleSeconds = 120,
        });

        // 第一跳会看到 core 9（新键表是空的），同时 shim 从响应头发现 epoch 变了；
        // 第二跳之前重放已完成，组必须回来。
        _ = await ListToolNamesAsync(shim);
        var after = await ListToolNamesAsync(shim);

        Assert.Contains("editor_sync", after);
        Assert.Contains("game_start", after);
    }

    [Fact]
    public async Task shim_survives_positional_params_in_a_tool_call()
    {
        // JSON-RPC 2.0 允许位置参数。修复前这条会让 shim 进程退出。
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 120,
        });

        using var shim = ShimProcess.Start(new ShimSpawnOptions
        {
            Port = daemon.Port,
            StateDir = stateDir,
            IdleSeconds = 30,
        });

        // 位置参数（数组）：不该崩，应拿到 daemon 的协议错误或空参数调用结果。
        var positional = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":["godot_connect"]}""",
            TimeSpan.FromSeconds(60));
        Assert.False(string.IsNullOrWhiteSpace(positional));

        // params 为 null：同样不该崩。
        var nullParams = await shim.SendAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":null}""",
            TimeSpan.FromSeconds(60));
        Assert.False(string.IsNullOrWhiteSpace(nullParams));

        // **进程还活着**才是最关键的断言：崩溃的表现就是后面的请求再也拿不到响应。
        using var document = JsonDocument.Parse(
            await shim.SendAsync(
                """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}""",
                TimeSpan.FromSeconds(60)));
        Assert.True(document.RootElement.TryGetProperty("result", out _), "畸形请求之后 shim 必须仍然可用");
    }

    /// <summary>经 shim 发一次工具调用。</summary>
    /// <param name="shim">shim 进程句柄。</param>
    /// <param name="id">JSON-RPC id。</param>
    /// <param name="tool">工具名。</param>
    /// <param name="argsJson">参数对象字面量。</param>
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
