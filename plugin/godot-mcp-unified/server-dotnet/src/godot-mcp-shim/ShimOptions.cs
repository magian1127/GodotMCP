namespace GodotMcp.Shim;

/// <summary>
/// shim 运行参数(全部来自环境变量,与 daemon 同名同义):
/// GODOT_MCP_DAEMON_PORT / GODOT_MCP_DAEMON_STATE_DIR 缺省即 ADR-0004 机器级默认;
/// GODOT_MCP_DAEMON_EXE 为 daemon 可执行文件覆盖(*.dll 时经 dotnet exec 拉起,
/// 供开发/测试;发布布局缺省为 shim 同目录的 godot-mcp-daemon(.exe))。
/// </summary>
internal sealed class ShimOptions
{
    /// <summary>daemon HTTP 端口(GODOT_MCP_DAEMON_PORT;缺省 6590,非法值在 FromEnvironment 抛错)。</summary>
    public required int Port { get; init; }

    /// <summary>状态目录(GODOT_MCP_DAEMON_STATE_DIR;兼注册表目录,缺省走平台机器级默认)。</summary>
    public required string StateDir { get; init; }

    /// <summary>daemon 可执行文件覆盖(GODOT_MCP_DAEMON_EXE;*.dll 经 dotnet exec 拉起;缺省 null 用 shim 同目录默认名)。</summary>
    public required string? DaemonExeOverride { get; init; }

    /// <summary>
    /// <c>tools/call</c> 的单次等待上限(GODOT_MCP_TOOL_CALL_TIMEOUT,毫秒)。
    /// </summary>
    /// <remarks>
    /// <para>默认 <b>210 秒</b>。这是三层阶梯的中间层:daemon 长工具 180s &lt; shim 210s &lt; 宿主 240s。
    /// 严格递减的意义是让正常路径上<b>永远是 daemon 先给出干净的 TIMEOUT</b>,上层走不到自己的截止点;
    /// 只有 daemon 自身卡死时才由 shim 兜底,那时"该操作可能已执行"才是正确文案。</para>
    /// <para>只对 <c>tools/call</c> 生效。短请求(<c>tools/list</c>/<c>initialize</c>)仍用
    /// <see cref="ShortRequestTimeout"/>,否则一次卡住的 <c>tools/list</c> 会拖 3 分半。</para>
    /// </remarks>
    public required TimeSpan ToolCallTimeout { get; init; }

    /// <summary>短请求(<c>tools/list</c>、<c>initialize</c> 等)的单次上限。</summary>
    public static readonly TimeSpan ShortRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary><c>tools/call</c> 超时的默认值(毫秒)。</summary>
    public const int DefaultToolCallTimeoutMs = 210_000;

    /// <summary>从环境变量读取全部运行参数(与 daemon 同名同义)。</summary>
    /// <returns>填充完毕的运行参数。</returns>
    /// <exception cref="InvalidOperationException">端口值非法,或平台目录无法定位。</exception>
    public static ShimOptions FromEnvironment()
    {
        return new ShimOptions
        {
            Port = ReadPort(),
            StateDir = ReadStateDir(),
            DaemonExeOverride = NullIfEmpty(Environment.GetEnvironmentVariable("GODOT_MCP_DAEMON_EXE")),
            ToolCallTimeout = ReadToolCallTimeout(),
        };
    }

    /// <summary>
    /// 读 <c>GODOT_MCP_TOOL_CALL_TIMEOUT</c>(毫秒);缺省 210 秒。
    /// </summary>
    /// <remarks>非法值<b>回落默认而不抛错</b>:一个拼错的环境变量不该让 MCP 服务器起不来。</remarks>
    /// <returns>合法的调用上限。</returns>
    private static TimeSpan ReadToolCallTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("GODOT_MCP_TOOL_CALL_TIMEOUT");
        if (string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw, out var milliseconds)
            || milliseconds <= 0)
        {
            return TimeSpan.FromMilliseconds(DefaultToolCallTimeoutMs);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>读 GODOT_MCP_DAEMON_PORT:缺省 6590;非整数或越界(1-65535 之外)抛错。</summary>
    /// <returns>合法端口号。</returns>
    /// <exception cref="InvalidOperationException">端口值非法。</exception>
    private static int ReadPort()
    {
        var raw = Environment.GetEnvironmentVariable("GODOT_MCP_DAEMON_PORT");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 6590;
        }
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"GODOT_MCP_DAEMON_PORT 无效: \"{raw}\"");
        }
        return port;
    }

    /// <summary>与 DaemonOptions / registry_paths.gd registry_dir 同一配方(状态目录兼注册表目录)。
    /// <para>逻辑链:显式覆盖优先 → Windows 取 APPDATA(缺失则以 USERPROFILE 拼 AppData/Roaming)→
    /// macOS 取 ~/Library/Application Support → 其余取 XDG_DATA_HOME(缺失则 ~/.local/share),
    /// 均拼上 godot-mcp-toolkit。</para>
    /// </summary>
    /// <returns>状态目录绝对路径(目录本身不在此创建)。</returns>
    private static string ReadStateDir()
    {
        var overridden = NullIfEmpty(Environment.GetEnvironmentVariable("GODOT_MCP_DAEMON_STATE_DIR"));
        if (overridden is not null)
        {
            return overridden;
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
            {
                var profile = Environment.GetEnvironmentVariable("USERPROFILE")
                    ?? throw new InvalidOperationException("无法定位注册表目录:APPDATA 与 USERPROFILE 均未设置。");
                appData = Path.Combine(profile, "AppData", "Roaming");
            }
            return Path.Combine(appData, "godot-mcp-toolkit");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(RequireHome(), "Library", "Application Support", "godot-mcp-toolkit");
        }
        var dataHome = NullIfEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
            ?? Path.Combine(RequireHome(), ".local", "share");
        return Path.Combine(dataHome, "godot-mcp-toolkit");
    }

    /// <summary>取 HOME 环境变量(macOS/Unix 目录推导依赖它)。</summary>
    /// <returns>用户主目录。</returns>
    /// <exception cref="InvalidOperationException">HOME 未设置。</exception>
    private static string RequireHome()
    {
        return Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("无法定位注册表目录:HOME 未设置。");
    }

    /// <summary>空白字符串归一为 null(环境变量未设/空串/全空白同义对待)。</summary>
    /// <param name="value">原始字符串。</param>
    /// <returns>非空白返回原值;否则 null。</returns>
    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
