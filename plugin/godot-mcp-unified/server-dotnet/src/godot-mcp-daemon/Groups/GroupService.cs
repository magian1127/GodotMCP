using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Extensions;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tools;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Groups;

/// <summary>组状态结果(discover_tools 响应行;Node GroupResult 同形)。</summary>
/// <param name="Name">组名。</param>
/// <param name="Status">状态:activated(本次激活)/ already_loaded(先前已激活)/ available(未激活或禁用)。</param>
/// <param name="Tools">组内当前可注册的工具名(禁用/未知组为空)。</param>
/// <param name="Description">组描述;未知组携带提示文案,内置可用为 null 之外的描述。</param>
public sealed record GroupStatus(string Name, string Status, IReadOnlyList<string> Tools, string? Description);

/// <summary>
/// 按需工具组激活引擎(issue 12;Node groups/groupActivation/groupMatch 语义的 C# 移植;
/// issue 13 全量:31 组 / 64 工具,表驱动;issue 14:扩展组 + 版本门控并集)。
/// 激活状态为 daemon 全局(与实例/会话无关):每个新请求会话语义上从全局状态同步
/// 工具面(ConfigureSession),激活动作在当前请求会话的工具集合上触发
/// 一次 tools/list_changed。
/// </summary>
/// <remarks>
/// 完整机制链(发现 → 激活 → 暴露 → 调用 → 通知):
/// ① 发现:内置组来自 GroupCatalogue(静态目录);扩展组来自 ExtensionService(插件登记,
///    含版本门控);discover_tools 元工具按精确名或关键词打分流向 Activate/Catalog。
/// ② 激活/停用:Activate 把组名记入全局 _loaded(内置)或 _loadedExtGroups(扩展);
///    Deactivate 移除(names 缺省即全部停用)。unsafe 组受 GODOT_MCP_UNSAFE=1 门控。
/// ③ tools/list 暴露:无状态 HTTP 下每个请求经 ConfigureSession 捕获会话工具集合并
///    SyncCollection —— EnsureGroupToolsLocked/EnsureExtGroupToolsLocked 懒建工具对象
///    (内置组 → JsonSchemaGroupTool,扩展 → ExtensionTool),再按 IsToolActiveLocked
///    增删集合成员;ApplyVersionGates 再隐藏当前实例集无法提供的门控工具。因此已激活
///    组工具对后续所有请求可见,未激活组不可见。
/// ④ 调用:组工具的 InvokeAsync 经 GroupToolInvoker 按工具名分发(特例工具整形参数,
///    默认走 DeclaredParams),转成 wire 调用发往 toolkit —— 组工具自身不实现逻辑。
/// ⑤ 通知:真实状态变化后 NotifyToolSurfaceChanged 触发 ToolSurfaceChanged 事件,
///    由挂起的 subscriptions/listen 长流(GroupListenStream)投递 tools/list_changed
///    —— 无状态 HTTP 下这是 host 感知工具面变化的唯一通道。
/// </remarks>
public sealed class GroupService
{
    /// <summary>模糊匹配单个关键词的命中数上限(每关键词取前 3)。</summary>
    private const int FuzzyPerElementCap = 3;
    /// <summary>模糊匹配总选中数上限(跨关键词合计 5)。</summary>
    private const int FuzzyTotalCap = 5;
    /// <summary>主导匹配过滤阈值:低于榜首分数一半的非精确命中被裁掉。</summary>
    private const double DominantMatchRatio = 0.5;

    /// <summary>Node isGroupEnabled 同文案:unsafe 组默认缺席,需显式启用。</summary>
    public const string UnsafeDisabledMessage =
        "高风险工具已禁用。仅在操作获得明确授权后，才可设置 GODOT_MCP_UNSAFE=1 并重启 MCP 服务器。";

    /// <summary>未知组名的 Node 同文案(activateGroupByName → activateExtGroup 兜底)。</summary>
    public const string UnknownGroupMessage = "Unknown group: ";

