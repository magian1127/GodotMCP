using System.Diagnostics;
using System.Net.Sockets;

namespace GodotMcp.Shim;

/// <summary>
/// daemon 自举器:探活(loopback 端口可连接)→ 缺席则拉起 self-contained/开发形态的
/// daemon → 等端口就绪并读取机器级稳定 token。已有实例在跑时绝不重复拉起
/// (拉起的竞态由 daemon 单例锁兜底:子进程可能以退出码 2/3 退出,此时继续等端口)。
/// </summary>
/// <param name="options">shim 运行参数(daemon 端口/状态目录/可执行文件覆盖)。</param>
internal sealed class DaemonBootstrap(ShimOptions options)
{
    /// <summary>拉起单飞闸:同一时刻仅一个调用者进入"探测+拉起"临界区。</summary>
    private readonly SemaphoreSlim _spawnGate = new(1, 1);
    /// <summary>上次实际发起拉起的时刻(UTC);配合 500ms 去抖,防止转发失败引发拉起风暴。</summary>
    private DateTime _lastSpawnAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// 确保 daemon 在目标端口可用;缺席则拉起并等待就绪。
    /// <para>逻辑链:先探活端口,开着直接返回 → 否则进单飞闸二次探活(并发者可能已拉起)→
    /// 距上次拉起不足 500ms 则只等端口不重复拉起,否则记时刻并 SpawnDaemon →
    /// 退出闸后以 200ms 步进轮询端口至 30s 截止 → 到期仍不就绪抛 InvalidOperationException。</para>
    /// </summary>
    /// <param name="ct">取消令牌(探活与等待期间生效)。</param>
    /// <returns>端口就绪即正常完成;超时以异常报告。</returns>
    /// <exception cref="InvalidOperationException">daemon 30s 内未在目标端口就绪。</exception>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (await IsPortOpenAsync(ct))
        {
            return;
        }

        // 单飞 + 500ms 去抖:避免每次转发失败都触发一轮拉起风暴。
        await _spawnGate.WaitAsync(ct);
        try
        {
            if (await IsPortOpenAsync(ct))
            {
                return;
            }
            if (DateTime.UtcNow - _lastSpawnAttemptUtc < TimeSpan.FromMilliseconds(500))
            {
                // 刚刚尝试过——直接等待端口(可能是拉起中的另一实例)。
            }
            else
            {
                _lastSpawnAttemptUtc = DateTime.UtcNow;
                SpawnDaemon();
            }
        }
        finally
        {
            _spawnGate.Release();
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsPortOpenAsync(ct))
            {
                return;
            }
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException($"daemon 在 30s 内未在端口 {options.Port} 就绪");
    }

    /// <summary>机器级稳定 token(跨重启稳定;缺失时短暂等待,覆盖 daemon 首个写盘窗口)。
    /// <para>逻辑链:拼 {StateDir}/daemon-token 路径 → 以 100ms 步进轮询至 10s 截止:
    /// 文件存在且内容 Trim 后非空即返回 → 到期仍读不到抛 InvalidOperationException。</para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>daemon 的稳定令牌(HTTP Bearer 认证用)。</returns>
    /// <exception cref="InvalidOperationException">10s 内未等到令牌文件写出。</exception>
    public async Task<string> ReadTokenAsync(CancellationToken ct)
    {
        var tokenPath = Path.Combine(options.StateDir, "daemon-token");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(tokenPath))
            {
                var token = File.ReadAllText(tokenPath).Trim();
                if (token.Length > 0)
                {
                    return token;
                }
            }
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException($"daemon token 未在 {tokenPath} 写出");
    }

    /// <summary>
    /// 拉起 daemon 子进程(只负责启动,就绪由调用方轮询端口)。
    /// <para>逻辑链:解析可执行路径(override 优先,否则 shim 同目录默认名)→ *.dll 走
    /// "dotnet exec" 拉起,否则直接执行 → Start 失败或可执行文件缺失(Win32Exception/
    /// FileNotFoundException)抛 InvalidOperationException → stdout/stderr 各起一个泵任务,
    /// 逐行转写本进程 stderr(daemon 的 stdout 保留给协议通道,正常应为空)。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">可执行文件不存在或不可拉起。</exception>
    private void SpawnDaemon()
    {
        var exe = options.DaemonExeOverride ?? DefaultDaemonPath();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(exe);
        }
        else
        {
            startInfo.FileName = exe;
        }

        // 实例式启动(与 Process.Start 等价;StartInfo + ArgumentList 传参,无 shell 拼接)。
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"daemon 启动失败:{exe}");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"daemon 可执行文件不可拉起:{exe}({ex.Message})");
        }

        Console.Error.WriteLine($"[shim] 已拉起 daemon (pid {process.Id}): {exe}");
        // 子进程输出一律转 stderr(daemon 的 stdout 保留给协议通道,正常应为空)。
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                Console.Error.WriteLine($"[daemon] {line}");
            }
        });
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                Console.Error.WriteLine($"[daemon:out] {line}");
            }
        });
    }

    /// <summary>默认 daemon 路径:shim 同目录(AppContext.BaseDirectory)下的 godot-mcp-daemon(Windows 带 .exe)。</summary>
    /// <returns>平台对应的可执行文件绝对路径。</returns>
    private string DefaultDaemonPath()
    {
        var name = OperatingSystem.IsWindows() ? "godot-mcp-daemon.exe" : "godot-mcp-daemon";
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>loopback 探活:400ms 超时内 TCP 连上 127.0.0.1:Port 即视为 daemon 在跑。</summary>
    /// <param name="ct">取消令牌(与 400ms 超时取先到者)。</param>
    /// <returns>连接成功 true;超时/拒绝/取消等任何异常一律 false。</returns>
    private async Task<bool> IsPortOpenAsync(CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
            await client.ConnectAsync("127.0.0.1", options.Port, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
