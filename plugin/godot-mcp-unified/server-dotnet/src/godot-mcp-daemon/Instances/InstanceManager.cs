using System.Collections.Concurrent;
using System.Text.Json;
using GodotMcp.Daemon.Groups;

namespace GodotMcp.Daemon.Instances;

/// <summary>
/// 实例表与注册表 reconcile 循环(issue 05):watch projects.json 增量 + 1s 周期轮询兜底,
/// 对每个活跃条目维护一条出站 WS 连接(<see cref="InstanceConnection"/>),实例上下线
/// 实时反映到 <see cref="ListInstancesJson"/>(list_instances),并向空闲监视器上报
/// 已连接实例数(spec US12:任一实例连接期间 daemon 不空闲退出)。
/// 注册表是只读输入:进程消失的残留条目只从实例表移除,绝不写/删注册表文件。
/// <para>生命周期:ExecuteAsync 起 watcher + 1s 轮询 → Reconcile 按"新键启动 / 关键变化重启 /
/// 键缺失宽限后拆除 / 进程死亡移除"维护编辑器与运行时两组通道字典;StopAll 停机时统一取消与处置。
/// 调用入口(CallInstanceAsync / CallRuntimeAsync / GetLspClientAsync)经 ResolveConnection 寻址后落到对应通道。</para>
/// </summary>
public sealed class InstanceManager : BackgroundService
{
    /// <summary>注册表所在状态目录(只读消费,不写不删)。</summary>
    private readonly string _stateDir;

    /// <summary>空闲监视器:上报已连接实例数(spec US12:任一实例连接期间 daemon 不空闲退出)。</summary>
    private readonly IdleMonitor _idleMonitor;

    /// <summary>结构化日志器(类别 InstanceManager)。</summary>
    private readonly ILogger<InstanceManager> _logger;

    /// <summary>daemon 版本(程序集三段式),随每个连接的鉴权首帧上报。</summary>
    private readonly string _daemonVersion;
    /// <summary>编辑器通道表:键(规范化项目路径)→ 连接(对外"实例表"即此表的投影)。</summary>
    private readonly ConcurrentDictionary<string, InstanceConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>编辑器通道的取消源表(与 _connections 同键同生命周期)。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _connectionCts = new(StringComparer.Ordinal);
    // 运行时通道(Mode B,issue 10):按实例键跟踪;生命周期由注册表 runtime_port 拆装驱动。
    /// <summary>运行时通道表(键同 _connections;生命周期由注册表 runtime_port 拆装驱动)。</summary>
    private readonly ConcurrentDictionary<string, InstanceConnection> _runtime = new(StringComparer.Ordinal);

    /// <summary>运行时通道的取消源表。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runtimeCts = new(StringComparer.Ordinal);
    // 键消失的宽限:真实 addon 与测试替身的原子重写都可能出现"目标短暂缺失"的缝隙
    // (先删/先改名再落位);要求连续缺失超过宽限才拆除,避免把重写误判为下线。
    /// <summary>键缺失宽限(2s):连续缺失超过该时长才拆除(已在上方注释说明动机)。</summary>
    private static readonly TimeSpan MissingGrace = TimeSpan.FromSeconds(2);

    /// <summary>编辑器通道键首次缺失的时刻(宽限计时起点)。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _missingSince = new(StringComparer.Ordinal);

    /// <summary>运行时通道首次缺失的时刻(runtime_port 消失或实例键消失时起算)。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _runtimeMissingSince = new(StringComparer.Ordinal);

    // 死条目抑制(issue 20):注册表残留"进程已消失"的行时,只读消费方(addon 异常退出无清理方)
    // 无法自行删行 —— 每秒 watch 重读会不断重建连接、判 Dead、再移出,把日志刷成无限循环
    // (本机实测刷到 3 GB+,拖慢 I/O 并拖垮同机 MCP 会话)。抑制以"实例身份"(见
    // RegistryEntry.Identity)为粒度,而不是以 key 为粒度:编辑器重启会改变 pid/started_at,
    // 身份随之变化,自愈路径(边车重拉/编辑器重启)不受影响。
    /// <summary>已判定死亡的身份指纹集合(tombstone):同一身份不再重建连接。</summary>
    private readonly ConcurrentDictionary<string, byte> _deadIdentities = new(StringComparer.Ordinal);

    /// <summary>死条目 tombstone 的上限;超出后整体清空(防长跑进程无界增长)。</summary>
    private const int DeadIdentityCap = 256;

    // LSP 通道(issue 11):每实例一条自持 LSP 客户端(惰性连接);实例移除时一并处置。
    /// <summary>每实例一条 LSP 客户端(惰性创建;实例拆除时一并处置)。</summary>
    private readonly ConcurrentDictionary<string, LspClient> _lspClients = new(StringComparer.Ordinal);

    /// <summary>唤醒信号量:watcher 事件与连接状态回调经它提前触发 reconcile(计数上限 1,合并唤醒)。</summary>
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>服务级取消源(链接 stoppingToken;全部连接 CTS 的父)。</summary>
    private CancellationTokenSource? _serviceCts;