    /// <summary>
    /// 已由 daemon 常驻注册(eager,WithToolsFromAssembly)的组工具 —— 这些工具组激活
    /// 只改变状态与目录呈现,不重复注册/移出会话工具集合(它们始终可见)。
    /// </summary>
    private static readonly HashSet<string> EagerRegisteredToolNames = new(StringComparer.Ordinal)
    {
        "editor_sync",
        "lsp_diagnostics",
        "lsp_symbols",
        "lsp_hover",
        "lsp_completion",
        "lsp_navigate",
    };

    /// <summary>实例管理器(版本门控可提供性判定用)。</summary>
    private readonly InstanceManager _instances;
    /// <summary>扩展服务(扩展组/扩展工具/版本门控定义源)。</summary>
    private readonly ExtensionService _extensions;
    /// <summary>组工具调用器(所有 JsonSchemaGroupTool 共享)。</summary>
    private readonly GroupToolInvoker _invoker;
    /// <summary>保护全局激活状态与工具对象表的锁。</summary>
    private readonly object _gate = new();
    /// <summary>已激活内置组名(daemon 全局,跨请求/会话共享)。</summary>
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    /// <summary>已激活扩展组名(与 _loaded 并列,随扩展生命周期)。</summary>
    private readonly HashSet<string> _loadedExtGroups = new(StringComparer.Ordinal);
    /// <summary>已登记工具名 → 工具对象(全局唯一、跨会话复用;扩展命令更新时原位重建)。</summary>
    private readonly Dictionary<string, McpServerTool> _toolObjects = new(StringComparer.Ordinal);
    /// <summary>当前请求会话的工具集合(AsyncLocal,仅本请求异步上下文内可见)。</summary>
    private readonly AsyncLocal<McpServerPrimitiveCollection<McpServerTool>?> _sessionCollection = new();

    /// <summary>注入依赖并预建组工具调用器。</summary>
    /// <param name="instances">实例管理器(转交给调用器做 wire 调用)。</param>
    /// <param name="extensions">扩展服务(扩展组/扩展工具定义源)。</param>
    public GroupService(InstanceManager instances, ExtensionService extensions)
    {
        _instances = instances;
        _extensions = extensions;
        _invoker = new GroupToolInvoker(instances);
    }

    /// <summary>
    /// 工具面变更信号:任意会话真实改变全局激活状态后触发(discover_tools 每批至多一次)。
    /// 无状态 HTTP 下本信号是 tools/list_changed 的唯一来源 —— 由挂起的
    /// subscriptions/listen 长流消费(GroupListenStream,issue 12)。
    /// </summary>
    public event Action? ToolSurfaceChanged;

    /// <summary>通报一次工具面变更(仅真实变化时调用;空闲/幂等激活不触发)。</summary>
    public void NotifyToolSurfaceChanged() => ToolSurfaceChanged?.Invoke();

    /// <summary>订阅工具面变更;释放返回值即退订(监听长流随客户端断开而收尾)。</summary>
    public IDisposable SubscribeToolSurface(Action onChanged)
    {
        ToolSurfaceChanged += onChanged;
        return new ToolSurfaceSubscription(this, onChanged);
    }

    private sealed class ToolSurfaceSubscription(GroupService owner, Action onChanged) : IDisposable
    {
        /// <summary>持有人引用;Dispose 后置 null 保证退订恰好执行一次。</summary>
        private GroupService? _owner = owner;

