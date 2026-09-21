namespace GodotMcp.Daemon;

/// <summary>
/// daemon 进程退出码契约(与 README 退出码表同源):拉起方据此决定放弃、重试或报错。
/// </summary>
public static class ExitCodes
{
    /// <summary>正常退出(含空闲超时自退)。</summary>
    public const int Ok = 0;

    /// <summary>启动期环境故障(状态目录/锁/token/日志的 IO 失败)—— 拉起方按可重试处理。</summary>
    public const int EnvironmentFailure = 1;

    /// <summary>另一 daemon 明确持有单例锁(Windows 精确归因)—— 拉起方应放弃(已有实例)。</summary>
    public const int SingletonLockUnavailable = 2;

    /// <summary>单例锁状态不明确(Unix 无法区分锁冲突与环境故障)—— 拉起方应视为可重试。</summary>
    public const int LockStateUnclear = 3;

    /// <summary>监听地址绑定失败(通常为端口被其它进程占用)。</summary>
    public const int BindFailure = 4;
}
