using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GodotMcp.Daemon.Instances;

/// <summary>连接类别:编辑器通道(重连/pid 活性/编辑器 ack)与运行时通道(Mode B:noReconnect、裸 ack)。</summary>
public enum InstanceConnectionKind
{
    /// <summary>编辑器通道:常驻重连、pid 活性判定、鉴权 ack 携带 Godot 版本。</summary>
    Editor,

    /// <summary>运行时通道:游戏进程连接,10s 连接上限、裸 ack;重试以 runtime_pid 存活为界(进程死即终态)。</summary>
    Runtime,
}

/// <summary>
/// 对单个 Godot 实例的出站 WS 连接(契约:C1/C2 —— 首帧鉴权、
/// 逐帧单 JSON 文档)。断线按既定重连纪律恢复:指数退避 1·2·4·…·60s,
/// 在成功鉴权时重置(与 Node 桥 channel.ts 同形);每次尝试重新读取令牌
/// (插件重启后的轮换自愈)并复核进程存活(进程已死 → Dead,交管理器移除)。
/// 调用原语与 channel.ts 同语义:id 关联、超时、_queued/_executing 进度
/// 通知重置超时、断开时在途请求以 DISCONNECTED 失败。
/// 运行时通道(Mode B):10s 连接上限、ack 为裸 {authed:true};重试以游戏进程
/// (runtime_pid)存活为界——存活期间短退避重试(吸收游戏启动加载期的 auth 超时),
/// 进程已死即终态不再重试(noReconnect 纪律的活性化版本)。
/// </summary>
internal sealed class InstanceConnection
{
    /// <summary>连接状态机(单向推进,只经 SetStatus 变更并回调通知)。</summary>
    public enum ConnectionStatus
    {
        /// <summary>初始/重试中:尚未完成鉴权。</summary>
        Connecting,

        /// <summary>鉴权通过、读循环驻留中。</summary>
        Connected,

        /// <summary>断开:编辑器通道按退避重试;运行时通道则终态返回。</summary>
        Disconnected,

        /// <summary>实例进程已消失:终态,交管理器从实例表移除。</summary>
        Dead,
    }

    /// <summary>在途操作的状态(issue 15 只读状态面):在飞 / 排队(_queued 已到)/ 执行中(_executing 已到)。</summary>
    public enum OperationStatus
    {
        /// <summary>请求已发出,尚未收到 _queued 进度。</summary>
        InFlight,

        /// <summary>已收到 _queued:插件侧排队(可能在等变更车道或场景租约)。</summary>
        Queued,

        /// <summary>已收到 _executing:插件侧开始执行。</summary>
        Executing,
    }

    /// <summary>在途操作的只读快照行(issue 15 状态面;由 PendingOperations() 生成)。</summary>
    /// <param name="RequestId">JSON-RPC 请求 id(与响应帧 id 关联)。</param>
    /// <param name="Method">被调用的方法名。</param>
    /// <param name="Status">当前阶段(在飞/排队/执行中)。</param>
    /// <param name="WaitedMs">自发起起累计的等待毫秒数(进度通知重开计时窗口但不重置此起点)。</param>
    internal sealed record PendingOperation(string RequestId, string Method, OperationStatus Status, long WaitedMs);

    /// <summary>
    /// 单次调用的在途登记:请求 id、单发计时器与完成源三合一。计时器到点即以 TIMEOUT
    /// 失败完成源;_queued/_executing 进度通知会重开完整计时窗口(channel.ts 同纪律)。
    /// </summary>
    private sealed class PendingCall : IDisposable
    {
        /// <summary>单发计时器:构造时不启动,发送前经 StartTimer() 启动。</summary>
        private readonly Timer _timer;

