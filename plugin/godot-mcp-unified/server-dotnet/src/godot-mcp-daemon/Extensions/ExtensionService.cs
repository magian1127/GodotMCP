using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Extensions;

/// <summary>扩展命令的上下文协议注解(插件未提供时逐项默认 false,Node extensionCommand.ts 同规)。</summary>
/// <param name="ReadOnly">只读提示(readOnlyHint):命令不改动编辑器/游戏状态。</param>
/// <param name="Destructive">破坏性提示(destructiveHint):命令可能造成不可逆变更。</param>
/// <param name="Idempotent">幂等提示(idempotentHint):重复调用与单次调用效果相同。</param>
public sealed record ExtensionAnnotations(bool ReadOnly, bool Destructive, bool Idempotent);

/// <summary>
/// 一条扩展命令(Node ExtensionCmdWire 的 daemon 模型):
/// method → toolName 的点号映射("a.b" → "a_b")在构造时固化。
/// </summary>
/// <param name="Method">插件侧方法名(如 "a.b"),执行时原样发回实例。</param>
/// <param name="ToolName">MCP 工具名:method 的点号映射("a.b" → "a_b"),登记表主键。</param>
/// <param name="Description">工具描述;插件未提供时由 ParseCommand 填默认文案。</param>
/// <param name="InputSchema">入参 JSON Schema;缺失时回退为空 object schema。</param>
/// <param name="Annotations">上下文协议注解,缺失项默认 false。</param>
/// <param name="MinGodotVersion">Godot 版本门控下限;null 表示不限。</param>
/// <param name="MaxGodotVersion">Godot 版本门控上限;null 表示不限。</param>
/// <param name="TimeoutMs">执行超时(毫秒);null 时用 DefaultTimeoutMs。</param>
/// <param name="GroupName">所属扩展组名;null 表示未分组。</param>
/// <param name="GroupDescription">组描述(仅建组时生效,合并语义见 EnsureGroupLocked)。</param>
/// <param name="GroupKeywords">组关键词(建组时并入,检索用)。</param>
public sealed record ExtensionCommand(
    string Method,
    string ToolName,
    string Description,
    JsonElement InputSchema,
    ExtensionAnnotations Annotations,
    string? MinGodotVersion,
    string? MaxGodotVersion,
    int? TimeoutMs,
    string? GroupName,
    string? GroupDescription,
    IReadOnlyList<string> GroupKeywords);

/// <summary>扩展组快照(名称/描述/关键词/工具名;只读视图)。</summary>
/// <param name="Name">组名(Ordinal 主键)。</param>
/// <param name="Description">组描述。</param>
/// <param name="Keywords">组关键词。</param>
/// <param name="ToolNames">组内工具名(按登记顺序)。</param>
public sealed record ExtensionGroupSnapshot(
    string Name, string Description, IReadOnlyList<string> Keywords, IReadOnlyList<string> ToolNames);

/// <summary>discover_tools refresh_extensions 的结算摘要(Node ExtensionRefreshSummary 同形)。</summary>
/// <param name="Registered">本次新登记的未分组工具数。</param>
/// <param name="Deferred">本次新登记且暂归扩展组的工具数。</param>
/// <param name="Commands">全部实例拉取到的命令总数(含重复与被跳过者)。</param>
/// <param name="Hint">插件回传的提示文本(如"请刷新编辑器");取本轮首个非空。</param>
public sealed record ExtensionRefreshSummary(int Registered, int Deferred, int Commands, string? Hint);

/// <summary>
/// 扩展子系统(Node src/extensions/* 的 daemon 移植):把 addon 推送的第三方扩展
/// 投影进 daemon 工具面。
///
/// 与 Node 单实例桥的差异(多实例语义):extensions.refresh 对全部已连接实例逐个拉取
/// 并做并集登记,首次登记者保名(extensionNameCollides 同规);extensions.changed
/// 通知按实例键到达后全局应用(removed 方法移除 / commands 新增或原位更新)。
/// 扩展工具的执行经 Editor 通道直连其声明实例(方法与版本门控由实例管理器统一把关)。
/// </summary>
/// <param name="instances">实例管理器:提供已连接实例快照、跨实例调用与版本门控解析入口。</param>
/// <param name="logger">结构化日志。</param>
public sealed class ExtensionService(InstanceManager instances, ILogger<ExtensionService> logger)
{
    /// <summary>未分组扩展工具的默认超时(Node DEFAULT_EXTENSION_TIMEOUT_MS)。</summary>
    private const int DefaultTimeoutMs = 30_000;

