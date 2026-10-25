namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 可手工推进的时钟,用于会话键状态机与 reaper 的测试。
/// </summary>
/// <remarks>
/// <para>项目没有引入 <c>Microsoft.Extensions.TimeProvider.Testing</c> —— 只需"推进时间"这一件事,
/// 自己实现比多一个包依赖更省。生产用的是 <see cref="TimeProvider.System"/>。</para>
/// <para>只覆盖 <see cref="SessionKeyRegistry"/> / <see cref="SessionKeyReaper"/> 实际用到的成员:
/// <see cref="GetTimestamp"/>、<see cref="GetElapsedTime(long,long)"/> 与 <see cref="TimestampFrequency"/>。
/// <see cref="CreateTimer"/> 刻意不实现 —— 测试直接调 <c>SweepOnce()</c>,不启动后台循环;
/// 若将来有测试要跑 <c>ExecuteAsync</c>,再补一个可控 timer。</para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>用 tick 作为时间戳单位,便于 <see cref="Advance(TimeSpan)"/> 精确换算。</summary>
    private long _timestamp;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() =>
        new(DateTimeOffset.UnixEpoch.UtcDateTime.Ticks + GetTimestamp(), TimeSpan.Zero);

    /// <summary>把时钟向前推进指定时长。</summary>
    /// <param name="delta">推进量;负数被忽略。</param>
    public void Advance(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        Interlocked.Add(ref _timestamp, (long)(delta.TotalSeconds * TimestampFrequency));
    }
}
