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
/// issue 13 全量:17 组 / 85 槽位(83 唯一),表驱动;issue 14:扩展组 + 版本门控并集)。
/// 激活状态<b>按会话键隔离</b>(ADR-0007):每个请求从<b>本键</b>的已激活集合同步
/// 工具面(ConfigureSession),激活动作在当前请求会话的工具集合上触发
/// 一次 tools/list_changed。
/// </summary>
/// <remarks>
/// 完整机制链(发现 → 激活 → 暴露 → 调用 → 通知):
/// ① 发现:内置组来自 GroupCatalogue(静态目录);扩展组来自 ExtensionService(插件登记,
///    含版本门控);discover_tools 元工具按精确名或关键词打分流向 Activate/Catalog。
/// ② 激活/停用:Activate 把组名记入<b>本会话键</b>的已激活集合(内置与扩展分开存);
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
    /// 已由 daemon 常驻注册(eager,<c>WithToolsFromAssembly</c>)且同时属于某个组的工具名(ADR-0007)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么是这 25 个</b>:它们是"常驻且属于组"的精确交集 —— 34 个常驻工具减去
    /// core 9 里不属于任何组的那些(以及 <c>godot_connect</c> 本身)。</para>
    /// <para><b>为什么必须列进来</b>:EnsureGroupToolsLocked 只跳过本集合里的名字,
    /// 其余成员会在组激活时被建成 JsonSchemaGroupTool 影子。而填工具集合时
    /// Add 会抛、TryAdd 是"先加入者胜" —— 两份是<b>不同实现</b>
    /// (C# 的 node_set_property 有复合路由,影子是纯 wire 转发),顺序不同就静默漂移。
    /// 列进来还顺手修掉"停用组时 collection.Remove(tool) 误摘常驻同名工具"的老毛病。</para>
    /// <para><b>代价</b>:discover_tools 的目录对这 25 个只能报名字
    /// (TryGetToolObject 取不到 description/schema);仅在 include_schemas: true 时可见,
    /// 而该参数默认 false。</para>
    /// </remarks>
    private static readonly HashSet<string> EagerRegisteredToolNames = new(StringComparer.Ordinal)
    {
        "editor_answer_dialog",
        "editor_build_csharp",
        "editor_build_csharp_status",
        "editor_build_csharp_wait",
        "editor_save_scene",
        "editor_sync",
        "editor_windows",
        "game_start",
        "game_stop",
        "input_simulate",
        "lsp_completion",
        "lsp_diagnostics",
        "lsp_hover",
        "lsp_navigate",
        "lsp_symbols",
        "node_manage",
        "node_set_property",
        "node_set_script",
        "project_get_settings",
        "runtime_inspect_node",
        "scene_create",
        "scene_create_node",
        "scene_delete_node",
        "scene_open",
        "script_check",
    };

    /// <summary>实例管理器(版本门控可提供性判定用)。</summary>
    private readonly InstanceManager _instances;
    /// <summary>扩展服务(扩展组/扩展工具/版本门控定义源)。</summary>
    private readonly ExtensionService _extensions;
    /// <summary>组工具调用器(所有 JsonSchemaGroupTool 共享)。</summary>
    private readonly GroupToolInvoker _invoker;
    /// <summary>当前请求的会话键载体(AsyncLocal);无键请求回落到 <see cref="DefaultKey"/>。</summary>
    private readonly Sessions.SessionKeyAccessor _keys;
    /// <summary>当前请求的会话态(键 + cwd)的<b>唯一真源</b>。</summary>
    private readonly Sessions.SessionRequestScope _scope;
    /// <summary>保护激活状态与工具对象表的锁。</summary>
    /// <remarks>锁序:会话键注册表的锁<b>先于</b>本锁。工具面填充开始前先取注册表快照,
    /// 不得在本锁内回取注册表,否则与"激活/通知先注册表后本锁"形成 ABBA。</remarks>
    private readonly object _gate = new();
    /// <summary>按会话键隔离的已激活内置组名(ADR-0007)。</summary>
    private readonly Dictionary<string, HashSet<string>> _loadedByKey = new(StringComparer.Ordinal);
    /// <summary>按会话键隔离的已激活扩展组名。</summary>
    private readonly Dictionary<string, HashSet<string>> _loadedExtByKey = new(StringComparer.Ordinal);
    /// <summary>已登记工具名 → 工具对象(全局唯一、跨会话复用;扩展命令更新时原位重建)。</summary>
    /// <remarks>工具对象是 DI 单例级共享的,因此<b>禁止按会话改写</b>其 Description/schema。</remarks>
    private readonly Dictionary<string, McpServerTool> _toolObjects = new(StringComparer.Ordinal);
    /// <summary>当前请求会话的工具集合(AsyncLocal,仅本请求异步上下文内可见)。</summary>
    private readonly AsyncLocal<McpServerPrimitiveCollection<McpServerTool>?> _sessionCollection = new();

    /// <summary>无会话键时使用的桶名。用于裸 HTTP 调试与尚未接入键的调用方。</summary>
    public const string DefaultKey = "";

    /// <summary>注入依赖并预建组工具调用器。</summary>
    /// <param name="instances">实例管理器(组工具经它发起 wire 调用)。</param>
    /// <param name="extensions">扩展服务(扩展组/扩展工具定义源)。</param>
    /// <param name="keys">会话键访问器(兜底读键)。</param>
    /// <param name="scope">请求作用域(会话键与 cwd 的唯一真源)。</param>
    /// <param name="targets">
    /// 按本键默认目标寻址的解析器。<b>本类不直接使用它</b> —— 它的用途是让 DI 在建
    /// <c>GroupService</c> 时就把这个单例实例化出来,好让组合根能立刻给
    /// <c>InstanceManager.DefaultTargetProvider</c> 接线。
    /// </param>
    public GroupService(
        InstanceManager instances,
        ExtensionService extensions,
        Sessions.SessionKeyAccessor keys,
        Sessions.SessionRequestScope scope,
        Tools.SessionTargetResolver targets)
    {
        _ = targets;
        _instances = instances;
        _extensions = extensions;
        _keys = keys;
        _scope = scope;
        _invoker = new GroupToolInvoker(instances);
    }

    /// <summary>当前请求的会话键;无键请求回落到 <see cref="DefaultKey"/>。</summary>
    public string CurrentKey => _scope.CurrentKey ?? _keys.CurrentKey ?? DefaultKey;

    /// <summary>取当前键的已激活内置组集合(持锁调用;首次访问建空桶)。</summary>
    private HashSet<string> LoadedLocked => Bucket(_loadedByKey, CurrentKey);

    /// <summary>取当前键的已激活扩展组集合(持锁调用)。</summary>
    private HashSet<string> LoadedExtLocked => Bucket(_loadedExtByKey, CurrentKey);

    /// <summary>按键取桶,不存在则建空桶。</summary>
    /// <param name="map">键 → 组名集合的表。</param>
    /// <param name="key">会话键。</param>
    /// <returns>该键的集合实例(后续增删直接作用其上)。</returns>
    private static HashSet<string> Bucket(Dictionary<string, HashSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }

        return set;
    }

    /// <summary>
    /// <summary>
    /// 工具面变更信号:<b>带会话键</b>。只有该键自己的 listen 流应该被唤醒。
    /// </summary>
    /// <remarks>
    /// <para>spec §1.7 要求"通知推给<b>该键</b>所有活跃流"。全局广播(不带 key)会让
    /// N 个会话在一次激活后收到 N 条无关通知 —— N² 噪声;会话 B 的工具面根本没变却要重拉一次,
    /// 真正该变的 A 反而被掩盖在噪声里。</para>
    /// <para>键为 <see cref="DefaultKey"/>(空串)时表示"影响全部会话"(扩展热更新/回滚面)。</para>
    /// </remarks>
    public event Action<string>? ToolSurfaceChanged;

    /// <summary>通报一次工具面变更(仅真实变化时调用;空闲/幂等激活不触发)。</summary>
    /// <param name="sessionKey">变化的会话键;null 或空串表示影响全部会话。</param>
    public void NotifyToolSurfaceChanged(string? sessionKey = null)
        => ToolSurfaceChanged?.Invoke(string.IsNullOrEmpty(sessionKey) ? DefaultKey : sessionKey);

    /// <summary>订阅工具面变更;释放返回值即退订(监听长流随客户端断开而收尾)。</summary>
    /// <param name="sessionKey">本订阅关心的会话键;null 或空串表示关心全部变化。</param>
    /// <param name="onChanged">变化回调。</param>
    /// <returns>退订句柄。</returns>
    public IDisposable SubscribeToolSurface(string? sessionKey, Action onChanged)
    {
        var watched = string.IsNullOrEmpty(sessionKey) ? DefaultKey : sessionKey;

        // 只有"本键变了"或"全局变了"才唤醒 —— 别的键变化与本流无关。
        void Handler(string changedKey)
        {
            if (changedKey == watched || changedKey == DefaultKey)
            {
                onChanged();
            }
        }

        ToolSurfaceChanged += Handler;
        return new ToolSurfaceSubscription(this, Handler);
    }

    private sealed class ToolSurfaceSubscription(GroupService owner, Action<string> onChanged) : IDisposable
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
                return LoadedLocked.Count;
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
            return LoadedLocked.Contains(name);
        }
    }

    /// <summary>当前请求会话的工具集合(AsyncLocal;仅在该请求的异步上下文内可见)。</summary>
    public McpServerPrimitiveCollection<McpServerTool>? CurrentSessionCollection => _sessionCollection.Value;

    /// <summary>
    /// 当前请求的会话键(AsyncLocal)。由 <see cref="ConfigureSession"/> 在
    /// <c>ConfigureSessionOptions</c> 里写入 —— 这条路径与 <see cref="_sessionCollection"/>
    /// 完全一致,而后者已被 <c>DiscoverTools</c> 长期使用并验证可读。
    /// </summary>
    /// <remarks>
    /// 会话态本身的载体在 <see cref="Sessions.SessionRequestScope"/> —— 那是**唯一真源**,
    /// 提出来是为了让 <c>SessionTargetResolver</c> 也能读它而不与 GroupService 形成构造环。
    /// </remarks>
    /// <summary>当前请求的会话键;无键请求为 null。</summary>
    public string? CurrentSessionKey => _scope.CurrentKey;

    /// <summary>当前请求的会话工作目录;未知为 null。</summary>
    public string? CurrentWorkingDirectory => _scope.CurrentWorkingDirectory;

    /// <summary>
    /// 忘掉某个会话键的全部组状态(键被回收时调用)。
    /// </summary>
    /// <remarks>
    /// <para>为什么必须清:每个历史键留两个 <c>HashSet</c>,而 daemon 是常驻单例、shim 每次
    /// 启动换 UUID —— 不清就是无界增长。</para>
    /// <para>更隐蔽的后果:同一 shim 用同一 UUID 重连时,<c>godot_connect</c> 建出的新键组集为空
    /// (工具面 = core 9),但只要模型调一次**无参**的 <c>discover_tools()</c>(规范里那是"只浏览"),
    /// 桶里的旧组就会被写回注册表并静默展开 —— 模型从未激活过的工具自己出现。
    /// 清了桶,"浏览"才真的是浏览。</para>
    /// <para><b>绝不清 <see cref="DefaultKey"/></b>:那是无键请求与回滚面的公共桶,
    /// 不归任何会话所有。</para>
    /// </remarks>
    /// <param name="sessionKey">被回收的会话键。</param>
    public void ForgetKey(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey) || sessionKey == DefaultKey)
        {
            return;
        }

        lock (_gate)
        {
            _loadedByKey.Remove(sessionKey);
            _loadedExtByKey.Remove(sessionKey);
        }
    }

    /// <summary>
    /// 会话配置钩子(每个请求调用):捕获当前会话工具集合与会话键,并把工具面对齐到
    /// <b>本会话键</b>的激活状态(幂等)。
    /// </summary>
    /// <param name="collection">本请求的工具集合。</param>
    /// <param name="sessionKey">本请求的会话键;无键请求传 null。</param>
    public void ConfigureSession(McpServerPrimitiveCollection<McpServerTool> collection, string? sessionKey = null, string? workingDirectory = null)
    {
        _sessionCollection.Value = collection;
        // 三态里只区分"没带 header"与"带了":非法键在中间件那层已被识别并记日志,
        // 它无法跨执行上下文传到工具 handler(AsyncLocal 不流过去)。这一分已足够消除
        // 最有害的误导 —— 让"客户端根本没带键"与"带了但还没 connect"给出不同的修复动作。
        _scope.Set(sessionKey, workingDirectory,
            string.IsNullOrEmpty(sessionKey) ? Sessions.RequestKeyShape.Missing : Sessions.RequestKeyShape.Present);
        SyncCollection(collection);
    }

    /// <summary>
    /// 只为<b>本会话已激活</b>的组/扩展组构造工具对象，<b>不碰任何请求的集合</b>。
    /// </summary>
    /// <remarks>
    /// <para>这是 <c>discover_tools</c> 该用的入口（spec §7）。<see cref="SyncCollection"/>
    /// 把"懒建工具对象"和"往当前请求的集合里增删"揉在一起，而后者在每请求新建集合的
    /// 模型下既多余、又是<b>反向</b>的：它会在漂移时先把工具从本次请求的面摘掉，
    /// 而不是留到下一请求由 <see cref="ComposeFace"/> 自愈。</para>
    /// <para><c>discover_tools</c> 只需要前者 —— <c>include_schemas</c> 要能读到刚激活组的
    /// schema，而工具面会由紧接着的 <c>tools/list</c> 经 <c>ConfigureSessionOptions</c> 重算。</para>
    /// </remarks>
    public void EnsureActivatedTools()
    {
        lock (_gate)
        {
            foreach (var groupName in LoadedLocked.ToList())
            {
                EnsureGroupToolsLocked(groupName);
            }
            foreach (var groupName in LoadedExtLocked.ToList())
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
        }
    }

    /// <summary>把某工具集合对齐到全局激活状态(活动组工具在、停用组工具不在;含扩展工具与版本门控)。</summary>
    public void SyncCollection(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        lock (_gate)
        {
            foreach (var groupName in LoadedLocked.ToList())
            {
                EnsureGroupToolsLocked(groupName);
            }
            foreach (var groupName in LoadedExtLocked.ToList())
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
    /// 把<b>本会话键</b>当前的激活集合写回会话键注册表(ADR-0007)。
    /// </summary>
    /// <remarks>
    /// 工具面的可见集是从注册表快照算的(<see cref="ToolFaceComposer"/>),所以任何改动激活状态的
    /// 路径(激活/停用/扩展刷新)都必须调用本方法,否则"激活了组但工具面没变"。
    /// </remarks>
    /// <returns>本键当前的组名(内置 + 扩展,已排序)。</returns>
    public string[] SnapshotGroupsForCurrentKey()
    {
        lock (_gate)
        {
            return LoadedLocked.Concat(LoadedExtLocked)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
    }
    /// <remarks>
    /// <para>组工具四段一个都不能少:内置组 Ensure、扩展组 Ensure、未分组扩展 Ensure、
    /// 陈旧 <see cref="ExtensionTool"/> 清理。缺任一段都会丢面。</para>
    /// <para><b>整体持 <c>_gate</c></b>:<c>_toolObjects</c> 是普通 <c>Dictionary</c>,
    /// 而 <c>Ensure*Locked</c> 要求调用者已持锁。只对"读"加锁会在 Dictionary 上并发写。</para>
    /// <para>锁序:调用方必须<b>先</b>取会话键注册表快照再调本方法(不得在本锁内回取注册表)。</para>
    /// </remarks>
    /// <param name="collection">本请求的工具集合。</param>
    /// <param name="connected">该会话是否已连接(未连接时只留 <c>godot_connect</c>)。</param>
    /// <param name="visible">调用方算好的可见工具名集合。</param>
    /// <param name="order">可见工具名的稳定输出顺序。</param>
    /// <param name="sessionKey">本请求的会话键;无键请求传 null。</param>
    /// <param name="workingDirectory">shim 上报的 cwd;供空参 <c>godot_connect</c> 发现候选项目。</param>
    public void ComposeFace(
        McpServerPrimitiveCollection<McpServerTool> collection,
        bool connected,
        IReadOnlySet<string> visible,
        IReadOnlyList<string> order,
        string? sessionKey = null,
        string? workingDirectory = null)
    {
        _sessionCollection.Value = collection;
        // 三态里只区分"没带 header"与"带了":非法键在中间件那层已被识别并记日志,
        // 它无法跨执行上下文传到工具 handler(AsyncLocal 不流过去)。这一分已足够消除
        // 最有害的误导 —— 让"客户端根本没带键"与"带了但还没 connect"给出不同的修复动作。
        _scope.Set(sessionKey, workingDirectory,
            string.IsNullOrEmpty(sessionKey) ? Sessions.RequestKeyShape.Missing : Sessions.RequestKeyShape.Present);
        lock (_gate)
        {
            if (connected)
            {
                foreach (var groupName in LoadedLocked.ToList())
                {
                    EnsureGroupToolsLocked(groupName);
                }
                foreach (var groupName in LoadedExtLocked.ToList())
                {
                    EnsureExtGroupToolsLocked(groupName);
                }
                EnsureUngroupedExtToolsLocked();
                foreach (var toolName in _toolObjects.Keys.ToList())
                {
                    if (_toolObjects[toolName] is ExtensionTool && _extensions.TryGet(toolName) is null)
                    {
                        _toolObjects.Remove(toolName);
                    }
                }
            }

            // 组工具对象按可见集补进集合(常驻工具已由 assembly 注册在集合里)。
            // **先按 order 走一遍拿稳定顺序,再补齐 order 里没有的可见名** ——
            // order 只覆盖 Node 表内的工具,扩展工具(未分组的、扩展组登记的)不在表里,
            // 若只按 order 填,扩展工具会被后面的裁剪逻辑删掉(它已在 visible 里,却从未被加进集合)。
            foreach (var toolName in order)
            {
                if (visible.Contains(toolName) && _toolObjects.TryGetValue(toolName, out var tool))
                {
                    collection.TryAdd(tool);
                }
            }

            foreach (var toolName in visible)
            {
                if (_toolObjects.TryGetValue(toolName, out var tool))
                {
                    collection.TryAdd(tool);
                }
            }

            // 不在可见集里的一律移除 —— 这是"未连接只看到 godot_connect"的实现点。
            foreach (var tool in collection.ToArray())
            {
                if (!visible.Contains(tool.ProtocolTool.Name))
                {
                    collection.Remove(tool);
                }
            }
        }
    }

    /// <summary>
    /// 本会话键的<b>完整可见集</b>(core ∪ 内置组 ∪ 扩展组 ∪ 未分组扩展)。
    /// </summary>
    /// <remarks>
    /// <para><b>这是可见集的唯一计算入口</b>。工具面组装器(<c>ToolFaceComposer</c>)与
    /// 未命中兜底(<c>ToolCallFallback</c>)都必须调它 —— 两处各算一套必然漂移,
    /// 漂移的后果是"某个工具明明在面上,一调却说它不存在/组未激活"。</para>
    /// <para>内置组由 <see cref="ToolFace.VisibleToolNames"/> 回答(它只认
    /// <see cref="GroupCatalogue"/>),扩展组与未分组扩展不在目录里,必须在这里补 ——
    /// 这是"扩展组激活了、工具却永不出现"的唯一成因。</para>
    /// </remarks>
    /// <param name="activeGroups">本会话键的激活组名;null 表示未连接。</param>
    /// <returns>可见工具名集合(可继续 Add)。</returns>
    public HashSet<string> VisibleToolNamesFor(IEnumerable<string>? activeGroups)
    {
        var visible = ToolFace.VisibleToolNames(activeGroups);
        if (activeGroups is null)
        {
            // 未连接:只有入口工具,绝不回落全量(fail-open)。
            return visible;
        }

        var groups = activeGroups as IReadOnlyList<string> ?? activeGroups.ToList();
        foreach (var name in UngroupedExtensionToolNames())
        {
            visible.Add(name);
        }
        foreach (var name in ExtensionGroupToolNames(groups))
        {
            visible.Add(name);
        }

        return visible;
    }

    /// <summary>
    /// 未分组扩展工具的名字集合(供可见集计算并进)。
    /// </summary>
    /// <remarks>必须在<b>裁剪之前</b>并进可见集 —— 裁剪会移除所有不在可见集里的工具。</remarks>
    /// <returns>未分组扩展工具名。</returns>
    public IReadOnlyList<string> UngroupedExtensionToolNames()
    {
        lock (_gate)
        {
            EnsureUngroupedExtToolsLocked();
            return _toolObjects.Keys
                .Where(name => _extensions.IsUngrouped(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// 已激活<b>扩展组</b>所含的工具名(供工具面组装器并进可见集)。
    /// </summary>
    /// <remarks>
    /// <para><see cref="ToolFace.VisibleToolNames"/> 只认内置组(它只查
    /// <see cref="GroupCatalogue"/>)—— 扩展组不在目录里,所以必须在这里补。</para>
    /// <para>不补的后果:扩展组"激活成功"但工具永远不出现在面上,而调用它们又会落到
    /// 未命中兜底 —— 看起来像"工具不存在"。</para>
    /// </remarks>
    /// <param name="activeGroups">本会话键的激活组名(内置与扩展混在一起)。</param>
    /// <returns>扩展组工具名(去重、稳定排序)。</returns>
    public IReadOnlyList<string> ExtensionGroupToolNames(IEnumerable<string> activeGroups)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var groupName in activeGroups)
        {
            // 内置组由 ToolFace.VisibleToolNames 负责,这里只补扩展组。
            if (GroupCatalogue.Find(groupName) is not null)
            {
                continue;
            }

            if (_extensions.TryGetGroup(groupName) is { } group)
            {
                foreach (var toolName in group.ToolNames)
                {
                    names.Add(toolName);
                }
            }
        }

        return names.ToList();
    }

    /// <summary>
    /// 某工具所属的<b>扩展组</b>名;不在任何扩展组里返回 null。
    /// </summary>
    /// <remarks>
    /// 未命中兜底需要它来区分"扩展组未激活"与"工具名拼错" —— 前者可行动,后者只能让模型去查目录。
    /// 内置组的归属由 <see cref="GroupCatalogue"/> 回答,不走这里。
    /// </remarks>
    /// <param name="toolName">工具名。</param>
    /// <returns>扩展组名;无归属为 null。</returns>
    public string? ExtensionGroupOf(string toolName)
    {
        return _extensions.Groups()
            .FirstOrDefault(g => g.ToolNames.Contains(toolName, StringComparer.Ordinal))
            ?.Name;
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
    /// 命中扩展组 → 已激活回 alreadyLoadedLocked,否则记入 LoadedExtLocked 回 activated。
    /// 命中内置组 → unsafe 且未设 GODOT_MCP_UNSAFE=1 时回 available + 禁用文案;
    /// 已激活回 already_loaded;否则记入本会话键的集合回 activated(成员以表内已实现工具为准)。
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
                if (LoadedExtLocked.Contains(groupName))
                {
                    return new GroupStatus(groupName, "already_loaded", ext.ToolNames, ext.Description);
                }
                LoadedExtLocked.Add(groupName);
                return new GroupStatus(groupName, "activated", ext.ToolNames, ext.Description);
            }
            if (!IsGroupEnabled(group))
            {
                // Node isGroupEnabled:unsafe 组默认缺席,如实回 available + 禁用说明。
                return new GroupStatus(group.Name, "available", [], UnsafeDisabledMessage);
            }
            var implemented = ImplementedToolsOf(group);
            if (LoadedLocked.Contains(group.Name))
            {
                return new GroupStatus(group.Name, "already_loaded", implemented, group.Description);
            }
            LoadedLocked.Add(group.Name);
            return new GroupStatus(group.Name, "activated", implemented, group.Description);
        }
    }

    /// <summary>停用工具组(命令);未知名的移除请求静默忽略。</summary>
    /// <para>逻辑链:names 为 null(reset 全部)→ 目标 = 已激活内置组 ∪ 已激活扩展组;
    /// 否则逐名尝试从 LoadedLocked 与 LoadedExtLocked 移除,移除成功者计入返回。
    /// 会话工具面的收缩由下一请求的 SyncCollection 对齐。</para>
    /// <param name="names">要停用的组名;null 表示停用全部已激活组。</param>
    /// <returns>本次实际停用的组名列表。</returns>
    public IReadOnlyList<string> Deactivate(IReadOnlyList<string>? names)
    {
        lock (_gate)
        {
            // Node:reset 目标 = 已加载的内置组 + 已加载的扩展组。
            var targets = names is null
                ? LoadedLocked.Concat(LoadedExtLocked).ToList()
                : names.ToList();
            var deactivated = new List<string>();
            foreach (var name in targets)
            {
                if (LoadedLocked.Remove(name) || LoadedExtLocked.Remove(name))
                {
                    deactivated.Add(name);
                }
            }
            return deactivated;
        }
    }

    /// <summary>查询单个组的当前状态(查询,不改状态)。</summary>
    /// <para>逻辑链:内置未命中 → 查扩展组 → 两者皆无回 available + 空成员(null 描述);
    /// 扩展组按本会话键的扩展集合分辨 already_loaded/available;内置组先过 unsafe 门控,
    /// 再按本会话键的内置集合分辨 already_loaded/available(成员均以已实现工具为准)。</para>
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
                return LoadedExtLocked.Contains(groupName)
                    ? new GroupStatus(groupName, "already_loaded", ext.ToolNames, ext.Description)
                    : new GroupStatus(groupName, "available", ext.ToolNames, ext.Description);
            }
            if (!IsGroupEnabled(group))
            {
                return new GroupStatus(group.Name, "available", [], UnsafeDisabledMessage);
            }
            var implemented = ImplementedToolsOf(group);
            return LoadedLocked.Contains(group.Name)
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
        if (LoadedLocked.Any(groupName =>
                GroupCatalogue.Find(groupName)?.ToolNames.Contains(toolName) == true))
        {
            return true;
        }
        // 未分组扩展工具始终在面;分组扩展工具随其组加载(Node 同语义)。
        if (_extensions.IsUngrouped(toolName))
        {
            return true;
        }
        return LoadedExtLocked.Any(groupName => _extensions.GroupToolNames(groupName).Contains(toolName));
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
