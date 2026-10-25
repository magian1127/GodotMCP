using ModelContextProtocol.Client;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 把一次测试会话接入 daemon 的 loopback HTTP 面 —— 官方 SDK 客户端直连。
/// </summary>
/// <remarks>
/// <para><b>为什么带会话键</b>:daemon 的工具面按会话键分级(ADR-0007)。不带
/// <c>x-godot-session</c> 的请求走"未连接"语义,只看到 <c>godot_connect</c> —— 那会让既有测试
/// 全部失去它们要测的工具。所以这里默认<b>每个连接自带一个随机 UUID</b>并<b>自动 connect</b>,
/// 让测试拿到"已连接"的完整工具面。</para>
/// <para><b>要测"未连接"的用例</b>请用 <see cref="KeyedSessionConnector"/> 并传
/// <c>autoConnect: false</c>;要测特定组请传 <c>groups</c>。</para>
/// <para><b>协议版本</b>:钉 2025-11-25(initialize 握手)。SDK 客户端"server/discover 探测 →
/// 慢探测超时回退 initialize"路径上有已知竞态(回退时传输层缓存的协商版本不被重置,导致
/// initialize 携带过期 2026-07-28 头而被 daemon 拒收);daemon 行为正确。
/// 2026-07-28 面(server/discover、per-request metadata)由裸 HTTP 测试直接覆盖。</para>
/// </remarks>
internal static class SessionConnector
{
    /// <summary>
    /// 建立一条直连 daemon 的 MCP 客户端会话(Streamable HTTP + Bearer token + 会话键),
    /// 并自动完成一次 <c>godot_connect</c>。
    /// </summary>
    /// <remarks>
    /// <para>默认<b>激活全部 17 组</b>:既有测试大多直接调用组工具(如 <c>editor_sync</c>、
    /// <c>lsp_diagnostics</c>、<c>node_set_property</c>),它们要求工具已展开。</para>
    /// <para>要测"未连接只看到 1 个工具"、"只激活某几个组"或"某组未激活时的行为"的用例,
    /// 请改用 <see cref="ConnectKeyedAsync"/> 并显式传 <c>groups</c> 与
    /// <c>activateAllGroups: false</c>。</para>
    /// </remarks>
    /// <param name="port">daemon HTTP 端口。</param>
    /// <param name="token">状态目录中的稳定 token(经 Authorization: Bearer 注入)。</param>
    /// <returns>已完成 initialize 握手与 connect 的 MCP 客户端。</returns>
    public static async Task<McpClient> ConnectAsync(int port, string token)
    {
        var connector = await ConnectKeyedAsync(port, token).ConfigureAwait(false);
        return connector.Client;
    }

    /// <summary>建立连接并返回带会话键的连接器(需要断言键、控制组、或做二次 connect 时用)。</summary>
    /// <param name="port">daemon HTTP 端口。</param>
    /// <param name="token">状态目录中的稳定 token。</param>
    /// <param name="sessionKey">会话键;null 表示新生成。</param>
    /// <param name="autoConnect">是否自动 connect(默认 true)。</param>
    /// <param name="groups">自动 connect 时激活的组;null 时按 <paramref name="activateAllGroups"/> 决定。</param>
    /// <param name="project">自动 connect 时指定的项目路径。</param>
    /// <param name="activateAllGroups">是否激活全部 17 组(默认 true,兼容既有测试)。</param>
    /// <returns>已就绪的连接器。</returns>
    public static Task<KeyedSessionConnector> ConnectKeyedAsync(
        int port,
        string token,
        string? sessionKey = null,
        bool autoConnect = true,
        IReadOnlyList<string>? groups = null,
        string? project = null,
        bool activateAllGroups = true)
        => KeyedSessionConnector.ConnectAsync(
            port, token, sessionKey, autoConnect, groups, project, activateAllGroups);
}