    /// <summary>全局锁:保护下方三张登记结构;读写一律持锁(方法名以 Locked 结尾表示调用前已持锁)。</summary>
    private readonly object _gate = new();
    /// <summary>已登记命令表,键为工具名(Ordinal);与 _ungroupedOrder/_groups 保持一致。</summary>
    private readonly Dictionary<string, ExtensionCommand> _entries = new(StringComparer.Ordinal);
    /// <summary>扩展组表,键为组名(Ordinal);组内工具清空时整组移除。</summary>
    private readonly Dictionary<string, ExtGroup> _groups = new(StringComparer.Ordinal);
    /// <summary>未分组工具名,按首次登记顺序排列(供工具面稳定输出)。</summary>
    private readonly List<string> _ungroupedOrder = new();

    /// <summary>扩展组的内部可变模型(对外只以 ExtensionGroupSnapshot 只读快照暴露)。</summary>
    private sealed class ExtGroup
    {
        /// <summary>组名(创建后不变)。</summary>
        public required string Name { get; init; }
        /// <summary>组描述;同名组合并时按 Node 语义以 "; " 追加。</summary>
        public required string Description { get; set; }
        /// <summary>组关键词(合并时 Ordinal 去重)。</summary>
        public List<string> Keywords { get; } = new();
        /// <summary>组内工具名(按登记顺序;末位工具移出后整组销毁)。</summary>
        public List<string> ToolNames { get; } = new();
    }

    // ── 只读视图(工具面/目录/匹配消费)─────────────────────────

    /// <summary>查询某工具名当前是否为未分组扩展(extension)工具。</summary>
    /// <param name="toolName">MCP 工具名。</param>
    /// <returns>在未分组清单中返回 true;否则(含内置/组内工具)false。</returns>
    public bool IsUngrouped(string toolName)
    {
        lock (_gate)
        {
            return _ungroupedOrder.Contains(toolName);
        }
    }

    /// <summary>全部未分组扩展工具(按登记顺序的只读快照)。</summary>
    /// <returns>命令定义快照列表;无未分组工具时为空列表。</returns>
    public IReadOnlyList<ExtensionCommand> UngroupedTools()
    {
        lock (_gate)
        {
            return _ungroupedOrder.Select(n => _entries[n]).ToList();
        }
    }

