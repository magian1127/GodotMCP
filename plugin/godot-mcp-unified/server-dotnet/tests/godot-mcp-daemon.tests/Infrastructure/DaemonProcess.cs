using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using GodotMcp.Daemon;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// DaemonProcess.Start 的拉起参数(全部可选,缺省 = 全新一次性状态目录 + 随机空闲端口)。
/// 被 ShimTests(单例锁/exit code)、RealAcceptanceTests、各进程级场景测试使用。
/// </summary>
internal sealed class DaemonSpawnOptions
{
    /// <summary>不传则每个 daemon 使用全新的一次性状态目录(单例锁与 token 互不干扰)。</summary>
    public string? StateDir { get; init; }

    /// <summary>不传则取一个空闲端口。Port 恒通过环境变量注入。</summary>
    public int? Port { get; init; }

    /// <summary>设置则注入 GODOT_MCP_DAEMON_IDLE_SECONDS(测试用短超时)。</summary>
    public int? IdleSeconds { get; init; }

    /// <summary>true 时注入 GODOT_MCP_UNSAFE=1(unsafe 组门控测试)。</summary>
    public bool UnsafeEnabled { get; init; }

    /// <summary>true 时不注入端口环境变量,用于验证默认端口 6590。</summary>
    public bool UseDefaultPort { get; init; }
}

/// <summary>
/// 把 daemon 作为真实子进程拉起并观测其外部行为:端口、stdout/stderr、退出码、token 文件。
/// 这是进程级场景(单例锁、idle 退出、stdout 纯净)唯一的观测通道 —— 断言外部行为,不碰内部结构。
/// </summary>
internal sealed class DaemonProcess : IDisposable
{
    /// <summary>保护 _stdout/_stderr 行缓冲的锁(异步事件回调与快照读取并发)。</summary>
    private readonly object _gate = new();

    /// <summary>stdout 行缓冲 —— 断言"stdout 纯净只有协议消息"的原始证据。</summary>
    private readonly StringBuilder _stdout = new();

    /// <summary>stderr 行缓冲 —— 进程提前退出/超时异常附带尾巴辅助定位。</summary>
    private readonly StringBuilder _stderr = new();

    /// <summary>私有构造:由 Start 统一拉起进程后调用,并挂接 stdout/stderr 异步行收集。</summary>
    /// <param name="process">已启动的 daemon 子进程。</param>
    /// <param name="port">本次注入(或默认)的监听端口。</param>
    /// <param name="stateDir">本次注入的状态目录(单例锁与 token 文件所在)。</param>
    private DaemonProcess(Process process, int port, string stateDir)
    {
        Process = process;
        Port = port;
        StateDir = stateDir;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_gate)
                {
                    _stdout.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_gate)
                {
                    _stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>底层 daemon 子进程(直接观测退出码/是否退出等外部形态)。</summary>
    public Process Process { get; }

    /// <summary>daemon 监听的 loopback 端口(UseDefaultPort 时即 DaemonOptions.DefaultPort)。</summary>
    public int Port { get; }

    /// <summary>本次注入的状态目录(锁与 token 文件所在)。</summary>
    public string StateDir { get; }

    /// <summary>StartReady 缓存的 token;直接 Start 的实例保持 null(经 WaitForToken 获取)。</summary>
    private string? _token;

    /// <summary>StartReady 之后为 daemon 写下的稳定 token;直接 Start 的进程请用 WaitForToken()。</summary>
    public string Token => _token
        ?? throw new InvalidOperationException(
            "Token 仅在 StartReady() 之后可用;直接 Start 的进程请调用 WaitForToken()。");

    /// <summary>进程就绪(端口可连接)的统一等待上限。</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 拉起 daemon 子进程但不等待就绪(端口可能尚未监听,需再调 WaitReady)。
    /// 逻辑链:解析状态目录(缺省 NewStateDir) → 解析端口(显式 &gt; 默认 6590 &gt; 随机空闲)
    /// → 组装 dotnet exec 环境变量(状态目录/端口/idle/unsafe) → 启动进程 → 返回实例。
    /// </summary>
    /// <param name="options">拉起参数(状态目录、端口、idle 秒数、unsafe 门控等)。</param>
    /// <returns>已启动但未必就绪的 daemon 进程句柄。</returns>
    public static DaemonProcess Start(DaemonSpawnOptions options)
    {
        var stateDir = options.StateDir ?? TestPaths.NewStateDir();
        Directory.CreateDirectory(stateDir);
        var port = options.Port
            ?? (options.UseDefaultPort ? DaemonOptions.DefaultPort : TestPorts.GetFreePort());

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{TestPaths.DaemonDll}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment[DaemonOptions.StateDirEnvVar] = stateDir;
        if (!options.UseDefaultPort)
        {
            startInfo.Environment[DaemonOptions.PortEnvVar] = port.ToString();
        }
        if (options.IdleSeconds is { } idle)
        {
            startInfo.Environment[DaemonOptions.IdleSecondsEnvVar] = idle.ToString();
        }
        if (options.UnsafeEnabled)
        {
            startInfo.Environment["GODOT_MCP_UNSAFE"] = "1";
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return new DaemonProcess(process, port, stateDir);
    }

    /// <summary>拉起 daemon,等待就绪并取得 token —— 覆盖测试中最常见的编排形状。</summary>
    public static DaemonProcess StartReady(DaemonSpawnOptions options)
    {
        var daemon = Start(options);
        daemon.WaitReady(ReadyTimeout);
        daemon._token = daemon.WaitForToken();
        return daemon;
    }

    /// <summary>轮询端口直至可连接;进程提前退出则抛出并附上 stderr 尾巴。</summary>
    public void WaitReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"daemon 提前退出(exit code {Process.ExitCode})。stderr:\n{StderrTail()}");
            }

