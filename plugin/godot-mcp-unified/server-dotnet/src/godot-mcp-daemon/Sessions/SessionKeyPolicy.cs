namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 会话键的过期与容量策略(ADR-0006)。默认值都是<b>会话常量</b>,生产不设环境变量即等于
/// <see cref="Default"/>;daemon 侧另有 <c>GODOT_MCP_DEBUG_UNKEYED</c> 与
/// <c>GODOT_MCP_TOOLFACE</c> 两个开关,那是工具面的事,不要混为一谈。
/// </summary>
/// <remarks>
/// <para><b>为什么需要两个 TTL</b>:<c>NoStreamTtl</c> 是给"从没真正连接过"的孤儿键用的
/// (直连 SDK 用任意合法 UUID 建键、或只开了流没 connect)。而<b>已连接</b>的键不能按 10 分钟回收:
/// 传输层不保证有 listen 流(legacy 宿主用 initialize 握手、根本不开流),用户去读代码十分钟
/// 回来工具就全消失 —— 用户看到的是"连着的会话自己断了",与"没有流"这个实现细节无关。</para>
/// </remarks>
/// <param name="HandoffGrace">
/// 最后一条流关闭后保留键的宽限期。作用有二:① 盖住 shim 抖动/交接时的重连窗口;
/// ② 防止 <c>discover_tools</c> 激活的组在断流瞬间丢失。
/// </param>
/// <param name="NoStreamTtl">
/// <b>从未连接成功</b>的键的存活上限。用于回收直连 SDK 留下的孤儿键,
/// 并提供"任意合法 UUID 建键"的内存上限。
/// </param>
/// <param name="ConnectedTtl">
/// <b>已连接</b>但当前无流的键的存活上限。取一个远大于人类中断时长的值(默认 24 小时):
/// 这类键代表"用户真的在用",不该因为"传输层没有流"而被判成空闲。
/// 真正的清理交给容量淘汰与 daemon 重启。
/// </param>
/// <param name="MaxSessionKeys">
/// 键数上限。达到上限时只淘汰<b>未连接</b>的 <c>IdleNoStream</c> 与 grace 已过的 <c>Dormant</c>
/// (且都要求在途为 0);没有可淘汰项就拒绝新建,绝不挤掉已连接的会话。
/// </param>
public sealed record SessionKeyPolicy(
    TimeSpan HandoffGrace,
    TimeSpan NoStreamTtl,
    TimeSpan ConnectedTtl,
    int MaxSessionKeys)
{
    /// <summary>
    /// 生产默认值:断流宽限 30 秒、孤儿键存活 10 分钟、已连接键存活 24 小时、上限 1024 个键。
    /// </summary>
    public static SessionKeyPolicy Default { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromHours(24), 1024);

    /// <summary>
    /// 从环境变量构造策略(供测试压缩 TTL;生产不设这些变量 → 等于 <see cref="Default"/>)。
    /// </summary>
    /// <remarks>
    /// <para>为什么允许 env 覆盖:<c>NoStreamTtl</c>/<c>ConnectedTtl</c> 的默认值是 10 分钟与 24 小时,
    /// 而"回收是否发生"必须能被测试观察到 —— 否则守护测试会在修复被回退时照样变绿(假绿)。</para>
    /// <para>三个变量都按<b>秒</b>解析;非法值直接忽略(回落默认),不要因为一个拼错的
    /// 环境变量让 daemon 起不来。</para>
    /// </remarks>
    /// <returns>实际生效的策略。</returns>
    public static SessionKeyPolicy FromEnvironment()
    {
        var noStream = ReadSeconds("GODOT_MCP_SESSION_NO_STREAM_TTL_SECONDS") ?? Default.NoStreamTtl;
        var connected = ReadSeconds("GODOT_MCP_SESSION_CONNECTED_TTL_SECONDS") ?? Default.ConnectedTtl;
        var grace = ReadSeconds("GODOT_MCP_SESSION_GRACE_SECONDS") ?? Default.HandoffGrace;
        return new SessionKeyPolicy(grace, noStream, connected, Default.MaxSessionKeys);
    }

    /// <summary>读一个"秒"环境变量;未设置或非法返回 null。</summary>
    /// <param name="name">变量名。</param>
    /// <returns>非负时长;无法解析为 null。</returns>
    private static TimeSpan? ReadSeconds(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
            || seconds < 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
