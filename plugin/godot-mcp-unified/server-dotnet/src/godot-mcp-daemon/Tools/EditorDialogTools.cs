using System.ComponentModel;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 编辑器对话框应答工具(issue 23):枚举编辑器主窗口之外的额外窗口(对话框),
/// 并按 ok/cancel/custom/dismiss 应答 —— 模态对话框(如外改场景文件后 refresh 弹出的
/// "scene was modified outside Godot" 确认框)不再卡死 agent。eager 注册:对话框
/// 出现在任何流程中,工具必须无需组激活即可用(与 editor_sync 同理)。
/// </summary>
/// <param name="instances">实例管理器,提供按 instance 寻址的编辑器实例调用。</param>
[McpServerToolType]
public sealed class EditorDialogTools(InstanceManager instances)
{
    /// <summary>editor_windows:列出可见的编辑器额外窗口(对话框),含按钮清单。只读。</summary>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>toolkit 结果信封(失败映射为 isError)。</returns>
    [McpServerTool(Name = "editor_windows", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("列出编辑器当前可见的额外窗口（对话框、弹窗）。每行含 id、kind、标题与按钮清单；主编辑器窗口永不列出。无对话框时 count 为 0。")]
    public Task<CallToolResult> EditorWindows(string? instance = null)
        => ToolRouting.RouteAsync(instances, instance, "editor.windows", []);

    /// <summary>
    /// editor_answer_dialog:按窗口 id 与按钮应答对话框。button 取 'ok'/'cancel'/'dismiss'、
    /// 自定义动作名或按钮精确文本;应答后返回窗口可见性供验证。
    /// </summary>
    /// <param name="window">窗口 id(editor.windows 返回的 w&lt;数字&gt;)。</param>
    /// <param name="button">要按的按钮:ok / cancel / dismiss / 自定义动作名 / 精确文本。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>toolkit 结果信封(失败映射为 isError)。</returns>
    [McpServerTool(Name = "editor_answer_dialog", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = false)]
    [Description("应答编辑器对话框：按窗口 id 按下 ok/cancel/自定义按钮，或 dismiss 关闭。window id 来自 editor_windows。对“场景在外部被修改”类确认框应选 reload/overwrite 动作——cancel/dismiss 保留编辑器内副本，后续保存会覆盖外部修改。")]
    public Task<CallToolResult> EditorAnswerDialog(
        string window,
        string? button = null,
        string? instance = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["window"] = window,
            ["button"] = button ?? "ok",
        };
        return ToolRouting.RouteAsync(instances, instance, "editor.answer_dialog", args);
    }
}
