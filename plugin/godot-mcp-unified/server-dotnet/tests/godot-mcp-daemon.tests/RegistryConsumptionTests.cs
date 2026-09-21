using System.Diagnostics;
using System.Text.Json;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 05 验收:daemon 对机器级注册表的只读消费与对 Godot 实例的出站连接——
/// ① 条目写入后自动连接并在 list_instances 可见(无需重启 daemon);
/// ② 进程消失的残留条目经活性判定移出实例表(且只读消费:注册表文件保持原样);
/// ③ daemon 重启后全量重连;④ 实例连接期间不得空闲退出,断开后按期退出(spec US12)。
/// </summary>
public class RegistryConsumptionTests
{
    /// <summary>list_instances 返回行的强类型投影(仅测试关心的字段)。</summary>
    private sealed record InstanceRow(string Path, string Id, int Port, int Pid, string GodotVersion, bool Connected);

    /// <summary>注册表条目写入后 daemon 免重启自动连接实例;断连后无缝改连同项目的新实例。</summary>
    /// <para>
    /// Arrange:经 DaemonProcess 真实拉起 daemon、SessionConnector 接入 SDK 客户端,确认实例表为空;
    /// 起 FakeGodot 假实例写入注册表条目,显式 ack 携带补丁级版本 4.5.1。
    /// Act/Assert:轮询 list_instances 至恰 1 行已连接,断言 path/id/port 等于假实例的
    /// 项目键/哈希/端口,godot_version 取 ack 值("4.5.1")而非注册表配对;随后销毁假实例、
    /// 以同项目路径起新实例(新端口),断言 daemon 重连到新端口且实例表仍只有 1 行。
    /// </para>
    [Fact]
    public async Task instance_written_to_registry_appears_and_connects_without_daemon_restart()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        Assert.Empty(await GetInstancesAsync(client));

