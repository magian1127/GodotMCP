using ModelContextProtocol.Client;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 把一次测试会话接入 daemon 的 loopback HTTP 面 —— 官方 SDK 客户端直连(spec 预约定的唯一新测试 seam)。
/// 显式钉协议版本 2025-11-25(initialize 握手):规避 SDK 客户端「server/discover 探测 →
/// 慢探测超时回退 initialize」路径上的已知竞态——其传输层缓存的协商版本在回退时不被重置,
/// 导致回退的 initialize 携带过期 2026-07-28 头而被 daemon 依 SEP-2575 拒收(客户端 bug;
/// daemon 行为正确)。2026-07-28 面(server/discover、per-request metadata)由裸 HTTP 测试
/// (DiscoveryFaceTests / ListenStream)直接覆盖。
/// </summary>
internal static class SessionConnector
{
    /// <summary>建立一条直连 daemon 的 MCP 客户端会话(Streamable HTTP + Bearer token;协议版本钉死的理由见类注释)。</summary>
    /// <param name="port">daemon HTTP 端口。</param>
    /// <param name="token">状态目录中的稳定 token(经 Authorization: Bearer 注入)。</param>
    /// <returns>已完成 initialize 握手的 MCP 客户端。</returns>
    public static async Task<McpClient> ConnectAsync(int port, string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token}",
            },
        });

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
        });
    }
}