            try
            {
                using var tcp = new TcpClient();
                tcp.Connect(System.Net.IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException($"daemon 在 {timeout} 内未开始在端口 {Port} 监听。stderr:\n{StderrTail()}");
    }

    /// <summary>daemon 首次启动写下的稳定 token(轮询等待文件出现)。</summary>
    public string WaitForToken(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var tokenPath = Path.Combine(StateDir, DaemonToken.TokenFileName);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(tokenPath))
            {
                return File.ReadAllText(tokenPath).Trim();
            }

            if (Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"daemon 提前退出(exit code {Process.ExitCode}),token 未生成。stderr:\n{StderrTail()}");
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"daemon 未在 {effectiveTimeout.TotalSeconds}s 内写出 token 文件。stderr:\n{StderrTail()}");
    }

    /// <summary>进程是否已退出(单例锁拒绝第二实例、idle 自行退出等场景的判定依据)。</summary>
    public bool HasExited => Process.HasExited;

    /// <summary>进程退出码(仅 HasExited 后有意义;单例锁冲突约定为非 0)。</summary>
    public int ExitCode => Process.ExitCode;

    /// <summary>在 timeout 内等待进程退出;超时返回 false(不杀死进程)。</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        return Process.WaitForExit((int)timeout.TotalMilliseconds);
    }

    /// <summary>stdout 全量快照(断言"协议消息之外无杂质"用)。</summary>
    /// <returns>至今累计的全部 stdout 行(换行拼接)。</returns>
    public string StdoutSnapshot()
    {
        lock (_gate)
        {
            return _stdout.ToString();
        }
    }

    /// <summary>stderr 全量快照(失败诊断用)。</summary>
    /// <returns>至今累计的全部 stderr 行(换行拼接)。</returns>
    public string StderrSnapshot()
    {
        lock (_gate)
        {
            return _stderr.ToString();
        }
    }

    /// <summary>取 stderr 末尾至多 2000 字符(异常消息附带的定位线索,避免超长输出)。</summary>
    /// <returns>stderr 快照尾部;不足 2000 字符时为全量。</returns>
    private string StderrTail()
    {
        var text = StderrSnapshot();
        return text.Length <= 2000 ? text : text[^2000..];
    }

    /// <summary>杀死整个进程树(若仍在运行)并释放句柄;进程已退出时直接清理。</summary>
    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出 —— 目标状态本就是退出。
        }

        Process.Dispose();
    }
}
