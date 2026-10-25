using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Shim;

/// <summary>
/// stdio ↔ daemon HTTP(Streamable HTTP)转发器:每条 stdin 消息 POST 到 daemon,
/// 把响应体中的 JSON-RPC 消息(SSE data 行或纯 JSON)逐条写回 stdout。
/// <para>两种响应形态,按**请求方法**区分(不是按响应 Content-Type):</para>
/// <list type="bullet">
/// <item>普通请求:读完全体后一次性写回——顺序与 stdin 一致,与历史行为等价。</item>
/// <item><c>subscriptions/listen</c> 长流:响应永不结束,故**不读体**,交给后台泵
/// 逐行读 SSE 并把每条 data 行立刻写回;主循环随即返回,长流挂起期间后续 stdin 行照常转发。</item>
/// </list>
/// <para>协议头(SEP-2575):initialize 期间**不得**带 <c>MCP-Protocol-Version</c>(头体必须一致,
/// 错配会被拒收);协商出结果后每个请求带上它;而 <c>subscriptions/listen</c> 是 2026-07-28
/// 专用信封——版本头固定为该版本且 <c>Mcp-Method</c> 强制(缺失即 400)。</para>
/// <para>daemon 短暂消失/重启:重试一轮并在必要时重新自举,全部失败时向 host 输出
/// JSON-RPC 错误(有请求 id 时)——绝不静默挂死。stdout 只承载协议消息,且写入经信号量串行化
/// (长流泵与普通响应可能并发写同一个 TextWriter)。</para>
/// </summary>
/// <param name="options">shim 运行参数(daemon 端口)。</param>
/// <param name="bootstrap">自举器(令牌缺失或转发失败时重新拉起 daemon/读令牌)。</param>
internal sealed class HttpForwarder(ShimOptions options, DaemonBootstrap bootstrap) : IAsyncDisposable
{
    /// <summary>单条消息的最大转发尝试次数(含首次;轮间退避 500ms×attempt)。</summary>
    private const int MaxAttempts = 3;

    /// <summary>长流方法名:唯一会挂起不结束的请求。</summary>
    private const string ListenMethod = "subscriptions/listen";

    /// <summary>长流信封的协议版本(listen 是 2026-07-28 专用信封,与协商版本无关)。</summary>
    private const string ListenProtocolVersion = "2026-07-28";

    /// <summary>会话键头名(daemon 侧 <c>SessionKeyMiddleware.HeaderName</c> 同值)。</summary>
    private const string SessionHeaderName = "x-godot-session";

    /// <summary>shim 版本头名,用于识别版本混跑。</summary>
    private const string ShimVersionHeaderName = "x-godot-shim-version";

    /// <summary>会话工作目录头名,供 <c>godot_connect</c> 空参时发现候选项目。</summary>
    private const string WorkingDirectoryHeaderName = "x-godot-cwd";

    /// <summary>
    /// daemon 进程身份头（每次启动不同）。shim 靠它判断"会话状态还算数吗"。
    /// </summary>
    /// <remarks>与 daemon 侧 <c>DaemonEpoch.HeaderName</c> 必须一致；改一处就是静默失效。</remarks>
    private const string DaemonEpochHeaderName = "x-godot-daemon-epoch";

    /// <summary>
    /// 本进程的会话键:<b>一个 shim 进程 = 一个 MCP 会话</b>。
    /// </summary>
    /// <remarks>
    /// <para>用 UUID 形状(daemon 的 <c>IsValidKey</c> 要求),进程启动时生成一次。</para>
    /// <para>不能跨进程复用:两个 host 各自拉起一个 shim,若共用一个键,它们的工具面会互相污染
    /// (A 激活的组在 B 上也可见)。</para>
    /// </remarks>
    private readonly string _sessionKey = Guid.NewGuid().ToString();

    /// <summary>shim 程序集版本(与 <c>godot-mcp-shim.csproj</c> 的版本同步)。</summary>
    private static readonly string ShimVersion =
        typeof(HttpForwarder).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>共享 HttpClient;总超时设为无限,单次请求超时改由每请求 CTS(30s)控制。</summary>
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>缓存的 bearer 令牌;置 null 表示下次转发前须重新自举并读令牌(daemon 可能重启)。</summary>
    private string? _token;

    /// <summary>
    /// 最后一次成功的 <c>godot_connect</c> 请求帧(供 daemon 重启后重放)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须重放</b>:会话键活在 daemon 的内存里。daemon 一重启,键表清空 ——
    /// 用户的连接状态与 <c>discover_tools</c> 激活的组全部消失,而模型毫不知情:
    /// 它下一轮 <c>tools/list</c> 只会看到 core 9,以为"工具变少了",却没有任何线索
    /// 指向"daemon 重启过"。</para>
    /// <para>spec §1.7 要求两段都重放。只重放 connect 会让 <c>discover_tools</c>
    /// 激活的组静默丢失。</para>
    /// </remarks>
    private string? _lastConnectFrame;

    /// <summary>最后一次成功的 <c>discover_tools</c> 请求帧(供 daemon 重启后重放)。</summary>
    private string? _lastDiscoverFrame;

    /// <summary>
    /// 是否需要在新 daemon 上重放会话状态。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="ObserveEpoch"/> 在检测到 <b>daemon epoch 变化</b> 时置位 ——
    /// 那是"daemon 确实换了"的唯一可靠判据。<b>不再由"请求失败"置位</b>：
    /// 睡眠恢复、杀毒卡顿、调试器暂停都会让一次短请求超时，而 daemon 与键表完好，
    /// 据此重放会把活会话的组静默裁掉（重放的 connect 是**替换**语义）。
    /// </remarks>
    private bool _needsReplay;

    /// <summary>上一次见到的 daemon 进程身份；null 表示尚未观测到。</summary>
    private string? _lastEpoch;