        var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\alpha",
            StateDir = stateDir,
            // 显式 ack 携带补丁级版本:连接后 list_instances 应采用 ack 值(优于注册表配对)。
            AuthAckJson = """{"authed":true,"godot_version":"4.5.1","version":"1.0.0","headless":false}""",
        });
        try
        {
            var rows = await WaitUntilAsync(
                () => GetInstancesAsync(client),
                r => r.Count == 1 && r[0].Connected,
                TimeSpan.FromSeconds(20));
            Assert.Equal(fake.ProjectKey, rows[0].Path);
            Assert.Equal(fake.ProjectHash, rows[0].Id);
            Assert.Equal(fake.Port, rows[0].Port);
            Assert.Equal("4.5.1", rows[0].GodotVersion);

            // 断线重连 + 端口重发现:同一项目换一个(新端口)实例,daemon 应无缝改连。
            fake.Dispose();
            using var replacement = FakeGodotInstance.Start(
                new FakeGodotOptions { ProjectPath = @"D:\proj\alpha", StateDir = stateDir });
            var rows2 = await WaitUntilAsync(
                () => GetInstancesAsync(client),
                r => r.Count == 1 && r[0].Connected && r[0].Port == replacement.Port,
                TimeSpan.FromSeconds(30));
            Assert.Single(rows2);
        }
        finally
        {
            fake.Dispose();
        }
    }

    /// <summary>注册表行内数字为 float 形态(真实 addon 经 Godot JSON 写盘)时仍宽容解析为整数。</summary>
    /// <para>
    /// Arrange:手写 projects.json,port/pid 写成 6550.0/41268.0 的 float 形态。
    /// Act:RegistryReader.Read 读出条目。
    /// Assert:恰 1 条,键为 "d:/proj/real",port/pid 解析为整数 6550/41268,godot_version 为 "4.7"。
    /// </para>
    [Fact]
    public void registry_rows_in_real_godot_float_form_are_parsed()
    {
        // 真实 addon 经 Godot JSON 写盘,数字为 float 形态(如 6550.0)——必须宽容解析。
        var stateDir = TestPaths.NewStateDir();
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(
            Path.Combine(stateDir, "projects.json"),
            """{"by_path":{"d:/proj/real":{"port":6550.0,"pid":41268.0,"token_path":"C:/x/project_instance_0123456789ab/mcp_token","godot_version":"4.7"}}}""");

        var entries = RegistryReader.Read(stateDir);
        var entry = Assert.Single(entries);
        Assert.Equal("d:/proj/real", entry.Key);
        Assert.Equal(6550, entry.Port);
        Assert.Equal(41268, entry.Pid);
        Assert.Equal("4.7", entry.GodotVersion);
    }

    /// <summary>残留条目(进程已消失)经活性判定移出实例表,且注册表文件保持原样(只读消费)。</summary>
    /// <para>
    /// Arrange:用 FindDeadPid 造必不存在的 pid,经 FakeGodotRegistry 写入 zombie 条目与 token 文件;
    /// 另起 healthy 假实例,再拉起 daemon 并接入 SDK 客户端。
    /// Act:轮询 list_instances。
    /// Assert:恰 1 行且为 healthy 已连接,zombie 的规范化项目路径不在表内;
    /// entries/&lt;hash&gt;.json 仍存在、projects.json 的 by_path 仍含 zombie 键 —— daemon 绝不写/删注册表。
    /// </para>
    [Fact]
    public async Task stale_entry_with_dead_pid_is_removed_from_instance_table()
    {
        var stateDir = TestPaths.NewStateDir();

        // 造一个"进程已消失"的 pid:取一个当前必然不存在的 pid —— 场景本义就是
        // "条目残留而进程不再存在",无需真的拉起再杀掉一个进程(也避开安全扫描器对
        // Process.Start 的过度告警)。
        var deadPid = FindDeadPid();

        var zombieKey = @"D:\proj\zombie";
        var zombieHash = JsonMatch.ProjectHashOf(JsonMatch.CanonicalProjectKey(zombieKey));
        var tokenPath = FakeGodotRegistry.WriteTokenFile(stateDir, zombieHash, "zombie-token");
        FakeGodotRegistry.WriteEntry(stateDir, zombieKey, TestPorts.GetFreePort(), tokenPath, pid: deadPid);

        using var healthy = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = @"D:\proj\healthy", StateDir = stateDir });
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        var rows = await WaitUntilAsync(
            () => GetInstancesAsync(client),
            r => r.Count == 1 && r[0].Path == healthy.ProjectKey && r[0].Connected,
            TimeSpan.FromSeconds(20));

        Assert.Single(rows);
        Assert.DoesNotContain(rows, r => r.Path == JsonMatch.CanonicalProjectKey(zombieKey));
        // 只读消费:残留条目文件与聚合视图保持原样(daemon 绝不写/删注册表)。
        Assert.True(
            File.Exists(Path.Combine(stateDir, "entries", zombieHash + ".json")),
            "注册表条目文件应保持原样");
        using var projects = JsonDocument.Parse(File.ReadAllText(Path.Combine(stateDir, "projects.json")));
        Assert.True(projects.RootElement.GetProperty("by_path").TryGetProperty(JsonMatch.CanonicalProjectKey(zombieKey), out _));
    }

    /// <summary>daemon 重启后对仍存活的实例全量重连。</summary>
    /// <para>
    /// Arrange:先起 FakeGodot 假实例,再拉起第一个 daemon 并接入,轮询确认项目 persist 已连接。
    /// Act:强制终止第一个 daemon(锁随之释放),在同一状态目录重启第二个 daemon 并重新接入。
    /// Assert:第二次轮询到恰 1 行已连接,端口等于假实例端口 —— 注册表数据跨重启生效。
    /// </para>
    [Fact]
    public async Task daemon_restart_reconnects_all_active_instances()
    {
        var stateDir = TestPaths.NewStateDir();
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = @"D:\proj\persist", StateDir = stateDir });

        using (var first = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 }))
        {
            await using var client1 = await SessionConnector.ConnectAsync(first.Port, first.Token);
            var rows = await WaitUntilAsync(
                () => GetInstancesAsync(client1),
                r => r.Count == 1 && r[0].Connected,
                TimeSpan.FromSeconds(20));
            Assert.Equal(fake.ProjectKey, rows[0].Path);
        }

        // first 已被强制终止(锁释放);同状态目录重启 → 启动后全量重连。
        using var second = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client2 = await SessionConnector.ConnectAsync(second.Port, second.Token);
        var rows2 = await WaitUntilAsync(
            () => GetInstancesAsync(client2),
            r => r.Count == 1 && r[0].Connected,
            TimeSpan.FromSeconds(20));
        Assert.Equal(fake.Port, rows2[0].Port);
    }

    /// <summary>实例连接期间抑制空闲退出,断开后按空闲阈值自行退出(spec US12)。</summary>
    /// <para>
    /// Arrange:以 IdleSeconds=5 拉起 daemon 并等就绪,起 FakeGodot 假实例,接入 SDK 客户端后
    /// 轮询至实例已连接。Assert 前半:空等 7 秒(越过空闲阈值)daemon 仍未退出;
    /// Act:断开 SDK 客户端并清理假实例。Assert 后半:daemon 在 15 秒内自行退出且退出码为 0。
    /// </para>
    [Fact]
    public async Task connected_instance_prevents_idle_exit_until_disconnected()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.Start(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 5 });
        daemon.WaitReady(DaemonProcess.ReadyTimeout);

        var fake = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = @"D:\proj\keepalive", StateDir = stateDir });
        try
        {
            await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.WaitForToken());
            await WaitUntilAsync(
                () => GetInstancesAsync(client),
                r => r.Count == 1 && r[0].Connected,
                TimeSpan.FromSeconds(20));

            // 空闲阈值(5s)已过仍存活 —— 实例连接抑制空闲退出(spec US12)。
            await Task.Delay(TimeSpan.FromMilliseconds(7000));
            Assert.False(daemon.HasExited, "实例连接期间 daemon 不得空闲退出");
            await client.DisposeAsync();
        }
        finally
        {
            fake.Dispose();
        }

        Assert.True(daemon.WaitForExit(TimeSpan.FromSeconds(15)), "实例断开后应在空闲阈值内自行退出");
        Assert.Equal(0, daemon.ExitCode);
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>找一个当前不存在的 pid(高值区间,远离真实分配范围)。</summary>
    private static int FindDeadPid()
    {
        for (var candidate = 1_000_000_000; candidate < 1_000_010_000; candidate += 7)
        {
            try
            {
                using var process = Process.GetProcessById(candidate);
            }
            catch (ArgumentException)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未能找到一个不存在的 pid");
    }

    /// <summary>调用 list_instances 并把 instances 数组解析为 InstanceRow 列表。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <returns>实例行列表(每行含 path/id/port/pid/godot_version/connected)。</returns>
    private static async Task<List<InstanceRow>> GetInstancesAsync(McpClient client)
    {
        var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var doc = JsonDocument.Parse(text);
        var rows = new List<InstanceRow>();
        foreach (var el in doc.RootElement.GetProperty("instances").EnumerateArray())
        {
            rows.Add(new InstanceRow(
                el.GetProperty("path").GetString()!,
                el.GetProperty("id").GetString()!,
                el.GetProperty("port").GetInt32(),
                el.GetProperty("pid").GetInt32(),
                el.GetProperty("godot_version").GetString()!,
                el.GetProperty("connected").GetBoolean()));
        }
        return rows;
    }

    /// <summary>轮询探针直至谓词满足或超时;每轮间隔 250ms。</summary>
    /// <param name="probe">每轮执行的异步探针(如读 list_instances)。</param>
    /// <param name="predicate">成功条件。</param>
    /// <param name="timeout">总时限;超时抛 TimeoutException 并附最后一次观察的 JSON。</param>
    /// <returns>使谓词为真的那次探针结果。</returns>
    private static async Task<T> WaitUntilAsync<T>(Func<Task<T>> probe, Func<T, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        T last = default!;
        while (DateTime.UtcNow < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException(
            $"条件在 {timeout.TotalSeconds}s 内未满足;最后一次观察:{JsonSerializer.Serialize(last)}");
    }

    /// <summary>
    /// issue 20:注册表残留死条目不得导致"每秒重建连接 → 判死 → 移出"的日志无限循环。
    /// <para>
    /// Arrange:写入一条 pid 已消失的条目(死身份)+ 一个健康实例,拉起 daemon 并接入。
    /// Act:等待死条目被首次移出(健康实例连接就绪即为信号),随后静置若干秒让 watch/轮询跑多轮。
    /// Assert:① 死条目不在实例表(既有语义不变);② daemon.log 中"移出实例表"对该死条目
    /// 恰出现一次 —— 修复前每个 reconcile 周期都会新增一条,静置期会累积数十条;
    /// ③ 注册表条目文件保持原样(只读消费语义不变)。
    /// </para>
    /// </summary>
    [Fact]
    public async Task dead_registry_entry_is_suppressed_instead_of_log_looping()
    {
        var stateDir = TestPaths.NewStateDir();
        var zombieKey = @"D:\proj\zombie-loop";
        var zombieHash = JsonMatch.ProjectHashOf(JsonMatch.CanonicalProjectKey(zombieKey));
        var tokenPath = FakeGodotRegistry.WriteTokenFile(stateDir, zombieHash, "zombie-token");
        FakeGodotRegistry.WriteEntry(stateDir, zombieKey, TestPorts.GetFreePort(), tokenPath, pid: FindDeadPid());

        using var healthy = FakeGodotInstance.Start(new FakeGodotOptions { ProjectPath = @"D:\proj\healthy-loop", StateDir = stateDir });
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        var rows = await WaitUntilAsync(
            () => GetInstancesAsync(client),
            r => r.Count == 1 && r[0].Path == healthy.ProjectKey && r[0].Connected,
            TimeSpan.FromSeconds(20));
        Assert.Single(rows);

        // 静置:让 1s 轮询与文件监视跑多轮(修复前每轮都会刷一条移出日志)。
        await Task.Delay(TimeSpan.FromSeconds(6));

        var logPath = Path.Combine(stateDir, "daemon.log");
        Assert.True(File.Exists(logPath), $"状态目录应有 daemon.log:{logPath}");
        var logText = ReadFileShared(logPath);
        var occurrences = logText.Split('\n')
            .Count(line => line.Contains("移出实例表", StringComparison.Ordinal)
                && line.Contains(JsonMatch.CanonicalProjectKey(zombieKey), StringComparison.Ordinal));
        Assert.Equal(1, occurrences);

        // 只读消费:残留条目文件保持原样(daemon 绝不写/删注册表)。
        Assert.True(
            File.Exists(Path.Combine(stateDir, "entries", zombieHash + ".json")),
            "注册表条目文件应保持原样");
    }

    /// <summary>
    /// issue 20 的自愈护栏:同一项目键在编辑器重启后(新 pid/started_at → 新身份)必须重新连接,
    /// 不能被死条目抑制误伤。
    /// <para>
    /// Arrange:写死条目让 daemon 抑制它(同 key 旧身份);随后以同项目路径起真实假实例
    /// (新 pid/started_at → 新身份,注册表条目被覆盖)。
    /// Act:轮询 list_instances。
    /// Assert:新实例在期限内连上且实例表恰 1 行 —— 证明抑制以身份为粒度而非以 key 为粒度。
    /// </para>
    /// </summary>
    [Fact]
    public async Task restarted_instance_with_same_key_reconnects_after_suppression()
    {
        var stateDir = TestPaths.NewStateDir();
        var projectPath = @"D:\proj\revive";
        var hash = JsonMatch.ProjectHashOf(JsonMatch.CanonicalProjectKey(projectPath));
        var tokenPath = FakeGodotRegistry.WriteTokenFile(stateDir, hash, "revive-token");
        // 旧身份:pid 已消失。
        FakeGodotRegistry.WriteEntry(stateDir, projectPath, TestPorts.GetFreePort(), tokenPath, pid: FindDeadPid());

        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        // 等死条目被消费(实例表为空,因为唯一条目是死的)。
        await WaitUntilAsync(
            () => GetInstancesAsync(client),
            r => r.Count == 0,
            TimeSpan.FromSeconds(20));

        // 编辑器重启:同 key、新身份(新端口 + 真实存活进程)。
        using var revived = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = projectPath,
            StateDir = stateDir,
        });
        var rows = await WaitUntilAsync(
            () => GetInstancesAsync(client),
            r => r.Count == 1 && r[0].Connected && r[0].Port == revived.Port,
            TimeSpan.FromSeconds(30));
        Assert.Equal(revived.ProjectKey, rows[0].Path);
    }

    /// <summary>以共享读方式读取文件(daemon 持有日志句柄时不阻塞)。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件全文。</returns>
    private static string ReadFileShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
