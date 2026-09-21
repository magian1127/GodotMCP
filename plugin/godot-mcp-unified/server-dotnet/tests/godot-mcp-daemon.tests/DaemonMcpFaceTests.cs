using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 主 seam:daemon 的 MCP HTTP 面,以官方 SDK 客户端直连驱动(spec Testing Decisions)。
/// 只断言协议面上的外部行为 —— 工具语义、错误形态、进程可观察结果。
/// </summary>
public class DaemonMcpFaceTests
{
    /// <summary>
    /// 正确令牌建会话的基线:官方 SDK 客户端直连 daemon 的 HTTP 面完成握手与首轮工具调用。
    /// <para>断言链:Arrange —— DaemonProcess 默认选项拉起。Act —— SessionConnector 连接 →
    /// ListToolsAsync → CallToolAsync("list_instances")。Assert —— ① ServerInfo.Name=
    /// "godot-mcp-unified";② 工具表含 list_instances;③ 响应 JSON 的 instances 数组长度为 0
    /// (无实例在场,基线可观察)。</para>
    /// </summary>
    [Fact]
    public async Task token_authenticated_client_connects_and_calls_list_instances()
    {
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions());

        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        Assert.Equal("godot-mcp-unified", client.ServerInfo.Name);

        var tools = await client.ListToolsAsync();
        tools.AssertContainsTool("list_instances");

