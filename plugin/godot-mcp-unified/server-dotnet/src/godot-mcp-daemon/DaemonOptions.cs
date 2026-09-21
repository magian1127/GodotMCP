using System.Globalization;

namespace GodotMcp.Daemon;

/// <summary>
/// daemon 运行参数,全部来自环境变量;缺省值即 ADR-0004 的机器级默认。
/// GODOT_MCP_DAEMON_PORT —— loopback 监听端口,默认 6590。
/// GODOT_MCP_DAEMON_STATE_DIR —— 单例锁与稳定 token 所在的机器级状态目录;
/// 默认与 GDScript 注册表目录同一配方(registry_paths.gd registry_dir)。
/// GODOT_MCP_DAEMON_IDLE_SECONDS —— 全部连接断开后的空闲退出阈值,默认 600(10 分钟)。
/// </summary>
public sealed class DaemonOptions
{
    /// <summary>默认监听端口,未设置 GODOT_MCP_DAEMON_PORT 时生效。</summary>
    public const int DefaultPort = 6590;

    /// <summary>默认空闲退出阈值(10 分钟),未设置 GODOT_MCP_DAEMON_IDLE_SECONDS 时生效。</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>环境变量名集中在此:读取(DaemonOptions)与测试注入(DaemonProcess)共用,避免改名时双文件漂移。</summary>
    public const string PortEnvVar = "GODOT_MCP_DAEMON_PORT";

    /// <summary>机器级状态目录的环境变量名;缺省时按平台配方推导(见 <see cref="ReadStateDir"/>)。</summary>
    public const string StateDirEnvVar = "GODOT_MCP_DAEMON_STATE_DIR";

    /// <summary>空闲退出阈值(单位秒,支持小数)的环境变量名。</summary>
    public const string IdleSecondsEnvVar = "GODOT_MCP_DAEMON_IDLE_SECONDS";

    /// <summary>loopback 监听端口(1-65535)。</summary>
    public required int Port { get; init; }

    /// <summary>机器级状态目录:单例锁(daemon.lock)、稳定 token(daemon-token)与日志(daemon.log)均落于此。</summary>
    public required string StateDir { get; init; }

    /// <summary>空闲退出阈值:全部连接断开且持续空闲超过该时长后进程自退。</summary>
    public required TimeSpan IdleTimeout { get; init; }

    /// <summary>
    /// 从当前进程环境变量构造运行参数。
    /// </summary>
    /// <returns>填充完毕的 <see cref="DaemonOptions"/>。</returns>
    /// <para>逻辑链:ReadPort → ReadStateDir → ReadIdleTimeout 三者独立解析;
    /// 任一环境变量非法时抛 <see cref="InvalidOperationException"/>,由 Program 外层按环境故障(退出码 1)归因。</para>
    public static DaemonOptions FromEnvironment()
    {
        return new DaemonOptions
        {
            Port = ReadPort(),
            StateDir = ReadStateDir(),
            IdleTimeout = ReadIdleTimeout(),
        };
    }

    /// <summary>
    /// 解析监听端口。
    /// </summary>
    /// <returns>端口号;环境变量未设置/空白时返回 <see cref="DefaultPort"/>。</returns>
    /// <para>逻辑链:读 <see cref="PortEnvVar"/> → 空白则取默认 → 非 1-65535 的整数时抛 <see cref="InvalidOperationException"/>。</para>
    private static int ReadPort()
    {
        var raw = Environment.GetEnvironmentVariable(PortEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultPort;
        }

        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"GODOT_MCP_DAEMON_PORT 无效: \"{raw}\"(需要 1-65535 的整数)。");
        }

        return port;
    }

    /// <summary>
    /// 解析空闲退出阈值。
    /// </summary>
    /// <returns>阈值;环境变量未设置/空白时返回 <see cref="DefaultIdleTimeout"/>。</returns>
    /// <para>逻辑链:读 <see cref="IdleSecondsEnvVar"/> → 空白则取默认 → 用 InvariantCulture 解析秒数,
    /// 非正数或无法解析时抛 <see cref="InvalidOperationException"/>。</para>
    private static TimeSpan ReadIdleTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(IdleSecondsEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultIdleTimeout;
        }

        if (!double.TryParse(raw, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw new InvalidOperationException(
                $"GODOT_MCP_DAEMON_IDLE_SECONDS 无效: \"{raw}\"(需要正数,单位秒)。");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 解析机器级状态目录,配方与 GDScript 注册表目录(registry_paths.gd registry_dir)一致,
    /// 保证 C# daemon 与 GDScript 侧落在同一目录。
    /// </summary>
    /// <returns>状态目录绝对路径。</returns>
    /// <para>逻辑链:优先取 <see cref="StateDirEnvVar"/> 覆盖 → Windows 取 APPDATA(缺失时回退
    /// USERPROFILE\AppData\Roaming,两者皆缺抛 <see cref="InvalidOperationException"/>)→ macOS 取
    /// ~/Library/Application Support → 其余取 XDG_DATA_HOME(缺失回退 ~/.local/share)→
    /// 各平台末段统一拼接 "godot-mcp-toolkit"。</para>
    private static string ReadStateDir()
    {
        var overridden = Environment.GetEnvironmentVariable(StateDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
            {
                var userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
                    ?? throw new InvalidOperationException(
                        "无法定位注册表目录:APPDATA 与 USERPROFILE 均未设置。");
                appData = Path.Combine(userProfile, "AppData", "Roaming");
            }

            return Path.Combine(appData, "godot-mcp-toolkit");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(RequireHome(), "Library", "Application Support", "godot-mcp-toolkit");
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            dataHome = Path.Combine(RequireHome(), ".local", "share");
        }

        return Path.Combine(dataHome, "godot-mcp-toolkit");
    }

    /// <summary>
    /// 读取 HOME 环境变量,macOS/Linux 推导状态目录时的用户主目录依赖。
    /// </summary>
    /// <returns>HOME 值;未设置时抛 <see cref="InvalidOperationException"/>。</returns>
    private static string RequireHome()
    {
        return Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("无法定位注册表目录:HOME 未设置。");
    }
}
