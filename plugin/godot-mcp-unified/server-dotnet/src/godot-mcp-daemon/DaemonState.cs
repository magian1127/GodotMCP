namespace GodotMcp.Daemon;

/// <summary>
/// 另一 daemon 明确持有单例锁(Windows 精确归因)—— 第二个实例应以退出码 2 终止。
/// </summary>
/// <param name="lockPath">被占用的锁文件路径。</param>
/// <param name="inner">打开锁文件时的原始 <see cref="IOException"/>。</param>
public sealed class SingletonLockUnavailableException(string lockPath, IOException inner)
    : Exception($"已有 daemon 实例持有单例锁:{lockPath}", inner);

/// <summary>
/// 无法区分"已有实例持锁"与"环境故障"(权限/磁盘等)。Unix 的 flock 模拟对一切冲突
/// 抛同一形态的 IOException,无法精确归因 —— 以独立退出码(3)告诉拉起方:可重试,
/// 不要当成"已有实例在跑"而放弃自愈。
/// </summary>
/// <param name="lockPath">打开失败的锁文件路径。</param>
/// <param name="inner">打开锁文件时的原始 <see cref="IOException"/>。</param>
public sealed class LockStateUnclearException(string lockPath, IOException inner)
    : Exception($"单例锁状态不明确(可能是已有实例持锁,也可能是环境故障):{lockPath}", inner);

/// <summary>
/// 机器级单例锁 + 稳定 token。锁是 FileShare.None 的文件句柄,持有至进程终止;
/// 无论正常退出还是崩溃,操作系统都会释放句柄,不遗留陈旧锁(ADR-0004:可移植实现)。
/// </summary>
/// <remarks>
/// 生命周期:Program 启动最前经 <see cref="Acquire"/> 获取(先于 WebApplication 构建),
/// 以 using 持有至进程退出;token 供 <see cref="RequireDaemonTokenMiddleware"/> 做 HTTP 门禁。
/// </remarks>
public sealed class DaemonState : IDisposable
{
    /// <summary>状态目录下的锁文件名,与 token、日志同目录共存。</summary>
    public const string LockFileName = "daemon.lock";

    /// <summary>单例锁本体:FileShare.None 的文件流,Dispose 即释放锁。</summary>
    private readonly FileStream _lockHandle;

    /// <summary>
    /// 私有构造:仅 <see cref="Acquire"/> 在锁到手后调用,保证实例必然持有锁。
    /// </summary>
    /// <param name="lockHandle">已成功打开的单例锁文件流。</param>
    /// <param name="token">状态目录内的稳定持有者令牌(token)。</param>
    private DaemonState(FileStream lockHandle, string token)
    {
        _lockHandle = lockHandle;
        Token = token;
    }

    /// <summary>本次进程的持有者令牌,写入状态目录并供 HTTP 鉴权中间件比对。</summary>
    public string Token { get; }

    /// <summary>
    /// 获取机器级单例锁并确保稳定 token,是 daemon 启动链的第二步(参数解析之后)。
    /// </summary>
    /// <param name="options">运行参数,提供状态目录。</param>
    /// <returns>持有锁与 token 的 <see cref="DaemonState"/>。</returns>
    /// <para>逻辑链:创建状态目录 → 以 OpenOrCreate + FileShare.None 打开锁文件 →
    /// 分支(Windows):sharing violation 归因为另一实例持锁,抛
    /// <see cref="SingletonLockUnavailableException"/>;其余 IOException(磁盘满/权限等)原样上抛,不冒充锁冲突 →
    /// 分支(Unix):任何 IOException 都无法归因,抛 <see cref="LockStateUnclearException"/> 交拉起方可重试处理 →
    /// 锁到手后取稳定 token,构造实例返回。</para>
    public static DaemonState Acquire(DaemonOptions options)
    {
        Directory.CreateDirectory(options.StateDir);
        var lockPath = Path.Combine(options.StateDir, LockFileName);

        FileStream lockHandle;
        try
        {
            lockHandle = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            if (OperatingSystem.IsWindows())
            {
                if (IsSharingViolation(ex))
                {
                    throw new SingletonLockUnavailableException(lockPath, ex);
                }

                throw; // 磁盘满、权限等 —— 保留原始错误,不冒充锁冲突。
            }

            // Unix:无法区分锁冲突与环境故障 —— 归入"状态不明确",交给拉起方按可重试处理。
            throw new LockStateUnclearException(lockPath, ex);
        }

        return new DaemonState(lockHandle, DaemonToken.EnsureStable(options.StateDir));
    }

    /// <summary>Windows 上 sharing/lock violation 的 HRESULT —— 唯一能精确代表"文件已被占用"的形态。</summary>
    private static bool IsSharingViolation(IOException ex)
    {
        return ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
    }

    /// <summary>
    /// 释放锁文件句柄,单例锁随之解除;正常退出与宿主 using 清理都会走到这里。
    /// </summary>
    public void Dispose()
    {
        _lockHandle.Dispose();
    }
}
