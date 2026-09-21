using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotMcp.Daemon.Tests.Infrastructure;

// ── 选项与脚本 ─────────────────────────────────────────────────────

internal sealed class FakeGodotOptions
{
    /// <summary>项目根路径(决定注册表 _key 与条目文件名;不必真实存在)。</summary>
    public required string ProjectPath { get; init; }

    /// <summary>非空时向该目录注入真实格式的注册表(entries/&lt;hash&gt;.json + projects.json + token 文件)。</summary>
    public string? StateDir { get; init; }

    /// <summary>会话令牌;缺省随机生成(与 addon 的 32 字节 hex 同形状)。</summary>
    public string? Token { get; init; }

    /// <summary>鉴权 ack 原始 JSON;缺省为编辑器 ack(含 godot_version/version/headless)。</summary>
    public string? AuthAckJson { get; init; }

    /// <summary>非空时(原始 JSON {"code":1008,"reason":"…"}):收到鉴权帧即按此关闭——模拟错误 token 的对端视角。</summary>
    public string? CloseOnAuthJson { get; init; }

    /// <summary>鉴权超时(契约值 2000ms;测试可缩短)。</summary>
    public int AuthTimeoutMs { get; init; } = 2000;

    /// <summary>注册表条目中的 LSP 端点(issue 11 测试用);缺省 null(按引擎默认 6005 计)。</summary>
    public int? LspPort { get; init; }

    /// <summary>监听端口;0(缺省)时取随机空闲端口(固定端口用于注册表条目需对齐已知端口的场景)。</summary>
    public int Port { get; init; }

    /// <summary>按方法注册的应答脚本(到达顺序消费;"$request_id" 在发送前替换为请求 id)。</summary>
    public List<FakeScript> Scripts { get; init; } = new();

    /// <summary>
    /// 变更串行化方法集(镜像 addon MutationLane):命中方法走实例级全局 FIFO 车道 ——
    /// 排队者先收 `_queued`、开执行前收 `_executing`(契约 C3/C5/C6);空集 = 旧行为。
    /// </summary>
    public IReadOnlyCollection<string> SerializedMutationMethods { get; init; } = Array.Empty<string>();

    /// <summary>鉴权成功后的主动推送(广播信封帧)。</summary>
    public List<FakePush> OnConnect { get; init; } = new();
}

/// <summary>替身收到的一次操作记录(矩阵验收的串行/零串台证据;窗口仅串行方法真实计时)。</summary>
/// <param name="Method">请求方法名(domain.verb)。</param>
/// <param name="ParamsRaw">请求 params 的原始 JSON 文本(无 params 时为 "null")。</param>
/// <param name="WasQueued">是否曾在变更车道排队(true = 发送过 _queued)。</param>
/// <param name="StartedUtc">开执行时刻(获得车道后;非串行方法即收到请求时刻)。</param>
/// <param name="EndedUtc">脚本帧全部发完的时刻。</param>
internal sealed record FakeOpRecord(
    string Method, string ParamsRaw, bool WasQueued, DateTime StartedUtc, DateTime EndedUtc);

/// <summary>单方法应答脚本:匹配到该方法的一次请求按 Frames 顺序回放(Reusable 时可反复回放)。</summary>
internal sealed class FakeScript
{
    /// <summary>匹配的请求方法名(domain.verb)。</summary>
    public required string Method { get; init; }

    /// <summary>按顺序回放的应答帧(可含 $request_id/$params 占位)。</summary>
    public List<FakeFrame> Frames { get; init; } = new();

    /// <summary>可复用脚本:不按"每脚本消费一次"计数,每次匹配的请求都回放(多轮交替调用场景)。</summary>
    public bool Reusable { get; init; }
}

/// <summary>脚本应答帧:发送前可选延迟,模拟编辑器处理耗时(乱序/超时场景的驱动器)。</summary>
internal sealed class FakeFrame
{
    /// <summary>发送本帧前的延迟毫秒数(0 = 立即发送)。</summary>
    public int AfterMs { get; init; }

