using System.Text.Encodings.Web;
using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 工具层返回与错误信封(与 Node 桥 shared/errorContract.ts 对齐):
/// 成功 = 结果 JSON 文本原样透传(REFLECT);失败 =
/// {success:false, error, code, hint?, instances?} 并置 isError。
/// transport 错误码的恢复提示与 Node 桥 EXCEPTION_HINTS 对应。
/// </summary>
internal static class ToolResults
{
    /// <summary>daemon 侧重建 JSON 时的统一序列化策略:payload 键小驼峰(与 Node 桥一致)
    /// + 不转义非 ASCII。
    /// <para>Encoder 必须显式设为 UnsafeRelaxedJsonEscaping:System.Text.Json 的默认
    /// encoder 会把每个非 ASCII 字符转成 \uXXXX 转义序列,而 Node/GDScript 的
    /// JSON.stringify 不转义。缺了它,中文项目路径/日志/错误消息到达模型时是字面量
    /// 转义文本 — 既数倍放大词元占用,又让模型拿到无法直接读取的路径。注意这只会
    /// 放行非 ASCII 与 HTML 敏感字符,JSON 必需字符(引号/反斜杠/控制字符)仍被转义,
    /// 输出仍是合法 JSON。</para>
    /// <para>仅用于 daemon 自行重建的载荷;toolkit 原样透传的文本
    /// (JsonElement.GetRawText)不经过这里,也无需经过。</para></summary>
    internal static readonly JsonSerializerOptions Output = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>transport 错误码 → 恢复提示的兜底映射;异常未携带 hint 时按码取用。</summary>
    private static readonly Dictionary<string, string> ExceptionHints = new()
    {
        ["TIMEOUT"] = "The editor may be busy. Try editor_sync before retrying.",
        ["DISCONNECTED"] =
            "Plugin WebSocket not connected. Ensure Godot is running with the plugin enabled. If running headless, launch with: godot --headless --editor --path <project>",
        ["CONNECT_FAILED"] =
            "Could not connect to the Godot editor plugin. Ensure Godot is running with the plugin enabled. If running headless, launch with: godot --headless --editor --path <project>",
        ["GAME_NOT_RUNNING"] =
            "No running game. Call log_read(channel:'auto', level_filter:['error']) for startup or crash diagnostics. " +
            "The auto channel serves editor output or cached game output after a crash. To restart: fix the errors, then game_start.",
        ["COMPILATION_FAILED"] =
            "The game failed to compile. Fix the script errors listed above, then call game_start again. " +
            "If no errors are shown, call editor_sync to retrigger imports, then log_read(channel:'editor') for the full log.",
    };

    /// <summary>成功结果包装:JSON 文本原样透传(REFLECT),不 isError。</summary>
    /// <param name="jsonText">toolkit 返回的结果 JSON 文本。</param>
    /// <returns>单文本内容、非错误的 CallToolResult。</returns>
    public static CallToolResult Json(string jsonText)
    {
        return new CallToolResult { Content = [new TextContentBlock { Text = jsonText }] };
    }

    /// <summary>构造失败信封:{success:false, error, code, hint?, instances?} 并置 isError。</summary>
    /// <param name="code">错误码(如 INVALID_PARAMS / UNSUPPORTED / transport 错误码)。</param>
    /// <param name="message">面向调用方的错误描述。</param>
    /// <param name="hint">可选恢复提示;null 时不写入 payload。</param>
    /// <param name="instances">可选实例列表(多实例歧义场景);null 时不写入 payload。</param>
    /// <returns>isError 的 CallToolResult,内容为小驼峰 JSON 信封。</returns>
    public static CallToolResult Error(
        string code, string message, string? hint = null, List<InstanceSummary>? instances = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["error"] = message,
            ["code"] = code,
        };
        if (hint is not null)
        {
            payload["hint"] = hint;
        }
        if (instances is not null)
        {
            payload["instances"] = instances;
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, Output) }],
            IsError = true,
        };
    }

    /// <summary>把 InstanceCallException 映射为错误信封:优先异常自带 hint,否则按码查兜底映射。</summary>
    /// <param name="exception">实例调用异常(携带 code/message/hint/instances)。</param>
    /// <returns>isError 的 CallToolResult。</returns>
    public static CallToolResult FromException(InstanceCallException exception)
    {
        var hint = exception.Hint
            ?? (ExceptionHints.TryGetValue(exception.Code, out var mapped) ? mapped : null);
        return Error(exception.Code, exception.Message, hint, exception.Instances);
    }

    /// <summary>toolkit 的失败信封(result 内 {success:false})转换为 isError 响应;成功原样透传。</summary>
    /// <para>逻辑链:IsFailure 为真 → 逐键抽 code/error/hint(缺省分别落 INTERNAL /
    /// "unknown error" / null)构造错误信封;否则结果 JSON 原样透传(REFLECT)。</para>
    /// <param name="result">toolkit 返回的结果 JSON。</param>
    /// <returns>失败 → isError 错误信封;成功 → 原样透传的 CallToolResult。</returns>
    public static CallToolResult FromToolkitResult(JsonElement result)
    {
        if (IsFailure(result))
        {
            var code = result.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString()!
                : "INTERNAL";
            var error = result.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()!
                : "unknown error";
            var hint = result.TryGetProperty("hint", out var hintEl) && hintEl.ValueKind == JsonValueKind.String
                ? hintEl.GetString()
                : null;
            return Error(code, error, hint);
        }
        return Json(result.GetRawText());
    }

    /// <summary>判定 toolkit 结果是否为失败信封(对象且 success 字段为 false)。</summary>
    /// <param name="result">待判定的结果 JSON。</param>
    /// <returns>失败信封为 true;非对象、无 success 或 success 非 false 均为 false。</returns>
    public static bool IsFailure(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("success", out var successEl)
            && successEl.ValueKind == JsonValueKind.False;
    }
}
