using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 测试用 GDScript LSP 替身(issue 11):TCP + Content-Length 成帧的 JSON-RPC。
/// 行为:initialize → {capabilities} 应答;didOpen → 按 uri 后缀配置延迟发布
/// textDocument/publishDiagnostics;hover/completion/definition/references/
/// documentSymbol 返回脚本化结果(帧内 "$uri"/"$params" 占位替换)。
/// </summary>
internal sealed class FakeLspServer : IDisposable
{
    /// <summary>TCP 监听器(构造即启动,端口由操作系统分配)。</summary>
    private readonly TcpListener _listener;

    /// <summary>实例级取消源(Dispose 时停接受/中断阻塞读)。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>保护 _streams 与 ReceivedLog 的锁(多会话并发读写)。</summary>
    private readonly object _gate = new();

    /// <summary>活跃会话流表(Dispose 时逐一断开)。</summary>
    private readonly List<NetworkStream> _streams = new();

    // 写串行化:同一 socket 上的并发 WriteAsync 会让帧字节交错(真实 LSP 串行写,
    // 替身必须同纪律,否则 daemon 端成帧失步 —— 全量并行跑时暴露过该竞态)。
    /// <summary>全局写信号量(跨会话串行化全部帧写入,保成帧完整性)。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>按文件后缀匹配的诊断脚本(uri 含该后缀 → 该诊断数组);default 用于其余。</summary>
    public Dictionary<string, string> DiagnosticsBySuffix { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>未按后缀命中 DiagnosticsBySuffix 时的缺省诊断数组(一条 severity 1 的 error 形状);null 表示回空数组。</summary>
    public string? DefaultDiagnosticsJson { get; init; } =
        """[{"range":{"start":{"line":2,"character":4},"end":{"line":2,"character":9}},"severity":1,"message":"boom error","code":"UNDEFINED"}]""";

    /// <summary>延迟发布毫秒数(模拟解析耗时)。</summary>
    public int DiagnosticsDelayMs { get; init; } = 50;

    /// <summary>
    /// 发布诊断时对 uri 施加的变形(模拟真实 Godot LSP 按自身解析回显真实大小写,
    /// 与 daemon 发送的规范化小写 uri 不同)——用于固化两侧 URI 规范化的回归。
    /// </summary>
    public Func<string, string>? DiagnosticsUriMutator { get; init; }

    /// <summary>方法 → 结果 JSON(替换 "$uri"/"$params" 占位);缺省回 null 结果。</summary>
    public Dictionary<string, string> MethodResults { get; init; } = new(StringComparer.Ordinal);

    /// <summary>收到的消息记录(诊断排查用):"method uri?" 序列。</summary>
    public List<string> ReceivedLog { get; } = new();

    /// <summary>本实例监听的 loopback 端口(注入注册表条目 lsp_port 用)。</summary>
    public int Port { get; }

    /// <summary>构造即在随机空闲端口开始监听并启动接受循环(测试只需读 Port 后直连)。</summary>
    public FakeLspServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>接受循环:每条 TCP 连接交给独立会话任务;取消或监听器停止时自然退出。</summary>
    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => SessionLoopAsync(client));
        }
    }

    /// <summary>
    /// 单连接会话:字节级累积缓冲并持续尝试成帧,每解出一条完整帧交给 HandleMessageAsync
    /// (fire-and-forget:诊断发布等异步动作在内层各自捕获异常或忽略,不阻塞读循环);
    /// 读到 0 字节或异常即断开,并把流移出活跃表。
    /// </summary>
    /// <param name="client">已接受的 TCP 连接。</param>
    private async Task SessionLoopAsync(TcpClient client)
    {
        var stream = client.GetStream();
        lock (_gate)
        {
            _streams.Add(stream);
        }
        var buffer = new List<byte>();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, _cts.Token);
                if (read == 0)
                {
                    break;
                }
                for (var i = 0; i < read; i++)
                {
                    buffer.Add(chunk[i]);
                }
                foreach (var body in DrainFrames(buffer))
                {
                    _ = HandleMessageAsync(stream, body);
                }
            }
        }
        catch (Exception)
        {
        }
        lock (_gate)
        {
            _streams.Remove(stream);
        }
    }

    /// <summary>
    /// 处理一条已解出的 JSON-RPC 消息并记录收包日志。
    /// 逻辑链:解析失败静默丢弃 → initialize 回 capabilities;initialized/didChange/didClose 即忘
    /// → didOpen 按 uri 后缀取诊断脚本,延迟 DiagnosticsDelayMs 后经 DiagnosticsUriMutator 变形 uri
    /// 发布 publishDiagnostics(发布成功/失败均记入 ReceivedLog) → 其余带 id 的请求按 MethodResults
    /// 脚本回结果($uri/$params 占位替换),无脚本回 null 结果。
    /// </summary>
    /// <param name="stream">回写应答的会话流。</param>
    /// <param name="body">已解出的消息正文(JSON 文本)。</param>
    private async Task HandleMessageAsync(NetworkStream stream, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number;
            var idRaw = hasId ? idEl.GetRawText() : "null";
            var paramsRaw = root.TryGetProperty("params", out var p) ? p.GetRawText() : "{}";
            lock (_gate)
            {
                ReceivedLog.Add(method ?? (hasId ? "(response)" : "(?)"));
            }

            switch (method)
            {
                case "initialize":
                    await SendAsync(stream, $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"capabilities\":{{\"hoverProvider\":true,\"completionProvider\":{{}},\"definitionProvider\":true,\"referencesProvider\":true,\"documentSymbolProvider\":true}}}}}}");
                    return;
                case "initialized":
                    return;
                case "textDocument/didOpen":
                    var uri = JsonDocument.Parse(paramsRaw).RootElement
                        .GetProperty("textDocument").GetProperty("uri").GetString()!;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (DiagnosticsDelayMs > 0)
                            {
                                await Task.Delay(DiagnosticsDelayMs, _cts.Token);
                            }
                            var diagnostics = ResolveDiagnostics(uri);
                            var publishedUri = DiagnosticsUriMutator?.Invoke(uri) ?? uri;
                            await SendAsync(stream,
                                $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":{{\"uri\":{JsonSerializer.Serialize(publishedUri)},\"diagnostics\":{diagnostics}}}}}");
                            lock (_gate)
                            {
                                ReceivedLog.Add("publish-sent:" + uri);
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (_gate)
                            {
                                ReceivedLog.Add($"publish-error:{ex.GetType().Name}:{ex.Message}");
                            }
                        }
                    }, _cts.Token);
                    return;
                case "textDocument/didChange":
                    return;
                case "textDocument/didClose":
                    return;
            }

            if (hasId && method is not null)
            {
                var result = MethodResults.TryGetValue(method, out var scripted)
                    ? scripted
                        .Replace("$uri", ExtractUri(paramsRaw))
                        .Replace("\"$params\"", paramsRaw)
                    : "null";
                await SendAsync(stream, $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{result}}}");
            }
        }
    }

    /// <summary>从 params 原文提取 textDocument.uri(仅用于占位替换,解析失败回空串)。</summary>
    /// <param name="paramsRaw">params 的原始 JSON 文本。</param>
    /// <returns>uri 字符串;无法解析时为空串。</returns>
    private static string ExtractUri(string paramsRaw)
    {
        try
        {
            return JsonDocument.Parse(paramsRaw).RootElement
                .GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>按 uri 后缀选诊断脚本(DiagnosticsBySuffix 大小写不敏感),无命中回缺省(或空数组)。</summary>
    /// <param name="uri">didOpen 的文档 uri。</param>
    /// <returns>诊断数组的原始 JSON 文本。</returns>
    private string ResolveDiagnostics(string uri)
    {
        foreach (var (suffix, diagnostics) in DiagnosticsBySuffix)
        {
            if (uri.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return diagnostics;
            }
        }
        return DefaultDiagnosticsJson ?? "[]";
    }

    /// <summary>经全局写门串行化发送一条 Content-Length 成帧消息(先 ASCII 头后 UTF-8 体,flush 推送)。</summary>
    /// <param name="stream">目标会话流。</param>
    /// <param name="message">要发送的 JSON-RPC 消息文本。</param>
    private async Task SendAsync(NetworkStream stream, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeGate.WaitAsync();
        try
        {
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 从字节缓冲解出尽可能多的完整 LSP 帧。
    /// 逻辑链:扫描 \r\n\r\n 头尾分隔 → 无则留待更多字节 → 头中无 Content-Length 则清空缓冲丢弃
    /// → 体不足 length 则留待下次 → 解出整帧并从缓冲移除,循环至无法再解。
    /// </summary>
    /// <param name="buffer">共享累积缓冲(方法内就地消费已解出的字节)。</param>
    /// <returns>本次解出的帧正文列表(UTF-8 解码)。</returns>
    private static List<string> DrainFrames(List<byte> buffer)
    {
        var frames = new List<string>();
        while (true)
        {
            var headerEnd = -1;
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                {
                    headerEnd = i;
                    break;
                }
            }
            if (headerEnd < 0)
            {
                break;
            }
            var headerText = Encoding.ASCII.GetString(buffer.Take(headerEnd).ToArray());
            var match = System.Text.RegularExpressions.Regex.Match(headerText, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                buffer.Clear();
                break;
            }
            var length = int.Parse(match.Groups[1].Value);
            var frameStart = headerEnd + 4;
            if (buffer.Count < frameStart + length)
            {
                break;
            }
            frames.Add(Encoding.UTF8.GetString(buffer.GetRange(frameStart, length).ToArray()));
            buffer.RemoveRange(0, frameStart + length);
        }
        return frames;
    }

    /// <summary>幂等守卫:Dispose 只生效一次(对齐 FakeGodotInstance/WsTestClient 的守卫风格)。</summary>
    private bool _disposed;

    /// <summary>拆除替身:取消循环、停监听并断开全部活跃会话流;幂等,重复调用无副作用。</summary>
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
            _listener.Stop();
        }
        catch (Exception)
        {
        }
        lock (_gate)
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }
        _cts.Dispose();
    }
}