        /// <summary>生成请求 id 并装好计时器(不启动;发送前才计时,排队/执行进度会重开窗口)。</summary>
        /// <param name="method">被调用的方法名(仅用于超时错误文案)。</param>
        /// <param name="timeout">超时窗口时长。</param>
        public PendingCall(string method, TimeSpan timeout)
        {
            Id = Guid.NewGuid().ToString("N");
            Method = method;
            TimeoutWindow = timeout;
            StartedAtUtc = DateTime.UtcNow;
            _timer = new Timer(
                _ => Fail(new InstanceCallException(
                    "TIMEOUT", $"call to {method} timed out after {timeout.TotalMilliseconds:F0}ms")),
                null,
                System.Threading.Timeout.InfiniteTimeSpan,
                System.Threading.Timeout.InfiniteTimeSpan);
        }

        /// <summary>JSON-RPC 请求 id(32 位十六进制 GUID,与响应帧的 id 关联)。</summary>
        public string Id { get; }

        /// <summary>被调用的方法名。</summary>
        public string Method { get; }

        /// <summary>当前计时窗口时长(进度通知重开时沿用)。</summary>
        public TimeSpan TimeoutWindow { get; }

        /// <summary>登记时刻(UTC),WaitedMs 的计时起点。</summary>
        public DateTime StartedAtUtc { get; }

        /// <summary>在途阶段,只进不退:InFlight → Queued → Executing(无进度通知则停留原级)。</summary>
        public OperationStatus Status { get; private set; } = OperationStatus.InFlight;

        /// <summary>已等待毫秒数(自登记起累计,不受进度重开窗口影响)。</summary>
        public long WaitedMs => (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds;

        /// <summary>完成源:响应/错误/超时/断开/取消各事件经它落到 await 方。</summary>
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>发送前启动计时(占发送门期间不计入;到点即以 TIMEOUT 失败)。</summary>
        public void StartTimer() => _timer.Change(TimeoutWindow, System.Threading.Timeout.InfiniteTimeSpan);

        /// <summary>_queued 进度通知:标记排队并重置计时(channel.ts 同纪律:重开到完整窗口)。</summary>
        public void MarkQueued()
        {
            Status = OperationStatus.Queued;
            ResetTimer();
        }

        /// <summary>_executing 进度通知:标记执行中并重置计时。</summary>
        public void MarkExecuting()
        {
            Status = OperationStatus.Executing;
            ResetTimer();
        }

        /// <summary>重开完整计时窗口(收到进度通知时调用)。</summary>
        public void ResetTimer() => _timer.Change(TimeoutWindow, System.Threading.Timeout.InfiniteTimeSpan);

        /// <summary>以结果完成(响应帧到达;已完成后的重复调用被忽略)。</summary>
        /// <param name="result">响应的 result。</param>
        public void Succeed(JsonElement result) => Completion.TrySetResult(result);

        /// <summary>以异常完成(超时/断开/取消/RPC 错误)。</summary>
        /// <param name="exception">失败原因。</param>
        /// <returns>是否由本次调用首次完成(TrySet 语义)。</returns>
        public bool Fail(Exception exception) => Completion.TrySetException(exception);

        /// <summary>释放计时器(调用收尾 finally 必达)。</summary>
        public void Dispose() => _timer.Dispose();
    }

    /// <summary>daemon 自身版本,随鉴权首帧上报(插件侧可据此提示升级)。</summary>
    private readonly string _daemonVersion;

    /// <summary>状态变更回调(管理器注入 Wake,驱动 reconcile 及时跟进)。</summary>
    private readonly Action _onChanged;

    /// <summary>通道类别(编辑器/运行时):决定端口选择与重连纪律。</summary>
    private readonly InstanceConnectionKind _kind;

    /// <summary>在途调用表:请求 id → 登记(读循环与调用方并发访问)。</summary>
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);

    /// <summary>发送门:WebSocket 发送不得并发,以此串行化。</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>当前注册表条目(volatile;经 UpdateEntry 轻量刷新)。</summary>
    private volatile RegistryEntry _entry;

    /// <summary>连接状态(volatile;只经 SetStatus 变更)。</summary>
    private volatile ConnectionStatus _status = ConnectionStatus.Connecting;

