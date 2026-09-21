using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 原始 subscriptions/listen 长流客户端(测试侧 seam):无状态 HTTP 下 host 接收
/// tools/list_changed 的唯一通道(2026-07-28 修订 / SEP-2575)。官方 SDK 客户端
/// 不暴露该通道,故直接驱动裸 HTTP + SSE;订阅 id 固定 "listen-1" 便于断言多路分解标注。
/// </summary>
internal sealed class ListenStream : IAsyncDisposable
{
    /// <summary>固定订阅 id(同时用作挂起请求的 JSON-RPC id,便于断言多路分解标注)。</summary>
    private const string SubscriptionId = "listen-1";

    /// <summary>长流专用 HttpClient(无限超时;Dispose 时释放)。</summary>
    private readonly HttpClient _http;

    /// <summary>挂起的订阅响应(保持连接打开;Dispose 时释放)。</summary>
    private readonly HttpResponseMessage _response;

    /// <summary>读取循环取消源(Dispose 时中断阻塞读)。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>后台读取循环任务(Dispose 时等待其收尾)。</summary>
    private readonly Task _reader;

    /// <summary>acknowledged 事件的完成源(首条订阅确认即完成)。</summary>
    private readonly TaskCompletionSource<JsonObject> _ack = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>保护 _listChanged 的锁(读取循环与轮询断言并发)。</summary>
    private readonly object _gate = new();

    /// <summary>已收到的 tools/list_changed 通知(到达顺序)。</summary>
    private readonly List<JsonObject> _listChanged = new();

    /// <summary>私有构造:由 OpenAsync 在响应头就绪后调用,并立即启动后台读取循环。</summary>
    /// <param name="http">长流专用 HttpClient。</param>
    /// <param name="response">挂起的订阅响应。</param>
    /// <param name="body">响应体流(交给读取循环消费)。</param>
    private ListenStream(HttpClient http, HttpResponseMessage response, Stream body)
    {
        _http = http;
        _response = response;
        _reader = ReadLoopAsync(body, _cts.Token);
    }

    /// <summary>已收到的 tools/list_changed 事件(按到达顺序)。</summary>
    public IReadOnlyList<JsonObject> ListChanged
    {
        get
        {
            lock (_gate)
            {
                return _listChanged.ToList();
            }
        }
    }

    /// <summary>打开 listen 长流(订阅 toolsListChanged;挂起请求自身即测试夹具)。
    /// 逻辑链:构造 subscriptions/listen 请求(_meta 携带 2026-07-28 协议版本与客户端能力/信息)
    /// → Bearer token + Accept 同时声明 JSON 与 SSE(缺一即 406) → ResponseHeadersRead 发送
    /// → 非 2xx 抛出 → 包装为 ListenStream 并启动读取循环。</summary>
    /// <param name="port">daemon HTTP 端口。</param>
    /// <param name="token">Bearer 令牌(状态目录中的稳定 token)。</param>
    /// <returns>已打开的长流客户端(读取循环已启动)。</returns>
    public static async Task<ListenStream> OpenAsync(int port, string token)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var body =
            "{\"jsonrpc\":\"2.0\",\"id\":\"" + SubscriptionId + "\",\"method\":\"subscriptions/listen\",\"params\":{" +
            "\"notifications\":{\"toolsListChanged\":true}," +
            "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"," +
            "\"io.modelcontextprotocol/clientCapabilities\":{\"tools\":{\"listChanged\":true}}," +
            "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"raw-listen\",\"version\":\"1\"}}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Streamable HTTP 的 Accept 协商:同时声明 JSON 与 SSE(缺一即 406)。
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", "subscriptions/listen");

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        return new ListenStream(http, response, stream);
    }

    /// <summary>等待唯一一条 acknowledged(订阅成立的标志;授予类型由调用方断言)。</summary>
    /// <param name="timeout">等待上限。</param>
    /// <returns>acknowledged 通知消息;超时抛 TimeoutException。</returns>
    public async Task<JsonObject> WaitForAckAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_ack.Task, Task.Delay(timeout));
        if (completed != _ack.Task)
        {
            throw new TimeoutException("等待 listen acknowledged 超时");
        }
        return await _ack.Task;
    }

    /// <summary>等待第 count 条 tools/list_changed(1 起;50ms 轮询快照)。</summary>
    /// <param name="count">目标条数(1 起)。</param>
    /// <param name="timeout">等待上限。</param>
    /// <returns>第 count 条通知消息;超时抛 TimeoutException。</returns>
    public async Task<JsonObject> WaitForListChangedAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var seen = ListChanged;
            if (seen.Count >= count)
            {
                return seen[count - 1];
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"等待第 {count} 条 tools/list_changed 超时");
    }

    /// <summary>取消读取循环(至多等 2s 收尾)并释放响应与 HttpClient(服务器侧连接随之断开)。</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(2)));
        _response.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// 长流读取循环(后台任务)。
    /// 逻辑链:逐行读 → 跳过非 data 行(SSE 其余字段对本夹具无关) → data 行按 JSON 解析(非对象跳过)
    /// → method 为 notifications/subscriptions/acknowledged 时完成 _ack → 为 notifications/tools/list_changed
    /// 时加锁追加 _listChanged → 流结束/取消/异常时循环自然收尾(不抛出)。
    /// </summary>
    /// <param name="body">响应体流(OpenAsync 交入)。</param>
    /// <param name="cancellationToken">Dispose 触发的取消令牌。</param>
    private async Task ReadLoopAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (!cancellationToken.IsCancellationRequested)
            {
                var data = await ReadNextDataLineAsync(reader, cancellationToken);
                if (data is null)
                {
                    break;
                }
                if (JsonNode.Parse(data) is not JsonObject message)
                {
                    continue;
                }
                switch (message["method"]?.GetValue<string>())
                {
                    case "notifications/subscriptions/acknowledged":
                        _ack.TrySetResult(message);
                        break;
                    case "notifications/tools/list_changed":
                        lock (_gate)
                        {
                            _listChanged.Add(message);
                        }
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 流结束/取消/连接拆除:读取循环自然收尾。
        }
    }

    /// <summary>读取下一条 SSE data 行(事件其余字段对本夹具无关)。</summary>
    /// <param name="reader">响应体文本读取器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>data 行负载(去掉 "data:" 前缀);流结束时 null。</returns>
    private static async Task<string?> ReadNextDataLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line["data:".Length..].TrimStart();
            }
        }
    }
}
