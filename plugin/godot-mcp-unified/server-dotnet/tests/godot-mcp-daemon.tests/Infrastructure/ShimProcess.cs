using System.Diagnostics;
using System.Text;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// ShimProcess.Start 的拉起参数:端口与状态目录必填,指向 shim 应连的 daemon 环境
/// (daemon 本体由 shim 依 GODOT_MCP_DAEMON_EXE 自行拉起,不由测试直接管理)。
/// </summary>
internal sealed class ShimSpawnOptions
{
    /// <summary>daemon HTTP 端口(经 GODOT_MCP_DAEMON_PORT 注入)。</summary>
    public required int Port { get; init; }

    /// <summary>daemon 状态目录(经 GODOT_MCP_DAEMON_STATE_DIR 注入;token 由此读取)。</summary>
    public required string StateDir { get; init; }

    /// <summary>注入 daemon 侧空闲阈值(测试用短值,保证被拉起的 daemon 自行清理)。</summary>
    public int? IdleSeconds { get; init; }
}

/// <summary>
/// 把 shim 作为真实子进程拉起并以行帧 stdio 驱动(stdout 只应有协议消息)。
/// GODOT_MCP_DAEMON_EXE 指向本仓库构建的 daemon dll(经 dotnet exec 拉起)。
/// </summary>
internal sealed class ShimProcess : IDisposable
{
    /// <summary>stderr 行缓冲(超时/退出异常附带的定位线索)。</summary>
    private readonly StringBuilder _stderr = new();

    /// <summary>保护 _stderr 的锁(异步事件回调与快照读取并发)。</summary>
    private readonly object _gate = new();

    /// <summary>私有构造:由 Start 拉起进程后调用,并挂接 stderr 异步行收集(stdout 由测试逐行读取)。</summary>
    /// <param name="process">已启动的 shim 子进程(标准三流均已重定向)。</param>
    private ShimProcess(Process process)
    {
        Process = process;
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
        process.BeginErrorReadLine();
    }

    /// <summary>底层 shim 子进程(stdin 发协议行,stdout 收协议行,stderr 异步收集)。</summary>
    public Process Process { get; }

    /// <summary>
    /// 拉起 shim 子进程并注入 daemon 连接环境:端口/状态目录/daemon dll 路径(GODOT_MCP_DAEMON_EXE)
    /// /可选 idle 秒数。行帧 stdio 协议随后经 SendAsync/ReadLineAsync 驱动。
    /// </summary>
    /// <param name="options">拉起参数(端口、状态目录、idle 秒数)。</param>
    /// <returns>已启动的 shim 进程句柄。</returns>
    public static ShimProcess Start(ShimSpawnOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(TestPaths.ShimDll);
        startInfo.Environment["GODOT_MCP_DAEMON_PORT"] = options.Port.ToString();
        startInfo.Environment["GODOT_MCP_DAEMON_STATE_DIR"] = options.StateDir;
        startInfo.Environment["GODOT_MCP_DAEMON_EXE"] = TestPaths.DaemonDll;
        if (options.IdleSeconds is { } idle)
        {
            startInfo.Environment["GODOT_MCP_DAEMON_IDLE_SECONDS"] = idle.ToString();
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("shim 子进程启动失败");
        }
        return new ShimProcess(process);
    }

    /// <summary>发送一条消息并等待一条响应行(通知不应等待)。</summary>
    /// <param name="json">要发送的一行 JSON 协议消息。</param>
    /// <param name="timeout">等待响应行的上限。</param>
    /// <returns>收到的响应行(原始 JSON 文本)。</returns>
    public async Task<string> SendAsync(string json, TimeSpan timeout)
    {
        await WriteLineAsync(json);
        return await ReadLineAsync(timeout);
    }

    /// <summary>仅发送一行消息不等待响应(通知发送或"先发多条再收"的编排用)。</summary>
    /// <param name="json">要写入 shim stdin 的一行 JSON 文本。</param>
    public async Task WriteLineAsync(string json)
    {
        await Process.StandardInput.WriteLineAsync(json);
        await Process.StandardInput.FlushAsync();
    }

    /// <summary>读取一行 stdout;超时抛 TimeoutException,stdout 流已尽(进程退出)抛 InvalidOperationException,两者均附 stderr 快照。</summary>
    /// <param name="timeout">等待一行输出的上限。</param>
    /// <returns>下一行 stdout 文本。</returns>
    public async Task<string> ReadLineAsync(TimeSpan timeout)
    {
        var readTask = Process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout));
        if (completed != readTask)
        {
            throw new TimeoutException($"shim stdout 在 {timeout.TotalSeconds}s 内无输出;stderr:\n{StderrSnapshot()}");
        }
        return await readTask
            ?? throw new InvalidOperationException($"shim 已退出(exit {Process.ExitCode});stderr:\n{StderrSnapshot()}");
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

    /// <summary>shim 进程是否已退出(退出码归属 shim 自身而非其拉起的 daemon)。</summary>
    public bool HasExited => Process.HasExited;

    /// <summary>关闭 stdin 请求优雅退出,3s 未退则杀整个进程树;进程已退出时仅清理句柄。</summary>
    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.StandardInput.Close();
                if (!Process.WaitForExit(3000))
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        Process.Dispose();
    }
}