    /// <summary>当前活的 WS 连接;null = 未连接(读循环退出/连接失败时置空)。</summary>
    private volatile ClientWebSocket? _currentWs;

    /// <summary>鉴权 ack 携带的 Godot 版本;离开 Connected 即清空。</summary>
    private volatile string? _godotVersionAck;

    /// <summary>仅装填依赖;网络连接在 LoopAsync 中进行。</summary>
    /// <param name="entry">初始注册表条目(提供键、端口、令牌路径、pid)。</param>
    /// <param name="daemonVersion">随鉴权首帧上报的 daemon 版本。</param>
    /// <param name="onChanged">状态变更回调(管理器用它唤醒 reconcile)。</param>
    /// <param name="kind">通道类别,默认编辑器通道。</param>
    public InstanceConnection(RegistryEntry entry, string daemonVersion, Action onChanged,
        InstanceConnectionKind kind = InstanceConnectionKind.Editor)
    {
        _entry = entry;
        _daemonVersion = daemonVersion;
        _onChanged = onChanged;
        _kind = kind;
    }

    /// <summary>实例键(规范化项目路径,注册表 by_path 的键)。</summary>
    public string Key => _entry.Key;

    /// <summary>当前注册表条目(可能已被 UpdateEntry 刷新)。</summary>
    public RegistryEntry Entry => _entry;

    /// <summary>刷新条目引用(端口/进程无变化时的轻量同步;有变化时由管理器重启连接)。</summary>
    public void UpdateEntry(RegistryEntry entry)
    {
        _entry = entry;
    }

    /// <summary>当前连接状态。</summary>
    public ConnectionStatus Status => _status;

    /// <summary>是否已鉴权连接(调用路由与 list_instances 连通列的判定依据)。</summary>
    public bool Connected => _status == ConnectionStatus.Connected;

    /// <summary>鉴权 ack 携带的完整 Godot 版本(如 4.5.1);未连接或 ack 未携带时为 null。</summary>
    public string? GodotVersionAck => _godotVersionAck;