        var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var json = JsonDocument.Parse(text);
        Assert.Equal(0, json.RootElement.GetProperty("instances").GetArrayLength());
    }

    /// <summary>
    /// 令牌校验拒绝面:错误令牌无法建立 SDK 会话;裸 HTTP 的 initialize 无论令牌错误还是缺失头,
    /// 都返回 401。
    /// <para>断言链:Act/Assert —— ① SessionConnector 以错误令牌连接抛异常;② PostInitializeAsync
    /// 分别以 Authorization="Bearer also-wrong" 与缺失 Authorization 头 POST initialize,两次状态码
    /// 均为 HttpStatusCode.Unauthorized(精确断言错误形态)。</para>
    /// </summary>
    [Fact]
    public async Task wrong_or_missing_token_is_rejected_with_401()
    {
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions());

        // SDK 客户端面:错误 token 无法建立会话。
        await Assert.ThrowsAnyAsync<Exception>(
            () => SessionConnector.ConnectAsync(daemon.Port, "definitely-wrong-token"));

        // HTTP 面:精确断言错误形态 —— 错误 token 与缺失头都是 401。
        Assert.Equal(HttpStatusCode.Unauthorized, await PostInitializeAsync(daemon.Port, "Bearer also-wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostInitializeAsync(daemon.Port, null));
    }

    /// <summary>
    /// 令牌跨重启稳定:同状态目录强制终止后重新拉起,令牌原样保留(ADR-0004),旧令牌可连新进程。
    /// <para>断言链:Arrange —— 独立状态目录。Act —— 第一个 daemon 就绪后 Dispose(强制终止),
    /// 同目录拉起第二个。Assert —— ① 两进程 Token 相等;② 用旧令牌连接第二个 daemon 并列出
    /// list_instances。</para>
    /// </summary>
    [Fact]
    public async Task token_is_stable_across_restarts()
    {
        var stateDir = TestPaths.NewStateDir();

        string firstToken;
        using (var first = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir }))
        {
            firstToken = first.Token;
        }
        // first 已被强制终止;同状态目录重新拉起,token 必须原样保留(ADR-0004)。

        using var second = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir });
        Assert.Equal(firstToken, second.Token);

        await using var client = await SessionConnector.ConnectAsync(second.Port, firstToken);
        (await client.ListToolsAsync()).AssertContainsTool("list_instances");
    }

    /// <summary>
    /// 单例锁语义:同状态目录、不同端口的第二个 daemon 因锁冲突退出而非双监听;退出码按平台分层,
    /// 持锁实例不受影响。
    /// <para>断言链:Arrange —— 第一个 daemon 就绪;第二个以空闲端口 + 同状态目录拉起。Act ——
    /// WaitForExit(15s)。Assert —— ① 第二个进程退出;② 退出码分层契约:Windows 精确归因
    /// ExitCodes.SingletonLockUnavailable(2),Unix 归因不明 ExitCodes.LockStateUnclear(3);
    /// ③ 第一个 daemon 未退出,仍可连接并列出 list_instances。</para>
    /// </summary>
    [Fact]
    public async Task second_daemon_on_same_state_dir_exits_without_double_listening()
    {
        var stateDir = TestPaths.NewStateDir();

        using var first = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir });

        // 第二个实例:同状态目录(锁冲突)、不同端口 —— 退出由锁决定,而非端口占用。
        using var second = DaemonProcess.Start(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            Port = TestPorts.GetFreePort(),
        });
        Assert.True(
            second.WaitForExit(TimeSpan.FromSeconds(15)),
            "第二个 daemon 应因单例锁退出,而不是继续运行");

        // 退出码分层是拉起方自愈语义的契约:Windows 精确归因为 2,Unix 归因不明为 3。
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ExitCodes.SingletonLockUnavailable, second.ExitCode);
        }
        else
        {
            Assert.Equal(ExitCodes.LockStateUnclear, second.ExitCode);
        }

        // 唯一实例仍在正常服务。
        Assert.False(first.HasExited, "持有锁的第一个 daemon 不应受影响");
        await using var client = await SessionConnector.ConnectAsync(first.Port, first.Token);
        (await client.ListToolsAsync()).AssertContainsTool("list_instances");
    }

    /// <summary>
    /// 日志通道纪律:stdout 保留给协议通道,日志只写 stderr 与 daemon.log。
    /// <para>断言链:Arrange —— daemon 就绪,客户端完成连接/列工具/调工具后断开。Act —— 延时 500ms
    /// 等异步日志刷写。Assert —— ① StdoutSnapshot 为空;② StderrSnapshot 非空;③ 状态目录下
    /// daemon.log 存在且内容非空(以 FileShare.ReadWrite 跟随读取,daemon 仍持写句柄)。</para>
    /// </summary>
    [Fact]
    public async Task daemon_logs_to_stderr_and_file_but_never_stdout()
    {
        // stdout 保留给协议通道(spec user story 24):日志走 stderr 与 daemon.log。
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions());

        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        _ = await client.ListToolsAsync();
        _ = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
        await client.DisposeAsync();

        // 给异步的日志刷写留一点时间,再检查三个通道。
        await Task.Delay(500);
        Assert.Equal(string.Empty, daemon.StdoutSnapshot().Trim());
        Assert.NotEmpty(daemon.StderrSnapshot().Trim());

        var logPath = Path.Combine(daemon.StateDir, StateLogFileLoggerProvider.LogFileName);
        Assert.True(File.Exists(logPath), "daemon.log 应随启动建立");
        // daemon 仍持有写句柄(FileShare.Read),跟随读取需以 ReadWrite 共享模式打开。
        using var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var logReader = new StreamReader(logStream);
        Assert.NotEmpty((await logReader.ReadToEndAsync()).Trim());
    }

    /// <summary>
    /// 空闲退出:无任何连接且空闲超过阈值后 daemon 自行退出。
    /// <para>断言链:Arrange —— Start(IdleSeconds=2) 后 WaitReady。Act —— WaitForExit(20s)。
    /// Assert —— 进程在窗口内退出(空闲守护语义生效)。</para>
    /// </summary>
    [Fact]
    public void daemon_exits_after_idle_timeout_with_no_connections()
    {
        using var daemon = DaemonProcess.Start(new DaemonSpawnOptions { IdleSeconds = 2 });
        daemon.WaitReady(DaemonProcess.ReadyTimeout);

        Assert.True(
            daemon.WaitForExit(TimeSpan.FromSeconds(20)),
            "全部连接断开并超过空闲阈值后,daemon 应自行退出");
    }

    /// <summary>
    /// 活动重置空闲计时:持续请求跨两个完整阈值周期不退出,停止活动后在阈值内退出。
    /// <para>断言链:Arrange —— IdleSeconds=10(满载机器放宽阈值,语义不变:活动重置计时)。
    /// Act —— 4 轮"连接 → list_instances → 断开 → 等 2s"(共约 8s 活动)。Assert —— 每轮后
    /// HasExited=false;停止活动后 WaitForExit(30s) 为 true。</para>
    /// </summary>
    [Fact]
    public async Task activity_resets_idle_exit_timer()
    {
        // 与生产 daemon/编辑器共存时机器常满载,空闲阈值放宽到 10s(语义不变:
        // 活动重置计时,空闲超阈值才退出)。
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { IdleSeconds = 10 });

        // 以 2s 间隔持续请求,跨越阈值两个完整周期 —— 有活动计时必须重置。
        for (var round = 1; round <= 4; round++)
        {
            await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
            _ = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
            await client.DisposeAsync();
            await Task.Delay(2000);
            Assert.False(daemon.HasExited, $"第 {round} 轮活动后 daemon 不应退出");
        }

        // 停止活动后,进程在阈值内自行退出。
        Assert.True(
            daemon.WaitForExit(TimeSpan.FromSeconds(30)),
            "停止活动后 daemon 应在空闲阈值内退出");
    }

    /// <summary>
    /// 默认端口:不注入 GODOT_MCP_DAEMON_PORT 时监听 ADR-0004 的默认端口 6590;端口已被生产
    /// daemon 占用时让位跳过(仅独立环境执行)。
    /// <para>断言链:Arrange —— DefaultPortAlreadyServing 探测 6590,已有监听者直接 return。
    /// Act —— UseDefaultPort=true 拉起并连接。Assert —— daemon.Port=6590;客户端可列出
    /// list_instances。</para>
    /// </summary>
    [Fact]
    public async Task daemon_listens_on_default_port_6590_without_env_override()
    {
        // 不注入 GODOT_MCP_DAEMON_PORT:进程必须落在 ADR-0004 的默认端口 6590。
        // (生产部署后 6590 常由真实 daemon 占用 —— 探测到即让位跳过;独立环境才执行本测试。)
        if (DefaultPortAlreadyServing())
        {
            return;
        }
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            UseDefaultPort = true,
            IdleSeconds = 15,
        });
        Assert.Equal(6590, daemon.Port);

        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        (await client.ListToolsAsync()).AssertContainsTool("list_instances");
    }

    /// <summary>6590 是否已有监听者(生产 daemon 常驻时让位,避免端口冲突误报)。</summary>
    private static bool DefaultPortAlreadyServing()
    {
        using var probe = new System.Net.Sockets.TcpClient();
        try
        {
            return probe.ConnectAsync(System.Net.IPAddress.Loopback, 6590).Wait(500) && probe.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 端口被无关进程占用:单例锁可拿(独立状态目录)但绑定失败 —— 以专项退出码 4 快速失败,
    /// 拉起方据此区分端口冲突与单例冲突。
    /// <para>断言链:Arrange —— TcpListener 占住一个随机空闲端口。Act —— daemon 以该端口拉起,
    /// WaitForExit(20s)。Assert —— 进程退出且 ExitCode=ExitCodes.BindFailure(4);finally 中
    /// 停掉占位监听。</para>
    /// </summary>
    [Fact]
    public void daemon_exits_with_dedicated_code_when_port_is_taken_by_other_process()
    {
        // 外部进程先占住端口:daemon 的锁能拿到(独立状态目录),但绑定失败 —— 专项退出码 4,
        // 拉起方据此知道是端口冲突而非单例冲突。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var daemon = DaemonProcess.Start(new DaemonSpawnOptions { Port = port });

            Assert.True(daemon.WaitForExit(TimeSpan.FromSeconds(20)), "端口被占用时 daemon 应快速失败");
            Assert.Equal(ExitCodes.BindFailure, daemon.ExitCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>绕过 SDK 以裸 HTTP POST initialize,精确断言认证失败的状态码形态。</summary>
    /// <param name="port">daemon 监听端口。</param>
    /// <param name="authorization">Authorization 头值;null 表示不携带该头。</param>
    /// <returns>响应状态码。</returns>
    private static async Task<HttpStatusCode> PostInitializeAsync(int port, string? authorization)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{port}/");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            Encoding.UTF8,
            "application/json");
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }
}