    /// <summary>
    /// 连续重放尝试次数（审核 S1）。成功后清零，超过上限则放弃本轮。
    /// </summary>
    /// <remarks>
    /// 没有这个上限时，一个持续失败的帧会让**每个**请求都多两个 POST ——
    /// 主循环是串行的，那等于把宿主的所有调用都按比例拖慢。
    /// </remarks>
    private int _replayAttempts;

    /// <summary>连续重放尝试上限。3 次足够覆盖"daemon 刚起、实例还没发现"这类瞬时失败。</summary>
    private const int MaxReplayAttempts = 3;

    /// <summary>
    /// 重放期间观测到的 epoch 变化<b>次数</b>；-1 表示当前不在重放中。
    /// </summary>
    /// <remarks>
    /// <b>为什么用计数器而不是"前后比较"</b>：重放开始时 <c>_lastEpoch</c> 仍是**旧**
    /// daemon 的身份（新 daemon 的第一次响应还没到），所以重放中第一次观测到新 epoch
    /// 是**预期**行为。只有观测到第二次及以后，才说明"重放期间 daemon 又换了"。
    /// 用前后比较会把这次预期变化误判成异常，导致每次重启后都多重放一轮。
    /// </remarks>
    private int _epochChangesDuringReplay = -1;

    /// <summary>initialize 协商出的协议版本;未协商前不发版本头。</summary>
    private string? _negotiatedProtocolVersion;

    /// <summary>stdout 写入闸门:长流泵与普通响应可能并发写同一个 TextWriter(非线程安全)。</summary>
    private readonly SemaphoreSlim _stdoutGate = new(1, 1);

    /// <summary>在飞的长流泵(宿主撤退时取消并收尾)。</summary>
    private readonly List<Task> _streams = [];
    /// <summary>保护 _streams 的锁。</summary>
    private readonly object _streamsGate = new();
    /// <summary>长流取消源:宿主撤退(stdin EOF)时触发。</summary>
    private readonly CancellationTokenSource _streamCts = new();

    private Task? _legacyListenTask;
    private readonly TaskCompletionSource _legacyListenReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _legacySurfaceMayBeStale;

    /// <summary>PostAsync 的结果:要么是一批待写回的消息,要么是一条仍在进行、由调用方接管的长流。</summary>
    /// <param name="Messages">要写回 stdout 的 JSON-RPC 消息(通知被接受/空响应时为空)。</param>
    /// <param name="Stream">长流响应(非空时调用方必须交给后台泵并负责释放)。</param>
    private readonly record struct PostOutcome(List<string> Messages, HttpResponseMessage? Stream);

