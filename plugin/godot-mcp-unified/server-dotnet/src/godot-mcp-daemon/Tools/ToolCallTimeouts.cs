namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 工具级调用超时台账 —— <b>唯一真源</b>(spec §2.2)。
/// </summary>
/// <remarks>
/// <para><b>为什么不能"把三个常量统一抬到 180s"</b>:<c>ToolRouting</c> 与
/// <c>GroupToolInvoker</c> 的 30 秒不是"长工具专用",而是该层<b>所有</b>工具的唯一默认 ——
/// 它同时服务 <c>node_inspect</c>、<c>scene_get_tree</c>、<c>tilemap_read_cells</c>、
/// <c>signal_list</c> 这类读工具。统一抬到 180 秒会让"编辑器卡住"的反馈从 30 秒变成 3 分钟,
/// 而"长工具恰好执行一次"的测试照样绿 —— 那是能被用户直接感觉到的倒退。</para>
/// <para><b>正确形状</b>:默认留 30 秒,只有<b>名单内的长工具</b>拿 180 秒。名单按操作性质定,
/// 不按"某个工具曾经慢过"定。</para>
/// <para><b>三层阶梯必须严格递减</b>:daemon 180s &lt; shim 210s &lt; 宿主 240s。
/// 只是"对齐"不够:若两层同值,shim abort 与 daemon 的干净 TIMEOUT 会抢同一个截止点,
/// "该操作可能已执行"与"明确的超时"会在不同机器上随机出现。严格递减后正常路径总是
/// daemon 先给出干净结论,上层永远走不到自己的截止点。</para>
/// </remarks>
internal static class ToolCallTimeouts
{
    /// <summary>短操作上限:读、查询、单节点编辑。留小值以保证卡住时反馈快。</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    /// <summary>长操作上限:整项目构建/保存、脚本执行、跨场景操作。</summary>
    public static readonly TimeSpan Long = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 长工具名单。判据是"操作规模可能无界",不是"曾经慢过"。
    /// </summary>
    /// <remarks>
    /// <para><c>editor_build_csharp*</c> 三个都在:发起构建、查进度、等构建结束。
    /// 其中 <c>_wait</c> 本身就设计成阻塞等待。</para>
    /// <para><c>editor_save_scene</c> 在大场景上会随节点数线性变慢;
    /// <c>execute_code</c>/<c>node_call_method</c> 跑用户代码,耗时不设上限。</para>
    /// </remarks>
    private static readonly HashSet<string> LongToolNames = new(StringComparer.Ordinal)
    {
        "execute_code",
        "node_call_method",
        "editor_save_scene",
        "editor_build_csharp",
        "editor_build_csharp_status",
        "editor_build_csharp_wait",

        // editor_sync = editor.refresh + editor.wait_for_idle。后者**等编辑器空闲**,
        // 在大场景/长构建期间可能远超 30 秒。spec §2.2 特别点名它:
        // 它的 30s 曾是 EditorSyncTools 里的字面量、根本不经过 ToolRouting,
        // 只把名字加进名单覆盖不到 —— 现在那两处已改为 For("editor_sync")。
        "editor_sync",
    };

    /// <summary>取某工具的单次调用上限。</summary>
    /// <param name="toolName">工具名(裸名,不带 <c>godot_</c> 前缀)。</param>
    /// <returns>长工具 180 秒;其余 30 秒。</returns>
    public static TimeSpan For(string toolName)
        => LongToolNames.Contains(toolName) ? Long : Default;

    /// <summary>该工具是否属于长操作(供测试与诊断断言)。</summary>
    /// <param name="toolName">工具名。</param>
    /// <returns>在长名单内则为 true。</returns>
    public static bool IsLong(string toolName) => LongToolNames.Contains(toolName);

    /// <summary>长工具名单的只读视图(供测试逐项断言)。</summary>
    public static IReadOnlyCollection<string> LongNames => LongToolNames;
}
