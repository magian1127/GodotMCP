using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 首批迁移的编辑器工具(issue 06 先导 editor_sync;其余 eager 工具随 09 号)。
/// 所有工具都接受可选 instance 参数(规范化项目路径或 12 位短 id;恰好一个活跃实例时可省略)
/// —— 见 ADR-0003。schema 参数名与 Node 桥同名工具一致(snake_case)。
/// </summary>
/// <param name="instances">实例管理器,提供按 instance 寻址的编辑器实例调用。</param>
[McpServerToolType]
public sealed class EditorSyncTools(InstanceManager instances)
{
    /// <summary>
    /// editor_sync:刷新外部修改的项目文件,并等待 EditorFileSystem 完成扫描与导入。
    /// <para>逻辑链:editor.refresh(带 file_paths 时只刷新指定文件,30 秒超时)→ toolkit 失败透传
    /// → editor.wait_for_idle(可带 timeout_ms)等待空闲,失败透传 → 两步结果合并为
    /// {success:true, refresh, idle};实例调用异常映射为错误信封。</para>
    /// </summary>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <param name="file_paths">要刷新的 res:// 路径;省略时扫描整个项目。</param>
    /// <param name="timeout_ms">等待空闲的超时时间(毫秒),默认 10 秒。</param>
    /// <returns>合并 JSON:refresh 与 idle 两步的原始结果;失败时为错误信封。</returns>
    [McpServerTool(Name = "editor_sync")]
    [Description("刷新在外部修改的项目文件，并等待 EditorFileSystem 完成扫描和导入。省略 file_paths 时扫描整个项目。")]
    public async Task<CallToolResult> EditorSync(
        string? instance = null,
        string[]? file_paths = null,
        int? timeout_ms = null)
    {
        try
        {
            // 与 Node 桥 editorSyncHandler 同形:editor.refresh → editor.wait_for_idle,
            // 组合为 {success:true, refresh, idle}。
            var refreshParams = file_paths is null ? null : JsonSerializer.Serialize(new { file_paths });
            var refreshed = await instances.CallInstanceAsync(
                instance, "editor.refresh", refreshParams, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (ToolResults.IsFailure(refreshed))
            {
                return ToolResults.FromToolkitResult(refreshed);
            }

            var idleParams = timeout_ms is null ? null : JsonSerializer.Serialize(new { timeout_ms });
            var idle = await instances.CallInstanceAsync(
                instance, "editor.wait_for_idle", idleParams, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (ToolResults.IsFailure(idle))
            {
                return ToolResults.FromToolkitResult(idle);
            }

            return ToolResults.Json(
                $"{{\"success\":true,\"refresh\":{refreshed.GetRawText()},\"idle\":{idle.GetRawText()}}}");
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }
    }

    /// <summary>editor_save_scene:保存当前编辑场景;带 file_path 时另存为。经共享路由转发 toolkit 结果。</summary>
    /// <param name="file_path">另存为的 res:// 路径;省略时保存到原路径。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>toolkit 结果信封(失败映射为 isError)。</returns>
    [McpServerTool(Name = "editor_save_scene", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("保存当前编辑场景。可选的 file_path 用于另存为。")]
    public Task<CallToolResult> EditorSaveScene(
        string? file_path = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>();
        if (file_path is not null) args["file_path"] = file_path;
        return ToolRouting.RouteAsync(instances, instance, "editor.save_scene", args);
    }

    /// <summary>project_get_settings:列出 ProjectSettings 的键与值,可按前缀筛选;匹配敏感键模式(密码/token/密钥类)的条目由服务端基础过滤移除。</summary>
    /// <param name="prefix">键名前缀筛选;省略时返回全部(经过滤)。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>toolkit 结果信封(失败映射为 isError)。</returns>
    [McpServerTool(Name = "project_get_settings", ReadOnly = true, OpenWorld = false)]
    [Description("列出 ProjectSettings 的键和值，可按 prefix 筛选。匹配 /password|token|secret|key/i 的键会被移除（基础过滤）。")]
    public Task<CallToolResult> ProjectGetSettings(
        string? prefix = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>();
        if (prefix is not null) args["prefix"] = prefix;
        return ToolRouting.RouteAsync(instances, instance, "project.get_settings", args);
    }
}
