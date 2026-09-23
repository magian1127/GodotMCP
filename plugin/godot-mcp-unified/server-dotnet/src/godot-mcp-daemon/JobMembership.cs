using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GodotMcp.Daemon;

/// <summary>
/// Windows 作业对象(Job)归属自检。
/// <para>daemon 是机器级常驻服务,但它若**诞生在某个宿主的"关闭即杀"作业对象里**,
/// 宿主一退出就会被系统连带终止——其他宿主会突然失去服务(下一次调用会自举恢复,
/// 但表现为"服务莫名消失")。</para>
/// <para>为什么会发生:Job 成员身份**随进程创建继承**,且无法在创建后脱离。若某个 MCP 宿主
/// 把自己的 stdio 服务器放进带 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> 的 Job,而该服务器
/// (shim)又拉起了 daemon,daemon 就继承了这个 Job。要避免,只有两条路:让 daemon 由 Job
/// **之外**的进程拉起(编辑器边车 autostart / 机器级开机自启),或在创建时用
/// <c>CREATE_BREAKAWAY_FROM_JOB</c> 挣脱——后者受宿主 Job 的 <c>BREAKAWAY_OK</c> 制约,
/// 宿主不允许时操作系统直接拒绝,我方无法单方面保证。</para>
/// <para>本自检**只读、零行为变更**:非 Windows、查不出来、或任何异常都返回 null,绝不影响启动。</para>
/// </summary>
internal static class JobMembership
{
    /// <summary>JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation。</summary>
    private const int ExtendedLimitInformationClass = 9;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE:关闭最后一个 Job 句柄时杀掉其中所有进程。</summary>
    private const uint LimitKillOnJobClose = 0x2000;

    /// <summary>
    /// 若当前进程处于"关闭即杀"的作业对象中,返回可读的告警文本;否则返回 null。
    /// <para>只读探测:任何异常(含非 Windows)一律吞掉返回 null——自检绝不阻断启动。</para>
    /// </summary>
    public static string? DescribeKillOnCloseRisk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            var inJob = IsInAnyJob();
            var limitFlags = inJob ? ReadCurrentJobLimitFlags() : 0;
            return DescribeRisk(inJob, limitFlags);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 纯决策:给定"是否在 Job 中"与 Job 的 LimitFlags,返回告警文本或 null。
    /// <para>拆成纯函数是为了让判定与文案可被单测覆盖(互操作部分只剩薄薄一层)。
    /// 注意:带 <c>KILL_ON_JOB_CLOSE</c> 即告警,即使同时带 <c>BREAKAWAY_OK</c>——
    /// 后者只说明"创建时可以尝试挣脱",并不能免除已被继承的 Job 成员身份。</para>
    /// </summary>
    /// <param name="inAnyJob">当前进程是否属于某个 Job。</param>
    /// <param name="limitFlags">该 Job 的 LimitFlags(不在 Job 中时无意义)。</param>
    /// <returns>需要告警时返回文本,否则 null。</returns>
    internal static string? DescribeRisk(bool inAnyJob, uint limitFlags)
    {
        if (!inAnyJob || (limitFlags & LimitKillOnJobClose) == 0)
        {
            return null;
        }
        return "本 daemon 诞生于一个「宿主关闭即杀」的作业对象(Job)中:拉起它的宿主退出时,"
            + "系统会把 daemon 一并终止,其他宿主会突然失去服务(下次调用会自举恢复,但用户会看到"
            + "服务莫名消失)。要让 daemon 真正机器级常驻,请由宿主会话之外的进程拉起它——"
            + "打开 Godot 编辑器(daemon 边车 autostart 默认开启),或配置机器级开机自启。";
    }

    /// <summary>当前进程是否属于任何作业对象。</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsInAnyJob()
    {
        return IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) && inJob;
    }

    /// <summary>读当前进程所属 Job 的 LimitFlags;读不到返回 0(当作"无风险",不误报)。</summary>
    [SupportedOSPlatform("windows")]
    private static uint ReadCurrentJobLimitFlags()
    {
        var size = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new JobObjectExtendedLimitInfo(), buffer, false);
            if (!QueryInformationJobObject(IntPtr.Zero, ExtendedLimitInformationClass, buffer, (uint)size, out _))
            {
                return 0;
            }
            return Marshal.PtrToStructure<JobObjectExtendedLimitInfo>(buffer).BasicLimitInformation.LimitFlags;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr processHandle, IntPtr jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr jobHandle, int informationClass, IntPtr information, uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
