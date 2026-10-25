using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Daemon.Sessions;

/// <summary>
/// 把会话键表周期性落到 <c>&lt;stateDir&gt;/sessions.json</c>（spec §1.4 / §10）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要这个文件</b>：用户报"工具突然只剩 <c>godot_connect</c>"时，
/// 日志只能证明"某个键被回收了"，回答不了"现在还有几个会话、各自什么状态、
/// 连的哪个项目、跑的是哪个版本的 shim"。这些恰好是排查时必须的第一屏信息，
/// 而它们都能从 <see cref="SessionKeyRegistry.Snapshot"/> 直接得出。</para>
///
/// <para><b>为什么是文件而不是日志</b>：日志是流式的，事后要 grep；而
/// <c>sessions.json</c> 是"当前状态"，<c>--status</c> 与用户都能直接读，
/// 也不受日志轮转影响。</para>
///
/// <para><b>写盘策略</b>：先写临时文件再原子替换。<see cref="File.Move(string, string, bool)"/>
/// 在 Windows 上是替换语义（等价 <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>），
/// 所以读者永远看不到半个 JSON —— 直接覆写源文件会让并发读的一方拿到截断内容。</para>
///
/// <para><b>失败不影响服务</b>：磁盘满、目录只读、杀软锁文件都可能让写盘失败。
/// 这类失败绝不能让 daemon 退出（那是运维设施，不是业务），只记一次 Warning 后继续。</para>
/// </remarks>
/// <param name="registry">会话键注册表。</param>
/// <param name="timeProvider">时钟（测试注入）。</param>
/// <param name="state">状态目录与文件策略。</param>
/// <param name="logger">日志。</param>
internal sealed class SessionSnapshotWriter(
    SessionKeyRegistry registry,
    TimeProvider timeProvider,
    DaemonOptions options,
    ILogger<SessionSnapshotWriter> logger) : BackgroundService
{
    /// <summary>落盘周期。5 秒足够让 <c>--status</c> 反映"刚刚发生的变化"，又不至于成为 I/O 噪声。</summary>
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(5);

    /// <summary>快照文件路径：与单例锁、token 同目录，运维一眼能找到。</summary>
    private string SessionsFile => Path.Combine(options.StateDir, "sessions.json");

    /// <summary>连续写盘失败时，两次告警之间的最小间隔（避免磁盘满时刷屏）。</summary>
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(5);

    /// <summary>上次告警的时间戳（节流用）。</summary>
    private long _lastWarnTimestamp = long.MinValue;

    /// <summary>立即写一次（测试与启动期用）。</summary>
    /// <returns>写入成功时 true。</returns>
    public bool WriteOnce()
    {
        try
        {
            var payload = BuildPayload();
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var target = SessionsFile;
            var temp = target + ".tmp";

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(temp, json);

            // 原子替换：读者要么看到旧的完整文件，要么看到新的完整文件。
            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 节流告警：磁盘满时每 5 秒一条会把真正有用的日志冲掉。
            var now = timeProvider.GetTimestamp();
            if (_lastWarnTimestamp == long.MinValue
                || timeProvider.GetElapsedTime(_lastWarnTimestamp, now) >= WarnThrottle)
            {
                _lastWarnTimestamp = now;
                logger.LogWarning(ex, "写入会话快照失败（{Path}）；不影响工具服务，稍后重试。", SessionsFile);
            }

            return false;
        }
    }

    /// <summary>把当前键表投影成可序列化的形状。</summary>
    /// <returns>快照文档。</returns>
    /// <remarks>
    /// <b>只写前 8 位短键，不写完整 UUID</b>：完整 UUID 是会话凭据的一部分
    /// （带上它就能冒充该会话），而排查只需要能对上 <c>godot_connect</c> 返回的短键。
    /// </remarks>
    private object BuildPayload()
    {
        var sessions = registry.Snapshot();
        return new
        {
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            daemonVersion = typeof(SessionSnapshotWriter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            policy = new
            {
                graceSeconds = registry.Policy.HandoffGrace.TotalSeconds,
                noStreamTtlSeconds = registry.Policy.NoStreamTtl.TotalSeconds,
                connectedTtlSeconds = registry.Policy.ConnectedTtl.TotalSeconds,
                maxSessionKeys = registry.Policy.MaxSessionKeys,
            },
            count = sessions.Count,
            sessions = sessions.Select(s => new
            {
                key = s.ShortKey,
                state = s.State.ToString(),
                connected = s.Connected,
                groups = s.Groups,
                defaultTarget = s.DefaultTarget,
                streams = s.StreamCount,
                inFlight = s.InFlight,
                workingDirectory = s.WorkingDirectory,
                shimVersion = s.ShimVersion,
            }).ToArray(),
        };
    }

    /// <summary>序列化选项：缩进便于人工阅读，不转义非 ASCII（中文路径可读）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动即写一次：daemon 刚起来时 <stateDir>/sessions.json 应该是"0 个会话"，
        // 而不是上一个 daemon 留下的旧内容 —— 后者会让 --status 报出根本不存在的会话。
        WriteOnce();

        using var timer = new PeriodicTimer(WriteInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                WriteOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关机。
        }
    }
}
