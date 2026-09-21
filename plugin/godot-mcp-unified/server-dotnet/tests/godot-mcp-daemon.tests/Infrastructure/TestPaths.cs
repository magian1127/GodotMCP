namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>定位被测 daemon 产物的仓库内路径(相对解析,不绑定本机绝对路径)。</summary>
internal static class TestPaths
{
    /// <summary>解决方案目录(含 godot-mcp-daemon.slnx,静态初始化时向上定位一次)。</summary>
    public static string SolutionDir { get; } = FindSolutionDir();

    /// <summary>daemon 主程序集。dotnet test 会先构建 ProjectReference,因此该产物必然存在。</summary>
    public static string DaemonDll { get; } = FindProjectDll("godot-mcp-daemon");

    /// <summary>shim 程序集(stdio ↔ HTTP 自举器,dotnet exec 运行)。</summary>
    public static string ShimDll { get; } = FindProjectDll("godot-mcp-shim");

    /// <summary>02 号产出的 wire fixture 语料(19 号退役后由 daemon 测试树持有)。</summary>
    public static string WireFixturesDir { get; } =
        Path.Combine(SolutionDir, "tests", "godot-mcp-daemon.tests", "fixtures", "wire");

    /// <summary>全新的一次性 daemon 状态目录(单例锁与 token 互不干扰)。</summary>
    public static string NewStateDir()
    {
        return Path.Combine(Path.GetTempPath(), "godot-mcp-daemon-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>自测试输出目录逐级向上查找 godot-mcp-daemon.slnx;找不到即抛(非仓库内构建)。</summary>
    /// <returns>解决方案目录绝对路径。</returns>
    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "godot-mcp-daemon.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "无法从测试输出目录向上定位 godot-mcp-daemon.slnx;请从仓库内正常构建运行测试。");
    }

    /// <summary>在 src/&lt;project&gt;/bin 下按 Debug → Release 顺序查找 net10.0 产物 dll。</summary>
    /// <param name="projectName">项目名(与 dll 同名)。</param>
    /// <returns>存在的 dll 绝对路径;两配置皆无则抛出(提示先构建)。</returns>
    private static string FindProjectDll(string projectName)
    {
        var projectBin = Path.Combine(SolutionDir, "src", projectName, "bin");
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(projectBin, configuration, "net10.0", projectName + ".dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"未找到 {projectName}.dll(已尝试 Debug/Release 于 {projectBin})。");
    }
}
