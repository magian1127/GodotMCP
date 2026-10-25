using GodotMcp.Shim;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// shim 侧纯判据的单元测试（第五轮审核 G2 / G3 的守护）。
/// </summary>
/// <remarks>
/// <para><c>DiscoverChangesState</c> 决定"要不要把这次 discover_tools 缓存成重放目标"。
/// 判错两个方向都有后果：</para>
/// <list type="bullet">
/// <item><b>会改状态却判成只读</b> → daemon 重启后重放更早的帧，用户刚停用的组被**复活**。</item>
/// <item><b>只读却判成会改</b> → 模型浏览一次目录就把有效帧覆盖掉，重启后什么都不恢复。</item>
/// </list>
/// <para>这两种都属于"静默"：测试若只走端到端，只会覆盖其中一两条组合。</para>
/// </remarks>
public sealed class ShimFrameCacheTests
{
    [Theory]
    // 会改变激活集合 —— 必须缓存。
    [InlineData("""{"params":{"arguments":{"request":"playtest"}}}""", true)]
    [InlineData("""{"params":{"arguments":{"request":["a","b"]}}}""", true)]
    [InlineData("""{"params":{"arguments":{"reset":true}}}""", true)]
    [InlineData("""{"params":{"arguments":{"reset":["a"]}}}""", true)]
    [InlineData("""{"params":{"arguments":{"refresh_extensions":true}}}""", true)]
    // **审核 G2 的核心**：reset 不受 activate 影响，daemon 侧 reset 分支先于 activate 判定。
    // 判成只读的后果是"用户刚停用的组在重启后被复活"。
    [InlineData("""{"params":{"arguments":{"reset":true,"activate":false}}}""", true)]
    [InlineData("""{"params":{"arguments":{"reset":["x"],"activate":false}}}""", true)]
    [InlineData("""{"params":{"arguments":{"refresh_extensions":true,"activate":false}}}""", true)]
    // 只读 —— 不该覆盖缓存。
    [InlineData("""{"params":{"arguments":{}}}""", false)]
    [InlineData("""{"params":{}}""", false)]
    [InlineData("""{"params":{"arguments":{"include_schemas":true}}}""", false)]
    [InlineData("""{"params":{"arguments":{"activate":false}}}""", false)]
    [InlineData("""{"params":{"arguments":{"activate":false,"request":"x"}}}""", false)]
    [InlineData("""{"params":{"arguments":{"request":""}}}""", false)]
    [InlineData("""{"params":{"arguments":{"request":[]}}}""", false)]
    // 畸形输入一律按"不改状态"处理（宁可少缓存，也不要因重放畸形帧出错）。
    [InlineData("""{"params":[]}""", false)]
    [InlineData("""{"params":null}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("""not json""", false)]
    public void discover_changes_state_matches_daemon_semantics(string frame, bool expected)
        => Assert.Equal(expected, ToolFaceProbe.DiscoverChangesState(frame));

    [Theory]
    [InlineData("""{"params":{"name":"godot_connect"}}""", "godot_connect")]
    [InlineData("""{"params":{"name":"discover_tools"}}""", "discover_tools")]
    // 以下都必须返回 null 而不是抛异常 —— 抛出去就会打挂整个 shim 进程（审核 F5）。
    [InlineData("""{"params":[]}""", null)]
    [InlineData("""{"params":null}""", null)]
    [InlineData("""{"params":{"name":42}}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""not json""", null)]
    public void read_tool_name_never_throws(string frame, string? expected)
        => Assert.Equal(expected, ToolFaceProbe.ReadToolName(frame));

    [Fact]
    public void with_refresh_extensions_injects_without_touching_other_fields()
    {
        var result = ToolFaceProbe.WithRefreshExtensions(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"discover_tools","arguments":{"request":"playtest"}}}""");

        // 原有参数必须保留，并补上 refresh_extensions。
        Assert.Contains("\"request\":\"playtest\"", result, StringComparison.Ordinal);
        Assert.Contains("\"refresh_extensions\":true", result, StringComparison.Ordinal);
        Assert.Contains("\"id\":7", result, StringComparison.Ordinal);
        Assert.Contains("discover_tools", result, StringComparison.Ordinal);

        // 没有 arguments 时也要能补出来（否则重放会漏掉扩展刷新）。
        var withoutArguments = ToolFaceProbe.WithRefreshExtensions(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"discover_tools"}}""");
        Assert.Contains("\"refresh_extensions\":true", withoutArguments, StringComparison.Ordinal);

        // 解析失败时原样返回 —— 宁可少刷新一次，也不要发出畸形请求。
        Assert.Equal("not json", ToolFaceProbe.WithRefreshExtensions("not json"));
    }
}

/// <summary>
/// 把 shim 的判据暴露给测试的薄包装。
/// </summary>
/// <remarks>
/// 直接调 <c>HttpForwarder</c> 的静态方法需要引用 shim 程序集；这些判据是纯函数，
/// 包一层可以避免测试里出现 <c>HttpForwarder</c> 这个"网络类"的名字。
/// </remarks>
internal static class ToolFaceProbe
{
    /// <summary>转发到 shim 的判据（见 <c>HttpForwarder.DiscoverChangesState</c>）。</summary>
    /// <param name="frame">discover_tools 请求帧。</param>
    /// <returns>会改变激活集合时 true。</returns>
    public static bool DiscoverChangesState(string frame)
        => HttpForwarder.DiscoverChangesState(frame);

    /// <summary>转发到 shim 的判据（见 <c>HttpForwarder.ReadToolName</c>）。</summary>
    /// <param name="frame">JSON-RPC 请求帧。</param>
    /// <returns>工具名或 null。</returns>
    public static string? ReadToolName(string frame) => HttpForwarder.ReadToolName(frame);

    /// <summary>转发到 shim 的判据（见 <c>HttpForwarder.WithRefreshExtensions</c>）。</summary>
    /// <param name="frame">discover_tools 请求帧。</param>
    /// <returns>补好 refresh_extensions 的帧。</returns>
    public static string WithRefreshExtensions(string frame)
        => HttpForwarder.WithRefreshExtensions(frame);
}
