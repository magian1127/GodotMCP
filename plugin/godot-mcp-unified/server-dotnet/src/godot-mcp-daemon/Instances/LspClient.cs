using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GodotMcp.Daemon.Instances;

/// <summary>
/// daemon 自持的 GDScript LSP 客户端(issue 11;lsp/lspClient.ts 的 C# 移植):
/// TCP + Content-Length 成帧的 JSON-RPC;initialize 握手(rootUri=项目根,
/// 4.5+ 根目录不匹配告警不阻塞)、didOpen/didChange/didClose 文档同步、
/// publishDiagnostics 三态等待(收到 [] = 干净 / 收到列表 / 超时 = 未知)、
/// 通用请求关联(单符号 5s、references 30s、initialize 30s)。
/// 由 InstanceManager 每实例持有一条(GetLspClientAsync 惰性创建),实例拆除时一并 DisposeAsync;
/// 与 InstanceConnection 互不依赖 —— 本类直连 Godot 的 LSP TCP 端口,不走编辑器 WS 通道。
/// </summary>
/// <param name="host">LSP 主机(注册表 lsp_host,空则 127.0.0.1)。</param>
/// <param name="port">LSP 端口(注册表 lsp_port 或受保护默认 6005)。</param>
/// <param name="projectPath">项目根(本机路径;initialize 握手(handshake)的 rootUri 由此而来)。</param>
internal sealed partial class LspClient(string host, int port, string projectPath) : IAsyncDisposable
{
    /// <summary>发送门:JSON-RPC 帧的头部与体必须原子写出,以此串行化。</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>在途请求表:id → 完成源。</summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    /// <summary>无等待者时到达的诊断(diagnostics)缓存:规范化 URI → 数组(供后续等待者立即取走)。</summary>
    private readonly ConcurrentDictionary<string, JsonElement> _diagnosticsByUri = new(StringComparer.Ordinal);

    /// <summary>诊断等待表:规范化 URI → 完成源(publishDiagnostics 到达时摘除并完成)。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _diagnosticWaiters = new(StringComparer.Ordinal);

    /// <summary>已 didOpen 的文档 URI 集(didChange/didClose 分支判定;调用方串行使用,不加锁)。</summary>
    private readonly HashSet<string> _openDocuments = new(StringComparer.Ordinal);

    /// <summary>接收缓冲:原始字节按序累入,ProcessFrames 从头抽帧。</summary>
    private readonly List<byte> _rxBuffer = new();

    /// <summary>TCP 客户端;null = 未连接。</summary>
    private TcpClient? _tcp;

    /// <summary>网络流;null = 未连接。</summary>
    private NetworkStream? _stream;

    /// <summary>后台读循环任务(连接期间存在,断开后自然结束)。</summary>
    private Task? _readLoop;

    /// <summary>文档版本与请求 id 共用的自增计数器(didOpen 固定 version=1,此后本计数器递增供两者复用)。</summary>
    private int _nextVersion = 2;

    /// <summary>initialize 握手是否已完成(volatile;DisposeSocket 复位)。</summary>
    private volatile bool _initialized;

    /// <summary>项目根(本机路径形态)。</summary>
    public string ProjectPath { get; } = projectPath;

    /// <summary>LSP 端口。</summary>
    public int Port { get; } = port;

    /// <summary>LSP 主机。</summary>
    public string Host { get; } = host;

    /// <summary>是否可用:initialize 握手已完成且 TCP 仍处于连接态。</summary>
    public bool IsConnected => _initialized && _tcp?.Connected == true;

    /// <summary>
    /// 确保连接并完成 LSP initialize 握手;已连接时幂等返回。
    /// <para>逻辑链:先 DisposeSocket 清旧态 → TCP 连接(5s 上限)→ 起后台读循环 →
    /// 发 initialize(rootUri=项目根,30s 超时)→ 响应缺 capabilities → 抛错
    /// (调用方映射为 LSP_UNAVAILABLE)→ 发 initialized 通知 → 置 _initialized。</para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected)
        {
            return;
        }