    /// <summary>原始 JSON 文本(逐帧单文档;可含 "$request_id" 占位)。</summary>
    public required string Json { get; init; }
}

/// <summary>鉴权成功后的主动推送帧(信封通知,如 dock 面变更)。</summary>
internal sealed class FakePush
{
    /// <summary>推送前的延迟毫秒数(0 = 鉴权 ack 后立即推)。</summary>
    public int AfterMs { get; init; }

    /// <summary>推送的原始 JSON 文本(单文档信封)。</summary>
    public required string Json { get; init; }
}

// ── 实例 ─────────────────────────────────────────────────────────

/// <summary>
/// 可编程 Godot 编辑器实例测试替身(issue 04):按 C1–C22 wire contract 的服务器侧行为——
/// 首帧认证(2s 超时 / 1008 关闭)、逐帧单 JSON 文档、domain.verb 按到达顺序分发到脚本、
/// 未匹配方法回 -32601、解析失败回 -32700、广播通知;并按真实注册表格式写盘。
/// 契约真源:addons/godot_mcp_toolkit/transport/**(server_request_router.gd:线路契约冻结)。
/// </summary>
internal sealed class FakeGodotInstance : IDisposable
{
    /// <summary>默认鉴权 ack(编辑器形态:含 godot_version/version/headless)。</summary>
    private const string DefaultEditorAck =
        """{"authed":true,"godot_version":"4.5","version":"1.0.0","headless":false}""";

    /// <summary>本实例的启动选项(脚本/鉴权配置/串行方法集的只读视图)。</summary>
    private readonly FakeGodotOptions _options;

    /// <summary>各脚本的已消费标记(下标对应 Scripts;Reusable 脚本永不置位)。</summary>
    private readonly bool[] _scriptConsumed;

    /// <summary>已完成鉴权的对端连接(广播目标;_gate 保护)。</summary>
    private readonly List<WebSocket> _authedPeers = new();

    /// <summary>保护 _authedPeers 的锁(鉴权加入/拆除移除/广播快照并发)。</summary>
    private readonly object _gate = new();

    /// <summary>实例级取消源(Dispose 时取消接受循环与全部挂起任务)。</summary>
    private readonly CancellationTokenSource _cts = new();

    // 变更车道(每实例全局,跨连接共享——镜像 addon MutationLane 的实例级 FIFO)。
    /// <summary>变更车道信号量(初值 1;RunSerializedMutation 在此排队,整个脚本窗口持锁)。</summary>
    private readonly SemaphoreSlim _mutationLane = new(1, 1);

    /// <summary>保护 _ops 的锁(请求处理与串行任务并发写入)。</summary>
    private readonly object _opsGate = new();

    /// <summary>收到过的操作记录(Ops 属性的底层数据)。</summary>
    private readonly List<FakeOpRecord> _ops = new();

    /// <summary>已发送的 _queued 通知计数(Interlocked;契约 C6 证据)。</summary>
    private int _queuedProgressCount;

    /// <summary>已发送的 _executing 通知计数(Interlocked;契约 C5 证据)。</summary>
    private int _executingProgressCount;

    /// <summary>HTTP 监听器(WS 升级入口;Stop 前为 null)。</summary>
    private HttpListener? _listener;

    /// <summary>接受循环后台任务(Dispose 时等待其收尾)。</summary>
    private Task? _acceptLoop;

    /// <summary>本实例监听的 loopback 端口(注册表条目中的 port 字段来源)。</summary>
    public int Port { get; }

    /// <summary>本实例的会话令牌(客户端首帧 auth 字段必须与其一致)。</summary>
    public string Token { get; }

    /// <summary>项目根路径(透传自选项)。</summary>
    public string ProjectPath => _options.ProjectPath;

    /// <summary>规范化项目键(注册表 _key;经 JsonMatch.CanonicalProjectKey 复刻 addon 规则)。</summary>
    public string ProjectKey { get; }

