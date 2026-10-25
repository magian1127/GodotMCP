namespace GodotMcp.Daemon.Groups;

/// <summary>
/// 工具面的分层定义(ADR-0007):core 9 与"未连接时唯一的入口工具"。
/// </summary>
/// <remarks>
/// <para><b>core 的依据是"自我恢复面 + 只读",不是调用频次</b>:实测 <c>node_set_property</c>(259 次)
/// 与 <c>signal_emit</c>(371 次)的高频几乎全部来自 <c>channel:"runtime"</c>,属玩测场景;
/// 按频次选 core 会把玩测工具误当通用工具。证据见 <c>.scratch/godot-toolface/stats.md</c>。</para>
/// <para><b>为什么 <c>godot_connect</c> 必须常驻</b>:未连接时它是唯一可见工具,也是
/// "会话尚未就绪"时唯一的自救路径(它能自行建键)。</para>
/// </remarks>
public static class ToolFace
{
    /// <summary>连接入口工具名。未连接时工具面<b>只有</b>它。</summary>
    public const string ConnectToolName = "godot_connect";

    /// <summary>
    /// 常驻只读面(9 个,含 <see cref="ConnectToolName"/>)。已连接时恒可见。
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        ConnectToolName,
        "discover_tools",
        "list_instances",
        "list_operations",
        "log_read",
        "capture_screenshot",
        "scene_query",
        "scene_get_tree",
        "node_inspect",
    ];

    /// <summary>core 的集合视图(成员判定用)。</summary>
    public static readonly IReadOnlySet<string> CoreSet =
        Core.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 计算某个会话键应看到的工具名集合。
    /// </summary>
    /// <param name="activeGroups">
    /// 该键已激活的组名。<b>必须按并集传入</b> —— 两处有意重叠
    /// (<c>node_set_property</c> ∈ scene_edit+playtest、<c>signal_emit</c> ∈ playtest+signals)
    /// 意味着停用一个组不能摘掉另一个组还在用的工具。
    /// </param>
    /// <returns>
    /// 已连接时为 <c>core ∪ 组工具</c>;未连接(键不存在或未 connect)时为<b>只有</b>
    /// <see cref="ConnectToolName"/>。
    /// </returns>
    public static HashSet<string> VisibleToolNames(IEnumerable<string>? activeGroups)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        if (activeGroups is null)
        {
            // 未连接:只给入口工具。绝不回落到全量 —— 那是 fail-open。
            visible.Add(ConnectToolName);
            return visible;
        }

        foreach (var name in Core)
        {
            visible.Add(name);
        }

        foreach (var groupName in activeGroups)
        {
            if (GroupCatalogue.Find(groupName) is { } group)
            {
                foreach (var toolName in group.ToolNames)
                {
                    visible.Add(toolName);
                }
            }
        }

        return visible;
    }

    /// <summary>
    /// 按基准序输出工具名子集(<c>godot_connect</c> 固定首位,其余按 <c>NodeToolTable</c> 顺序)。
    /// </summary>
    /// <remarks>
    /// 协议基线要求目录顺序稳定以保 prompt cache,而集合枚举顺序不保证稳定 ——
    /// 所以顺序必须显式算出来,不能依赖插入序。<c>godot_connect</c> 不在表内,固定排最前。
    /// </remarks>
    /// <param name="names">要排序的工具名集合。</param>
    /// <returns>稳定顺序的列表。</returns>
    /// <summary>表内工具名 → 表序(用于 tools/list 的稳定排序;表外工具由调用方排到末尾)。</summary>
    public static IReadOnlyDictionary<string, int> TableOrderIndex { get; } =
        NodeToolTable.AllEntries
            .Select((entry, index) => (entry.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);

    public static List<string> OrderForListing(IEnumerable<string> names)
    {
        var set = names as ICollection<string> ?? names.ToHashSet(StringComparer.Ordinal);
        var ordered = new List<string>(set.Count);

        if (set.Contains(ConnectToolName))
        {
            ordered.Add(ConnectToolName);
        }

        foreach (var def in NodeToolTable.AllEntries)
        {
            if (set.Contains(def.Name) && !string.Equals(def.Name, ConnectToolName, StringComparison.Ordinal))
            {
                ordered.Add(def.Name);
            }
        }

        // **表外的名字必须补在末尾,不能丢**。
        //
        // 这个方法的返回值常被当成"完整清单"用(godot_connect 响应里的 tools 就是),
        // 而 NodeToolTable 只收录内置工具 —— 扩展组与未分组扩展都不在表里。
        // 只走上面那个循环会把它们静默吞掉,于是响应声明的工具集**小于**真实面,
        // 模型据此以为"连上了但没拿到扩展工具",反复 discover_tools 或直接放弃。
        // 排序上扩展工具排最后:表内顺序是稳定的既有契约,不该被扩展打乱。
        foreach (var name in set.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!ordered.Contains(name))
            {
                ordered.Add(name);
            }
        }

        return ordered;
    }
}
