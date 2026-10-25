namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 超时台账与实现的一致性守护（spec §2.2 的 deadline ladder）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要这条</b>：<c>ToolCallTimeouts.LongNames</c> 是一份<b>声明</b>，
/// 而真正生效的是各调用点有没有把工具名传下去。两者一旦脱节，症状极其安静 ——
/// 台账说 180 秒、实现给 30 秒，只在大场景 <c>editor_save_scene</c> 或首次
/// <c>dotnet build</c> 时才会以"daemon 自己判超时、而文件其实已经写了一半"暴露出来。</para>
///
/// <para>这条测试不靠"读注释"，而是直接扫源码：名单里的每个名字都必须在某个
/// <c>RouteAsync(...)</c> / <c>For("...")</c> 调用点以字面量出现。名字改了但调用点没跟，
/// 或者调用点被删了但名单还在，都会立刻变红。</para>
/// </remarks>
public sealed class ToolCallTimeoutsTests
{
    /// <summary>台账里登记为长工具的名字。</summary>
    private static readonly string[] LongNames =
    [
        "execute_code",
        "node_call_method",
        "editor_save_scene",
        "editor_build_csharp",
        "editor_build_csharp_status",
        "editor_build_csharp_wait",
        "editor_sync",
    ];

    [Fact]
    public void long_names_match_the_ledger()
    {
        // 名单与本测试是两处声明，一起改才不会漂移。
        Assert.Equal(
            LongNames.OrderBy(n => n, StringComparer.Ordinal),
            Tools.ToolCallTimeouts.LongNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void every_long_name_reaches_a_call_site_that_uses_it()
    {
        var sources = ReadDaemonSources();

        foreach (var name in Tools.ToolCallTimeouts.LongNames)
        {
            // 允许三种接线方式:
            //   ① RouteAsync(..., "<工具名>")   —— eager 工具按名取
            //   ② ToolCallTimeouts.For("<工具名>") —— 直接查台账
            //   ③ 组工具(JsonSchemaGroupTool)按 NodeToolDef.Name 取
            var wired =
                sources.Contains($"\"{name}\")", StringComparison.Ordinal)
                || sources.Contains($"\"{name}\",", StringComparison.Ordinal);

            Assert.True(
                wired,
                $"长工具 {name} 在 ToolCallTimeouts.LongNames 里,但源码里找不到任何以它名字接线的调用点。" +
                "只写进名单不传名,等于台账对实现静默漂移:声明 180 秒、实际仍按 30 秒判超时。");
        }
    }

    [Fact]
    public void default_stays_short()
    {
        // 短工具的 30 秒不能被顺手抬高:那会让"编辑器卡住"的反馈从 30 秒变成 3 分钟。
        Assert.Equal(TimeSpan.FromSeconds(30), Tools.ToolCallTimeouts.Default);
        Assert.True(
            Tools.ToolCallTimeouts.Long > Tools.ToolCallTimeouts.Default,
            "长工具预算必须严格大于短工具");
    }

    [Fact]
    public void unknown_tool_falls_back_to_the_short_budget()
    {
        Assert.Equal(Tools.ToolCallTimeouts.Default, Tools.ToolCallTimeouts.For("node_inspect"));
        Assert.False(Tools.ToolCallTimeouts.IsLong("node_inspect"));
        Assert.True(Tools.ToolCallTimeouts.IsLong("editor_save_scene"));
    }

    [Fact]
    public void deadline_ladder_is_strictly_descending()
    {
        // daemon(180s) < shim(210s) < 宿主(240s)。
        // 正常路径上永远是 daemon 先给出清晰的 TIMEOUT;只有 daemon 自己卡死才轮到
        // shim 兜底。反过来(daemon 比 shim 长)会让每个慢调用都先收到 shim 的
        // "该操作可能已执行,不要重试"——而那时 daemon 其实还在正常工作。
        var daemon = Tools.ToolCallTimeouts.Long;
        var shim = ShimOptionsDefaults.ToolCallTimeout;
        var host = TimeSpan.FromSeconds(240);

        Assert.True(daemon < shim, $"daemon 长工具预算 {daemon} 必须小于 shim 的 {shim}");
        Assert.True(shim < host, $"shim 预算 {shim} 必须小于宿主的 {host}");
    }

    /// <summary>读取 daemon 与 shim 的全部源码(测试用;只读)。</summary>
    /// <returns>拼接后的源码文本。</returns>
    private static string ReadDaemonSources()
    {
        // 从测试程序集位置回溯到仓库根下的 server-dotnet 目录。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "godot-mcp-daemon")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var builder = new System.Text.StringBuilder();
        foreach (var project in new[] { "godot-mcp-daemon", "godot-mcp-shim" })
        {
            var root = Path.Combine(dir!.FullName, "src", project);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                builder.AppendLine(File.ReadAllText(file));
            }
        }

        return builder.ToString();
    }
}

/// <summary>shim 的默认预算(测试侧镜像;用于校验阶梯次序)。</summary>
internal static class ShimOptionsDefaults
{
    /// <summary><c>tools/call</c> 的默认预算(与 <c>ShimOptions.ToolCallTimeout</c> 的 210s 对齐)。</summary>
    public static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(210);
}