    /// <summary>项目 12 位哈希(条目文件名;经 JsonMatch.ProjectHashOf 复刻)。</summary>
    public string ProjectHash { get; }

    /// <summary>写入的 mcp_token 文件绝对路径(仅 StateDir 非空时非 null)。</summary>
    public string? TokenFilePath { get; }

    /// <summary>私有构造:由 Start 解析完 token/键/哈希后调用(不启动监听,监听由 Start 继续)。</summary>
    /// <param name="options">启动选项。</param>
    /// <param name="port">监听端口。</param>
    /// <param name="token">会话令牌。</param>
    /// <param name="key">规范化项目键。</param>
    /// <param name="hash">项目 12 位哈希。</param>
    /// <param name="tokenFilePath">已写入的 token 文件路径(未写盘时 null)。</param>
    private FakeGodotInstance(FakeGodotOptions options, int port, string token, string key, string hash, string? tokenFilePath)
    {
        _options = options;
        Port = port;
        Token = token;
        ProjectKey = key;
        ProjectHash = hash;
        TokenFilePath = tokenFilePath;
        _scriptConsumed = new bool[options.Scripts.Count];
    }

    /// <summary>
    /// 启动假编辑器实例:开监听并按需注入注册表。
    /// 逻辑链:解析 token(显式或随机 32 字节 hex) → 算规范化键与 12 位哈希 → StateDir 非空时
    /// 先写 mcp_token 文件 → 取端口(显式或随机空闲)并启动 HttpListener → StateDir 非空时
    /// 再写 entries/&lt;hash&gt;.json 与 projects.json 投影 → 返回实例(daemon 侧经注册表发现并连入)。
    /// </summary>
    /// <param name="options">启动选项(项目路径、鉴权配置、应答脚本等)。</param>
    /// <returns>已在监听的假编辑器实例。</returns>
    public static FakeGodotInstance Start(FakeGodotOptions options)
    {
        var token = options.Token ?? RandomNumberGenerator.GetHexString(32);
        var key = JsonMatch.CanonicalProjectKey(options.ProjectPath);
        var hash = JsonMatch.ProjectHashOf(key);

        string? tokenFilePath = null;
        if (options.StateDir is not null)
        {
            tokenFilePath = FakeGodotRegistry.WriteTokenFile(options.StateDir, hash, token);
        }

        var port = options.Port != 0 ? options.Port : TestPorts.GetFreePort();
        var instance = new FakeGodotInstance(options, port, token, key, hash, tokenFilePath);
        instance.StartListener();
        if (options.StateDir is not null)
        {
            FakeGodotRegistry.WriteEntry(options.StateDir, options.ProjectPath, port, tokenFilePath!, lspPort: options.LspPort);
        }
        return instance;
    }

    /// <summary>启动 HTTP 监听并拉起接受循环后台任务(构造流程的最后一步)。</summary>
    private void StartListener()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>收到过的操作记录快照(串行窗口 + params;矩阵验收的串行/零串台证据)。</summary>
    public IReadOnlyList<FakeOpRecord> Ops
    {
        get
        {
            lock (_opsGate)
            {
                return _ops.ToList();
            }
        }
    }

    /// <summary>已发出的 `_queued` 进度通知计数(契约 C6 排队事件证据)。</summary>
    public int QueuedProgressCount => Volatile.Read(ref _queuedProgressCount);

    /// <summary>已发出的 `_executing` 进度通知计数(契约 C5 开执行事件证据)。</summary>
    public int ExecutingProgressCount => Volatile.Read(ref _executingProgressCount);

