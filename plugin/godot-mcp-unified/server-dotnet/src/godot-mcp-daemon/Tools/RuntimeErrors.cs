using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 运行时调用的错误补全(Node runtimeErrorWithCrashContext 同形):
/// TIMEOUT / GAME_NOT_RUNNING / COMPILATION_FAILED 时抓取编辑器侧崩溃上下文
/// (调试器 error_buffer + 日志行 → 回退编辑器控制台)并附入错误提示。
/// playtest 工具与运行时组工具共用。
/// </summary>
internal static class RuntimeErrors
{
    /// <summary>为运行时错误补全崩溃上下文:命中三类可诊断错误码时附编辑器侧近期错误。</summary>
    /// <para>逻辑链:ex.Code 为 TIMEOUT / GAME_NOT_RUNNING / COMPILATION_FAILED 之一 →
    /// 抓取崩溃证据(仅"确有错误"才算证据,见 <see cref="FetchCrashContextAsync"/>);
    /// 拿到证据 → 用专用 hint(编辑器近期错误 + 修复指引)构造错误信封;
    /// 无证据或错误码不命中 → 退回通用错误映射(ToolResults.FromException)。
    /// 关键:没有错误证据时绝不声称"崩溃"——游戏只是没在运行(最常见)时,
    /// 通用提示才是准确的。</para>
    /// <param name="instances">实例管理器,用于回读编辑器侧诊断数据。</param>
    /// <param name="ex">运行时调用抛出的异常(携带错误码与实例列表)。</param>
    /// <param name="instance">目标实例;与原调用同址,恰好一个实例时可省略。</param>
    /// <returns>isError 的 CallToolResult;hint 含崩溃上下文(抓到时)或通用恢复提示。</returns>
    public static async Task<CallToolResult> WithCrashContextAsync(
        InstanceManager instances, InstanceCallException ex, string? instance)
    {
        if (ex.Code is "TIMEOUT" or "GAME_NOT_RUNNING" or "COMPILATION_FAILED")
        {
            var crashContext = await FetchCrashContextAsync(instances, instance);
            if (!string.IsNullOrEmpty(crashContext))
            {
                return ToolResults.Error(
                    ex.Code,
                    ex.Message,
                    "Game crashed or failed to compile. Recent errors from editor console:\n" +
                    crashContext +
                    "\nFix the script errors, then game_stop + game_start to retry. " +
                    "If errors are stale, call editor_sync to retrigger imports.");
            }
        }
        return ToolResults.FromException(ex);
    }

    /// <summary>抓取编辑器侧的崩溃证据文本:仅当确有错误时才返回非空。</summary>
    /// <para>逻辑链:先调 debugger.get_log(limit 15,5 秒超时)→ <b>只有 error_buffer 非空才算
    /// 崩溃证据</b>(error_buffer 是错误专用缓冲;其 lines 是普通日志尾部,单独出现在"游戏没在运行"
    /// 这种最常见场景里只是启动横幅,把它当错误证据会产出"Game crashed"的假警报 —— 实测该误报
    /// 会把正常启动日志列为"recent errors");有错误证据时把 error_buffer 行与 lines 一并拼接返回。
    /// error_buffer 为空或调试器不可用 → 回退 editor.get_console(仅 error 级,limit 15):
    /// 该分支已按 error 级过滤,故非空即证据。两路皆无证据 → 返回空串,
    /// 由调用方退回通用提示(不声称崩溃)。</para>
    /// <param name="instances">实例管理器,用于发起诊断调用。</param>
    /// <param name="instance">目标实例;恰好一个实例时可省略。</param>
    /// <returns>拼接的近期错误文本;无错误证据时为空串。</returns>
    private static async Task<string> FetchCrashContextAsync(InstanceManager instances, string? instance)
    {
        // 首选:debugger.get_log(调试器桥 error_buffer + 日志文件行)。
        try
        {
            var result = await instances.CallInstanceAsync(
                instance, "debugger.get_log", """{"limit":15}""", TimeSpan.FromSeconds(5), CancellationToken.None);
            if (!ToolResults.IsFailure(result))
            {
                var errorLines = new List<string>();
                if (result.TryGetProperty("error_buffer", out var errorBuffer) && errorBuffer.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in errorBuffer.EnumerateArray())
                    {
                        var message = GetStringOrNull(entry, "message") ?? "";
                        var source = GetStringOrNull(entry, "source") ?? "";
                        var line = GetNumberOrNull(entry, "line") ?? 0;
                        if (message.Length > 0)
                        {
                            errorLines.Add(source.Length > 0 && line > 0 ? $"{message} ({source}:{line})" : message);
                        }
                    }
                }
                // 无错误证据:即便 lines 有内容也不构成崩溃证据(那只是普通日志尾部)。
                if (errorLines.Count > 0)
                {
                    var parts = new List<string>(errorLines);
                    if (result.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in lines.EnumerateArray())
                        {
                            parts.Add(line.ValueKind == JsonValueKind.String ? line.GetString()! : line.GetRawText());
                        }
                    }
                    return string.Join("\n", parts);
                }
            }
        }
        catch (InstanceCallException)
        {
            // 继续落到编辑器控制台桥接方法。
        }

        // 回退:编辑器控制台(例如从未启动过游戏会话)。该请求按 error 级过滤,非空即崩溃证据。
        try
        {
            var result = await instances.CallInstanceAsync(
                instance, "editor.get_console", """{"limit":15,"level_filter":["error"]}""",
                TimeSpan.FromSeconds(5), CancellationToken.None);
            if (!ToolResults.IsFailure(result))
            {
                var returned = GetNumberOrNull(result, "returned") ?? 0;
                // entries 全空白等同无内容 —— 落回通用提示,不透传无效控制台文本。
                if (returned != 0 && GetStringOrNull(result, "entries") is { } entries && entries.Trim().Length > 0)
                {
                    return entries;
                }
            }
        }
        catch (InstanceCallException)
        {
            // 编辑器可能也已不可用 —— 落到通用提示。
        }
        return "";
    }

    /// <summary>安全读取对象字段的字符串值;字段缺失或非字符串时返回 null。</summary>
    /// <param name="element">待读取的 JSON 对象。</param>
    /// <param name="key">字段名。</param>
    /// <returns>字段字符串值;缺失/类型不符为 null。</returns>
    private static string? GetStringOrNull(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>安全读取对象字段的数值;字段缺失或非数字时返回 null。</summary>
    /// <param name="element">待读取的 JSON 对象。</param>
    /// <param name="key">字段名。</param>
    /// <returns>字段数值;缺失/类型不符为 null。</returns>
    private static double? GetNumberOrNull(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }
}
