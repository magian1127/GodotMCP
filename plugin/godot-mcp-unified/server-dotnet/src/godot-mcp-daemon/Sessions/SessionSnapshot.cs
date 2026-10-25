namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 会话键的不可变视图。工具面填充、日志、<c>state/sessions.json</c> 与 <c>--status</c> 都读它,
/// 避免在注册表锁外直接摸可变条目。
/// </summary>
/// <param name="Key">会话键本体(完整 UUID)。</param>
/// <param name="ShortKey">前 8 位,用于日志与错误文案。</param>
/// <param name="State">三态之一。</param>
/// <param name="Groups">本会话已激活的组(无序集合的数组视图)。</param>
/// <param name="DefaultTarget">默认目标(规范化项目路径),未设置为 null。</param>
/// <param name="StreamCount">当前持有的 listen 流数。</param>
/// <param name="InFlight">在途请求数。</param>
/// <param name="Connected">是否已调用过 <c>godot_connect</c>(决定工具面是 1 个还是 core 9)。</param>
/// <param name="WorkingDirectory">shim 上报的会话 cwd,供空参 <c>godot_connect</c> 发现候选项目。</param>
/// <param name="ShimVersion">shim 上报的版本;未知为 null(旧 shim 不带该头)。</param>
public readonly record struct SessionSnapshot(
    string Key,
    string ShortKey,
    SessionKeyState State,
    string[] Groups,
    string? DefaultTarget,
    int StreamCount,
    int InFlight,
    bool Connected,
    string? WorkingDirectory,
    string? ShimVersion);
