using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// C# 构建工具(issue 25):编辑器侧 dotnet 构建任务的启动/快照/阻塞等待。
/// 成功语义在 toolkit 侧(editor_csharp_build.gd):exit 0 不算成功,DLL 必须
/// 出现在引擎实际加载位,ALC 探针观测热重载(unknown 是正常结果,仅"重载进
/// 本该卸载的 ALC"建议重启编辑器)。eager 注册:C# 迭代是高频流程,
/// 与 editor_sync 同理无需组激活。
/// </summary>
/// <param name="instances">实例管理器,提供按 instance 寻址的编辑器实例调用。</param>
[McpServerToolType]
public sealed class EditorCsharpBuildTools(InstanceManager instances)
{
    /// <summary>单次状态调用超时(快照只做文件/pid 读取,秒级)。</summary>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);

    /// <summary>等待循环的轮询间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>editor_build_csharp:启动 dotnet build,立即返回 running 与解析出的路径。</summary>
    /// <param name="no_incremental">传 --no-incremental(全量重建)。</param>
    /// <param name="csproj">显式 .csproj;省略时自动发现 res:// 根的单个 *.csproj。</param>
    /// <param name="configuration">MSBuild 配置,默认 Debug(编辑器恒加载 Debug 程序集)。</param>
    /// <param name="timeout_s">构建超时秒数,默认 300,最小 5。</param>
    /// <param name="alc_timeout_s">热重载观测超时秒数,默认 45,最小 1。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>启动载荷(running + pid + 路径)或错误信封(BUSY/无 C# 项目)。</returns>
    [McpServerTool(Name = "editor_build_csharp", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = false)]
    [Description("启动 C# 项目的 dotnet build（仅适用于 C# 宿主项目），立即返回 running。用 editor_build_csharp_status 或 editor_build_csharp_wait 观察结果；成败以快照的 ok 与 alc.status 为准，绝不以 exit_code 为准。")]
    public Task<CallToolResult> EditorBuildCsharp(
        bool? no_incremental = null,
        string? csproj = null,
        string? configuration = null,
        float? timeout_s = null,
        float? alc_timeout_s = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>();
        if (no_incremental == true) args["no_incremental"] = true;
        if (csproj is not null) args["csproj"] = csproj;
        if (configuration is not null) args["configuration"] = configuration;
        if (timeout_s is not null) args["timeout_s"] = timeout_s;
        if (alc_timeout_s is not null) args["alc_timeout_s"] = alc_timeout_s;
        return ToolRouting.RouteAsync(instances, instance, "editor.build_csharp", args);
    }

    /// <summary>editor_build_csharp_status:当前构建快照(status/exit_code/errors/tail/alc)。只读。</summary>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>快照 JSON(idle 时各路径字段为空)。</returns>
    [McpServerTool(Name = "editor_build_csharp_status", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("读取 C# 构建任务快照：status（idle/running/succeeded/build_failed/alc_failed）、exit_code、errors/warnings、日志 tail 与 alc 观测。快照恒为成功信封，构建成败看其中的 ok 与 status 字段。")]
    public Task<CallToolResult> EditorBuildCsharpStatus(string? instance = null)
        => ToolRouting.RouteAsync(instances, instance, "editor.build_csharp_status", []);

    /// <summary>
    /// editor_build_csharp_wait:阻塞轮询直到构建离开 running(或超时),返回最终/当前快照。
    /// 对齐 editor_sync 的等待型先例 —— agent 一次调用完成等待,禁止盲 sleep 轮询。
    /// </summary>
    /// <param name="timeout_ms">最长等待毫秒数,默认 240000,钳制到 1000..540000;超时返回当前快照(仍为 running)。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>终态(或超时时刻)的构建快照。</returns>
    [McpServerTool(Name = "editor_build_csharp_wait", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("阻塞等待 C# 构建离开 running 状态（内部 500ms 轮询编辑器快照），返回最终快照；timeout_ms 超时则返回当时的 running 快照。配合 editor_build_csharp 使用，避免 agent 侧轮询。")]
    public async Task<CallToolResult> EditorBuildCsharpWait(
        int? timeout_ms = null,
        string? instance = null)
    {
        var clamped = Math.Clamp(timeout_ms ?? 240_000, 1_000, 540_000);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(clamped);
        try
        {
            while (true)
            {
                var snapshot = await instances.CallInstanceAsync(
                    instance, "editor.build_csharp_status", "{}", StatusTimeout, CancellationToken.None);
                if (ToolResults.IsFailure(snapshot))
                {
                    return ToolResults.FromToolkitResult(snapshot);
                }
                var status = snapshot.ValueKind == JsonValueKind.Object
                    && snapshot.TryGetProperty("status", out var statusEl)
                    && statusEl.ValueKind == JsonValueKind.String
                    ? statusEl.GetString()
                    : null;
                if (status is null or "idle" or "succeeded" or "build_failed" or "alc_failed")
                {
                    return ToolResults.Json(snapshot.GetRawText());
                }
                if (DateTime.UtcNow >= deadline)
                {
                    return ToolResults.Json(WithTimeoutNote(snapshot.GetRawText(), clamped));
                }
                await Task.Delay(PollInterval);
            }
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }
    }

    /// <summary>给超时时刻的 running 快照附加说明字段(不改原快照的其余内容)。</summary>
    /// <param name="snapshotJson">快照 JSON 文本。</param>
    /// <param name="timeoutMs">实际使用的超时毫秒数。</param>
    /// <returns>附加 wait_timed_out 字段后的 JSON 文本。</returns>
    private static string WithTimeoutNote(string snapshotJson, int timeoutMs) =>
        $$"""
        {"wait_timed_out":true,"wait_timeout_ms":{{timeoutMs}},"note":"still running after the wait timeout — call editor_build_csharp_status later, or re-wait","snapshot":{{snapshotJson}}}
        """;
}
