using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>测试用原始 WS 客户端:逐帧收发文本,记录关闭码/原因,供 fake Godot 回放断言使用。</summary>
internal sealed class WsTestClient : IDisposable
{
    /// <summary>底层 WS 连接(Dispose 时释放)。</summary>
    private readonly ClientWebSocket _ws = new();

    /// <summary>收到的完整文本帧队列(后台泵唯一写入,ReceiveFrameAsync 读取)。</summary>
    private readonly Channel<string> _frames = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>连接关闭(含暴力断开)时完成的信号源。</summary>
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>私有构造:由 ConnectAsync 建立连接后调用。</summary>
    private WsTestClient()
    {
    }

    /// <summary>收到的关闭码(正常关闭握手才有值;暴力断开为 null)。</summary>
    public int? CloseStatus { get; private set; }

    /// <summary>收到的关闭原因文本(断言 1008 "invalid token" 等场景用)。</summary>
    public string? CloseReason { get; private set; }

    /// <summary>连接关闭完成(接收泵退出)的任务。</summary>
    public Task Closed => _closedTcs.Task;

    /// <summary>连接 ws://127.0.0.1:&lt;port&gt;/ 并启动后台接收泵。</summary>
    /// <param name="port">假编辑器(fake)监听端口。</param>
    /// <returns>已连接的测试客户端。</returns>
    public static async Task<WsTestClient> ConnectAsync(int port)
    {
        var client = new WsTestClient();
        await client._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
        _ = client.PumpAsync();
        return client;
    }

    /// <summary>
    /// 后台接收泵:循环收帧拼装完整文本消息入队,直至对端关闭或连接异常。
    /// 逻辑链:收到 Close 帧 → 记录 CloseStatus/CloseReason 后退出 → 文本分片拼接至 EndOfMessage
    /// 才入队(单帧可能跨多个 WS 分片) → WebSocketException(对端暴力断开)按关闭处理 →
    /// 收尾时完成帧队列(后续 ReadAsync 抛 ChannelClosedException)并置 Closed。
    /// </summary>
    private async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = result.CloseStatus is { } status ? (int)status : null;
                    CloseReason = result.CloseStatusDescription;
                    break;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    _frames.Writer.TryWrite(sb.ToString());
                    sb.Clear();
                }
            }
        }
        catch (WebSocketException)
        {
            // 对端暴力断开(如 dispose)——按关闭处理。
        }
        _frames.Writer.TryComplete();
        _closedTcs.TrySetResult();
    }

    /// <summary>发送一条完整文本帧。</summary>
    /// <param name="text">帧文本(单文档 JSON,如鉴权帧)。</param>
    public async Task SendTextAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>读取下一帧文本;超时抛 TimeoutException(经取消令牌中断阻塞读)。</summary>
    /// <param name="timeout">等待帧的上限。</param>
    /// <returns>下一帧文本;队列已关闭(连接结束)时抛 OperationCanceledException/ChannelClosedException。</returns>
    public async Task<string> ReceiveFrameAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _frames.Reader.ReadAsync(cts.Token);
    }

    /// <summary>释放底层 WS 连接(接收泵经 WebSocketException 收尾并置 Closed)。</summary>
    public void Dispose() => _ws.Dispose();
}