    /// <summary>按工具名查已登记的扩展命令(工具执行前取最新定义用)。</summary>
    /// <param name="toolName">MCP 工具名。</param>
    /// <returns>命中返回命令定义;未登记返回 null。</returns>
    public ExtensionCommand? TryGet(string toolName)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(toolName);
        }
    }

    /// <summary>查询扩展组名是否已存在(组为空即被移除,届时返回 false)。</summary>
    /// <param name="name">组名。</param>
    /// <returns>存在返回 true。</returns>
    public bool HasGroup(string name)
    {
        lock (_gate)
        {
            return _groups.ContainsKey(name);
        }
    }

    /// <summary>取单个扩展组的只读快照。</summary>
    /// <param name="name">组名。</param>
    /// <returns>命中返回快照(内部列表均拷贝);组不存在返回 null。</returns>
    public ExtensionGroupSnapshot? TryGetGroup(string name)
    {
        lock (_gate)
        {
            return _groups.TryGetValue(name, out var group)
                ? new ExtensionGroupSnapshot(group.Name, group.Description, group.Keywords.ToList(), group.ToolNames.ToList())
                : null;
        }
    }

    /// <summary>全部扩展组的只读快照(供 discover_tools 列组)。</summary>
    /// <returns>快照列表;无组时为空列表。</returns>
    public IReadOnlyList<ExtensionGroupSnapshot> Groups()
    {
        lock (_gate)
        {
            return _groups.Values
                .Select(g => new ExtensionGroupSnapshot(g.Name, g.Description, g.Keywords.ToList(), g.ToolNames.ToList()))
                .ToList();
        }
    }

    /// <summary>某扩展组当前的全部工具名(按登记顺序)。</summary>
    public IReadOnlyList<string> GroupToolNames(string name)
    {
        lock (_gate)
        {
            return _groups.TryGetValue(name, out var group) ? group.ToolNames.ToList() : [];
        }
    }

    /// <summary>扩展方法的版本门控(实例管理器动态门控解析入口)。</summary>
    public VersionGate? GateForMethod(string method)
    {
        lock (_gate)
        {
            var entry = _entries.Values.FirstOrDefault(
                c => c.Method == method && (c.MinGodotVersion is not null || c.MaxGodotVersion is not null));
            return entry is null ? null : new VersionGate(entry.ToolName, entry.MinGodotVersion, entry.MaxGodotVersion);
        }
    }

    // ── 发现(refresh_extensions / 启动路径)────────────────────

    /// <summary>
    /// 对全部已连接实例拉取扩展(Node discoverExtensions 的多实例版):
    /// extensions.refresh 优先,旧插件回退 extensions.list;单实例失败静默跳过(不算错误)。
    /// <para>逻辑链:遍历实例快照 → 未连接者跳过 → 逐实例 FetchCommandsAsync(返回 null 即静默跳过)→
    /// 累计命令数并记录首个 hint → 每条命令 RegisterOrUpdate:新登记未分组计 registered、
    /// 新登记入组计 deferred、其余不计数 → 有登记/暂入则打信息日志 → 返回结算摘要。</para>
    /// </summary>
    /// <param name="ct">取消令牌(取消即中断整轮刷新)。</param>
    /// <returns>本次刷新的登记/暂入/命令数与首个插件 hint。</returns>
    public async Task<ExtensionRefreshSummary> RefreshAllAsync(CancellationToken ct)
    {
        var registered = 0;
        var deferred = 0;
        var commands = 0;
        string? hint = null;

        foreach (var connection in instances.Snapshot())
        {
            if (!connection.Connected)
            {
                continue;
            }
            var result = await FetchCommandsAsync(connection.Key, ct);
            if (result is null)
            {
                continue;
            }
            var (list, listHint) = result.Value;
            commands += list.Count;
            hint ??= listHint;
            foreach (var command in list)
            {
                var outcome = RegisterOrUpdate(command);
                if (outcome == RegisterOutcome.RegisteredUngrouped)
                {
                    registered++;
                }
                else if (outcome == RegisterOutcome.DeferredInGroup)
                {
                    deferred++;
                }
            }
        }

        if (registered > 0 || deferred > 0)
        {
            logger.LogInformation("extensions: {Registered} registered + {Deferred} deferred in groups", registered, deferred);
        }
        return new ExtensionRefreshSummary(registered, deferred, commands, hint);
    }

    /// <summary>
    /// 从单个实例拉取扩展命令清单。
    /// <para>逻辑链:先调 extensions.refresh(强制文件系统扫描,即使编辑器未聚焦)→
    /// 该调用抛 InstanceCallException(旧插件无此接口)则回退 extensions.list →
    /// 回退仍失败(编辑器不可达等)返回 null → 响应须含 success=true 与 commands 数组,
    /// 否则返回 null → 逐条 ParseCommand,畸形项直接丢弃 → 附带 hint(仅字符串形态才取)。</para>
    /// </summary>
    /// <param name="instanceKey">实例键(经 InstanceManager 寻址)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(命令清单, hint);实例不可达或响应不合格式时为 null。</returns>
    private async Task<(List<ExtensionCommand> Commands, string? Hint)?> FetchCommandsAsync(string instanceKey, CancellationToken ct)
    {
        JsonElement result;
        try
        {
            // 强制文件系统扫描(即使编辑器未聚焦);旧插件回退 list(Node 同回退纪律)。
            try
            {
                result = await instances.CallInstanceAsync(
                    instanceKey, "extensions.refresh", "{}", TimeSpan.FromSeconds(5), ct);
            }
            catch (InstanceCallException)
            {
                result = await instances.CallInstanceAsync(
                    instanceKey, "extensions.list", "{}", TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (InstanceCallException)
        {
            // 编辑器不可达或接口不可用 —— 不算错误(Node discoverExtensions 同规)。
            return null;
        }

        if (!result.TryGetProperty("success", out var successEl) || successEl.ValueKind != JsonValueKind.True
            || !result.TryGetProperty("commands", out var commandsEl) || commandsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<ExtensionCommand>();
        foreach (var entry in commandsEl.EnumerateArray())
        {
            if (ParseCommand(entry) is { } command)
            {
                list.Add(command);
            }
        }
        var hint = result.TryGetProperty("hint", out var hintEl) && hintEl.ValueKind == JsonValueKind.String
            ? hintEl.GetString()
            : null;
        return (list, hint);
    }

    /// <summary>
    /// 把插件回传的一条命令 JSON 解析为 ExtensionCommand(宽容解析,坏项返回 null 而非抛出)。
    /// <para>逻辑链:须为对象且含字符串 method,否则返回 null → toolName = method 点号替换为下划线 →
    /// description 缺失时用默认文案 → input_schema 非对象时回退空 object schema(Clone 脱离原文档)→
    /// annotations 逐项 strict-true 读取,缺失默认 false → group 仅在含非空字符串 name 时才算入组,
    /// description/keywords 缺失可空 → min/max_godot_version 取字符串或 null,timeout_ms 取数字或 null。</para>
    /// </summary>
    /// <param name="entry">commands 数组中的一个元素。</param>
    /// <returns>解析成功的命令定义;method 缺失或类型不符等畸形输入返回 null。</returns>
    private static ExtensionCommand? ParseCommand(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var method = methodEl.GetString()!;
        var toolName = method.Replace('.', '_');
        var description = entry.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
            && descEl.GetString() is { Length: > 0 } text
            ? text
            : $"扩展工具：{method}";
        JsonElement inputSchema;
        if (entry.TryGetProperty("input_schema", out var schemaEl) && schemaEl.ValueKind == JsonValueKind.Object)
        {
            inputSchema = schemaEl.Clone();
        }
        else
        {
            // 回退空对象 schema;临时文档用完即弃(Clone 出独立元素后立刻释放)。
            using var fallback = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            inputSchema = fallback.RootElement.Clone();
        }
        var annotations = new ExtensionAnnotations(false, false, false);
        if (entry.TryGetProperty("annotations", out var annotationsEl) && annotationsEl.ValueKind == JsonValueKind.Object)
        {
            annotations = new ExtensionAnnotations(
                Bool(annotationsEl, "readOnlyHint"),
                Bool(annotationsEl, "destructiveHint"),
                Bool(annotationsEl, "idempotentHint"));
        }
        string? groupName = null;
        string? groupDescription = null;
        IReadOnlyList<string> groupKeywords = [];
        if (entry.TryGetProperty("group", out var groupEl) && groupEl.ValueKind == JsonValueKind.Object
            && groupEl.TryGetProperty("name", out var groupNameEl) && groupNameEl.ValueKind == JsonValueKind.String
            && groupNameEl.GetString() is { Length: > 0 } name)
        {
            groupName = name;
            groupDescription = groupEl.TryGetProperty("description", out var gdEl) && gdEl.ValueKind == JsonValueKind.String
                ? gdEl.GetString()
                : null;
            if (groupEl.TryGetProperty("keywords", out var kwEl) && kwEl.ValueKind == JsonValueKind.Array)
            {
                groupKeywords = kwEl.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString()!)
                    .ToList();
            }
        }
        return new ExtensionCommand(
            method,
            toolName,
            description,
            inputSchema,
            annotations,
            StringOrNull(entry, "min_godot_version"),
            StringOrNull(entry, "max_godot_version"),
            entry.TryGetProperty("timeout_ms", out var timeoutEl) && timeoutEl.ValueKind == JsonValueKind.Number
                ? timeoutEl.GetInt32()
                : null,
            groupName,
            groupDescription,
            groupKeywords);
    }

    // ── 变更应用(extensions.changed)──────────────────────────

    /// <summary>
    /// 应用一条 extensions.changed(removed 方法移除;commands 新增或原位更新)。
    /// 返回是否发生了任何变化(供调用方触发 tools/list_changed)。
    /// <para>逻辑链:params 须为对象且含 commands 数组,否则告警并返回 false → 持锁依次应用:
    /// removed 数组逐方法 RemoveByMethodLocked → commands 数组逐条 ParseCommand 后
    /// RegisterOrUpdateLocked(仅非 Skipped 记为变化)→ 有变化打信息日志 → 返回 changed。</para>
    /// </summary>
    /// <param name="notificationParams">extensions.changed 通知的 params 节点。</param>
    /// <returns>有移除或新增/更新生效时 true;载荷无效或无实际变化时 false。</returns>
    public bool ApplyChanged(JsonElement notificationParams)
    {
        if (notificationParams.ValueKind != JsonValueKind.Object
            || !notificationParams.TryGetProperty("commands", out var commandsEl)
            || commandsEl.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("extensions.changed: invalid payload (no commands array)");
            return false;
        }

        var changed = false;
        lock (_gate)
        {
            if (notificationParams.TryGetProperty("removed", out var removedEl) && removedEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var methodEl in removedEl.EnumerateArray())
                {
                    if (methodEl.ValueKind == JsonValueKind.String && RemoveByMethodLocked(methodEl.GetString()!))
                    {
                        changed = true;
                    }
                }
            }

            foreach (var commandEl in commandsEl.EnumerateArray())
            {
                var command = ParseCommand(commandEl);
                if (command is null)
                {
                    continue;
                }
                if (RegisterOrUpdateLocked(command) != RegisterOutcome.Skipped)
                {
                    changed = true;
                }
            }
        }
        if (changed)
        {
            logger.LogInformation("extensions.changed applied");
        }
        return changed;
    }

    // ── 登记(首次保名;原位更新)─────────────────────────────

    /// <summary>登记一条命令的结算结果(供 refresh 统计与 changed 判定变化)。</summary>
    private enum RegisterOutcome
    {
        /// <summary>新登记且未分组(计入 refresh 摘要的 registered)。</summary>
        RegisteredUngrouped,
        /// <summary>新登记且归属扩展组(计入 refresh 摘要的 deferred)。</summary>
        DeferredInGroup,
        /// <summary>已存在且定义被原位更新(不计数,但算变化)。</summary>
        Updated,
        /// <summary>被跳过:与内置工具重名,或 toolName 已登记但 method 不同(不计数)。</summary>
        Skipped,
    }

    /// <summary>登记/原位更新一条命令的加锁入口(语义见 RegisterOrUpdateLocked)。</summary>
    /// <param name="command">待登记命令。</param>
    /// <returns>登记结算结果。</returns>
    private RegisterOutcome RegisterOrUpdate(ExtensionCommand command)
    {
        lock (_gate)
        {
            return RegisterOrUpdateLocked(command);
        }
    }

    /// <summary>
    /// 登记或原位更新一条命令(须持 _gate)。校验/保名/组迁移全链:
    /// <para>逻辑链:未登记过 → 与内置工具重名(NodeToolTable.Contains)则 Skipped(现有者保名),
    /// 否则写入 _entries;
    /// 已登记 → method 不一致(视为 toolName 撞名)则 Skipped,否则原位替换定义,且旧组归属
    /// 与新组归属不同时先从旧组移除 → 按新定义落位:有 GroupName 则 EnsureGroupLocked 并入组、
    /// 从未分组清单摘除,新登记返回 DeferredInGroup(旧为 Updated);无 GroupName 则加入未分组清单,
    /// 新登记返回 RegisteredUngrouped(旧为 Updated)。</para>
    /// </summary>
    /// <param name="command">待登记命令。</param>
    /// <returns>登记结算结果。</returns>
    private RegisterOutcome RegisterOrUpdateLocked(ExtensionCommand command)
    {
        var known = _entries.TryGetValue(command.ToolName, out var existing);
        if (!known)
        {
            // 与内置工具重名(NodeToolTable.Contains 只查内置表)→ 跳过;
            // 与已登记扩展撞名走下方 else 的 method 分支,不在此处。
            if (NodeToolTable.Contains(command.ToolName))
            {
                logger.LogWarning(
                    "extension tool '{Tool}' collides with a built-in tool — skipped",
                    command.ToolName);
                return RegisterOutcome.Skipped;
            }
            _entries[command.ToolName] = command;
        }
        else
        {
            // 已知工具:原位更新定义;组归属变化时迁移。
            if (!string.Equals(existing!.Method, command.Method, StringComparison.Ordinal))
            {
                return RegisterOutcome.Skipped;
            }
            _entries[command.ToolName] = command;
            if (existing.GroupName is not null && !string.Equals(existing.GroupName, command.GroupName, StringComparison.Ordinal))
            {
                RemoveFromGroupLocked(existing.GroupName, command.ToolName);
            }
            if (command.GroupName is null && existing.GroupName is null)
            {
                return RegisterOutcome.Updated;
            }
            if (command.GroupName is null && existing.GroupName is not null)
            {
                AddUngroupedLocked(command.ToolName);
                return RegisterOutcome.Updated;
            }
        }

        if (command.GroupName is { } groupName)
        {
            var group = EnsureGroupLocked(groupName, command.GroupDescription, command.GroupKeywords);
            if (!group.ToolNames.Contains(command.ToolName))
            {
                group.ToolNames.Add(command.ToolName);
            }
            _ungroupedOrder.Remove(command.ToolName);
            return known ? RegisterOutcome.Updated : RegisterOutcome.DeferredInGroup;
        }

        AddUngroupedLocked(command.ToolName);
        return known ? RegisterOutcome.Updated : RegisterOutcome.RegisteredUngrouped;
    }

    /// <summary>把工具名加入未分组清单(已存在则幂等不动;须持锁)。</summary>
    /// <param name="toolName">MCP 工具名。</param>
    private void AddUngroupedLocked(string toolName)
    {
        if (!_ungroupedOrder.Contains(toolName))
        {
            _ungroupedOrder.Add(toolName);
        }
    }

    /// <summary>取组或建组;已存在时按 Node addExtensionGroup 合并语义就地更新(须持锁)。</summary>
    /// <param name="name">组名(Ordinal 主键)。</param>
    /// <param name="description">组描述;建组缺失时以组名兜底,合并时非空且不同则以 "; " 追加。</param>
    /// <param name="keywords">组关键词;并入时 Ordinal 去重。</param>
    /// <returns>命中或新建的组模型。</returns>
    private ExtGroup EnsureGroupLocked(string name, string? description, IReadOnlyList<string> keywords)
    {
        if (!_groups.TryGetValue(name, out var group))
        {
            group = new ExtGroup
            {
                Name = name,
                Description = description ?? name,
            };
            foreach (var keyword in keywords)
            {
                group.Keywords.Add(keyword);
            }
            _groups[name] = group;
            return group;
        }
        // Node addExtensionGroup 合并语义:描述不同则追加;关键词去重并入。
        if (!string.IsNullOrEmpty(description) && description != group.Description)
        {
            group.Description = group.Description + "; " + description;
        }
        foreach (var keyword in keywords)
        {
            if (!group.Keywords.Contains(keyword, StringComparer.Ordinal))
            {
                group.Keywords.Add(keyword);
            }
        }
        return group;
    }

    /// <summary>按插件方法名移除命令(extensions.changed removed 路径;须持锁)。</summary>
    /// <param name="method">插件侧方法名。</param>
    /// <returns>找到并移除返回 true(同步清理未分组清单与所属组);无此方法返回 false。</returns>
    private bool RemoveByMethodLocked(string method)
    {
        var entry = _entries.Values.FirstOrDefault(c => c.Method == method);
        if (entry is null)
        {
            return false;
        }
        _entries.Remove(entry.ToolName);
        _ungroupedOrder.Remove(entry.ToolName);
        if (entry.GroupName is not null)
        {
            RemoveFromGroupLocked(entry.GroupName, entry.ToolName);
        }
        return true;
    }

    /// <summary>把工具移出指定组;组因此变空时整组销毁(须持锁)。</summary>
    /// <param name="groupName">组名。</param>
    /// <param name="toolName">要移出的工具名。</param>
    private void RemoveFromGroupLocked(string groupName, string toolName)
    {
        if (_groups.TryGetValue(groupName, out var group))
        {
            group.ToolNames.Remove(toolName);
            if (group.ToolNames.Count == 0)
            {
                _groups.Remove(groupName);
            }
        }
    }

    // ── 执行(Node registerExtensionTool/callAndWrap 同语义)────

    /// <summary>扩展工具的调用:params = schema 声明键(zod 剥离语义);超时提示为 Node 原文。
    /// <para>逻辑链:DeclaredParams 按 schema 声明键过滤入参(剥掉 instance)→ 超时取 timeout_ms,
    /// 否则用默认 30s → 经实例管理器调用 command.Method → 成功则包装 toolkit 结果 →
    /// 异常分支:InstanceCallException.Code 为 TIMEOUT 返回 TIMEOUT 错误(附 TimeoutHint 文案),
    /// 其余交 ToolResults.FromException 归一。</para>
    /// </summary>
    /// <param name="instance">目标实例键;null 时由实例管理器自选(单实例直连/默认实例)。</param>
    /// <param name="command">要执行的扩展命令(调用方应传登记表最新定义)。</param>
    /// <param name="rawArgs">MCP 调用的原始入参(含 instance;按 schema 声明过滤)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CallToolResult:成功包装 toolkit 结果;错误(含超时)亦以错误结果表达,不抛出。</returns>
    public async Task<CallToolResult> CallAsync(
        string? instance, ExtensionCommand command, IDictionary<string, JsonElement>? rawArgs, CancellationToken ct)
    {
        var parameters = DeclaredParams(command.InputSchema, rawArgs ?? new Dictionary<string, JsonElement>());
        var timeout = command.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMilliseconds(DefaultTimeoutMs);
        try
        {
            var result = await instances.CallInstanceAsync(instance, command.Method, parameters.ToJsonString(), timeout, ct);
            return ToolResults.FromToolkitResult(result);
        }
        catch (InstanceCallException ex)
        {
            if (ex.Code == "TIMEOUT")
            {
                return ToolResults.Error("TIMEOUT", ex.Message, TimeoutHint(command));
            }
            return ToolResults.FromException(ex);
        }
    }

    /// <summary>超时错误的提示文案(Node 原文):区分作者自定义超时与默认 30s 两种措辞。</summary>
    /// <param name="command">超时的扩展命令。</param>
    /// <returns>给宿主模型的解释与建议(提高 timeout_ms,或改造为"启动后轮询"式工具)。</returns>
    private static string TimeoutHint(ExtensionCommand command)
    {
        if (command.TimeoutMs is { } ms)
        {
            return $"Extension tool '{command.Method}' timed out after {ms}ms (custom timeout). " +
                   "If this exceeds 5 minutes, consider restructuring the tool to start work and return a polling handle rather than blocking the bridge.";
        }
        return $"Extension tool '{command.Method}' timed out after {DefaultTimeoutMs / 1000}s. " +
               "If this tool calls external services, the extension author can increase timeout_ms in registry.add() options.";
    }

    /// <summary>按 schema properties 声明键过滤入参(zod 剥离语义):未声明键与 instance 一律丢弃。</summary>
    /// <param name="schema">命令的入参 JSON Schema。</param>
    /// <param name="args">MCP 调用的原始入参。</param>
    /// <returns>仅含声明键(不含 instance)的 JSON 对象,序列化后作为方法参数下发。</returns>
    private static JsonObject DeclaredParams(JsonElement schema, IDictionary<string, JsonElement> args)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                declared.Add(property.Name);
            }
        }
        var obj = new JsonObject();
        foreach (var (key, value) in args)
        {
            if (key != "instance" && declared.Contains(key))
            {
                obj[key] = JsonNode.Parse(value.GetRawText());
            }
        }
        return obj;
    }

    /// <summary>strict-true 布尔读取:仅字面 JSON true 算真(缺键/非 true 均为 false)。</summary>
    /// <param name="element">所在 JSON 对象。</param>
    /// <param name="key">属性名。</param>
    /// <returns>属性存在且为 JSON true 时 true。</returns>
    private static bool Bool(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>字符串或 null 读取:属性存在且为字符串才返回其值,否则 null。</summary>
    /// <param name="element">所在 JSON 对象。</param>
    /// <param name="key">属性名。</param>
    /// <returns>字符串值或 null。</returns>
    private static string? StringOrNull(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
