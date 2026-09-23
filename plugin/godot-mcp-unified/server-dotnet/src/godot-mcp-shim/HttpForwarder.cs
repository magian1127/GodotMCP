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

    /// <summary>共享 HttpClient;总超时设为无限,单次请求超时改由每请求 CTS(30s)控制。</summary>
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>缓存的 bearer 令牌;置 null 表示下次转发前须重新自举并读令牌(daemon 可能重启)。</summary>
    private string? _token;

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
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (_token is null)
                {
                    await bootstrap.EnsureAsync(CancellationToken.None);
                    _token = await bootstrap.ReadTokenAsync(CancellationToken.None);
                }

                var outcome = await PostAsync(requestJson);
                if (outcome.Stream is { } stream)
                {
                    StartStreamPump(stream, stdout);
                    return;
                }
                RememberNegotiatedProtocolVersion(outcome.Messages);
                await WriteMessagesAsync(stdout, outcome.Messages);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or WebException)
            {
                Console.Error.WriteLine($"[shim] 转发失败(第 {attempt}/{MaxAttempts} 次): {ex.Message}");
                _token = null; // daemon 可能重启(令牌轮换或状态目录变更)——下轮重读。
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
    private async Task<PostOutcome> PostAsync(string requestJson)
    {
        var method = ReadMethod(requestJson);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"http://127.0.0.1:{options.Port}/"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        foreach (var (name, value) in ProtocolHeaders(method))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            response.Dispose();
            return new PostOutcome([], null);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new HttpRequestException("daemon 拒绝令牌(401)——重读令牌后重试");
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
