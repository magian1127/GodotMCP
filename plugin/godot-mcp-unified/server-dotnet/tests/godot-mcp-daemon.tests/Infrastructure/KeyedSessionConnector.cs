using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 把一次测试会话接入 daemon 的 loopback HTTP 面 —— 官方 SDK 客户端直连。
/// </summary>
/// <remarks>
/// <para><b>为什么要带会话键</b>:daemon 的工具面按会话键分级(ADR-0007)。不带
/// <c>x-godot-session</c> 的请求会走"未连接"语义,只看到 <c>godot_connect</c> —— 那会让几乎所有
/// 既有测试都失去它们要测的工具。所以默认<b>每个连接器实例自带一个随机 UUID</b>,
/// 并<b>自动完成一次 godot_connect</b>,让工具面进入"已连接"状态。</para>
/// <para><b>autoConnect 与 groups</b>:需要验证"未连接只看到 1 个工具"的测试传
/// <c>autoConnect: false</c>;需要特定组工具的测试传 <c>groups</c>。二者都是显式参数,
/// 不靠约定猜。</para>
/// <para><b>协议版本</b>:钉 2025-11-25(initialize 握手)。理由见旧版注释:SDK 客户端
/// "server/discover 探测 → 慢探测超时回退 initialize"路径上有已知竞态(回退时传输层缓存的
/// 协商版本不被重置,导致 initialize 携带过期 2026-07-28 头而被 daemon 拒收)。
/// 2026-07-28 面由裸 HTTP 测试直接覆盖。</para>
/// </remarks>
internal sealed class KeyedSessionConnector : IAsyncDisposable
{
    private readonly McpClient _client;

    private KeyedSessionConnector(McpClient client, string sessionKey)
    {
        _client = client;
        SessionKey = sessionKey;
    }

    /// <summary>本次连接使用的会话键(可断言两个连接器互不可见)。</summary>
    public string SessionKey { get; }

    /// <summary>底层 MCP 客户端。</summary>
    public McpClient Client => _client;

    /// <summary>
    /// 建立一个带会话键的连接。
    /// </summary>
    /// <param name="port">daemon HTTP 端口。</param>
    /// <param name="token">状态目录中的稳定 token。</param>
    /// <param name="sessionKey">会话键;null 表示新生成一个随机 UUID。</param>
    /// <param name="autoConnect">是否在建立连接后自动调一次 <c>godot_connect</c>(默认 true)。</param>
    /// <param name="groups">自动 connect 时一并激活的组;null 表示不激活任何组(只得到 core 9)。</param>
    /// <param name="project">自动 connect 时指定的项目路径;null 表示不指定(只建键)。</param>
    /// <param name="activateAllGroups">
    /// 自动 connect 时激活全部 17 组(等价于旧行为里"全激活后的工具面")。
    /// 需要调用组工具的既有测试用它;<paramref name="groups"/> 显式给出时以它为准。
    /// </param>
    /// <returns>已就绪的连接器。</returns>
    public static async Task<KeyedSessionConnector> ConnectAsync(
        int port,
        string token,
        string? sessionKey = null,
        bool autoConnect = true,
        IReadOnlyList<string>? groups = null,
        string? project = null,
        bool activateAllGroups = false)
    {
        var key = sessionKey ?? Guid.NewGuid().ToString();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["x-godot-session"] = key,
        };

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        });

        var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
        });

        var connector = new KeyedSessionConnector(client, key);
        if (autoConnect)
        {
            var effectiveGroups = groups ?? (activateAllGroups
                ? Groups.GroupCatalogue.GroupNames.ToList()
                : null);
            await connector.ConnectGodotAsync(effectiveGroups, project).ConfigureAwait(false);
        }

        return connector;
    }

    /// <summary>调用 <c>godot_connect</c>,把本键置为已连接并可选地激活初始组。</summary>
    /// <param name="groups">要激活的组;null 表示不激活。</param>
    /// <param name="project">项目路径;null 表示不指定。</param>
    /// <returns>工具调用结果(调用方自行断言)。</returns>
    public async Task<CallToolResult> ConnectGodotAsync(IReadOnlyList<string>? groups = null, string? project = null)
    {
        var args = new Dictionary<string, object?>();
        if (project is not null)
        {
            args["project"] = project;
        }
        if (groups is { Count: > 0 })
        {
            args["groups"] = groups;
        }

        return await _client.CallToolAsync("godot_connect", args).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