    /// <summary>
    /// 转发(forward)一条 stdin 行帧(line frame)到 daemon,并把响应中的 JSON-RPC 消息逐条写回 stdout。
    /// <para>逻辑链:至多 MaxAttempts 轮 → 无令牌先 EnsureAsync+ReadTokenAsync 自举 →
    /// PostAsync 取回结果 → 若是长流则交给后台泵并立即返回(不阻塞后续 stdin 行),
    /// 否则记住协商版本、逐条写回并 Flush(空列表 = 通知被接受,无回写)→
    /// 传输类异常(HttpRequestException/TaskCanceledException/InvalidOperationException/WebException)
    /// 记 stderr、令牌置 null(防 daemon 重启后令牌轮换)、EnsureAsync 重自举、退避 500ms×attempt
    /// 后进入下一轮 → 全轮失败:请求带非空 id 时向 stdout 写 JSON-RPC -32000 错误,通知则不回写。</para>
    /// </summary>
    /// <param name="requestJson">一行完整的 JSON-RPC 消息。</param>
    /// <param name="stdout">宿主侧标准输出(只承载协议消息)。</param>
    public async Task ForwardLineAsync(string requestJson, TextWriter stdout)
    {
        // 记录会话状态帧(见 _lastConnectFrame 的说明)。
        //
        // 这里缓存的是"最后一次**意图**",不是"最后一次成功的结果":判断成败要看响应，
        // 而判断的收益很小 —— 重放一个当初失败的 connect 只会再失败一次（多一条日志），
        // 却能在"失败后用户又改对了参数"这类情形里自动恢复正常。
        RememberSessionStateFrame(requestJson);

        if (_legacyListenTask is not null && ReadMethod(requestJson) != "initialize")
        {
            try
            {
                await _legacyListenReady.Task.WaitAsync(TimeSpan.FromSeconds(5), _streamCts.Token);
            }
            catch (TimeoutException)
            {
                _legacySurfaceMayBeStale = true;
                Console.Error.WriteLine("[shim] 内部通知流尚未就绪，继续转发并在后台重连。");
            }
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // 上一次请求发现 daemon 换了 → 先用当前令牌把会话状态放回去。
                // 放在重试 try 之前:重放失败不该影响当前请求的转发。
                if (_needsReplay && _token is not null)
                {
                    await ReplaySessionStateAsync();
                }

                if (_token is null)
                {
                    await bootstrap.EnsureAsync(CancellationToken.None);
                    _token = await bootstrap.ReadTokenAsync(CancellationToken.None);

                    // 令牌刚拿到时也可能有待重放的状态(上次失败 + daemon 确实换了)。
                    if (_needsReplay)
                    {
                        await ReplaySessionStateAsync();
                    }
                }

                var outcome = await PostAsync(requestJson);
                if (outcome.Stream is { } stream)
                {
                    StartStreamPump(stream, stdout);
                    return;
                }
                RememberNegotiatedProtocolVersion(outcome.Messages);
                var messages = outcome.Messages;
                if (ReadMethod(requestJson) == "initialize"
                    && _negotiatedProtocolVersion is { } version
                    && version != ListenProtocolVersion)
                {
                    messages = EnableLegacyListChanged(messages);
                    StartLegacyListen(stdout);
                }
                await WriteMessagesAsync(stdout, messages);
                return;
            }
            catch (TaskCanceledException ex) when (IsToolCall(requestJson))
            {
                // **`tools/call` 超时绝不重试**。工具可能已经在 daemon 侧真正执行
                // (取消令牌到不了工具的 CancellationToken.None),重试就会把非幂等变异
                // 跑 1-3 次、可能写一半。这里直接把"可能已执行"告诉 host,让它先查状态。
                Console.Error.WriteLine($"[shim] tools/call 超时(不重试): {ex.Message}");
                var timeoutFailure = BuildTimeoutNotice(requestJson);
                if (timeoutFailure is not null)
                {
                    await WriteSingleAsync(stdout, timeoutFailure);
                }

                return;
            }
            catch (HttpRequestException ex) when (IsToolCall(requestJson) && !IsDefinitelyNotSent(ex))
            {
                // **`tools/call` 的传输失败也只在不"确定未送达"时才不重试**。
                // 请求可能已经发出、daemon 中途崩溃 —— 工具跑了一半。这种情况与超时同性质:
                // 盲目重试等于让同一个变异再执行一遍。
                // 只有明确"连接根本没建立"(DNS/连接被拒)才算安全重试,那走下面的通用 catch。
                Console.Error.WriteLine($"[shim] tools/call 传输失败(不重试): {ex.Message}");
                var transportFailure = BuildTimeoutNotice(requestJson, ex.Message);
                if (transportFailure is not null)
                {
                    await WriteSingleAsync(stdout, transportFailure);
                }

                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or WebException)
            {
                Console.Error.WriteLine($"[shim] 转发失败(第 {attempt}/{MaxAttempts} 次): {ex.Message}");
                _token = null; // daemon 可能重启(令牌轮换或状态目录变更)——下轮重读。

                // **只有"确定没连上"才置重放位**。
                //
                // 两个方向都要准：
                //   ① 连接被拒 / DNS 解析失败 = daemon 确实不在（进程已退出，端口还关着）
                //      → 它重启后键表必然是空的，必须重放。这是最常见的重启时序。
                //   ② 超时 / 5xx / 响应中途断**不**算：睡眠恢复、杀毒卡顿、调试器暂停
                //      30 秒都会让一次短请求超时，而 daemon 与键表完好 ——
                //      据此重放会把活会话的组静默裁掉（重放的 connect 是替换语义）。
                //
                // 判据复用 IsDefinitelyNotSent 的语义，但**不限于 tools/call**：
                // 连接被拒与请求方法无关。至于"新 daemon 已就绪、请求返回 200"那种时序，
                // 由 ObserveEpoch 看响应头发现 —— 两条判据互补，覆盖两种重启形态。
                if (ex is HttpRequestException definite && IsDefinitelyNotSent(definite))
                {
                    _needsReplay = true;
                }
                // 真正的判据是响应头里的 daemon epoch(见 ObserveEpoch);失败路径上
                // 根本没有响应可看,所以这里什么都不做。
                try
                {
                    await bootstrap.EnsureAsync(CancellationToken.None);
                }
                catch (Exception ensureError)
                {
                    Console.Error.WriteLine($"[shim] daemon 自举失败: {ensureError.Message}");
                }
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
                }
            }
        }

        var failure = BuildFailureResponse(requestJson);
        if (failure is not null)
        {
            await WriteSingleAsync(stdout, failure);
        }
    }

    /// <summary>
    /// POST 一条消息。
    /// <para>逻辑链:解析请求方法 → 按 SEP-2575 组装协议头 → POST 到 daemon 根路径,
    /// Accept 声明 json+SSE,Bearer 带缓存令牌,30s 每请求超时(只约束**响应头**阶段)→
    /// 202 返回空列表(通知被接受)→ 401 抛 HttpRequestException(触发上层重读令牌)→
    /// 其余非 2xx 由 EnsureSuccessStatusCode 抛出 → **长流方法直接返回响应本体**(绝不读体:
    /// 订阅永不结束)→ 空体返回空列表 → Content-Type 为 text/event-stream 时抽取全部 data: 行
    /// → 其余按单条 JSON 消息返回。</para>
    /// </summary>
    /// <param name="requestJson">待转发的 JSON-RPC 消息。</param>
    /// <returns>待写回的消息列表,或一条需由调用方接管的长流响应。</returns>
    private async Task<PostOutcome> PostAsync(
        string requestJson, CancellationToken cancellationToken = default)
    {
        var method = ReadMethod(requestJson);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"http://127.0.0.1:{options.Port}/"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        // 会话身份:daemon 的工具面按会话键分级(ADR-0007)。shim 进程 = 一个会话,
        // 所以键在进程生命周期内固定。缺了它,daemon 会把这个会话当"未连接",
        // 只给 godot_connect —— 表现为"所有工具都消失了"。
        request.Headers.TryAddWithoutValidation(SessionHeaderName, _sessionKey);
        request.Headers.TryAddWithoutValidation(ShimVersionHeaderName, ShimVersion);

        // cwd:godot_connect 空参时拿它找 project.godot(不递归扫描,只做存在性校验)。
        request.Headers.TryAddWithoutValidation(
            WorkingDirectoryHeaderName, EncodeWorkingDirectory(Environment.CurrentDirectory));

        foreach (var (name, value) in ProtocolHeaders(method))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        // 超时按请求方法分流:`tools/call` 拿 210 秒(可经 GODOT_MCP_TOOL_CALL_TIMEOUT 覆盖),
        // 其余短请求仍 30 秒。这个 CTS 同时管 SendAsync 与读体,用一个值最不容易漏。
        var deadline = IsToolCall(requestJson) ? options.ToolCallTimeout : ShimOptions.ShortRequestTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        // 认 daemon 的进程身份。**这是判断"会话状态还算数吗"的唯一可靠依据** ——
        // 与请求成败无关，所以既能发现"新 daemon 已就绪、请求返回 200 但键表是空的"，
        // 也不会被一次超时误触发。
        ObserveEpoch(response);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            response.Dispose();
            return new PostOutcome([], null);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            // **必须带 StatusCode**。重放的 catch 里靠 `ex is HttpRequestException
            // { StatusCode: Unauthorized }` 判断"令牌失效",不带的话那段是死代码:
            // 重放 401 后 `_token` 不会被清,下一次请求会拿着同一个旧令牌再吃一次 401。
            throw new HttpRequestException(
                "daemon 拒绝令牌(401)——重读令牌后重试", null, HttpStatusCode.Unauthorized);
        }
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            response.Dispose();
            throw;
        }

        if (method == ListenMethod)
        {
            // 长流:响应体永不结束,读它必然卡到超时——交给调用方启动后台泵。
            return new PostOutcome([], response);
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (body.Length == 0)
            {
                return new PostOutcome([], null);
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType == "text/event-stream")
            {
                return new PostOutcome(ParseSseDataLines(body), null);
            }
            return new PostOutcome([body], null);
        }
        finally
        {
            response.Dispose();
        }
    }

    // HTTP 头只传 ASCII；显式标记保证旧路径里的字面量 %20 不被误解码。
    internal static string EncodeWorkingDirectory(string directory)
        => "uri:" + Uri.EscapeDataString(directory);

    private static List<string> EnableLegacyListChanged(List<string> messages)
    {
        return messages.Select(message =>
        {
            if (JsonNode.Parse(message) is not JsonObject root
                || root["result"] is not JsonObject result)
                return message;
            if (result["capabilities"] is not JsonObject capabilities)
                result["capabilities"] = capabilities = new JsonObject();
            if (capabilities["tools"] is not JsonObject tools)
                capabilities["tools"] = tools = new JsonObject();
            // 后端无状态 SDK 不对 legacy 宣告通知；由 shim 的内部订阅承担。
            tools["listChanged"] = true;
            return root.ToJsonString();
        }).ToList();
    }

    private void StartLegacyListen(TextWriter stdout)
    {
        if (_legacyListenTask is not null) return;
        _legacyListenTask = PumpLegacyNotificationsAsync(stdout);
        lock (_streamsGate) _streams.Add(_legacyListenTask);
    }

    /// <summary>仅把真实目录变更转换成 legacy 普通通知，内部 ack 和订阅 id 不出 stdout。</summary>
    private async Task PumpLegacyNotificationsAsync(TextWriter stdout)
    {
        const string frame = """
            {"jsonrpc":"2.0","id":"godot-shim-notifications","method":"subscriptions/listen","params":{"notifications":{"toolsListChanged":true},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"tools":{"listChanged":true}},"io.modelcontextprotocol/clientInfo":{"name":"godot-mcp-shim","version":"1"}}}}
            """;
        const string changed = """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";
        var connectedBefore = false;
        while (!_streamCts.IsCancellationRequested)
        {
            try
            {
                if (_token is null)
                {
                    await bootstrap.EnsureAsync(_streamCts.Token);
                    _token = await bootstrap.ReadTokenAsync(_streamCts.Token);
                }
                using var ackWindow = CancellationTokenSource.CreateLinkedTokenSource(_streamCts.Token);
                ackWindow.CancelAfter(TimeSpan.FromSeconds(5));
                var outcome = await PostAsync(frame, ackWindow.Token);
                using var response = outcome.Stream
                    ?? throw new InvalidOperationException("内部 listen 未返回订阅流。");
                await using var stream = await response.Content.ReadAsStreamAsync(_streamCts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var acknowledged = false;
                while (await reader.ReadLineAsync(acknowledged ? _streamCts.Token : ackWindow.Token) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var payload = line["data:".Length..].Trim();
                    if (payload.Length == 0) continue;
                    using var document = JsonDocument.Parse(payload);
                    if (!document.RootElement.TryGetProperty("method", out var method)) continue;
                    if (method.GetString() == "notifications/subscriptions/acknowledged")
                    {
                        acknowledged = true;
                        ackWindow.CancelAfter(Timeout.InfiniteTimeSpan);
                        _legacyListenReady.TrySetResult();
                        if (connectedBefore || _legacySurfaceMayBeStale)
                        {
                            _legacySurfaceMayBeStale = false;
                            await WriteSingleAsync(stdout, changed);
                        }
                        connectedBefore = true;
                    }
                    else if (method.GetString() == "notifications/tools/list_changed")
                    {
                        await WriteSingleAsync(stdout, changed);
                    }
                }
            }
            catch (OperationCanceledException) when (_streamCts.IsCancellationRequested) { break; }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                Console.Error.WriteLine("[shim] 内部 listen 被永久拒绝（400），停止重试。");
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                or IOException or JsonException or InvalidOperationException)
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                    _token = null;
                if (ex is HttpRequestException connectionError && IsDefinitelyNotSent(connectionError))
                    _token = null;
                Console.Error.WriteLine($"[shim] 内部通知流断开，将重连：{ex.GetType().Name}");
            }
            try { await Task.Delay(500, _streamCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>按 SEP-2575 组装协议头。
    /// <para>initialize 期间不发版本头——握手期头体必须一致,错配会被拒收;
    /// 协商出结果后每个请求带上它;subscriptions/listen 是 2026-07-28 专用信封:
    /// 版本头固定为该版本,且 Mcp-Method 强制(缺失即 400)。</para>
    /// </summary>
    /// <param name="method">请求方法名(解析失败为 null)。</param>
    /// <returns>要附加的 (头名, 头值) 序列。</returns>
    private IEnumerable<(string Name, string Value)> ProtocolHeaders(string? method)
    {
        if (method == ListenMethod)
        {
            yield return ("MCP-Protocol-Version", ListenProtocolVersion);
            yield return ("Mcp-Method", ListenMethod);
            yield break;
        }
        if (method != "initialize" && _negotiatedProtocolVersion is { Length: > 0 } negotiated)
        {
            yield return ("MCP-Protocol-Version", negotiated);
        }
    }

    /// <summary>读取请求的 method 字段;非对象/无 method/解析失败一律返回 null(不抛)。</summary>
    /// <param name="requestJson">一行 JSON-RPC 消息。</param>
    /// <returns>方法名,或 null。</returns>
    private static string? ReadMethod(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String
                    ? method.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从响应消息里记住 initialize 协商出的 protocolVersion(供后续请求的版本头)。</summary>
    /// <param name="messages">刚收到的响应消息列表。</param>
    private void RememberNegotiatedProtocolVersion(List<string> messages)
    {
        foreach (var message in messages)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("protocolVersion", out var version)
                    && version.ValueKind == JsonValueKind.String)
                {
                    _negotiatedProtocolVersion = version.GetString();
                }
            }
            catch (JsonException)
            {
                // 非 JSON 消息(理论上不存在):忽略,不影响转发。
            }
        }
    }

    /// <summary>把长流交给后台泵并登记,以便宿主撤退时收尾。主循环随即返回(不再阻塞 stdin)。</summary>
    /// <param name="response">长流响应(所有权移交泵)。</param>
    /// <param name="stdout">宿主侧标准输出。</param>
    private void StartStreamPump(HttpResponseMessage response, TextWriter stdout)
    {
        var pump = PumpSseAsync(response, stdout, _streamCts.Token);
        lock (_streamsGate)
        {
            _streams.RemoveAll(task => task.IsCompleted);
            _streams.Add(pump);
        }
    }

    /// <summary>
    /// 长流泵(后台):逐行读 SSE,**每读到一条 data 行立刻写回 stdout 并 Flush**——
    /// 这正是长流与普通响应的根本区别(普通响应读完全体才写)。
    /// <para>逻辑链:读行 → 跳过非 data 行 → 空负载跳过 → 串行写回 → 流结束/取消/异常即收尾。
    /// **不重试**:重试会向 daemon 重复订阅;中断只记 stderr。响应由本方法负责释放。</para>
    /// </summary>
    /// <param name="response">长流响应。</param>
    /// <param name="stdout">宿主侧标准输出。</param>
    /// <param name="cancellationToken">宿主撤退触发的取消令牌。</param>
    private async Task PumpSseAsync(HttpResponseMessage response, TextWriter stdout, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                var trimmed = line.TrimEnd('\r');
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                var payload = trimmed["data:".Length..].TrimStart();
                if (payload.Length == 0)
                {
                    continue;
                }
                await WriteSingleAsync(stdout, payload);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主撤退(或进程退出):正常收尾。
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[shim] 长流中断: {ex.Message}");
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>从 SSE(server-sent events)响应体抽取全部 data: 行负载(按 \n 切分、容忍 \r、跳过空负载)。</summary>
    /// <param name="body">完整 SSE 响应体文本。</param>
    /// <returns>data: 行之后的 JSON-RPC 消息列表(保持原顺序)。</returns>
    private static List<string> ParseSseDataLines(string body)
    {
        var messages = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line["data:".Length..].TrimStart();
                if (payload.Length > 0)
                {
                    messages.Add(payload);
                }
            }
        }
        return messages;
    }

    /// <summary>串行写回一批消息(空列表直接返回,不产生任何输出)。</summary>
    /// <param name="stdout">宿主侧标准输出。</param>
    /// <param name="messages">要写回的消息。</param>
    private async Task WriteMessagesAsync(TextWriter stdout, IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }
        await _stdoutGate.WaitAsync();
        try
        {
            foreach (var message in messages)
            {
                await stdout.WriteLineAsync(message);
            }
            await stdout.FlushAsync();
        }
        finally
        {
            _stdoutGate.Release();
        }
    }

    /// <summary>串行写回单条消息并立即 Flush(长流泵用:宿主必须尽快看到通知)。</summary>
    /// <param name="stdout">宿主侧标准输出。</param>
    /// <param name="message">要写回的消息。</param>
    private async Task WriteSingleAsync(TextWriter stdout, string message)
    {
        await _stdoutGate.WaitAsync();
        try
        {
            await stdout.WriteLineAsync(message);
            await stdout.FlushAsync();
        }
        finally
        {
            _stdoutGate.Release();
        }
    }

    /// <summary>转发全败时构造 JSON-RPC 错误(仅当请求带非空 id;通知无法应答)。
    /// <para>用 JsonObject 而非字符串插值构造:手写大括号曾多出一个 <c>}</c>,产出宿主无法解析的非法 JSON。</para>
    /// </summary>
    /// <param name="requestJson">原始请求行。</param>
    /// <summary>该请求是否是 tools/call(超时时不得重试)。</summary>
    /// <param name="requestJson">一行 JSON-RPC 消息。</param>
    /// <returns>是 tools/call 则为 true。</returns>
    /// <summary>
    /// 该异常是否<b>确定请求没送达</b>(只有这些才可以安全重试)。
    /// </summary>
    /// <remarks>
    /// <para><c>HttpRequestError.ConnectionError</c>(连接被拒)与 <c>NameResolutionError</c>(DNS)
    /// 发生在请求体发出之前 —— 重试不会重复执行。</para>
    /// <para>其余(如 <c>ResponseEnded</c>/<c>SecureConnectionError</c>/<c>Unknown</c>)都可能发生在
    /// 工具已经跑起来之后,按"可能已执行"处理。</para>
    /// </remarks>
    /// <param name="ex">传输异常。</param>
    /// <returns>确定未送达则为 true。</returns>
    private static bool IsDefinitelyNotSent(HttpRequestException ex)
        => ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError;

    private static bool IsToolCall(string requestJson) =>
        string.Equals(ReadMethod(requestJson), "tools/call", StringComparison.Ordinal);

    /// <summary>
    /// 记住会话状态帧：<c>godot_connect</c> 与 <c>discover_tools</c>（spec §1.7）。
    /// </summary>
    /// <remarks>
    /// <para>daemon 重启后键表清空，只有重放这两段才能让用户的连接与已激活的组回来。
    /// 只重放 connect 会让 <c>discover_tools</c> 激活的组静默丢失。</para>
    /// <para>识别方式：<c>tools/call</c> + <c>params.name</c>。不用 <c>Mcp-Name</c> 头，
    /// 因为那是 shim 自己按 body 组装的，读 body 更直接。</para>
    /// </remarks>
    /// <param name="requestJson">一行 JSON-RPC 请求。</param>
    private void RememberSessionStateFrame(string requestJson)
    {
        if (!IsToolCall(requestJson))
        {
            return;
        }

        var toolName = ReadToolName(requestJson);
        if (string.Equals(toolName, "godot_connect", StringComparison.Ordinal))
        {
            _lastConnectFrame = requestJson;
        }
        else if (string.Equals(toolName, "discover_tools", StringComparison.Ordinal))
        {
            // **只缓存会改变状态的 discover_tools**（审核 F3）。
            //
            // 无参 / 只有 include_schemas 的调用按规范是"浏览目录"、不改激活集合，
            // 而工具描述与提示都在**主动引导**模型这么调。若把浏览帧也缓存，
            // "激活 playtest → 浏览一次目录 → daemon 重启"之后重放的就是那次浏览 ——
            // 什么都没恢复，而 shim 手里本来有一份有效帧。
            if (DiscoverChangesState(requestJson))
            {
                _lastDiscoverFrame = requestJson;
            }
        }
    }

    /// <summary>
    /// 判断一次 <c>discover_tools</c> 调用是否会改变会话的激活集合。
    /// </summary>
    /// <remarks>
    /// 会改状态的参数：<c>request</c>（激活/查找）、<c>reset</c>（停用）、
    /// <c>refresh_extensions</c>（刷新扩展组）。其余（无参、只有 <c>include_schemas</c>、
    /// <c>activate:false</c>）都只读。
    /// </remarks>
    /// <param name="requestJson">一行 JSON-RPC 请求。</param>
    /// <returns>会改变激活集合时 true。</returns>
    internal static bool DiscoverChangesState(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (!document.RootElement.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object
                || !parameters.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
            {
                // 没有 arguments 就是无参调用 = 只浏览。
                return false;
            }

            // **`reset` 与 `refresh_extensions` 不受 `activate` 影响**（daemon 侧的
            // reset 分支先于 activate 判定）。旧实现把 `activate:false` 当成整体短路，
            // 于是 `discover_tools(reset:true, activate:false)` 被误判成只读、不更新缓存；
            // daemon 重启后重放上一帧 `{request:"playtest"}` —— 用户刚清掉的组被**复活**。
            if (HasValue(arguments, "reset") || IsTrue(arguments, "refresh_extensions"))
            {
                return true;
            }

            // `activate:false` 只压制"激活"这一路——它不阻止 reset 或扩展刷新。
            var activateExplicitlyFalse = arguments.TryGetProperty("activate", out var activate)
                && activate.ValueKind == JsonValueKind.False;
            if (activateExplicitlyFalse)
            {
                return false;
            }

            return HasValue(arguments, "request");
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 给 <c>discover_tools</c> 的重放帧补上 <c>refresh_extensions: true</c>。
    /// </summary>
    /// <remarks>
    /// <para>新 daemon 上扩展目录是空的 —— 它只在 <c>RefreshAllAsync</c> 或编辑器推送
    /// <c>extensions.changed</c> 时重建，而编辑器**只在文件变化时**广播、连接建立时不补推。
    /// 不补这一位，重放里的扩展组会返回 <c>available</c> 而非 <c>activated</c>，
    /// 被 <c>ConnectTool</c> 写回注册表后**永久**丢掉 —— 用户重启后扩展工具消失。</para>
    /// <para>只改 <c>arguments.refresh_extensions</c>，其余字段与 id 原样保留；
    /// 解析失败时退回原帧（宁可少刷新一次，也不要因重写而发出畸形请求）。</para>
    /// </remarks>
    /// <param name="frame">原始 discover_tools 请求帧。</param>
    /// <returns>补好 refresh_extensions 的帧；无法解析时返回原帧。</returns>
    internal static string WithRefreshExtensions(string frame)
    {
        try
        {
            var node = JsonNode.Parse(frame);
            if (node is not JsonObject root
                || root["params"] is not JsonObject parameters)
            {
                return frame;
            }

            if (parameters["arguments"] is not JsonObject arguments)
            {
                arguments = [];
                parameters["arguments"] = arguments;
            }

            arguments["refresh_extensions"] = true;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return frame;
        }
    }
    /// <summary>判断属性是否为布尔 true（缺失或非布尔一律 false）。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>布尔 true 时 true。</returns>
    private static bool IsTrue(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>判断属性是否存在且"有内容"（非 null / 非空串 / 非空数组）。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>有内容时 true。</returns>
    private static bool HasValue(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 },
            JsonValueKind.Array => value.GetArrayLength() > 0,
            // **`false` 是"有值但没意义"**（审核 S5）。`discover_tools(reset:false)`
            // 在 daemon 侧等于"不 reset"，判成有内容会把这种空操作帧缓存成重放目标，
            // 让真正有效的帧被挤掉 —— 重启后什么都没恢复。
            JsonValueKind.False => false,
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    /// <summary>
    /// 观测 daemon 的进程身份（epoch），变了就标记需要重放会话状态。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须用响应头而不是"请求失败"</b>：两个方向都要准。</para>
    /// <list type="bullet">
    /// <item><b>不能漏</b>：token 跨重启稳定、端口固定，新 daemon 先就绪时下一条请求
    /// 就是 200（未知键 → 只剩 <c>godot_connect</c>），失败路径根本走不到。</item>
    /// <item><b>不能误判</b>：一次超时可能只是笔记本睡眠或杀毒卡顿，daemon 与键表完好；
    /// 据此重放会把活会话的组裁掉。</item>
    /// </list>
    /// <para>首次观测只记录不置位 —— 那时还没有会话状态可救。</para>
    /// </remarks>
    /// <param name="response">刚收到的 daemon 响应（尚未读取正文）。</param>
    private void ObserveEpoch(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(DaemonEpochHeaderName, out var values))
        {
            return; // 旧版 daemon 没有这个头;退回"只在失败时重放"更差,所以宁可不重放。
        }

        var epoch = values.FirstOrDefault();
        if (string.IsNullOrEmpty(epoch))
        {
            return;
        }

        if (_lastEpoch is not null && !string.Equals(_lastEpoch, epoch, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[shim] daemon 身份变化({_lastEpoch[..Math.Min(8, _lastEpoch.Length)]} → {epoch[..Math.Min(8, epoch.Length)]}),下次请求前重放会话状态。");
            _needsReplay = true;

            // 重放进行中又观测到变化 = daemon 在重放期间被换过（见字段说明）。
            // 注意重放开始后的**第一次**变化是预期的（新 daemon 的响应刚回来），
            // 所以 _epochChangesDuringReplay 从 0 起算、>1 才算异常。
            if (_epochChangesDuringReplay >= 0)
            {
                _epochChangesDuringReplay++;
            }
        }

        _lastEpoch = epoch;
    }

    /// <summary>
    /// 在新 daemon 上重放会话状态（<c>godot_connect</c> → <c>discover_tools</c>）。
    /// </summary>
    /// <remarks>
    /// <para><b>静默执行</b>：响应一律丢弃、不写 stdout。宿主没有发起这次调用，
    /// 给它两条凭空出现的响应会让它按 id 去配对、配对不上就报协议错误。
    /// 失败也只记 stderr —— 状态恢复是尽力而为，不能反过来挡住当前请求。</para>
    /// <para>顺序固定：先 connect（建键 + 设默认目标），再 discover_tools（激活组）。
    /// 反过来 discover_tools 会在"键还没连接"的会话上执行，组激活了但工具面仍不展开。</para>
    /// <para><b>沿用原始帧（含原 id）</b>：响应被完全丢弃，所以 id 重复今天无害；
    /// 剥掉 id 反而需要重新序列化整帧、多一处出错面。若将来 daemon 会把带 id 的响应
    /// 路由到长流，这里需要改成"去掉 id 再发"。</para>
    /// <para><b>discover 帧补 refresh_extensions</b>（审核 F8）：新 daemon 刚起时
    /// <c>InstanceManager</c> 可能还没发现实例，扩展目录是空的 → 扩展组会被
    /// <c>SetGroups</c> **永久**丢掉。重放时强制刷新一次扩展，让扩展组有机会重建。</para>
    /// <para><b>尝试次数有上限</b>（审核 S1）：若某一帧持续失败（例如 daemon 一直拒绝），
    /// 无限重放会让**每个**请求都多两个 POST。超过上限后放弃，等下一次 daemon 更换再试。</para>
    /// <para><b>重放前后比较 epoch</b>（审核 S2）：若重放期间 daemon 又换了，
    /// 这次重放落在两个不同的 daemon 上 —— 此时**不能**清位，否则那两个 daemon
    /// 的键表都是空的，而 shim 以为恢复完成了，状态就永久丢失。</para>
    /// </remarks>
    private async Task ReplaySessionStateAsync()
    {
        if (_lastConnectFrame is null && _lastDiscoverFrame is null)
        {
            _needsReplay = false;
            _replayAttempts = 0;
            return;
        }

        if (_replayAttempts >= MaxReplayAttempts)
        {
            Console.Error.WriteLine(
                $"[shim] 会话状态重放已连续失败 {MaxReplayAttempts} 次,放弃本轮;等下次 daemon 更换再试。");
            _needsReplay = false;
            _replayAttempts = 0;
            return;
        }

        _replayAttempts++;
        Console.Error.WriteLine($"[shim] 重放会话状态(第 {_replayAttempts}/{MaxReplayAttempts} 次)。");

        // 计数器从 0 起算：重放中**第一次**观测到新 epoch 是预期的（新 daemon 的响应刚到），
        // 只有 >1 才说明"重放期间 daemon 又被换了一次"。用前后比较会把这次预期变化
        // 误判成异常，导致每次重启后都多重放一轮（实测会抖）。
        _epochChangesDuringReplay = 0;
        var allSucceeded = true;
        foreach (var frame in new[] { _lastConnectFrame, _lastDiscoverFrame })
        {
            if (frame is null)
            {
                continue;
            }

            // discover 帧补 refresh_extensions:true。
            //
            // 新 daemon 刚起时扩展目录是空的：扩展只由 RefreshAllAsync 或编辑器推送
            // extensions.changed 重建，而编辑器**只在文件变化时**广播、连接建立时不补推。
            // 于是 discover 里的扩展组会返回 available 而非 activated，被 ConnectTool
            // 写回注册表后**永久**丢掉。补这一位是精确修复，比盲等一个延迟可靠。
            var toSend = ReferenceEquals(frame, _lastDiscoverFrame)
                ? WithRefreshExtensions(frame)
                : frame;

            try
            {
                // 复用常规 POST(头部组装、超时分流都在里面),但**丢弃响应**:
                // 宿主没发起这次调用,凭空给它两条响应会让它按 id 配对失败。
                var outcome = await PostAsync(toSend).ConfigureAwait(false);
                outcome.Stream?.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or WebException)
            {
                allSucceeded = false;
                // 401 = 令牌失效,让外层走"重读令牌"路径(否则会拿着旧令牌再吃一次 401)。
                // 这依赖 PostAsync 抛异常时带上 StatusCode(见那边的说明)。
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                {
                    _token = null;
                }

                Console.Error.WriteLine($"[shim] 重放会话状态失败:{ex.Message}");
            }
        }

        // **两帧都成功才清位**（审核 F4）：connect 成功、discover 失败时若已清位，
        // 那一半状态就再也不会补上，直到下一次 daemon 更换。
        //
        // 且**重放期间 daemon 不能再换**（审核 S2）：若又换了一次，这次重放分别落在
        // 两个不同的 daemon 上，两边的键表都不是"完整状态"。此时清位会让 shim 以为
        // 恢复完成，而实际上两边各只有一半 —— 状态永久丢失。
        var changedDuringReplay = _epochChangesDuringReplay > 1;
        _epochChangesDuringReplay = -1;
        if (changedDuringReplay)
        {
            Console.Error.WriteLine("[shim] 重放期间 daemon 再次更换,保留重放标记等下一轮。");
            _needsReplay = true;
            return;
        }

        if (allSucceeded)
        {
            _needsReplay = false;
            _replayAttempts = 0;
        }
    }

    /// <summary>读取 <c>tools/call</c> 的 <c>params.name</c>（工具名）。</summary>
    /// <param name="requestJson">一行 JSON-RPC 请求。</param>
    /// <returns>工具名；非 <c>tools/call</c> 或解析失败时返回 null。</returns>
    internal static string? ReadToolName(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (!document.RootElement.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
            {
                // **必须检查 ValueKind**。JSON-RPC 允许位置参数（`"params": []`）与
                // `"params": null`，而 `JsonElement.TryGetProperty` 对非对象**抛
                // InvalidOperationException**（不是 JsonException，下面的 catch 抓不到）。
                // 本方法的调用点在重试 try 之外、主循环也没有 try/catch ——
                // 一条合法但非常规的请求就能让整个 shim 进程退出，
                // 宿主侧表现为"这个 MCP 服务器没了"。
                return null;
            }

            return parameters.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // 双保险：对错误 kind 的 JsonElement 调用成员方法时抛这个。
            return null;
        }
    }

    /// <summary>
    /// 构造"工具可能已执行"的超时回执 —— <b>故意用 isError 结果而不是 JSON-RPC error</b>。
    /// </summary>
    /// <remarks>
    /// <para>用 JSON-RPC error 会让宿主认为"请求没到达、可以安全重试",而那正是我们要防的:
    /// 工具很可能已经在 daemon 侧跑完。回 isError 结果可以让模型读文案后先查状态。</para>
    /// <para>文案必须明确说"可能已执行",否则模型会直觉重试同一个变异。</para>
    /// </remarks>
    /// <param name="requestJson">原始请求(取 id 回配)。</param>
    /// <returns>要写回 stdout 的 JSON-RPC 响应;无 id 返回 null。</returns>
    private static string? BuildTimeoutNotice(string requestJson, string? detail = null)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (!document.RootElement.TryGetProperty("id", out var id)
                || id.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            // 文案先算好再放进 JsonObject:复合字符串表达式直接赋给索引器会走
            // JsonNode 的隐式转换,在裁剪/AOT 下触发 IL2026/IL3050。
            var notice = (detail is null
                    ? "工具调用超时(shim 已放弃等待)。"
                    : $"工具调用传输中断({detail})。")
                + "**该操作可能已经在 Godot 侧执行**," +
                "不要直接重试:先用只读工具查询当前状态,确认未生效再重新提交。";

            var textPayload = new JsonObject
            {
                ["success"] = false,
                ["code"] = "TOOL_CALL_TIMEOUT",
                ["message"] = notice,
            };

            // 逐层显式转 JsonNode:collection initializer 里的 Add<T> 对引用类型
            // 同样带 RequiresUnreferencedCode/RequiresDynamicCode。
            JsonNode textBlock = new JsonObject
            {
                ["type"] = "text",
                ["text"] = textPayload.ToJsonString(),
            };
            var content = new JsonArray();
            content.Add(textBlock);

            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = new JsonObject
                {
                    ["isError"] = true,
                    ["content"] = content,
                },
            };
            return message.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <returns>可应答时返回 -32000 错误消息 JSON(id 原样回填);通知或解析失败返回 null。</returns>
    private static string? BuildFailureResponse(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (!document.RootElement.TryGetProperty("id", out var id)
                || id.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject
                {
                    ["code"] = -32000,
                    ["message"] = "godot-mcp daemon unavailable after retries; check daemon logs",
                },
            };
            return message.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>宿主撤退(stdin EOF)时调用:取消在飞的长流并等其收尾,避免留下半开连接。</summary>
    public async ValueTask DisposeAsync()
    {
        _streamCts.Cancel();
        Task[] pending;
        lock (_streamsGate)
        {
            pending = [.. _streams];
        }
        if (pending.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(2)));
        }
        _streamCts.Dispose();
        _stdoutGate.Dispose();
        _http.Dispose();
    }
}