        DisposeSocket();
        _tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _tcp.ConnectAsync(Host, Port, connectCts.Token);
        }
        _stream = _tcp.GetStream();
        _readLoop = Task.Run(() => ReadLoopAsync());

        var result = await SendRequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["capabilities"] = new JsonObject(),
            ["rootUri"] = AbsoluteToFileUri(ProjectPath),
        }, TimeSpan.FromSeconds(30), ct);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("capabilities", out _))
        {
            throw new InvalidOperationException("LSP initialize failed: no capabilities returned");
        }
        await SendNotificationAsync("initialized", new JsonObject());
        _initialized = true;
    }

    /// <summary>在 LSP 中打开/更新一个文档;打开前清掉该 URI 的存量诊断(Node 同语义)。</summary>
    /// <param name="uri">文档 URI。</param>
    /// <param name="content">全文内容(全量同步)。</param>
    public async Task OpenDocumentAsync(string uri, string content)
    {
        _diagnosticsByUri.TryRemove(NormalizeUri(uri), out _);
        if (_openDocuments.Contains(uri))
        {
            await SendNotificationAsync("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = _nextVersion++ },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = content }),
            });
            return;
        }

        // 着色器以 "gdshader" 打开:让引擎跳过 GDScript 解析(避免虚假诊断),4.2–4.7 一致。
        var languageId = uri.EndsWith(".gdshader", StringComparison.Ordinal)
            || uri.EndsWith(".gdshaderinc", StringComparison.Ordinal)
            ? "gdshader"
            : "gdscript";
        await SendNotificationAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = languageId,
                ["version"] = 1,
                ["text"] = content,
            },
        });
        _openDocuments.Add(uri);
    }

    /// <summary>关闭文档(项目扫描分块使用;仅在诊断收集完成后调用)。</summary>
    /// <param name="uri">文档 URI。</param>
    public async Task CloseDocumentAsync(string uri)
    {
        if (_openDocuments.Contains(uri))
        {
            await SendNotificationAsync("textDocument/didClose", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
            });
        }
        _openDocuments.Remove(uri);
        _diagnosticsByUri.TryRemove(NormalizeUri(uri), out _);
    }

    /// <summary>
    /// 等待某 URI 的诊断通知(默认 5s)。三态:收到列表(含空数组=干净)返回该列表;
    /// 超时返回 null(状态未知,调用方绝不能与"干净"混为一谈)。
    /// </summary>
    /// <param name="uri">文档 URI(经 NormalizeUri 对齐后匹配)。</param>
    /// <param name="timeoutMs">超时毫秒数,默认 5000。</param>
    /// <returns>diagnostics 数组(空数组 = 干净);超时为 null。</returns>
    public async Task<JsonElement?> WaitForDiagnosticsAsync(string uri, int timeoutMs = 5000)
    {
        var normalized = NormalizeUri(uri);
        if (_diagnosticsByUri.TryRemove(normalized, out var existing))
        {
            return existing;
        }

        var waiter = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticWaiters[normalized] = waiter;
        using var timer = new CancellationTokenSource(timeoutMs);
        using var registration = timer.Token.Register(() =>
        {
            _diagnosticWaiters.TryRemove(normalized, out _);
            // 最终检查:等待与超时之间到达的迟到发布。
            _diagnosticsByUri.TryRemove(normalized, out var late);
            waiter.TrySetResult(late.ValueKind == JsonValueKind.Array ? late : null);
        });
        return await waiter.Task;
    }

    /// <summary>
    /// 发送一次 JSON-RPC 请求并等待其 id 关联的响应。
    /// <para>逻辑链:自增计数器取 id → 登记完成源 → 成帧发出 → 超时/取消到点时若仍在表,
    /// 以 TimeoutException 失败 → 响应到达由读循环完成(见 HandleMessage);finally 兜底摘除登记。</para>
    /// </summary>
    /// <param name="method">LSP 方法名。</param>
    /// <param name="parameters">params 节点;null 时发送空对象。</param>
    /// <param name="timeout">响应超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>响应的 result;error 响应以异常抛出(见 HandleMessage)。</returns>
    public async Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextVersion);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendFrameAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            });
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            using var registration = timer.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out _))
                {
                    completion.TrySetException(new TimeoutException($"LSP request '{method}' timed out after {timeout.TotalSeconds}s"));
                }
            });
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>发送无 id 的 JSON-RPC 通知(不等响应)。</summary>
    /// <param name="method">LSP 方法名。</param>
    /// <param name="parameters">params 节点;null 时发送空对象。</param>
    public Task SendNotificationAsync(string method, JsonNode? parameters)
    {
        return SendFrameAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        });
    }

    /// <summary>按 LSP 成帧写出:ASCII 头 "Content-Length: n" + 空行 + UTF-8 体,发送门串行化;
    /// 未连接(_stream 为 null)时抛 InvalidOperationException。</summary>
    /// <param name="message">待序列化的 JSON 消息。</param>
    private async Task SendFrameAsync(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _sendGate.WaitAsync();
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("LSP socket not connected");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// 后台读循环:16KB 分块读流,字节累入接收缓冲,每轮交给 ProcessFrames 抽帧。
    /// <para>逻辑链:读到 0 字节(对端关闭)或异常 → 退出循环 → finally 统一收尾:
    /// 全部在途请求以 "LSP connection closed" 失败、全部诊断等待者以 null(未知)完成并清表。</para>
    /// </summary>
    private async Task ReadLoopAsync()
    {
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await (_stream ?? throw new InvalidOperationException()).ReadAsync(chunk);
                if (read == 0)
                {
                    break;
                }
                for (var i = 0; i < read; i++)
                {
                    _rxBuffer.Add(chunk[i]);
                }
                ProcessFrames();
            }
        }
        catch (Exception)
        {
            // 连接断开/解析故障:收尾由 finally 统一处理。
        }
        finally
        {
            foreach (var (id, completion) in _pending)
            {
                _pending.TryRemove(id, out _);
                completion.TrySetException(new InvalidOperationException("LSP connection closed"));
            }
            foreach (var waiter in _diagnosticWaiters.Values)
            {
                waiter.TrySetResult(null);
            }
            _diagnosticWaiters.Clear();
        }
    }

    /// <summary>
    /// 从接收缓冲反复抽帧直至半帧待续。
    /// <para>逻辑链:扫描头终止符(\\r\\n\\r\\n),未找到 → 返回等更多字节 → 头部解析不出
    /// Content-Length → 清空缓冲防死循环 → 体未收齐 → 返回等续读 → 抽出整帧交 HandleMessage
    /// 并从缓冲移除 → 循环。</para>
    /// </summary>
    private void ProcessFrames()
    {
        while (true)
        {
            var headerEnd = FindHeaderEnd(_rxBuffer);
            if (headerEnd < 0)
            {
                return;
            }
            var headerText = Encoding.ASCII.GetString(_rxBuffer.Take(headerEnd).ToArray());
            var match = ContentLengthPattern().Match(headerText);
            if (!match.Success)
            {
                // 无法识别的头部:丢弃已缓冲内容,避免死循环。
                _rxBuffer.Clear();
                return;
            }
            var length = int.Parse(match.Groups[1].Value);
            var frameStart = headerEnd + 4;
            if (_rxBuffer.Count < frameStart + length)
            {
                return;
            }
            var body = Encoding.UTF8.GetString(_rxBuffer.GetRange(frameStart, length).ToArray());
            _rxBuffer.RemoveRange(0, frameStart + length);
            HandleMessage(body);
        }
    }

    /// <summary>
    /// 分发一帧 JSON 消息。
    /// <para>逻辑链:解析失败/非对象 → 丢弃 → 带 id 的响应:摘登记,error → 异常完成、
    /// result → 结果完成、两者皆缺 → 默认值完成 → publishDiagnostics 通知:有等待者则直接完成,
    /// 否则存入缓存供后续等待者立即取;其余通知(window/showMessage 等)按 Node 语义忽略。</para>
    /// </summary>
    /// <param name="body">帧体(JSON 文本)。</param>
    private void HandleMessage(string body)
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                && _pending.TryRemove(idEl.GetInt32(), out var completion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : "LSP error";
                    completion.TrySetException(new InvalidOperationException($"LSP error: {message}"));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetResult(default);
                }
                return;
            }

            if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
                && methodEl.GetString() == "textDocument/publishDiagnostics"
                && root.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
            {
                var normalized = NormalizeUri(uriEl.GetString()!);
                var diagnostics = parameters.TryGetProperty("diagnostics", out var diagEl) && diagEl.ValueKind == JsonValueKind.Array
                    ? diagEl.Clone()
                    : JsonDocument.Parse("[]").RootElement.Clone();
                if (_diagnosticWaiters.TryRemove(normalized, out var waiter))
                {
                    waiter.TrySetResult(diagnostics);
                }
                else
                {
                    _diagnosticsByUri[normalized] = diagnostics;
                }
                if (Environment.GetEnvironmentVariable("GODOT_MCP_LSP_DEBUG") == "1")
                {
                    Console.Error.WriteLine($"[lsp-debug] publish uri={normalized} waiters=[{string.Join("|", _diagnosticWaiters.Keys)}] stored=[{string.Join("|", _diagnosticsByUri.Keys)}]");
                }
            }
            // 其余(window/showMessage 等)按 Node 语义忽略。
        }
    }

    /// <summary>在缓冲中扫描头终止符 \\r\\n\\r\\n(LSP 头部与 JSON 体的分界)。</summary>
    /// <param name="buffer">接收缓冲。</param>
    /// <returns>终止符起始下标;未找到为 -1。</returns>
    private static int FindHeaderEnd(List<byte> buffer)
    {
        for (var i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>释放套接字并复位会话态:握手标记清零、缓冲/已开文档/诊断缓存清空
    /// (等待表不在其中,由读循环的 finally 统一完成)。</summary>
    private void DisposeSocket()
    {
        _initialized = false;
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
        _rxBuffer.Clear();
        _openDocuments.Clear();
        _diagnosticsByUri.Clear();
    }

    /// <summary>完整处置:释放套接字 → 至多等读循环 2s 退出 → 释放发送门。</summary>
    /// <returns>异步处置任务。</returns>
    public async ValueTask DisposeAsync()
    {
        DisposeSocket();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }
        }
        _sendGate.Dispose();
    }

    /// <summary>LSP 头部的 Content-Length 匹配模式(大小写不敏感)。</summary>
    [GeneratedRegex("Content-Length:\\s*(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthPattern();

    // ── URI 工具(LspUri 语义内联:客户端与工具层共享) ──────────

    /// <summary>绝对路径 → file:// URI;Windows 盘符形态(X:/)加三斜杠,其余 POSIX 形态加两斜杠。</summary>
    /// <param name="absolutePath">绝对路径。</param>
    /// <returns>file URI 字符串。</returns>
    public static string AbsoluteToFileUri(string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/');
        return normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'
            ? "file:///" + normalized
            : "file://" + normalized;
    }

    /// <summary>URI 规范化(与 project_key.gd canonical 同规则):反转义、反斜杠转正斜杠、
    /// win/mac 整体小写 —— daemon 拼装的 URI 与 Godot 回显的 URI 经同一规范化后可直接相等比较。</summary>
    /// <param name="uri">原始 URI 或路径。</param>
    /// <returns>规范化后的字符串。</returns>
    public static string NormalizeUri(string uri)
    {
        var normalized = Uri.UnescapeDataString(uri).Replace('\\', '/');
        // 大小写不敏感的文件系统(Windows/macOS)上整体小写:daemon 以规范化
        // 项目键(全小写)拼 URI,而 Godot 按自身解析回显真实大小写 —— 两侧
        // 经同一规范化后必须相等(与 project_key.gd canonical 同规则)。
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            normalized = normalized.ToLowerInvariant();
        }
        return normalized;
    }
}
