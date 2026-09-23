namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 作业对象(Job)归属自检的判定与文案:纯函数部分可完全覆盖,互操作部分只做"不抛"冒烟。
/// <para>背景:Job 成员身份随进程创建继承且无法脱离。若宿主把自己的 stdio 服务器放进带
/// KILL_ON_JOB_CLOSE 的 Job,而该服务器(shim)又拉起了 daemon,daemon 就会被宿主的关闭
/// 连带杀掉 —— 其他宿主会突然失去服务。本自检把该隐形风险变成启动期告警。</para>
/// </summary>
public class JobMembershipTests
{
    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE。</summary>
    private const uint KillOnJobClose = 0x2000;

    /// <summary>JOB_OBJECT_LIMIT_BREAKAWAY_OK(允许创建时挣脱,但不免除既有成员身份)。</summary>
    private const uint BreakawayOk = 0x800;

    /// <summary>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION(与关闭无关的另一种限位)。</summary>
    private const uint DieOnUnhandledException = 0x400;

    /// <summary>不在任何 Job 中:无论 LimitFlags 如何都不告警。</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(KillOnJobClose)]
    [InlineData(BreakawayOk)]
    public void no_warning_when_not_in_any_job(uint limitFlags)
    {
        Assert.Null(JobMembership.DescribeRisk(inAnyJob: false, limitFlags));
    }

    /// <summary>在 Job 中但该 Job 不会因关闭而杀进程:不告警(常见且无害)。</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(BreakawayOk)]
    [InlineData(DieOnUnhandledException)]
    public void no_warning_when_job_does_not_kill_on_close(uint limitFlags)
    {
        Assert.Null(JobMembership.DescribeRisk(inAnyJob: true, limitFlags));
    }

    /// <summary>关闭即杀的 Job:告警,且文案给出可执行的出路(编辑器边车 / 开机自启)。</summary>
    [Fact]
    public void warns_and_offers_a_way_out_for_kill_on_close_job()
    {
        var risk = JobMembership.DescribeRisk(inAnyJob: true, KillOnJobClose);

        Assert.NotNull(risk);
        Assert.Contains("作业对象", risk);
        Assert.Contains("编辑器", risk);
        Assert.Contains("开机自启", risk);
    }

    /// <summary>即使同时允许 BREAKAWAY_OK 也仍然告警——"创建时可挣脱"并不免除已被继承的成员身份。</summary>
    [Fact]
    public void warns_even_when_breakaway_is_allowed()
    {
        Assert.NotNull(JobMembership.DescribeRisk(inAnyJob: true, KillOnJobClose | BreakawayOk));
    }

    /// <summary>互操作路径绝不抛(非 Windows 返回 null;探测失败也返回 null),不能影响 daemon 启动。</summary>
    [Fact]
    public void interop_probe_never_throws()
    {
        var risk = JobMembership.DescribeKillOnCloseRisk();

        // 测试宿主可能或可能不在"关闭即杀"的 Job 里,故只断言"不抛且为 null 或非空文本"。
        Assert.True(risk is null || risk.Length > 0);
    }
}