    /// <summary>广播信封通知({notification, params?};issue 14 起消费 extensions.changed)。</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>主循环:连接 → 鉴权 → 驻留读帧。编辑器通道断开后按退避重试且进程死亡即刻退出;
    /// 运行时通道以 runtime_pid 存活为界重试(慢启动自愈),进程已死即终态,交管理器按注册表拆装。
    /// <para>逻辑链:每轮先(编辑器通道)查 pid 存活,进程已死 → 失败全部在途(DISCONNECTED)、
    /// 置 Dead 并返回 → 读令牌,失败 → 置 Disconnected(运行时通道按 runtime_pid 存活决定重试或返回,
    /// 编辑器通道走退避)→ 连接 WS(编辑器 5s / 运行时 10s 上限)→ 发 {auth, version} 首帧 → 5s 内等 ack,
    /// 非 {authed:true} → 抛错落入通用 catch → 鉴权成功:记录 godot_version(运行时为 null)、
    /// 重置退避、置 Connected → 读循环驻留直至断开/取消 → 断开后失败全部在途(DISCONNECTED)、
    /// 置 Disconnected → 运行时通道:runtime_pid 存活则短退避(1s 起步翻倍封顶 5s)重试,
    /// 已死或未知则返回;编辑器通道退避 1s 起步翻倍封顶 60s 后重试 →
    /// 外层取消时清场(失败全部在途、置 Disconnected)后退出。</para></summary>
    /// <param name="ct">服务停止令牌(管理器经链接 CTS 取消)。</param>
    public async Task LoopAsync(CancellationToken ct)
    {
        var backoffMs = 1000;
        // 运行时通道重试退避:短封顶(5s)——CallAsync 的连接等待窗仅 10s,
        // 重试必须落在其内才有自愈意义(编辑器通道退避封顶 60s 不变)。
        var runtimeBackoffMs = 1000;
        var runtimeMode = _kind == InstanceConnectionKind.Runtime;
        while (!ct.IsCancellationRequested)
        {
            var entry = _entry;
            if (!runtimeMode && !RegistryReader.IsPidAlive(entry.Pid))
            {
                // 进程已消失而注册表条目残留 —— 活性判定移除(只从 daemon 实例表移除;
                // 注册表文件归 addon 管理,daemon 是只读消费方)。
                FailAllPending("DISCONNECTED", "instance process is gone");
                SetStatus(ConnectionStatus.Dead);
                return;
            }

            try
            {
                var token = RegistryReader.TryReadToken(entry);
                if (token is null)
                {
                    SetStatus(ConnectionStatus.Disconnected);
                }
                else
                {
                    using var ws = new ClientWebSocket();
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        // 编辑器通道 5s(本机 loopback);运行时通道按 Node 桥 10s(游戏可能刚启动)。
                        connectCts.CancelAfter(runtimeMode ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5));
                        await ws.ConnectAsync(
                            new Uri($"ws://127.0.0.1:{(runtimeMode ? entry.RuntimePort : entry.Port)}/"),
                            connectCts.Token);
                    }

                    await SendTextAsync(ws, JsonSerializer.Serialize(
                        new Dictionary<string, string> { ["auth"] = token, ["version"] = _daemonVersion }), ct);
                    var ack = await ReceiveTextAsync(ws, TimeSpan.FromSeconds(5), ct);
                    var ackRoot = ack is null ? default : JsonDocument.Parse(ack).RootElement;
                    var authed = ackRoot.ValueKind == JsonValueKind.Object
                        && ackRoot.TryGetProperty("authed", out var authedEl)
                        && authedEl.ValueKind == JsonValueKind.True;
                    if (!authed)
                    {
                        throw new InvalidOperationException("鉴权确认缺失或无效");
                    }

                    _godotVersionAck = runtimeMode
                        ? null // 运行时 ack 为裸 {authed:true}(Node 桥同读法)
                        : ackRoot.TryGetProperty("godot_version", out var versionEl)
                            && versionEl.ValueKind == JsonValueKind.String
                            ? versionEl.GetString()
                            : null;
                    backoffMs = 1000; // 成功往返重置退避(Node 桥同纪律)。
                    runtimeBackoffMs = 1000;
                    _currentWs = ws;
                    SetStatus(ConnectionStatus.Connected);

                    await ReadLoopAsync(ws, ct);
                    // 读循环正常返回 = 对端关闭/本端取消。
                    _currentWs = null;
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    FailAllPending("DISCONNECTED", "WebSocket closed before response");
                    SetStatus(ConnectionStatus.Disconnected);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (runtimeMode)
                {
                    // 运行时连接失败值得在 stderr 留痕(Node 桥 runtimeConnection 亦记录)。
                    Console.Error.WriteLine($"[runtime-channel] {_entry.Key}: {ex.Message}");
                }
                _currentWs = null;
                FailAllPending("DISCONNECTED", "WebSocket closed before response");
                SetStatus(ConnectionStatus.Disconnected);
            }

            if (runtimeMode)
            {
                // 游戏进程(runtime_pid)仍存活 → 短退避重试:游戏启动加载期(mono 装配/
                // 主场景实例化)transport 的 pump 不跑,auth ack 可能超时——一次失败不
                // 等于游戏已死。固定 noReconnect 会把"慢启动"误判为终态,使 runtime
                // 工具在游戏存活期间永久 GAME_NOT_RUNNING(game_start 的 runtime_ready
                // 探活走编辑器插件的独立临时连接,与此通道互不可见,无法暴露此状态)。
                // pid 未知(注册表异常行)或已死 → 保持终态返回(游戏已死不再重试;
                // 生命周期归管理器按注册表 runtime_port 拆装)。
                if (entry.RuntimePid is int runtimePid && RegistryReader.IsPidAlive(runtimePid))
                {
                    try
                    {
                        await Task.Delay(runtimeBackoffMs, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    runtimeBackoffMs = Math.Min(runtimeBackoffMs * 2, 5000);
                    continue;
                }
                return;
            }

            try
            {
                await Task.Delay(backoffMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoffMs = Math.Min(backoffMs * 2, 60_000);
        }

        FailAllPending("DISCONNECTED", "connection stopped");
        SetStatus(ConnectionStatus.Disconnected);
    }

    /// <summary>
    /// 向实例发起一次 JSON-RPC 调用(编辑器通道):等待连接就绪(Node 桥 call 的
    /// 10s 重连等待上限),发送并等待关联响应;失败以 <see cref="InstanceCallException"/>
    /// 抛出(TIMEOUT / DISCONNECTED / RPC_ERROR / CANCELLED)。
    /// <para>逻辑链:未连接 → 50ms 轮询等待,至多 10s(超时 DISCONNECTED,取消 CANCELLED)→
    /// 登记在途项并挂取消回调(取消 → CANCELLED 失败)→ 拼单行 JSON-RPC 帧 → 占发送门前再核对
    /// 连接(已断 → DISCONNECTED)→ 启动计时并串行发送(发送中断开 → DISCONNECTED)→
    /// await 完成源:响应返回 result、error 帧 → RPC_ERROR、计时器到点 → TIMEOUT、
    /// 断开清场 → DISCONNECTED。finally 摘除在途项并释放计时器。</para>
    /// </summary>
    /// <param name="method">工具方法名(与插件侧注册方法一一对应)。</param>
    /// <param name="paramsJson">已序列化的 params JSON;null 时发送 params:null。</param>
    /// <param name="timeout">响应超时;收到 _queued/_executing 进度会重开完整窗口。</param>
    /// <param name="ct">调用方取消令牌。</param>
    /// <returns>响应帧的 result(已 Clone,归调用方独享)。</returns>
    public async Task<JsonElement> CallAsync(string method, string? paramsJson, TimeSpan timeout, CancellationToken ct)
    {
        var waitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_currentWs is null || Status != ConnectionStatus.Connected)
        {
            if (ct.IsCancellationRequested)
            {
                throw new InstanceCallException("CANCELLED", "Request cancelled");
            }
            if (DateTime.UtcNow > waitDeadline)
            {
                throw new InstanceCallException("DISCONNECTED", "no connection to instance after 10s");
            }
            await Task.Delay(50, ct);
        }

        var pending = new PendingCall(method, timeout);
        _pending[pending.Id] = pending;
        using var ctRegistration = ct.Register(
            () => pending.Fail(new InstanceCallException("CANCELLED", "Request cancelled by client")));
        try
        {
            var payload = paramsJson is null
                ? $"{{\"jsonrpc\":\"2.0\",\"id\":\"{pending.Id}\",\"method\":\"{method}\",\"params\":null}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":\"{pending.Id}\",\"method\":\"{method}\",\"params\":{paramsJson}}}";
            var ws = _currentWs;
            if (ws is null)
            {
                throw new InstanceCallException("DISCONNECTED", "no connection to instance");
            }
            await _sendGate.WaitAsync(ct);
            try
            {
                // WebSocket 发送不得并发——以门串行化(发送仅占位,计时从发送前开始)。
                pending.StartTimer();
                await SendTextAsync(ws, payload, ct);
            }
            catch (WebSocketException)
            {
                throw new InstanceCallException("DISCONNECTED", "connection closed during send");
            }
            finally
            {
                _sendGate.Release();
            }

            return await pending.Completion.Task;
        }
        finally
        {
            _pending.TryRemove(pending.Id, out _);
            pending.Dispose();
        }
    }

    /// <summary>当前在途操作快照(issue 15 只读状态面;含进度通知标记的排队/执行中状态)。</summary>
    /// <returns>按已等待时长降序的快照列表(空表 = 无在途)。</returns>
    public IReadOnlyList<PendingOperation> PendingOperations()
    {
        return _pending.Values
            .Select(p => new PendingOperation(p.Id, p.Method, p.Status, p.WaitedMs))
            .OrderByDescending(p => p.WaitedMs)
            .ToList();
    }

    /// <summary>驻留读帧循环:分块接收、UTF-8 字节累积,消息收齐(EndOfMessage)即整帧交给
    /// HandleFrame;WebSocketException 或 Close 帧一律按断开返回,由主循环清场。</summary>
    /// <param name="ws">已鉴权的 WebSocket。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            }
            catch (WebSocketException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = sb.ToString();
            sb.Clear();
            HandleFrame(text);
        }
    }

    /// <summary>读循环的帧处理:id 关联应答 + _queued/_executing 进度重置 + 广播信封通知派发。
    /// <para>逻辑链:解析失败/非对象帧 → 静默丢弃 → 含 notification 字段 → 派发 Notification
    /// 事件(消费方异常被吞,保护读循环)→ method 为 _queued/_executing 且带 request_id →
    /// 命中在途项则标记并重开计时 → 其余帧按 id 摘除在途项:result → 完成,error → RPC_ERROR
    /// 失败;无 id 或无匹配在途项的帧一律忽略。</para></summary>
    /// <param name="text">整帧文本(单条 JSON 文档)。</param>
    private void HandleFrame(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // 广播信封 {notification, params?}(契约 C14;Node channel.ts 的 notification 字段分发)。
            if (root.TryGetProperty("notification", out var notificationEl)
                && notificationEl.ValueKind == JsonValueKind.String)
            {
                var notificationParams = root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object
                    ? paramsEl.Clone()
                    : default;
                try
                {
                    Notification?.Invoke(notificationEl.GetString()!, notificationParams);
                }
                catch (Exception)
                {
                    // 通知消费方异常不得中断读循环。
                }
                return;
            }

            if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
                && methodEl.GetString() is "_queued" or "_executing"
                && root.TryGetProperty("params", out var progressParams)
                && progressParams.ValueKind == JsonValueKind.Object
                && progressParams.TryGetProperty("request_id", out var requestId)
                && requestId.ValueKind == JsonValueKind.String)
            {
                if (_pending.TryGetValue(requestId.GetString()!, out var pending))
                {
                    if (methodEl.GetString() == "_queued")
                    {
                        pending.MarkQueued();
                    }
                    else
                    {
                        pending.MarkExecuting();
                    }
                }
                return;
            }

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && _pending.TryRemove(idEl.GetString()!, out var pendingCall))
            {
                if (root.TryGetProperty("result", out var result))
                {
                    pendingCall.Succeed(result.Clone());
                }
                else if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                        ? codeEl.GetInt32()
                        : 0;
                    var message = error.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
                        ? messageEl.GetString()
                        : "";
                    pendingCall.Fail(new InstanceCallException("RPC_ERROR", $"{code}: {message}"));
                }
            }
        }
    }

    /// <summary>以同一错误失败全部在途调用并清空登记(断开/停机清场用)。</summary>
    /// <param name="code">错误码(通常 DISCONNECTED)。</param>
    /// <param name="message">错误信息。</param>
    private void FailAllPending(string code, string message)
    {
        foreach (var pending in _pending.Values)
        {
            pending.Fail(new InstanceCallException(code, message));
        }
        _pending.Clear();
    }

    /// <summary>把整段文本作为单个 UTF-8 文本帧发送(契约 C2:逐帧单 JSON 文档)。</summary>
    /// <param name="ws">目标连接。</param>
    /// <param name="text">完整帧文本。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>带超时接收一帧文本(鉴权 ack 专用)。</summary>
    /// <param name="ws">目标连接。</param>
    /// <param name="timeout">接收超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>帧文本;Close 帧或超时为 null。</returns>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var buffer = new byte[64 * 1024];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>状态推进(同值幂等):离开 Connected 时清空版本 ack,真实变更回调 _onChanged。</summary>
    /// <param name="status">目标状态。</param>
    private void SetStatus(ConnectionStatus status)
    {
        if (_status == status)
        {
            return;
        }
        _status = status;
        if (status != ConnectionStatus.Connected)
        {
            _godotVersionAck = null;
        }
        _onChanged();
    }
}