        /// <summary>从持有人事件上摘除回调;幂等(重复 Dispose 无副作用)。</summary>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                owner.ToolSurfaceChanged -= onChanged;
            }
        }
    }

    /// <summary>当前已激活组数量(内置组;含锁,供状态/诊断读取)。</summary>
    public int LoadedCount
    {
        get
        {
            lock (_gate)
            {
                return _loaded.Count;
            }
        }
    }

    /// <summary>查询内置组是否已激活。</summary>
    /// <param name="name">组名。</param>
    /// <returns>已激活为 true(扩展组不在此判定范围)。</returns>
    public bool IsLoaded(string name)
    {
        lock (_gate)
        {
            return _loaded.Contains(name);
        }
    }

    /// <summary>当前请求会话的工具集合(AsyncLocal;仅在该请求的异步上下文内可见)。</summary>
    public McpServerPrimitiveCollection<McpServerTool>? CurrentSessionCollection => _sessionCollection.Value;

    /// <summary>
    /// 会话配置钩子(每个请求调用):捕获当前会话工具集合,并把工具面对齐到
    /// daemon 全局激活状态(幂等)——没这一步,后续请求将看不到已激活的组工具。
    /// </summary>
    public void ConfigureSession(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        _sessionCollection.Value = collection;
        SyncCollection(collection);
    }

    /// <summary>把某工具集合对齐到全局激活状态(活动组工具在、停用组工具不在;含扩展工具与版本门控)。</summary>
    public void SyncCollection(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        lock (_gate)
        {
            foreach (var groupName in _loaded.ToList())
            {
                EnsureGroupToolsLocked(groupName);
            }
            foreach (var groupName in _loadedExtGroups.ToList())
            {
                EnsureExtGroupToolsLocked(groupName);
            }
            EnsureUngroupedExtToolsLocked();
            // 扩展被移除/更新后的陈旧对象清理(下次登记会重建)。
            foreach (var toolName in _toolObjects.Keys.ToList())
            {
                if (_toolObjects[toolName] is ExtensionTool && _extensions.TryGet(toolName) is null)
                {
                    _toolObjects.Remove(toolName);
                }
            }
            foreach (var (toolName, tool) in _toolObjects)
            {
                if (IsToolActiveLocked(toolName) && IsToolProvidable(toolName))
                {
                    collection.TryAdd(tool);
                }
                else
                {
                    collection.Remove(tool);
                }
            }
        }
    }

    /// <summary>
    /// 常驻工具的版本门控(issue 14):当前连接实例集无法提供时,从本请求会话的
    /// tools/list 隐藏 —— 单实例行为与 Node 注册门控一致(无回退);多实例为并集
    /// (任一实例可提供即可见)。逐请求重算,实例上下线/版本变化自然生效。
    /// </summary>
    public void ApplyVersionGates(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        foreach (var tool in collection.ToArray())
        {
            if (!IsToolProvidable(tool.ProtocolTool.Name))
            {
                collection.Remove(tool);
            }
        }
    }

    /// <summary>版本门控可提供性(并集):工具无门槛;有门槛时任一已连接实例需满足。</summary>
    public bool IsToolProvidable(string toolName)
    {
        VersionGate? gate = NodeToolTable.GateForTool(toolName);
        if (gate is null
            && _extensions.TryGet(toolName) is { } command
            && (command.MinGodotVersion is not null || command.MaxGodotVersion is not null))
        {
            gate = new VersionGate(toolName, command.MinGodotVersion, command.MaxGodotVersion);
        }
        // 已连接实例版本全部未知 → 不可验证 → 保守隐藏(Node 注册门控同规)。
        return gate is null || _instances.AnyVersionCompatible(gate.Min, gate.Max);
    }

    // ── 激活 / 停用 / 目录(命令查询分离,Node 同形) ─────────────

    /// <summary>按名称激活工具组(命令);不直接改会话工具面,改全局状态。</summary>
    /// <para>逻辑链:内置目录未命中 → 查扩展组;两者皆无 → 返回 available + Unknown group 文案。
    /// 命中扩展组 → 已激活回 already_loaded,否则记入 _loadedExtGroups 回 activated。
    /// 命中内置组 → unsafe 且未设 GODOT_MCP_UNSAFE=1 时回 available + 禁用文案;
    /// 已激活回 already_loaded;否则记入 _loaded 回 activated(成员以表内已实现工具为准)。
    /// 会话工具面的实际增删由下一请求的 ConfigureSession/SyncCollection 统一对齐。</para>
    /// <param name="groupName">组名(内置或扩展)。</param>
    /// <returns>组状态行(状态为 activated/already_loaded/available 之一)。</returns>
    public GroupStatus Activate(string groupName)
    {
        lock (_gate)
        {
            var group = GroupCatalogue.Find(groupName);
            if (group is null)
            {
                // Node:内置未命中 → 扩展组分发;两者皆无 → available + "Unknown group: …"。
                var ext = _extensions.TryGetGroup(groupName);
                if (ext is null)
                {
                    return new GroupStatus(groupName, "available", [], UnknownGroupMessage + groupName);
                }
                if (_loadedExtGroups.Contains(groupName))
                {
                    return new GroupStatus(groupName, "already_loaded", ext.ToolNames, ext.Description);
                }
                _loadedExtGroups.Add(groupName);
                return new GroupStatus(groupName, "activated", ext.ToolNames, ext.Description);
            }
            if (!IsGroupEnabled(group))
            {
                // Node isGroupEnabled:unsafe 组默认缺席,如实回 available + 禁用说明。
                return new GroupStatus(group.Name, "available", [], UnsafeDisabledMessage);
            }
            var implemented = ImplementedToolsOf(group);
            if (_loaded.Contains(group.Name))
            {
                return new GroupStatus(group.Name, "already_loaded", implemented, group.Description);
            }
            _loaded.Add(group.Name);
            return new GroupStatus(group.Name, "activated", implemented, group.Description);
        }
    }

    /// <summary>停用工具组(命令);未知名的移除请求静默忽略。</summary>
    /// <para>逻辑链:names 为 null(reset 全部)→ 目标 = 已激活内置组 ∪ 已激活扩展组;
    /// 否则逐名尝试从 _loaded 与 _loadedExtGroups 移除,移除成功者计入返回。
    /// 会话工具面的收缩由下一请求的 SyncCollection 对齐。</para>
    /// <param name="names">要停用的组名;null 表示停用全部已激活组。</param>
    /// <returns>本次实际停用的组名列表。</returns>
    public IReadOnlyList<string> Deactivate(IReadOnlyList<string>? names)
    {
        lock (_gate)
        {
            // Node:reset 目标 = 已加载的内置组 + 已加载的扩展组。
            var targets = names is null
                ? _loaded.Concat(_loadedExtGroups).ToList()
                : names.ToList();
            var deactivated = new List<string>();
            foreach (var name in targets)
            {
                if (_loaded.Remove(name) || _loadedExtGroups.Remove(name))
                {
                    deactivated.Add(name);
                }
            }
            return deactivated;
        }
    }

    /// <summary>查询单个组的当前状态(查询,不改状态)。</summary>
    /// <para>逻辑链:内置未命中 → 查扩展组 → 两者皆无回 available + 空成员(null 描述);
    /// 扩展组按 _loadedExtGroups 分辨 already_loaded/available;内置组先过 unsafe 门控,
    /// 再按 _loaded 分辨 already_loaded/available(成员均以已实现工具为准)。</para>
    /// <param name="groupName">组名。</param>
    /// <returns>组状态行。</returns>
    public GroupStatus Report(string groupName)
    {
        lock (_gate)
        {
            var group = GroupCatalogue.Find(groupName);
            if (group is null)
            {
                var ext = _extensions.TryGetGroup(groupName);
                if (ext is null)
                {
                    return new GroupStatus(groupName, "available", [], null);
                }
                return _loadedExtGroups.Contains(groupName)
                    ? new GroupStatus(groupName, "already_loaded", ext.ToolNames, ext.Description)
                    : new GroupStatus(groupName, "available", ext.ToolNames, ext.Description);
            }
            if (!IsGroupEnabled(group))
            {
                return new GroupStatus(group.Name, "available", [], UnsafeDisabledMessage);
            }
            var implemented = ImplementedToolsOf(group);
            return _loaded.Contains(group.Name)
                ? new GroupStatus(group.Name, "already_loaded", implemented, group.Description)
                : new GroupStatus(group.Name, "available", implemented, group.Description);
        }
    }

    /// <summary>完整目录:内置组逐个 Report 后拼接扩展组条目(discover_tools 无参调用)。</summary>
    /// <returns>全部组的状态行(内置在前,扩展在后,均含已加载/可用状态)。</returns>
    public IReadOnlyList<GroupStatus> Catalog()
    {
        // Node:内置组枚举后接扩展组条目。
        return GroupCatalogue.Groups.Select(g => Report(g.Name))
            .Concat(_extensions.Groups().Select(g => Report(g.Name)))
            .ToList();
    }

    /// <summary>名称是否为已知组(内置或扩展;discover_tools 精确/模糊分流用)。</summary>
    /// <param name="name">待判定的组名。</param>
    /// <returns>内置或扩展任一命中即为 true。</returns>
    public bool IsKnownGroup(string name) =>
        GroupCatalogue.Find(name) is not null || _extensions.HasGroup(name);

    /// <summary>已加载组的工具对象(供响应注解毒化/描述/模式充实)。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="tool">命中的工具对象;未命中为 null。</param>
    /// <returns>登记表中存在该工具即为 true。</returns>
    public bool TryGetToolObject(string toolName, out McpServerTool tool)
    {
        lock (_gate)
        {
            return _toolObjects.TryGetValue(toolName, out tool!);
        }
    }

    /// <summary>Node isGroupEnabled:高风险 unsafe 组仅在显式启用时可见。</summary>
    /// <param name="group">待判定的组定义。</param>
    /// <returns>非 unsafe 组恒 true;unsafe 组仅 GODOT_MCP_UNSAFE=1 时 true。</returns>
    private static bool IsGroupEnabled(GroupDef group) =>
        group.Name != "unsafe" || Environment.GetEnvironmentVariable("GODOT_MCP_UNSAFE") == "1";

    // ── 关键词匹配(Node groupMatch 同算式) ─────────────────────

    /// <summary>关键词打分:全等 +3、query 含关键词 +2、关键词含 query(query 至少 3 字符)+1。</summary>
    /// <param name="query">已小写化的查询词。</param>
    /// <param name="keywords">候选关键词表。</param>
    /// <returns>累计得分(0 表示无命中)。</returns>
    public static int MatchKeywords(string query, IReadOnlyList<string> keywords)
    {
        var score = 0;
        foreach (var keyword in keywords)
        {
            if (query == keyword)
            {
                score += 3;
            }
            else if (query.Contains(keyword, StringComparison.Ordinal))
            {
                score += 2;
            }
            else if (keyword.Contains(query, StringComparison.Ordinal) && query.Length >= 3)
            {
                score += 1;
            }
        }
        return score;
    }

    /// <summary>按组内工具名追加打分:归一化名(下划线转空格)包含 query(+1,query 至少 3 字符);
    /// 原名或归一化名与 query 全等则标记精确命中。</summary>
    /// <param name="query">已小写化的查询词。</param>
    /// <param name="toolNames">组内工具名列表。</param>
    /// <returns>Delta 为累计加分,Exact 为是否精确命中某工具名。</returns>
    private static (int Delta, bool Exact) ScoreToolNameTokens(string query, IReadOnlyList<string> toolNames)
    {
        var delta = 0;
        var exact = false;
        foreach (var toolName in toolNames)
        {
            var normalized = toolName.Replace('_', ' ');
            if (normalized.Contains(query, StringComparison.Ordinal) && query.Length >= 3)
            {
                delta += 1;
            }
            if (toolName == query || normalized == query)
            {
                exact = true;
            }
        }
        return (delta, exact);
    }

    /// <summary>单关键词打分 + 主导匹配过滤(Node findMatchesSingle 同算式;含扩展组评分)。</summary>
    /// <para>逻辑链:内置组按关键词/工具名打分,扩展组另加描述分词命中 → 得分大于 0 入选
    /// → 按分数降序排序 → 多于一条时执行主导过滤(保留榜首、精确命中及分数不低于
    /// 榜首一半者)→ 输出名称与分数。</para>
    /// <param name="keyword">原始查询关键词(内部转小写)。</param>
    /// <returns>(组名, 分数) 列表,降序;无命中为空列表。</returns>
    public List<(string Name, int Score)> FindMatchesSingle(string keyword)
    {
        var query = keyword.ToLowerInvariant();
        var matches = new List<(string Name, int Score, bool Exact)>();
        foreach (var group in GroupCatalogue.Groups)
        {
            var score = MatchKeywords(query, group.Keywords);
            var exact = group.Keywords.Contains(query, StringComparer.Ordinal);
            var (delta, toolExact) = ScoreToolNameTokens(query, group.ToolNames);
            score += delta;
            exact = exact || toolExact;
            if (score > 0)
            {
                matches.Add((group.Name, score, exact));
            }
        }

        // 扩展组(Node groupMatch 同算子:关键词 + 描述分词 + 工具名分词)。
        foreach (var ext in _extensions.Groups())
        {
            var score = ext.Keywords.Count > 0 ? MatchKeywords(query, ext.Keywords) : 0;
            var exact = ext.Keywords.Contains(query, StringComparer.Ordinal);
            var description = ext.Description.Length > 0 ? ext.Description : ext.Name;
            foreach (var token in description.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (query == token)
                {
                    score += 2;
                }
                else if (token.Contains(query, StringComparison.Ordinal) && query.Length >= 3)
                {
                    score += 1;
                }
            }
            var (delta, toolExact) = ScoreToolNameTokens(query, ext.ToolNames);
            score += delta;
            exact = exact || toolExact;
            if (score > 0)
            {
                matches.Add((ext.Name, score, exact));
            }
        }

        matches.Sort((a, b) => b.Score.CompareTo(a.Score));

        var kept = matches;
        if (matches.Count > 1)
        {
            var cutoff = matches[0].Score * DominantMatchRatio;
            kept = matches.Where((m, i) => i == 0 || m.Exact || m.Score >= cutoff).ToList();
        }
        return kept.Select(m => (m.Name, m.Score)).ToList();
    }

    /// <summary>模糊结果封顶:每关键词 3 个、总共 5 个(先轮询榜首,再按分数补满)。</summary>
    /// <para>逻辑链:各关键词先截前 3 → 第一轮按关键词顺序轮询取各榜首个未选者 →
    /// 剩余名额按累计分数降序补满 → AdditionalCount = 全部去重命中数 - 选中数。</para>
    /// <param name="perKeyword">各关键词的(组名, 分数)命中列表(已降序)。</param>
    /// <returns>Selected 为最终选中组名(至多 5 个),AdditionalCount 为未入选的去重命中数。</returns>
    public static (List<string> Selected, int AdditionalCount) CapFuzzyResults(
        Dictionary<string, List<(string Name, int Score)>> perKeyword)
    {
        var capped = perKeyword.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Take(FuzzyPerElementCap).ToList());
        var allUnique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var matches in perKeyword.Values)
        {
            foreach (var match in matches)
            {
                allUnique.Add(match.Name);
            }
        }

        var selected = new List<string>();
        foreach (var matches in capped.Values)
        {
            if (selected.Count >= FuzzyTotalCap)
            {
                break;
            }
            var best = matches.FirstOrDefault(m => !selected.Contains(m.Name));
            if (best.Name is not null)
            {
                selected.Add(best.Name);
            }
        }

        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var matches in capped.Values)
        {
            foreach (var match in matches)
            {
                if (!selected.Contains(match.Name))
                {
                    remaining[match.Name] = remaining.GetValueOrDefault(match.Name) + match.Score;
                }
            }
        }
        foreach (var (name, _) in remaining.OrderByDescending(kv => kv.Value))
        {
            if (selected.Count >= FuzzyTotalCap)
            {
                break;
            }
            selected.Add(name);
        }
        return (selected, allUnique.Count - selected.Count);
    }

    /// <summary>请求参数归一化(Node coerceRequest):string 或 string[],含字符串化 JSON 数组。</summary>
    /// <para>逻辑链:数组 → 逐元素取字符串;字符串以 "[" 开头 → 尝试解析为 JSON 数组,
    /// 解析失败(JsonException)落回单元素路径 → 其余字符串按单元素处理;其他值 → 空表。</para>
    /// <param name="request">discover_tools 的原始请求参数。</param>
    /// <returns>归一化后的请求字符串列表(元素可为空串)。</returns>
    public static List<string> CoerceRequest(JsonElement request)
    {
        if (request.ValueKind == JsonValueKind.Array)
        {
            return request.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        if (request.ValueKind == JsonValueKind.String)
        {
            var raw = request.GetString() ?? "";
            if (raw.StartsWith('['))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(raw);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return parsed.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    }
                }
                catch (JsonException)
                {
                    // 落入单元素路径。
                }
            }
            return [raw];
        }
        return [];
    }

    /// <summary>discover_tools 描述(Node buildDiscoverToolsDesc;含 已加载/可用 状态标签与扩展节)。</summary>
    /// <para>逻辑链:遍历内置组(禁用组跳过,不进描述)拼"名称 [已加载|可用] — 描述",
    /// 再拼固定使用说明;扩展组非空时追加"扩展"节(同样带状态标签)。</para>
    /// <returns>组装好的 discover_tools 工具描述文本(以句号结尾)。</returns>
    public string BuildDiscoverToolsDesc()
    {
        var parts = new List<string>();
        foreach (var group in GroupCatalogue.Groups)
        {
            if (!IsGroupEnabled(group))
            {
                // Node 同规矩:禁用组不出现在目录描述里(完整目录仍经无参调用可见)。
                continue;
            }
            var loaded = IsLoaded(group.Name);
            parts.Add($"{group.Name} [{(loaded ? "已加载" : "可用")}] — {group.Description}");
        }

        var description = "按名称或领域关键词查找并激活工具组。" +
                          "仅激活当前任务需要的组，建议不超过约 5 个；同时加载过多工具组会挤满工具列表并降低回答质量。" +
                          "不传参数时返回完整目录。reset: true 停用全部工具组；reset: ['group_a'] 仅停用 group_a。" +
                          "添加、编辑或删除项目扩展后，请设置 refresh_extensions:true。" +
                          "工具组：" + string.Join("; ", parts);

        var extParts = _extensions.Groups()
            .Select(g => $"{g.Name} [{(_loadedExtGroups.Contains(g.Name) ? "已加载" : "可用")}] — {g.Description}")
            .ToList();
        if (extParts.Count > 0)
        {
            description += "。扩展：" + string.Join("; ", extParts);
        }
        return description + "。";
    }

    // ── 工具对象工厂(表驱动;每工具对象全局唯一,跨会话复用) ──────

    /// <summary>该组全部可注册工具(表内成员;64/64 已全实现,issue 13)。</summary>
    /// <param name="group">组定义。</param>
    /// <returns>成员工具名中存在于 NodeToolTable 的子集(保表序)。</returns>
    private static List<string> ImplementedToolsOf(GroupDef group)
    {
        return group.ToolNames.Where(NodeToolTable.Contains).ToList();
    }

    /// <summary>判定工具当前是否应出现在工具面上(持锁调用)。</summary>
    /// <para>逻辑链:任一已激活内置组的成员 → 激活;未分组扩展工具 → 恒激活;
    /// 其余 → 仅当属于某已激活扩展组时激活。</para>
    /// <param name="toolName">工具名。</param>
    /// <returns>应出现在工具面上为 true。</returns>
    private bool IsToolActiveLocked(string toolName)
    {
        if (_loaded.Any(groupName =>
                GroupCatalogue.Find(groupName)?.ToolNames.Contains(toolName) == true))
        {
            return true;
        }
        // 未分组扩展工具始终在面;分组扩展工具随其组加载(Node 同语义)。
        if (_extensions.IsUngrouped(toolName))
        {
            return true;
        }
        return _loadedExtGroups.Any(groupName => _extensions.GroupToolNames(groupName).Contains(toolName));
    }

    /// <summary>确保内置组的工具对象已全部登记(持锁调用;懒建、全局唯一、跨会话复用)。</summary>
    /// <para>逻辑链:逐成员跳过 eager 常驻工具与已登记者 → 表内查不到定义即抛
    /// InvalidOperationException(目录/表不同步的编程错误)→ 否则建 JsonSchemaGroupTool
    /// 存入 _toolObjects(同一工具被多组共享时复用首建对象)。</para>
    /// <param name="groupName">内置组名。</param>
    private void EnsureGroupToolsLocked(string groupName)
    {
        var group = GroupCatalogue.Find(groupName);
        if (group is null)
        {
            return;
        }
        foreach (var toolName in group.ToolNames)
        {
            if (EagerRegisteredToolNames.Contains(toolName) || _toolObjects.ContainsKey(toolName))
            {
                continue;
            }
            if (!NodeToolTable.TryGet(toolName, out var def) || def is null)
            {
                throw new InvalidOperationException($"组工具表缺少工具定义:{toolName}");
            }
            _toolObjects[toolName] = new JsonSchemaGroupTool(def, _invoker);
        }
    }

    /// <summary>扩展组工具对象(懒建;cmd 被原位更新时重建对象)。</summary>
    /// <param name="groupName">扩展组名。</param>
    private void EnsureExtGroupToolsLocked(string groupName)
    {
        foreach (var toolName in _extensions.GroupToolNames(groupName))
        {
            EnsureExtToolObjectLocked(toolName);
        }
    }

    /// <summary>未分组扩展工具对象(登记后即始终可见)。</summary>
    private void EnsureUngroupedExtToolsLocked()
    {
        foreach (var command in _extensions.UngroupedTools())
        {
            EnsureExtToolObjectLocked(command.ToolName);
        }
    }

    /// <summary>确保单个扩展工具对象就绪(持锁调用):eager/未登记跳过;命令被原位替换时重建。</summary>
    /// <para>逻辑链:eager 常驻或扩展表中查不到 → 直接返回;已登记且 Command 引用一致 → 复用;
    /// 已登记但 Command 已替换(扩展热更新)→ 以新命令重建 ExtensionTool;首次 → 新建登记。</para>
    /// <param name="toolName">扩展工具名。</param>
    private void EnsureExtToolObjectLocked(string toolName)
    {
        if (EagerRegisteredToolNames.Contains(toolName) || _extensions.TryGet(toolName) is not { } command)
        {
            return;
        }
        if (_toolObjects.TryGetValue(toolName, out var existing))
        {
            if (existing is ExtensionTool tool && !ReferenceEquals(tool.Command, command))
            {
                _toolObjects[toolName] = new ExtensionTool(command, _extensions);
            }
            return;
        }
        _toolObjects[toolName] = new ExtensionTool(command, _extensions);
    }

    /// <summary>include_schemas 充实的工具元数据(Node ToolMeta 子集:name/description/parameters)。</summary>
    /// <para>逻辑链:基础仅 name;includeSchemas 为真 → 追加 description、逐参数的
    /// type/required/description(integer/number 归并为 number)与四项 annotations 提示
    /// (schema 无 required 字段时参数视为非必填)。</para>
    /// <param name="tool">目标工具对象。</param>
    /// <param name="includeSchemas">是否充实 schema/annotations 元数据。</param>
    /// <returns>组装好的元数据 JSON 对象。</returns>
    public static JsonObject BuildToolMeta(McpServerTool tool, bool includeSchemas)
    {
        var meta = new JsonObject { ["name"] = tool.ProtocolTool.Name };
        if (!includeSchemas)
        {
            return meta;
        }
        meta["description"] = tool.ProtocolTool.Description;
        var parameters = new JsonObject();
        if (tool.ProtocolTool.InputSchema.ValueKind == JsonValueKind.Object
            && tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (tool.ProtocolTool.InputSchema.TryGetProperty("required", out var requiredEl)
                && requiredEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in requiredEl.EnumerateArray())
                {
                    required.Add(entry.GetString() ?? "");
                }
            }
            foreach (var property in properties.EnumerateObject())
            {
                var type = property.Value.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()!
                    : "string";
                if (type == "integer" || type == "number")
                {
                    type = "number";
                }
                var info = new JsonObject { ["type"] = type, ["required"] = required.Contains(property.Name) };
                if (property.Value.TryGetProperty("description", out var descriptionEl)
                    && descriptionEl.ValueKind == JsonValueKind.String)
                {
                    info["description"] = descriptionEl.GetString();
                }
                parameters[property.Name] = info;
            }
        }
        meta["parameters"] = parameters;
        var annotations = new JsonObject();
        if (tool.ProtocolTool.Annotations is { } annotation)
        {
            if (annotation.ReadOnlyHint is { } readOnly) annotations["readOnlyHint"] = readOnly;
            if (annotation.DestructiveHint is { } destructive) annotations["destructiveHint"] = destructive;
            if (annotation.IdempotentHint is { } idempotent) annotations["idempotentHint"] = idempotent;
            if (annotation.OpenWorldHint is { } openWorld) annotations["openWorldHint"] = openWorld;
        }
        meta["annotations"] = annotations;
        return meta;
    }
}