    /// <summary>projects.json 文件监视器(失效时静默,周期轮询兜底)。</summary>
    private FileSystemWatcher? _watcher;

    /// <summary>仅装填依赖;watch 与 reconcile 循环在 ExecuteAsync 中启动。</summary>
    /// <param name="options">daemon 选项(取 StateDir)。</param>
    /// <param name="idleMonitor">空闲监视器(上报连接数)。</param>
    /// <param name="logger">日志器。</param>
    public InstanceManager(DaemonOptions options, IdleMonitor idleMonitor, ILogger<InstanceManager> logger)
    {
        _stateDir = options.StateDir;
        _idleMonitor = idleMonitor;
        _logger = logger;
        _daemonVersion = typeof(DaemonOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>list_instances 的 JSON 视图(项目路径、短 id、引擎版本、端口、pid、连通性)。</summary>
    /// <returns>形如 {"instances":[…]} 的响应 JSON(godot_version 取鉴权 ack 优先、注册表兜底)。</returns>
    public string ListInstancesJson()
    {
        var rows = Snapshot()
            .Select(c => new Dictionary<string, object?>
            {
                ["path"] = c.Key,
                ["id"] = RegistryReader.HashOf(c.Key),
                ["port"] = c.Entry.Port,
                ["pid"] = c.Entry.Pid,
                ["godot_version"] = c.Connected && c.GodotVersionAck is not null
                    ? c.GodotVersionAck
                    : c.Entry.GodotVersion,
                ["connected"] = c.Connected,
            })
            .ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["instances"] = rows });
    }

    /// <summary>无活跃实例(NO_INSTANCE)时的英文修复提示。</summary>
    private const string HintNoInstance =
        "Ensure Godot is running with the plugin enabled. If running headless, launch with: godot --headless --editor --path <project>";

    /// <summary>需要指定实例(AMBIGUOUS_INSTANCE / INSTANCE_NOT_FOUND)时的英文修复提示。</summary>
    private const string HintSpecifyInstance =
        "Pass the instance parameter: the canonical project path (list_instances \"path\") or its 12-char short id.";

