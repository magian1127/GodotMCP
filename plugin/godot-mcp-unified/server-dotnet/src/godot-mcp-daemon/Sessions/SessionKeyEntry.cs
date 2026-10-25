namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 会话键的三态(ADR-0006)。不变式:<c>Streams.Count &gt; 0 ⇒ Active</c>;
/// <c>Dormant</c> 与 <c>IdleNoStream</c> 只在无流时存在。
/// </summary>
public enum SessionKeyState
{
    /// <summary>至少有一条 listen 流。永不淘汰、永不过期。</summary>
    Active,

    /// <summary>
    /// 最后一条流刚关闭,处于 <see cref="SessionKeyPolicy.HandoffGrace"/> 宽限期。
    /// 期间新流到达即回 <see cref="Active"/>;收到非 listen 请求则转 <see cref="IdleNoStream"/>。
    /// </summary>
    Dormant,

    /// <summary>
    /// 无流,只靠请求续命。由 <c>godot_connect</c> 建出(从未有流),或由 <c>Dormant</c> 收到请求转入。
    /// 达到 <see cref="SessionKeyPolicy.NoStreamTtl"/> 且无在途即回收。
    /// </summary>
    IdleNoStream,
}

/// <summary>
/// 单个会话键的条目。<b>所有字段的读写都在 <see cref="SessionKeyRegistry"/> 的锁内完成</b>,
/// 因此这里是可变类而非 record(struct 会在并发下被静默复制)。
/// </summary>
public sealed class SessionKeyEntry
{
    /// <summary>会话键本体(UUID 文本)。日志与错误只打印前 8 位。</summary>
    public required string Key { get; init; }

    /// <summary>三态之一;迁移规则见 <see cref="SessionKeyRegistry"/>。</summary>
    public SessionKeyState State { get; set; } = SessionKeyState.IdleNoStream;

    /// <summary>本键当前持有的 listen 流标识集合(一键可多流)。</summary>
    public HashSet<long> Streams { get; } = [];

    /// <summary>在途请求数。与 <see cref="Streams"/> 一起决定"能否淘汰/过期"。</summary>
    public int InFlight { get; set; }

    /// <summary>本会话已激活的组名(并集语义由调用方保障,这里只存集合)。</summary>
    public HashSet<string> Groups { get; } = [];

    /// <summary>本会话的默认目标(规范化项目路径);null 表示未设置。</summary>
    public string? DefaultTarget { get; set; }

    /// <summary>
    /// 是否已真正连接(<b>调用过 <c>godot_connect</c></b>)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不能拿"键存在"当"已连接"</b>:listen 长流也会建键(spec §1.6),
    /// 直连客户端用任意合法 UUID 也能建键。若按"键存在"判定,这两种情况都会直接看到 core 9,
    /// 而"未连接只看到 <c>godot_connect</c>"这一条核心需求立刻失效。</para>
    /// <para>置位点只有 <c>godot_connect</c>;重置为 false 的路径不存在(连接是不可逆的会话事实)。</para>
    /// </remarks>
    public bool Connected { get; set; }

    /// <summary>
    /// shim 上报的会话工作目录(来自 <c>x-godot-cwd</c>)。
    /// </summary>
    /// <remarks>
    /// 存在注册表里而不是只放请求作用域:<c>godot_connect</c> 空参时要靠它找
    /// <c>project.godot</c>,而工具 handler 里读不到中间件写入的 HttpContext 数据。
    /// </remarks>
    public string? WorkingDirectory { get; set; }

    /// <summary>最后一次活动的时间戳(<see cref="TimeProvider.GetTimestamp"/> 单调刻度)。</summary>
    public long LastSeenTimestamp { get; set; }

    /// <summary>
    /// shim 上报的版本(来自 <c>x-godot-shim-version</c>);未知为 null。
    /// </summary>
    /// <remarks>
    /// 存下来是为了 <c>state/sessions.json</c> 与 <c>--status</c> 能直接回答
    /// "这个会话是哪个版本的 shim 在跑" —— 旧 shim 混跑会导致工具面不展开,
    /// 而那种现场光看键状态是看不出来的。
    /// </remarks>
    public string? ShimVersion { get; set; }

    /// <summary>进入 <see cref="SessionKeyState.Dormant"/> 的时间戳;非 Dormant 时为 0。</summary>
    public long DormantSinceTimestamp { get; set; }

    /// <summary>日志/UI 用的短标识:键的前 8 位。运维靠它与 <c>--status</c> 对号。</summary>
    public string ShortKey => Key.Length <= 8 ? Key : Key[..8];
}