    /// <summary>
    /// 变更串行化(镜像 addon MutationLane):同实例全局 FIFO;排队者先收 `_queued`,
    /// 轮到执行前收 `_executing`;整个脚本帧序列在持锁窗口内发送(窗口即 apply 窗口)。
    /// 逻辑链:尝试即时取车道 → 失败则计 _queued、发 _queued 通知并阻塞等待 → 获得车道后
    /// 计 _executing、发 _executing → 依序延迟发送脚本帧($request_id/$params 占位替换) →
    /// 记录 FakeOpRecord(真实串行窗口) → finally 释放车道;异常仅记日志不外抛(模拟侧容错)。
    /// </summary>
    /// <param name="ws">当前对端连接(通知与帧仅回给请求方)。</param>
    /// <param name="script">命中的应答脚本。</param>
    /// <param name="method">请求方法名(记录用)。</param>
    /// <param name="idEl">请求 id 节点(通知与占位替换取其字符串形态)。</param>
    /// <param name="paramsRaw">请求 params 原文($params 占位替换源)。</param>
    /// <param name="timers">宿主连接的挂起任务表(本任务加入其中,供收尾统一等待)。</param>
    private void RunSerializedMutation(
        WebSocket ws, FakeScript script, string method, JsonElement idEl, string paramsRaw, List<Task> timers)
    {
        // id 兼容字符串/数值形态(daemon 现总发字符串;数值时取原文,避免通知里出现空 id)。
        var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
        var task = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                var queued = !_mutationLane.Wait(0);
                if (queued)
                {
                    Interlocked.Increment(ref _queuedProgressCount);
                    await SendTextAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"method\":\"_queued\",\"params\":{{\"request_id\":\"{id}\"}}}}");
                    await _mutationLane.WaitAsync(_cts.Token);
                }
                acquired = true;
                var startedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _executingProgressCount);
                await SendTextAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"method\":\"_executing\",\"params\":{{\"request_id\":\"{id}\"}}}}");
                foreach (var frame in script.Frames)
                {
                    var json = frame.Json
                        .Replace("\"$request_id\"", $"\"{id}\"")
                        .Replace("\"$params\"", paramsRaw);
                    if (frame.AfterMs > 0)
                    {
                        await Task.Delay(frame.AfterMs, _cts.Token);
                    }
                    await SendTextAsync(ws, json);
                }
                lock (_opsGate)
                {
                    _ops.Add(new FakeOpRecord(method, paramsRaw, queued, startedAt, DateTime.UtcNow));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fake:{Port}] mutation failed ({method}): {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                if (acquired)
                {
                    _mutationLane.Release();
                }
            }
        }, _cts.Token);
        timers.Add(task);
    }

    /// <summary>向全部已鉴权对端广播 dock 面信封帧({notification, params?});paramsJson 为 null 时不带 params 键。</summary>
    /// <param name="notificationType">信封 notification 字段(如 dock 面/文件变更类型)。</param>
    /// <param name="paramsJson">params 的原始 JSON 文本;null 表示省略 params 键。</param>
    public void Broadcast(string notificationType, string? paramsJson)
    {
        var payload = paramsJson is null
            ? $"{{\"notification\":\"{notificationType}\"}}"
            : $"{{\"notification\":\"{notificationType}\",\"params\":{paramsJson}}}";
        WebSocket[] peers;
        lock (_gate)
        {
            peers = _authedPeers.ToArray();
        }
        foreach (var peer in peers)
        {
            _ = SendTextAsync(peer, payload);
        }
    }

    /// <summary>
    /// 接受循环:阻塞等待 HTTP 连接,每个连接交给独立任务处理(连接间互不阻塞);
    /// 实例取消时从阻塞中退出(其余异常不可能是取消之外的正常路径)。
    /// </summary>
    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => HandlePeerAsync(ctx));
        }
    }

    /// <summary>
    /// 单个 WebSocket 对端的完整会话状态机(镜像 mcp_server.gd 服务器侧行为)。
    /// 逻辑链:升级 WS(失败回 400) → 启动独立鉴权超时看门狗 → 进入收帧循环:
    /// 解析失败回 -32700(id=null,鉴权状态不变) → 未鉴权时:首帧缺 auth 形状、CloseOnAuthJson
    /// 配置或 token 不符 → 按配置码(缺省 1008 "invalid token")关闭;token 正确 → 计入已鉴权对端、
    /// 回 ack、按 OnConnect 计划延迟推送 → 已鉴权时:带 id+method 的请求按到达顺序匹配脚本
    /// (Reusable 或未消费),命中即标消费并回放帧(串行方法走变更车道,否则立即记录 FakeOpRecord
    /// 后异步发帧),未命中回 -32601 → 通知形态/缺 id 帧忽略不回应 → 对端断开或实例拆除时
    /// 移出对端表,并等待本连接全部挂起任务收尾。
    /// </summary>
    /// <param name="ctx">已接受的 HTTP 上下文(在此升级为 WebSocket)。</param>
    private async Task HandlePeerAsync(HttpListenerContext ctx)
    {
        WebSocket ws;
        try
        {
            ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        var authed = false;
        var timers = new List<Task>();
        // 鉴权超时独立于收包循环计时——必须覆盖"从未发来任何帧"的静默对端(契约 1008 "auth timeout")。
        var authTimeoutTask = StartAuthTimeout(ws, () => authed, _options.AuthTimeoutMs);
        try
        {
            while (true)
            {
                var text = await ReceiveTextAsync(ws, _cts.Token);
                if (text is null)
                {
                    break;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(text);
                }
                catch (JsonException)
                {
                    // mcp_server.gd _handle_message:解析失败 → -32700(id=null),鉴权状态不变。
                    await SendTextAsync(ws, """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""");
                    continue;
                }

                if (!authed)
                {
                    if (!doc.RootElement.TryGetProperty("auth", out var authEl))
                    {
                        // 契约:首帧必须是 {auth, version} 形状——任何其他首帧按无效令牌关闭(validate_auth)。
                        await CloseAsync(ws, 1008, "invalid token");
                        break;
                    }
                    if (_options.CloseOnAuthJson is not null)
                    {
                        var close = JsonDocument.Parse(_options.CloseOnAuthJson).RootElement;
                        await CloseAsync(ws, close.GetProperty("code").GetInt32(), close.GetProperty("reason").GetString() ?? "");
                        break;
                    }
                    if (authEl.GetString() != Token)
                    {
                        await CloseAsync(ws, 1008, "invalid token");
                        break;
                    }
                    authed = true;
                    lock (_gate)
                    {
                        _authedPeers.Add(ws);
                    }
                    await SendTextAsync(ws, _options.AuthAckJson ?? DefaultEditorAck);
                    foreach (var push in _options.OnConnect)
                    {
                        timers.Add(Task.Run(async () =>
                        {
                            try
                            {
                                if (push.AfterMs > 0)
                                {
                                    await Task.Delay(push.AfterMs);
                                }
                                await SendTextAsync(ws, push.Json);
                            }
                            catch (WebSocketException)
                            {
                            }
                        }, _cts.Token));
                    }
                    continue;
                }

                // 已鉴权:路由(镜像 server_request_router.gd 的事务子集)。
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null
                    && doc.RootElement.TryGetProperty("method", out var methodEl)
                    && methodEl.ValueKind == JsonValueKind.String)
                {
                    var method = methodEl.GetString()!;
                    var idRaw = idEl.GetRawText();
                    var scriptIndex = -1;
                    for (var i = 0; i < _options.Scripts.Count; i++)
                    {
                        if ((_options.Scripts[i].Reusable || !_scriptConsumed[i]) && _options.Scripts[i].Method == method)
                        {
                            scriptIndex = i;
                            break;
                        }
                    }
                    if (scriptIndex < 0)
                    {
                        await SendTextAsync(
                            ws,
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32601,\"message\":\"Method not found: {method}\"}}}}");
                        continue;
                    }
                    _scriptConsumed[scriptIndex] = true;
                    var paramsRaw = doc.RootElement.TryGetProperty("params", out var paramsEl)
                        ? paramsEl.GetRawText()
                        : "null";
                    var script = _options.Scripts[scriptIndex];
                    if (_options.SerializedMutationMethods.Contains(method))
                    {
                        RunSerializedMutation(ws, script, method, idEl, paramsRaw, timers);
                    }
                    else
                    {
                        lock (_opsGate)
                        {
                            _ops.Add(new FakeOpRecord(method, paramsRaw, false, DateTime.UtcNow, DateTime.UtcNow));
                        }
                        foreach (var frame in script.Frames)
                        {
                            var json = frame.Json
                                .Replace("\"$request_id\"", $"\"{(idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText())}\"")
                                .Replace("\"$params\"", paramsRaw);
                            timers.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    if (frame.AfterMs > 0)
                                    {
                                        await Task.Delay(frame.AfterMs);
                                    }
                                    await SendTextAsync(ws, json);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[fake:{Port}] send failed (script#{scriptIndex}): {ex.GetType().Name} {ex.Message}");
                                }
                            }, _cts.Token));
                        }
                    }
                    continue;
                }
                // 其余为通知形态(如 _cancel,发出即忘)或缺 id 的帧:忽略,不回应。
            }
        }
        catch (WebSocketException)
        {
            // 对端已断开——目标状态本就是断开。
        }
        catch (OperationCanceledException)
        {
            // 实例拆除。
        }
        finally
        {
            lock (_gate)
            {
                _authedPeers.Remove(ws);
            }
            ws.Dispose();
        }

        try
        {
            await Task.WhenAll(timers);
            await authTimeoutTask;
        }
        catch (Exception)
        {
            // 拆除竞态期间的收尾——目标状态本就是停止。
        }
    }

    /// <summary>
    /// 鉴权超时看门狗:独立于收包循环计时,覆盖"从未发来任何帧"的静默对端。
    /// 逻辑链:延时 timeoutMs(实例取消则提前返回) → 到点仍未鉴权 → 1008 "auth timeout" 关闭。
    /// </summary>
    /// <param name="ws">被看护的连接。</param>
    /// <param name="authed">鉴权状态查询(到点时复查,避免误杀已鉴权对端)。</param>
    /// <param name="timeoutMs">鉴权时限(契约值 2000ms,测试可缩短)。</param>
    private async Task StartAuthTimeout(WebSocket ws, Func<bool> authed, int timeoutMs)
    {
        try
        {
            await Task.Delay(timeoutMs, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!authed() && !_cts.IsCancellationRequested)
        {
            try
            {
                await CloseAsync(ws, 1008, "auth timeout");
            }
            catch (WebSocketException)
            {
            }
        }
    }

    // 每条连接的发送门:WebSocket 发送不得并发(车道 `_queued` 通知会与在飞帧交错)。
    /// <summary>每连接发送信号量表(ConditionalWeakTable 挂接,随连接对象回收)。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendGates = new();

    /// <summary>经每连接发送门串行化地发送一帧完整 UTF-8 文本消息。</summary>
    /// <param name="ws">目标连接。</param>
    /// <param name="text">要发送的文本(单文档 JSON)。</param>
    private static async Task SendTextAsync(WebSocket ws, string text)
    {
        var gate = SendGates.GetValue(ws, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>以指定关闭码/原因关闭连接(对端已断开时吞 WebSocketException)。</summary>
    /// <param name="ws">目标连接。</param>
    /// <param name="code">关闭码(如 1008)。</param>
    /// <param name="reason">关闭原因文本。</param>
    private static async Task CloseAsync(WebSocket ws, int code, string reason)
    {
        try
        {
            await ws.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
    }

    /// <summary>接收一条完整文本消息(分片拼接,直至 EndOfMessage;逐帧单文档契约的接收侧)。</summary>
    /// <param name="ws">目标连接。</param>
    /// <param name="ct">取消令牌(实例拆除时中断阻塞的接收)。</param>
    /// <returns>消息文本;对端发起关闭时为 null。</returns>
    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return sb.ToString();
            }
        }
    }

    /// <summary>Dispose 幂等守卫(重复释放直接返回)。</summary>
    private bool _disposed;

    /// <summary>当前已鉴权对端数(测试断言运行时通道的建立/拆除)。</summary>
    public int AuthedPeerCount
    {
        get
        {
            lock (_gate)
            {
                return _authedPeers.Count;
            }
        }
    }

    /// <summary>
    /// 拆除实例:取消全部后台任务、停监听、断开已鉴权对端,并等待接受循环收尾(至多 2s)。
    /// 幂等(_disposed 守卫);对端侧表现为连接被暴力断开(无关闭握手)。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        lock (_gate)
        {
            foreach (var peer in _authedPeers)
            {
                peer.Dispose();
            }
        }
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
        _cts.Dispose();
    }
}

