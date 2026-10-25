using System.Globalization;
using System.Text.Json;

namespace GodotMcp.Daemon;

/// <summary>
/// <c>--status</c>：只读打印 daemon 现状（spec §10）。
/// </summary>
/// <remarks>
/// <para><b>为什么不连 HTTP 而是读文件</b>：这个命令的典型使用时刻是"连不上"。
/// 若它自己也要经 HTTP + Bearer 调 daemon，那么 token 丢了、端口被占、daemon 卡死
/// 这三种最需要它的场景全都用不了。读 <c>sessions.json</c> + 状态目录里的几个文件，
/// 在任何情况下都能给出答案。</para>
///
/// <para><b>为什么不取单例锁</b>：取锁就等于"daemon 没在跑才能查"，而用户想问的
/// 恰恰是"正在跑的那个现在怎么样"。锁只由运行中的 daemon 持有，本命令只读，不必介入。</para>
///
/// <para><b>输出格式</b>：稳定、可 grep 的行式文本，而不是 JSON —— 它给人看；
/// 要机器可读请直接读 <c>state/sessions.json</c>。</para>
/// </remarks>
internal static class DaemonStatus
{
    /// <summary>打印现状。</summary>
    /// <param name="options">有效运行参数（决定状态目录与端口）。</param>
    /// <returns>进程退出码：0 表示状态正常；1 表示发现需要处理的问题。</returns>
    public static int Print(DaemonOptions options)
    {
        var dir = options.StateDir;
        Console.Out.WriteLine("godot-mcp-daemon 状态");
        Console.Out.WriteLine($"  版本            : {typeof(DaemonStatus).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
        Console.Out.WriteLine($"  状态目录        : {dir}");
        Console.Out.WriteLine($"  监听端口        : {options.Port}");
        Console.Out.WriteLine($"  空闲自退        : {(options.IdleExitEnabled ? $"{options.IdleTimeout.TotalSeconds:0.#}s" : "禁用（常驻）")}");
        Console.Out.WriteLine($"  单例锁          : {DescribeLock(Path.Combine(dir, DaemonState.LockFileName))}");
        Console.Out.WriteLine($"  认证 token      : {DescribeToken(Path.Combine(dir, DaemonToken.TokenFileName))}");

        if (!Directory.Exists(dir))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("状态目录不存在 —— daemon 从未在本机启动过。");
            return 1;
        }

        var sessionsFile = Path.Combine(dir, "sessions.json");
        if (!File.Exists(sessionsFile))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("sessions.json 不存在 —— daemon 未运行，或运行的是不支持快照的旧版本。");
            return 1;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sessionsFile));
            var root = document.RootElement;
            Console.Out.WriteLine($"  快照时间        : {ReadString(root, "generatedAt")}");

            if (root.TryGetProperty("policy", out var policy))
            {
                Console.Out.WriteLine(
                    "  过期策略        : "
                    + $"grace={ReadDouble(policy, "graceSeconds")}s "
                    + $"noStream={ReadDouble(policy, "noStreamTtlSeconds")}s "
                    + $"connected={ReadDouble(policy, "connectedTtlSeconds")}s "
                    + $"maxKeys={policy.GetProperty("maxSessionKeys").GetInt32()}");
            }

            if (!root.TryGetProperty("sessions", out var sessions) || sessions.GetArrayLength() == 0)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("当前没有活动会话。客户端连接后会看到 godot_connect，连接项目后展开为 core 9 + 按需工具组。");
                return 0;
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine($"活动会话（{sessions.GetArrayLength()}）:");
            foreach (var session in sessions.EnumerateArray())
            {
                var key = ReadString(session, "key");
                var state = ReadString(session, "state");
                var connected = session.TryGetProperty("connected", out var c) && c.GetBoolean();
                var groups = session.TryGetProperty("groups", out var g) && g.GetArrayLength() > 0
                    ? string.Join(",", g.EnumerateArray().Select(x => x.GetString()))
                    : "(none)";
                var target = ReadString(session, "defaultTarget") ?? "(未设置)";
                var shim = ReadString(session, "shimVersion") ?? "(旧 shim 未上报)";

                Console.Out.WriteLine($"  [{key}] {state}{(connected ? " · 已连接" : " · 未连接")}");
                Console.Out.WriteLine($"      工具面        : {(connected ? "core 9 + 已激活组" : "仅 godot_connect")}");
                Console.Out.WriteLine($"      已激活组      : {groups}");
                Console.Out.WriteLine($"      默认目标      : {target}");
                Console.Out.WriteLine($"      流/在途       : {session.GetProperty("streams").GetInt32()} / {session.GetProperty("inFlight").GetInt32()}");
                Console.Out.WriteLine($"      shim 版本     : {shim}");
            }

            // "未连接"与"旧 shim"是两类最常见的问题，单独提示，避免用户对着数据自己猜。
            var hasUnconnected = sessions.EnumerateArray()
                .Any(s => !(s.TryGetProperty("connected", out var c) && c.GetBoolean()));
            var hasOldShim = sessions.EnumerateArray()
                .Any(s => ReadString(s, "shimVersion") is null);

            if (hasUnconnected || hasOldShim)
            {
                Console.Out.WriteLine();
                if (hasUnconnected)
                {
                    Console.Out.WriteLine("提示：有会话尚未连接项目 —— 它们只会看到 godot_connect。让模型调用 godot_connect 即可展开工具面。");
                }

                if (hasOldShim)
                {
                    Console.Out.WriteLine("提示：有会话的 shim 未上报版本 —— 可能是升级混跑。重启客户端会话以加载新 shim。");
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"读取 sessions.json 失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>描述单例锁文件的状态。</summary>
    /// <param name="path">锁文件路径。</param>
    /// <returns>一行说明。</returns>
    private static string DescribeLock(string path)
    {
        if (!File.Exists(path))
        {
            return "未创建（daemon 从未启动）";
        }

        try
        {
            // 锁文件能否被独占打开，是"daemon 在不在跑"的直接判据。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return "存在但无人持有（daemon 未运行）";
        }
        catch (IOException)
        {
            return "已被持有（daemon 正在运行）";
        }
        catch (UnauthorizedAccessException)
        {
            return "存在（无权限探测持有状态）";
        }
    }

    /// <summary>描述 token 文件的状态（<b>不打印内容</b>）。</summary>
    /// <param name="path">token 文件路径。</param>
    /// <returns>一行说明。</returns>
    private static string DescribeToken(string path)
    {
        if (!File.Exists(path))
        {
            return "未创建";
        }

        try
        {
            var info = new FileInfo(path);
            return $"已创建（{info.Length} 字节，内容不显示）";
        }
        catch (IOException)
        {
            return "已创建（无法读取大小）";
        }
    }

    /// <summary>读取字符串属性；缺失或 null 时返回 null。</summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>字符串值或 null。</returns>
    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>读取数字属性（用于策略秒数）。</summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>格式化后的数字；缺失时为 "?"。</returns>
    private static string ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number.ToString("0.#", CultureInfo.InvariantCulture)
            : "?";
}
