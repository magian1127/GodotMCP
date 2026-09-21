using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GodotMcp.Shim;

/// <summary>
/// stdio ↔ daemon HTTP(Streamable HTTP)转发器:每条 stdin 消息 POST 到 daemon,
/// 把响应体中的 JSON-RPC 消息(SSE data 行或纯 JSON)逐条写回 stdout。
/// daemon 短暂消失/重启:重试一轮并在必要时重新自举,全部失败时向 host 输出
/// JSON-RPC 错误(有请求 id 时)——绝不静默挂死。stdout 只承载协议消息。
/// </summary>
/// <param name="options">shim 运行参数(daemon 端口)。</param>
/// <param name="bootstrap">自举器(令牌缺失或转发失败时重新拉起 daemon/读令牌)。</param>
internal sealed class HttpForwarder(ShimOptions options, DaemonBootstrap bootstrap)
{
    /// <summary>单条消息的最大转发尝试次数(含首次;轮间退避 500ms×attempt)。</summary>
    private const int MaxAttempts = 3;

    /// <summary>共享 HttpClient;总超时设为无限,单次请求超时改由每请求 CTS(30s)控制。</summary>
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>缓存的 bearer 令牌;置 null 表示下次转发前须重新自举并读令牌(daemon 可能重启)。</summary>
    private string? _token;

    /// <summary>
    /// 转发(forward)一条 stdin 行帧(line frame)到 daemon,并把响应中的 JSON-RPC 消息逐条写回 stdout。
    /// <para>逻辑链:至多 MaxAttempts 轮 → 无令牌先 EnsureAsync+ReadTokenAsync 自举 →
    /// PostAsync 取回消息列表,逐条 WriteLine 后 Flush(空列表 = 通知被接受,无回写)→
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

                var messages = await PostAsync(requestJson);
                foreach (var message in messages)
                {
                    await stdout.WriteLineAsync(message);
                }
                if (messages.Count > 0)
                {
                    await stdout.FlushAsync();
                }
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
            await stdout.WriteLineAsync(failure);
            await stdout.FlushAsync();
        }
    }

    /// <summary>POST 一条消息;返回要写回 stdout 的 JSON-RPC 消息列表(通知被接受时为空)。
    /// <para>逻辑链:POST 到 daemon 根路径,Accept 声明 json+SSE,Bearer 带缓存令牌,
    /// 30s 每请求超时 → 202 返回空列表(通知被接受)→ 401 抛 HttpRequestException
    /// (触发上层重读令牌)→ 其余非 2xx 由 EnsureSuccessStatusCode 抛出 → 空体返回空列表 →
    /// Content-Type 为 text/event-stream 时抽取全部 data: 行 → 其余按单条 JSON 消息返回。</para>
    /// </summary>
    /// <param name="requestJson">待转发的 JSON-RPC 消息。</param>
    /// <returns>要写回 stdout 的 JSON-RPC 消息列表(通知被接受/空响应时为空列表)。</returns>
    private async Task<List<string>> PostAsync(string requestJson)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"http://127.0.0.1:{options.Port}/"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return [];
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException("daemon 拒绝令牌(401)——重读令牌后重试");
        }
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (body.Length == 0)
        {
            return [];
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "text/event-stream")
        {
            return ParseSseDataLines(body);
        }
        return [body];
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

    /// <summary>转发全败时构造 JSON-RPC 错误(仅当请求带非空 id;通知无法应答)。</summary>
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
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{{\"code\":-32000," +
                   "\"message\":\"godot-mcp daemon unavailable after retries; check daemon logs\"}}}";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