// ── 注册表注入(真实格式) ────────────────────────────────────────

/// <summary>
/// 按 addon 的注册表写盘配方注入测试条目:entries/&lt;hash&gt;.json(单写入者)+
/// projects.json(by_path 投影)+ project_instance_&lt;hash&gt;/mcp_token。
/// 字段集与 registry_entry_file.gd build_entry 一致;键规范化与 12 位哈希见
/// paths/project_key.gd(契约冻结,经 JsonMatch.CanonicalProjectKey/ProjectHashOf 复刻)。
/// </summary>
internal static class FakeGodotRegistry
{
    /// <summary>注册表写盘序列化选项(制表符缩进 + 不忽略 null —— 与 addon 写盘格式同形)。</summary>
    private static readonly JsonSerializerOptions TabIndented = new()
    {
        WriteIndented = true,
        IndentCharacter = '\t',
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>写 project_instance_&lt;hash&gt;/mcp_token 文件(内容即 token,无换行)。</summary>
    /// <param name="stateDir">注册表根目录。</param>
    /// <param name="hash">项目 12 位哈希(实例目录名)。</param>
    /// <param name="token">会话令牌。</param>
    /// <returns>token 文件的绝对路径(写入注册表条目 token_path 字段)。</returns>
    public static string WriteTokenFile(string stateDir, string hash, string token)
    {
        var dir = Path.Combine(stateDir, $"project_instance_{hash}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mcp_token");
        File.WriteAllText(path, token);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// 更新条目的运行时字段(模拟 playtest 启动:游戏侧发布 runtime_port/runtime_pid);
    /// runtimePort 为 null 表示游戏结束(清除运行时字段)。同时更新条目文件与聚合视图。
    /// </summary>
    /// <param name="stateDir">注册表根目录。</param>
    /// <param name="projectPath">项目根路径(经规范化定位条目)。</param>
    /// <param name="runtimePort">运行时端口;null 表示清除。</param>
    /// <param name="runtimePid">运行时进程 id;null 表示清除。</param>
    public static void UpdateRuntimeFields(string stateDir, string projectPath, int? runtimePort, int? runtimePid)
    {
        var key = JsonMatch.CanonicalProjectKey(projectPath);
        var hash = JsonMatch.ProjectHashOf(key);
        var entryPath = Path.Combine(stateDir, "entries", hash + ".json");
        if (!File.Exists(entryPath))
        {
            throw new InvalidOperationException($"条目文件不存在:{entryPath}");
        }

        var entry = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(entryPath))!;
        entry["runtime_port"] = runtimePort;
        entry["runtime_pid"] = runtimePid;
        AtomicWrite(entryPath, JsonSerializer.Serialize(entry, TabIndented));

        var projectsPath = Path.Combine(stateDir, "projects.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(projectsPath));
        var byPath = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var p in doc.RootElement.GetProperty("by_path").EnumerateObject())
        {
            byPath[p.Name] = JsonSerializer.Deserialize<Dictionary<string, object?>>(p.Value.GetRawText())
                ?? new Dictionary<string, object?>();
        }
        if (byPath.TryGetValue(key, out var row))
        {
            row["runtime_port"] = runtimePort;
            row["runtime_pid"] = runtimePid;
        }
        AtomicWrite(projectsPath, JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["by_path"] = byPath }, TabIndented));
    }

    /// <summary>
    /// 写入完整注册表条目:entries/&lt;hash&gt;.json(含 _key/port/token_path/lsp_*/runtime_* 字段集)
    /// + projects.json by_path 投影(读现有内容叠加本条目后原子写回,幂等)。
    /// </summary>
    /// <param name="stateDir">注册表根目录。</param>
    /// <param name="projectPath">项目根路径(决定 _key 与条目文件名)。</param>
    /// <param name="port">编辑器 WS 端口。</param>
    /// <param name="tokenPathAbsolute">mcp_token 文件绝对路径。</param>
    /// <param name="godotVersion">条目中的 godot_version 字段。</param>
    /// <param name="lspPort">条目中的 lsp_port;null 表示按引擎默认(6005)计。</param>
    /// <param name="pid">编辑器进程 id;缺省取当前测试进程 id(仅求占位真实)。</param>
    public static void WriteEntry(
        string stateDir,
        string projectPath,
        int port,
        string tokenPathAbsolute,
        string godotVersion = "4.5",
        int? lspPort = null,
        int? pid = null)
    {
        var key = JsonMatch.CanonicalProjectKey(projectPath);
        var hash = JsonMatch.ProjectHashOf(key);
        var entriesDir = Path.Combine(stateDir, "entries");
        Directory.CreateDirectory(entriesDir);

        var entry = new Dictionary<string, object?>
        {
            ["_key"] = key,
            ["port"] = port,
            ["token_path"] = tokenPathAbsolute,
            ["pid"] = pid ?? Environment.ProcessId,
            ["started_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["godot_version"] = godotVersion,
            ["runtime_port"] = null,
            ["runtime_pid"] = null,
            ["lsp_host"] = "127.0.0.1",
            ["lsp_port"] = lspPort,
        };
        AtomicWrite(Path.Combine(entriesDir, hash + ".json"), JsonSerializer.Serialize(entry, TabIndented));

        // 聚合投影:读现有 by_path,叠加本条目(去掉 _key),原子写回(与 registry_projection.gd 的
        // 幂等重建语义一致;端口剪除等完整重建规则归真实 addon,替身只需保证条目可见)。
        var projectsPath = Path.Combine(stateDir, "projects.json");
        var byPath = new Dictionary<string, Dictionary<string, object?>>();
        if (File.Exists(projectsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectsPath));
            if (doc.RootElement.TryGetProperty("by_path", out var existing) && existing.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in existing.EnumerateObject())
                {
                    byPath[p.Name] = JsonSerializer.Deserialize<Dictionary<string, object?>>(p.Value.GetRawText())
                        ?? new Dictionary<string, object?>();
                }
            }
        }
        var row = new Dictionary<string, object?>(entry);
        row.Remove("_key");
        byPath[key] = row;
        AtomicWrite(projectsPath, JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["by_path"] = byPath }, TabIndented));
    }

    /// <summary>原子写入口:单次尝试 + 100ms 后一次性重试(重试纪律见方法体内注释)。</summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="json">要写入的 JSON 文本。</param>
    private static void AtomicWrite(string path, string json)
    {
        // 与真实 addon(registry_projection.write_atomic)同纪律:单次尝试 + 一次性重试。
        // Windows 上目标文件正被读取方占用时 Replace 会瞬时失败,100ms 重试覆盖这类偶发
        // (并发读取方见 RegistryReader 的 FileShare 放宽)。
        if (TryAtomicWrite(path, json))
        {
            return;
        }
        Thread.Sleep(100);
        TryAtomicWrite(path, json);
    }

    /// <summary>单次原子写尝试:先写 .tmp 再 File.Move 覆盖(同卷原子替换)。</summary>
    /// <returns>成功 true;IO/权限瞬时失败返回 false(交由 AtomicWrite 决定重试)。</returns>
    private static bool TryAtomicWrite(string path, string json)
    {
        try
        {
            // File.Move(overwrite) 走 MoveFileEx(REPLACE_EXISTING):同卷原子替换。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