    /// <summary>编辑器通道快照(按键序数排序,输出与遍历顺序稳定)。</summary>
    /// <returns>连接列表的浅拷贝。</returns>
    internal List<InstanceConnection> Snapshot()
    {
        return _connections.Values.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>把连接快照压成寻址错误附带的实例摘要行(path/id/port/connected)。</summary>
    /// <param name="connections">连接快照。</param>
    /// <returns>摘要列表。</returns>
    private static List<InstanceSummary> Summaries(List<InstanceConnection> connections)
    {
        return connections
            .Select(c => new InstanceSummary(c.Key, RegistryReader.HashOf(c.Key), c.Entry.Port, c.Connected))
            .ToList();
    }

    /// <summary>
    /// 实例寻址(ADR-0003):instance 为 null/空时,恰好一个活跃实例则隐式选中,零实例报
    /// NO_INSTANCE,多实例报 AMBIGUOUS_INSTANCE 并附实例清单;给了值时按规范化项目路径
    /// (主标识)或 12 位短 id(别名,大小写不敏感)命中,未命中报 INSTANCE_NOT_FOUND 并附清单。
    /// 运行时实例不单独寻址;同项目双开一期不支持(键即项目路径,天然唯一)。
    /// <para>逻辑链:instance 空 → 零实例 NO_INSTANCE / 恰一个直接选中 / 多个 AMBIGUOUS_INSTANCE(附清单)→
    /// 非空 → 先按规范化路径精确命中 → 未中且恰为 12 位 hex → 按短 id 大小写不敏感命中 →
    /// 仍未中 → INSTANCE_NOT_FOUND(附清单)。</para>
    /// </summary>
    /// <param name="instance">实例标识:规范化项目路径或 12 位短 id;可空。</param>
    /// <param name="error">失败时的寻址错误;成功为 null。</param>
    /// <returns>目标连接;失败为 null。</returns>
    internal InstanceConnection? ResolveConnection(string? instance, out InstanceCallException? error)
    {
        error = null;
        var snapshot = Snapshot();
        if (string.IsNullOrWhiteSpace(instance))
        {
            if (snapshot.Count == 0)
            {
                error = new InstanceCallException("NO_INSTANCE", "no active instances", HintNoInstance);
                return null;
            }
            if (snapshot.Count > 1)
            {
                error = new InstanceCallException(
                    "AMBIGUOUS_INSTANCE",
                    "multiple active instances; specify the instance parameter (project path or short id)",
                    HintSpecifyInstance,
                    Summaries(snapshot));
                return null;
            }
            return snapshot[0];
        }

        var canonical = RegistryReader.Canonical(instance);
        var byPath = snapshot.FirstOrDefault(c => c.Key.Equals(canonical, StringComparison.Ordinal));
        if (byPath is not null)
        {
            return byPath;
        }

        if (instance.Length == 12 && instance.All(IsHexDigit))
        {
            var byId = snapshot.FirstOrDefault(
                c => RegistryReader.HashOf(c.Key).Equals(instance, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        error = new InstanceCallException(
            "INSTANCE_NOT_FOUND", $"instance not found: {instance}", HintSpecifyInstance, Summaries(snapshot));
        return null;
    }

    /// <summary>
    /// 广播信封通知的进程级消费入口(issue 14:extensions.changed)。参数:实例键、通知类型、params。
    /// 由组合根(Program)接线到 ExtensionService —— 避免 Instances → Extensions 的依赖边。
    /// </summary>
    public event Action<string, string, JsonElement>? ConnectionNotification;

    /// <summary>
    /// 表内版本门控之外的动态门控解析(扩展工具的 min/max 版本)。组合根接线。
    /// </summary>
    public Func<string, VersionGate?>? ExtraMethodGates { get; set; }

    /// <summary>实例当前已知的 Godot 版本(ack 优先,注册表兜底;未知为 null)。</summary>
    /// <param name="connection">目标连接。</param>
    /// <returns>版本字符串(如 "4.5.1");两路皆空为 null。</returns>
    internal string? VersionOf(InstanceConnection connection)
    {
        var ack = connection.GodotVersionAck;
        if (!string.IsNullOrEmpty(ack))
        {
            return ack;
        }
        var entryVersion = connection.Entry.GodotVersion;
        return string.IsNullOrEmpty(entryVersion) ? null : entryVersion;
    }

    /// <summary>
    /// 并集可见性判定(issue 14):是否存在任一已连接实例满足该版本门控。
    /// 全部实例版本未知时视为不可验证 —— 保守隐藏(Node 注册门控同规)。
    /// </summary>
    /// <param name="min">最低版本边界,可空。</param>
    /// <param name="max">最高版本边界,可空。</param>
    /// <returns>存在任一已连接实例可验证兼容为 true;否则 false。</returns>
    public bool AnyVersionCompatible(string? min, string? max)
    {
        return Snapshot().Any(c => GodotVersions.IsCompatible(VersionOf(c), min, max));
    }

    /// <summary>Node 调用期版本门控(registerToolWrapped 的纵深防御):仅当版本已知且不兼容时报错。
    /// <para>逻辑链:查门控(内置工具表优先,扩展门控兜底)→ 无门控 / 版本未知 / 兼容 → 放行 →
    /// 已知且不兼容 → 抛 UNSUPPORTED(带 SupportText 提示与 classdb.get_info 替代指引)。</para></summary>
    /// <param name="connection">目标连接(取已知的 Godot 版本)。</param>
    /// <param name="method">被调方法名(查门控表)。</param>
    private void EnforceVersionGate(InstanceConnection connection, string method)
    {
        var gate = NodeToolTable.GateForMethod(method) ?? ExtraMethodGates?.Invoke(method);
        if (gate is null)
        {
            return;
        }
        var version = VersionOf(connection);
        if (version is null || GodotVersions.IsCompatible(version, gate.Min, gate.Max))
        {
            return;
        }
        throw new InstanceCallException(
            "UNSUPPORTED",
            $"{gate.ToolName} is not supported on this Godot version (connected: {GodotVersions.MajorMinor(version)})",
            GodotVersions.SupportText(gate.Min, gate.Max) + " Use classdb.get_info for alternatives.");
    }

    /// <summary>把一次调用路由到目标实例的连接(寻址失败先抛,随后交给连接原语)。
    /// <para>逻辑链:ResolveConnection 寻址 → 失败原样抛 → EnforceVersionGate 门控 →
    /// connection.CallAsync(其超时/断开/取消语义见该处)。</para></summary>
    /// <param name="instance">实例标识,可空(省略时单实例隐式选中)。</param>
    /// <param name="method">方法名。</param>
    /// <param name="paramsJson">params JSON,可空。</param>
    /// <param name="timeout">响应超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实例响应的 result。</returns>
    public async Task<JsonElement> CallInstanceAsync(
        string? instance, string method, string? paramsJson, TimeSpan timeout, CancellationToken ct)
    {
        var connection = ResolveConnection(instance, out var error);
        if (error is not null || connection is null)
        {
            throw error!;
        }
        EnforceVersionGate(connection, method);
        return await connection.CallAsync(method, paramsJson, timeout, ct);
    }

    /// <summary>构造 GAME_NOT_RUNNING 错误(运行时通道缺席或断开的统一映射)。</summary>
    /// <param name="key">实例键(拼入文案)。</param>
    /// <param name="inner">可选底层异常(取其 Message 拼入文案)。</param>
    /// <returns>可抛的 InstanceCallException。</returns>
    private static InstanceCallException GameNotRunning(string key, Exception? inner = null)
    {
        return new InstanceCallException(
            "GAME_NOT_RUNNING",
            $"no running game for instance: {key}{(inner is null ? "" : $" ({inner.Message})")}");
    }

    /// <summary>
    /// 把一次调用路由到目标实例的运行时通道(Mode B)。运行时通道缺席或连接失败
    /// 一律映射为 GAME_NOT_RUNNING(与 Node 桥 callRuntime 同语义)。
    /// <para>逻辑链:寻址编辑器实例 → _runtime 无该键 → GAME_NOT_RUNNING → 版本门控 →
    /// 经运行时通道调用;捕获 DISCONNECTED(游戏半路退出)也折算为 GAME_NOT_RUNNING(拼入内因),
    /// 其余错误(TIMEOUT/RPC_ERROR/CANCELLED)原样上抛。</para>
    /// </summary>
    /// <param name="instance">实例标识,可空。</param>
    /// <param name="method">运行时侧方法名。</param>
    /// <param name="paramsJson">params JSON,可空。</param>
    /// <param name="timeout">响应超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>运行时响应的 result。</returns>
    public async Task<JsonElement> CallRuntimeAsync(
        string? instance, string method, string? paramsJson, TimeSpan timeout, CancellationToken ct)
    {
        var connection = ResolveConnection(instance, out var error);
        if (error is not null || connection is null)
        {
            throw error!;
        }
        if (!_runtime.TryGetValue(connection.Key, out var runtime))
        {
            throw GameNotRunning(connection.Key);
        }
        EnforceVersionGate(connection, method);
        try
        {
            return await runtime.CallAsync(method, paramsJson, timeout, ct);
        }
        catch (InstanceCallException ex) when (ex.Code == "DISCONNECTED")
        {
            throw GameNotRunning(connection.Key, ex);
        }
    }

    /// <summary>game_start(wait_for_runtime)的服务器侧等待:吸收运行时注册与连接的异步间隙。
    /// <para>逻辑链:寻址 → 每 100ms 轮询 _runtime 表,连接就绪 → 返回 runtime_port;
    /// 超时或取消 → 返回 null(由调用方决定后续)。</para></summary>
    /// <param name="instance">实例标识,可空。</param>
    /// <param name="timeout">最长等待时长。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>就绪时的运行时端口;超时/取消为 null。</returns>
    public async Task<int?> WaitForRuntimeConnectedAsync(string? instance, TimeSpan timeout, CancellationToken ct)
    {
        var connection = ResolveConnection(instance, out var error);
        if (error is not null || connection is null)
        {
            throw error!;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_runtime.TryGetValue(connection.Key, out var runtime) && runtime.Connected)
            {
                return runtime.Entry.RuntimePort;
            }
            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>LSP 连接失败提示(Node lspSession.lspConnectFailureHint 同文)。</summary>
    /// <param name="port">尝试连接的 LSP 端口(拼入文案)。</param>
    /// <returns>英文排查提示。</returns>
    public static string LspConnectFailureHint(int port)
    {
        return $"Could not reach the GDScript LSP on port {port}. Most likely the LSP is listening on a " +
               $"different port — the editor may have been launched with --lsp-port, or its " +
               $"network/language_server/remote_port setting differs from {port}; set GODOT_MCP_LSP_PORT to " +
               $"the actual LSP port to match. The LSP may also still be initializing — retry shortly. " +
               $"Only if no other MCP tool works at all is the editor not running.";
    }

    /// <summary>
    /// 取(必要时创建并连接)目标实例的 LSP 客户端。端点三级解析(Node ADR 0008 语义,实例化版):
    /// 条目 lsp_port → 缺失时受保护的 6005(被其他存活且可行的实例占用则 LSP_UNAVAILABLE);
    /// 目标端口被其他存活实例一并声称 → LSP_PORT_CONFLICT(仅在存活 pid + WS 端口探针佐证时计)。
    /// <para>逻辑链:寻址 → 已有客户端且仍连接 → 复用返回 → 解析端点(ResolveLspEndpointAsync)→
    /// 丢弃旧客户端(可能已失连)→ 新建、登记并 EnsureConnectedAsync;连接失败 → 摘除并处置
    /// 新客户端,抛 LSP_UNAVAILABLE(附端口排查提示)。</para>
    /// </summary>
    /// <param name="instance">实例标识,可空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已连接就绪的 LSP 客户端。</returns>
    internal async Task<LspClient> GetLspClientAsync(string? instance, CancellationToken ct)
    {
        var connection = ResolveConnection(instance, out var error);
        if (error is not null || connection is null)
        {
            throw error!;
        }
        if (_lspClients.TryGetValue(connection.Key, out var existing) && existing.IsConnected)
        {
            return existing;
        }

        var (host, port) = await ResolveLspEndpointAsync(connection);
        if (existing is not null)
        {
            _lspClients.TryRemove(connection.Key, out _);
            await existing.DisposeAsync();
        }
        var client = new LspClient(host, port, ProjectPathFromKey(connection.Key));
        _lspClients[connection.Key] = client;
        try
        {
            await client.EnsureConnectedAsync(ct);
        }
        catch (Exception ex)
        {
            _lspClients.TryRemove(connection.Key, out _);
            await client.DisposeAsync();
            throw new InstanceCallException(
                "LSP_UNAVAILABLE",
                $"GDScript LSP unavailable: {ex.Message}.",
                LspConnectFailureHint(port));
        }
        return client;
    }

    /// <summary>实例键(正斜杠形态)还原为本机路径分隔符的项目路径。</summary>
    /// <param name="key">规范化项目键。</param>
    /// <returns>本机形态的项目根路径。</returns>
    private static string ProjectPathFromKey(string key)
    {
        return key.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// 目标实例的 LSP 端点解析与冲突检测(Node ADR 0008/0025 语义,实例化版)。
    /// <para>逻辑链:条目 lsp_port 为正 → 直接采用;否则查受保护默认 6005 的存活声称者,
    /// 有 → 抛 LSP_UNAVAILABLE,无 → 用 6005 → 对最终端口再查声称者,有 → 抛 LSP_PORT_CONFLICT →
    /// lsp_host 为空补 127.0.0.1。</para>
    /// </summary>
    /// <param name="target">目标连接。</param>
    /// <returns>(host, port) 端点。</returns>
    private async Task<(string Host, int Port)> ResolveLspEndpointAsync(InstanceConnection target)
    {
        var entry = target.Entry;
        int port;
        if (entry.LspPort is int explicitPort && explicitPort > 0)
        {
            port = explicitPort;
        }
        else
        {
            // 受保护的 6005:仅当没有其他存活且可行的实例占用它时才使用。
            var claimants = await CorroboratedClaimantsAsync(6005, target.Key);
            if (claimants.Count > 0)
            {
                throw new InstanceCallException(
                    "LSP_UNAVAILABLE",
                    $"GDScript LSP unavailable: port 6005 is held by a live editor ({claimants[0]}) " +
                    "that is not the target instance. Set --lsp-port / GODOT_MCP_LSP_PORT per instance " +
                    "to disambiguate.",
                    LspConnectFailureHint(6005));
            }
            port = 6005;
        }

        var conflicts = await CorroboratedClaimantsAsync(port, target.Key);
        if (conflicts.Count > 0)
        {
            throw new InstanceCallException(
                "LSP_PORT_CONFLICT",
                $"GDScript LSP port conflict: port {port} is claimed by both the target instance and " +
                $"{conflicts[0]}. Stop the other editor, or set --lsp-port / GODOT_MCP_LSP_PORT per " +
                "instance to disambiguate.");
        }

        var host = string.IsNullOrEmpty(entry.LspHost) ? "127.0.0.1" : entry.LspHost;
        return (host, port);
    }

    /// <summary>
    /// 该端口的"存活且可行"声称者(Node ADR 0025 语义):条目 lsp_port(缺省按引擎默认 6005
    /// 计)匹配、pid 存活、且该条目自报的 WS 端口未拒绝连接(ECONNREFUSED = 已死;其他一律计入)。
    /// </summary>
    /// <param name="port">待查的 LSP 端口。</param>
    /// <param name="excludeKey">排除的目标实例键。</param>
    /// <returns>存活且可行的声称者键列表(可能为空)。</returns>
    private async Task<List<string>> CorroboratedClaimantsAsync(int port, string excludeKey)
    {
        var claimants = new List<string>();
        foreach (var candidate in Snapshot())
        {
            if (candidate.Key == excludeKey)
            {
                continue;
            }
            var effective = candidate.Entry.LspPort is int p && p > 0 ? p : 6005;
            if (effective != port || !RegistryReader.IsPidAlive(candidate.Entry.Pid))
            {
                continue;
            }
            if (await IsWsPortRefusedAsync(candidate.Entry.Port))
            {
                continue;
            }
            claimants.Add(candidate.Key);
        }
        return claimants;
    }

    /// <summary>对 127.0.0.1:port 做 300ms TCP 探针,判定是否"明确拒绝连接"。</summary>
    /// <param name="port">WS 端口。</param>
    /// <returns>明确拒绝(ECONNREFUSED)= true;连接成功/超时/其他错误一律 false(保守计入声称者)。</returns>
    private static async Task<bool> IsWsPortRefusedAsync(int port)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await probe.ConnectAsync("127.0.0.1", port, timeout.Token);
            return false; // 连接成功 = 存活(未拒绝)。
        }
        catch (OperationCanceledException)
        {
            return false; // 超时不是拒绝 —— 一律计入声称者。
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused })
        {
            return true; // 明确拒绝 = 该编辑器已死。
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 只读状态面(issue 15):经本 daemon 可观测的在途操作(在飞/排队/执行中)。
    /// instance 省略 = 全部实例的 daemon 全局只读视图(有意不走 AMBIGUOUS 规则——
    /// 它不做目标路由,只报告事实);给定 instance 时严格解析(未命中回 INSTANCE_NOT_FOUND)。
    /// 场景租约状态不可查询(线路契约冻结、无查询面),以 lease 字段如实标注。
    /// </summary>
    /// <param name="instance">实例标识;空 = 全部实例的汇总视图(有意不走 AMBIGUOUS 规则)。</param>
    /// <param name="error">给定 instance 且寻址失败时的错误;其余为 null。</param>
    /// <returns>operations + lease 的响应 JSON;寻址失败为 null。</returns>
    public string? ListOperationsJson(string? instance, out InstanceCallException? error)
    {
        error = null;
        var scope = new List<InstanceConnection>();
        if (string.IsNullOrWhiteSpace(instance))
        {
            scope.AddRange(Snapshot());
        }
        else
        {
            var connection = ResolveConnection(instance, out error);
            if (connection is null)
            {
                return null;
            }
            scope.Add(connection);
        }

        var operations = new List<Dictionary<string, object?>>();
        foreach (var connection in scope)
        {
            foreach (var pending in connection.PendingOperations())
            {
                operations.Add(new Dictionary<string, object?>
                {
                    ["instance"] = connection.Key,
                    ["instance_id"] = RegistryReader.HashOf(connection.Key),
                    ["method"] = pending.Method,
                    ["request_id"] = pending.RequestId,
                    ["status"] = pending.Status switch
                    {
                        InstanceConnection.OperationStatus.Queued => "queued",
                        InstanceConnection.OperationStatus.Executing => "executing",
                        _ => "in_flight",
                    },
                    ["waited_ms"] = pending.WaitedMs,
                });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["operations"] = operations,
            ["lease"] = new Dictionary<string, object?>
            {
                ["queryable"] = false,
                ["note"] = "Scene lease state is not queryable (the C1-C22 wire contract is frozen and " +
                           "the toolkit exposes no lease query face). A queued operation may be waiting on " +
                           "the mutation lane or on a scene lease — the daemon cannot distinguish. " +
                           "Only daemon-observable facts are reported.",
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// BackgroundService 主循环:启动文件监视后按"唤醒信号或 1s 到期"驱动 Reconcile,停机统一清场。
    /// <para>逻辑链:建链接 CTS → StartWatcher → 循环 { Reconcile(异常仅告警,保留上一状态)→
    /// 等 _wake 信号或 1s 超时 } → stoppingToken 取消退出 → finally StopAll(停 watcher、
    /// 取消全部连接、处置 LSP 客户端、释放 CTS)。</para>
    /// </summary>
    /// <param name="stoppingToken">宿主停机令牌。</param>
    /// <returns>服务生命周期任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        StartWatcher();
        _logger.LogInformation("实例管理器启动:registry={StateDir}", _stateDir);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Reconcile();
                }
                catch (Exception ex)
                {
                    // 解析半写/IO 失败等——保留上一状态,下个周期重试。
                    _logger.LogWarning(ex, "注册表 reconcile 失败(保留上一状态)");
                }

                try
                {
                    await _wake.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            StopAll();
        }
    }

    /// <summary>注册 projects.json 的四类事件(Changed/Created/Renamed/Deleted)→ Wake;
    /// watcher 自身失效静默(周期轮询兜底);目录不可建/不可监视则告警回落为纯轮询。</summary>
    private void StartWatcher()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            _watcher = new FileSystemWatcher(_stateDir, RegistryReader.ProjectsFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Wake();
            _watcher.Created += (_, _) => Wake();
            _watcher.Renamed += (_, _) => Wake();
            _watcher.Deleted += (_, _) => Wake();
            _watcher.Error += (_, _) =>
            {
                // watcher 失效不影响正确性 —— 1s 周期轮询兜底。
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注册表 watch 不可用,回落为周期轮询");
        }
    }

    /// <summary>
    /// 单轮对账(reconcile):把注册表条目与两组通道字典对齐,并顺带上报连接数、按需推送 LSP 判定。
    /// <para>逻辑链:读注册表(Json/IO 失败 → 本轮放弃,保留上一状态)→ 逐条目:新键 → 启编辑器通道;
    /// 已有且 port/pid/token_path 变化(编辑器重启)→ 重启连接;否则仅刷新条目引用 →
    /// 键消失的连接记宽限,超 2s 拆除 → Dead(进程消失)的连接即刻移除 → 运行时通道:
    /// runtime_port 出现/变化 → 启动/重启,消失记宽限后拆除(实例键已消失的同样处理)→
    /// 上报已连接数给空闲监视器 → 新连上的编辑器各推一次 LSP 判定(后台任务)。</para>
    /// </summary>
    private void Reconcile()
    {
        IReadOnlyList<RegistryEntry> entries;
        try
        {
            entries = RegistryReader.Read(_stateDir);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            present.Add(entry.Key);
            _missingSince.TryRemove(entry.Key, out _);
            if (_connections.TryGetValue(entry.Key, out var existing))
            {
                var previous = existing.Entry;
                if (previous.Port != entry.Port || previous.Pid != entry.Pid || previous.TokenPath != entry.TokenPath)
                {
                    // 端口/进程/令牌路径变化(编辑器重启)→ 重启连接(重发现)。
                    RestartConnection(entry);
                }
                else
                {
                    // 刷新条目引用(保持 runtime_port/runtime_pid 等字段最新——运行时通道据此拆装)。
                    existing.UpdateEntry(entry);
                }
            }
            else
            {
                // 死条目抑制(issue 20):该身份已被判定死亡 → 只保留"注册表行存在"的事实,
                // 不再重建连接。编辑器重启后 pid/started_at 变化 → 身份变化 → 正常重连。
                if (_deadIdentities.ContainsKey(entry.Identity))
                {
                    continue;
                }
                StartConnection(entry);
            }
        }

        // 键消失 → 连续缺失超过宽限才拆除(容忍注册表重写的瞬时缝隙)。
        foreach (var key in _connections.Keys.ToList())
        {
            if (present.Contains(key))
            {
                continue;
            }
            var since = _missingSince.GetOrAdd(key, _ => DateTime.UtcNow);
            if (DateTime.UtcNow - since >= MissingGrace)
            {
                StopConnection(key);
                _missingSince.TryRemove(key, out _);
            }
        }

        // 进程消失(活性判定)的连接从实例表移除。注册表文件保持不动(只读消费)。
        foreach (var (key, connection) in _connections.ToList())
        {
            if (connection.Status == InstanceConnection.ConnectionStatus.Dead)
            {
                // 记 tombstone(issue 20):同一身份不再重建,日志因此只出现一次。
                // 编辑器重启会改变身份,届时重新进入实例表。
                if (_deadIdentities.Count >= DeadIdentityCap)
                {
                    _deadIdentities.Clear();
                }
                _deadIdentities[connection.Entry.Identity] = 0;
                _logger.LogInformation(
                    "实例进程已消失,移出实例表并抑制该身份(编辑器重启后自动重连): {Key}", key);
                StopConnection(key);
            }
        }

        // 运行时通道(Mode B):按运行时条目的 runtime_port/runtime_pid 拆装(同样带缺失宽限)。
        foreach (var connection in Snapshot())
        {
            var entry = connection.Entry;
            if (entry.RuntimePort is int runtimePort && runtimePort > 0)
            {
                _runtimeMissingSince.TryRemove(entry.Key, out _);
                if (_runtime.TryGetValue(entry.Key, out var existing))
                {
                    if (existing.Entry.RuntimePort != runtimePort || existing.Entry.RuntimePid != entry.RuntimePid)
                    {
                        RestartRuntime(entry);
                    }
                    else
                    {
                        existing.UpdateEntry(entry);
                    }
                }
                else
                {
                    StartRuntime(entry);
                }
            }
            else if (_runtime.ContainsKey(entry.Key))
            {
                var since = _runtimeMissingSince.GetOrAdd(entry.Key, _ => DateTime.UtcNow);
                if (DateTime.UtcNow - since >= MissingGrace)
                {
                    _logger.LogInformation("运行时条目消失,拆除运行时通道: {Key}", entry.Key);
                    StopRuntime(entry.Key);
                    _runtimeMissingSince.TryRemove(entry.Key, out _);
                }
            }
        }
        foreach (var key in _runtime.Keys.ToList())
        {
            if (!present.Contains(key))
            {
                var since = _runtimeMissingSince.GetOrAdd(key, _ => DateTime.UtcNow);
                if (DateTime.UtcNow - since >= MissingGrace)
                {
                    StopRuntime(key);
                    _runtimeMissingSince.TryRemove(key, out _);
                }
            }
        }

        _idleMonitor.SetConnectedInstances(_connections.Values.Count(c => c.Connected));

        // LSP 判定推送(Node 桥 lspStatusReporter 对等能力):编辑器连上后推一次
        // editor.set_lsp_status(注册表判定:解析端点 + 存活佐证探测),停靠面板据此把
        // "等待 MCP 服务器"换成 活跃/冲突/不可用。断开后清除标记,重连时重推。
        foreach (var connection in _connections.Values)
        {
            if (connection.Connected && _lspStatusPushed.TryAdd(connection.Key, 0))
            {
                _ = Task.Run(() => PushLspStatusAsync(connection), CancellationToken.None);
            }
        }
    }

    /// <summary>已推送 LSP 判定的实例键(断开时移除,重连重推)。</summary>
    private readonly ConcurrentDictionary<string, byte> _lspStatusPushed = new(StringComparer.Ordinal);

    /// <summary>
    /// 向刚连上的编辑器推送一次 editor.set_lsp_status 判定(active/conflict;注册表解析 +
    /// 存活佐证,不真连 LSP),停靠面板据此更新状态。失败(编辑器未就绪/令牌轮换等)→
    /// 清除已推标记,等下轮 reconcile 重推。
    /// </summary>
    /// <param name="connection">目标连接(须已 Connected)。</param>
    private async Task PushLspStatusAsync(InstanceConnection connection)
    {
        var key = connection.Key;
        try
        {
            var entry = connection.Entry;
            var port = entry.LspPort is int p && p > 0 ? p : 6005;
            var host = string.IsNullOrEmpty(entry.LspHost) ? "127.0.0.1" : entry.LspHost;
            var conflicts = await CorroboratedClaimantsAsync(port, key);
            var parameters = new Dictionary<string, object?>
            {
                ["host"] = host,
                ["port"] = port,
            };
            if (conflicts.Count > 0)
            {
                parameters["state"] = "conflict";
                parameters["detail"] =
                    $"port {port} is claimed by both the target instance and {conflicts[0]}. " +
                    "Give each editor a distinct --lsp-port + GODOT_MCP_LSP_PORT. See docs/multi-instance.md.";
            }
            else
            {
                parameters["state"] = "active";
            }

            var payload = JsonSerializer.Serialize(parameters);
            await connection.CallAsync(
                "editor.set_lsp_status", payload, TimeSpan.FromSeconds(3), CancellationToken.None);
            _logger.LogInformation("LSP 判定已推送至编辑器停靠面板: {Key} = {State}", key, parameters["state"]);
        }
        catch (Exception ex)
        {
            // 推送失败(编辑器尚未就绪/令牌轮换等)→ 清除标记,下次 reconcile 重推。
            _lspStatusPushed.TryRemove(key, out _);
            _logger.LogDebug("LSP 判定推送失败,稍后重试: {Key}: {Message}", key, ex.Message);
        }
    }

    /// <summary>为带 runtime_port 的条目启动一条运行时通道(Mode B)并后台跑主循环。</summary>
    /// <param name="entry">最新注册表条目。</param>
    private void StartRuntime(RegistryEntry entry)
    {
        if (_serviceCts is null)
        {
            return;
        }

        var connection = new InstanceConnection(
            entry, _daemonVersion, Wake, InstanceConnectionKind.Runtime);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts.Token);
        _runtime[entry.Key] = connection;
        _runtimeCts[entry.Key] = cts;
        _logger.LogInformation("运行时通道启动: {Key} (runtime_port={Port})", entry.Key, entry.RuntimePort);
        _ = Task.Run(() => connection.LoopAsync(cts.Token), CancellationToken.None);
    }

    /// <summary>runtime_port/runtime_pid 变化时重建运行时通道(先拆后起)。</summary>
    /// <param name="entry">最新注册表条目。</param>
    private void RestartRuntime(RegistryEntry entry)
    {
        StopRuntime(entry.Key);
        StartRuntime(entry);
    }

    /// <summary>拆除运行时通道:移出表并取消其 CTS(进程死即终态;存活重试由通道自身退避驱动)。</summary>
    /// <param name="key">实例键。</param>
    private void StopRuntime(string key)
    {
        _runtime.TryRemove(key, out _);
        if (_runtimeCts.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>为注册表条目新建编辑器通道:登记入表、把通道通知转接到 ConnectionNotification、
    /// 后台跑主循环。</summary>
    /// <param name="entry">注册表条目。</param>
    private void StartConnection(RegistryEntry entry)
    {
        if (_serviceCts is null)
        {
            return;
        }

        var connection = new InstanceConnection(entry, _daemonVersion, Wake);
        connection.Notification += (type, notificationParams) =>
            ConnectionNotification?.Invoke(entry.Key, type, notificationParams);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts.Token);
        _connections[entry.Key] = connection;
        _connectionCts[entry.Key] = cts;
        _ = Task.Run(() => connection.LoopAsync(cts.Token), CancellationToken.None);
    }

    /// <summary>编辑器重启(port/pid/token_path 变化)时重建连接(先拆后起,重读令牌)。</summary>
    /// <param name="entry">最新注册表条目。</param>
    private void RestartConnection(RegistryEntry entry)
    {
        StopConnection(entry.Key);
        StartConnection(entry);
    }

    /// <summary>拆除编辑器通道:移出实例表、清 LSP 推送标记、取消 CTS,并处置该实例的 LSP 客户端。</summary>
    /// <param name="key">实例键。</param>
    private void StopConnection(string key)
    {
        _connections.TryRemove(key, out _);
        _lspStatusPushed.TryRemove(key, out _);
        if (_connectionCts.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        if (_lspClients.TryRemove(key, out var lsp))
        {
            _ = lsp.DisposeAsync();
        }
    }

    /// <summary>停机清场:停 watcher → 取消并清空编辑器/运行时两组连接 → 处置全部 LSP 客户端 → 释放服务 CTS。</summary>
    private void StopAll()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        foreach (var cts in _connectionCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _connectionCts.Clear();
        _connections.Clear();
        foreach (var cts in _runtimeCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _runtimeCts.Clear();
        _runtime.Clear();
        foreach (var lsp in _lspClients.Values)
        {
            _ = lsp.DisposeAsync();
        }
        _lspClients.Clear();
        _serviceCts?.Dispose();
        _serviceCts = null;
    }

    /// <summary>提前唤醒一轮 reconcile(watcher 事件与连接状态回调共用入口)。</summary>
    private void Wake()
    {
        // 合并唤醒:已有待处理唤醒时不叠加(1s 周期轮询本就兜底)。
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>ASCII 十六进制字符判定(短 id 形态预检)。</summary>
    /// <param name="c">待判字符。</param>
    /// <returns>0-9 / a-f / A-F 为 true。</returns>
    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
