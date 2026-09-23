using System.Text.Json;
using GodotMcp.Daemon.Tools;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// OS 截图兜底(OsWindowCapture)纯函数单元测试:触发判定、信封构建、文件命名。
/// 窗口枚举与 PrintWindow 路径需要真实窗口,由 capture_screenshot 的失败链路
/// 手动冒烟覆盖;此处只锚定零依赖的判定与塑形逻辑。
/// </summary>
public class OsWindowCaptureTests
{
    /// <summary>触发判定:可恢复错误码 + 空 node_path + Windows 才尝试兜底。</summary>
    /// <param name="code">原始错误码。</param>
    /// <param name="nodePath">node_path 参数。</param>
    /// <param name="isWindows">平台门控。</param>
    /// <param name="expected">期望的判定结果。</param>
    [Theory]
    [InlineData("TIMEOUT", null, true, true)]
    [InlineData("DISCONNECTED", "", true, true)]
    [InlineData("EDITOR_VIEWPORT_UNAVAILABLE", null, true, true)]
    [InlineData("RUNTIME_WINDOW_MINIMIZED", null, true, true)]
    [InlineData("GAME_NOT_RUNNING", null, true, false)] // 游戏未运行:窗口必然不存在,兜底无意义。
    [InlineData("INVALID_PARAMS", null, true, false)]
    [InlineData("TIMEOUT", "res://Main/Player", true, false)] // node 框选必须由引擎执行。
    [InlineData("TIMEOUT", null, false, false)] // 非 Windows:GDI 不可达。
    public void should_attempt_follows_code_nodepath_and_platform_gate(
        string code, string? nodePath, bool isWindows, bool expected)
    {
        Assert.Equal(expected, OsWindowCapture.ShouldAttempt(isWindows, nodePath, code));
    }

    /// <summary>信封构建:磁盘载荷形态 + 捕获来源/原始错误码披露,hint 含路径与码。</summary>
    [Fact]
    public void build_envelope_discloses_path_provenance_and_original_code()
    {
        var shot = new OsWindowShot(@"C:\capture.png", 1920, 1080, 12345);
        var json = OsWindowCapture.BuildEnvelope(shot, "TIMEOUT", "editor");
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        Assert.Equal(@"C:\capture.png", root.GetProperty("path").GetString());
        Assert.Equal(1920, root.GetProperty("width").GetInt32());
        Assert.Equal(1080, root.GetProperty("height").GetInt32());
        Assert.Equal(12345, root.GetProperty("bytes").GetInt32());
        Assert.Equal("image/png", root.GetProperty("mime_type").GetString());
        Assert.Equal("1920x1080", root.GetProperty("returned").GetString());
        Assert.Equal("os_window", root.GetProperty("capture_source").GetString());
        Assert.Equal("TIMEOUT", root.GetProperty("original_code").GetString());
        var hint = root.GetProperty("hint").GetString();
        Assert.Contains("PrintWindow", hint, StringComparison.Ordinal);
        Assert.Contains(@"C:\capture.png", hint, StringComparison.Ordinal);
        // 语义差异必须披露:全窗口内容而非孤立视口,node_path 不可用。
        Assert.Contains("node_path", hint, StringComparison.Ordinal);
    }

    /// <summary>文件命名:标签 + 毫秒时间戳形态,保证可读且基本唯一。</summary>
    [Fact]
    public void build_file_name_uses_tag_and_millisecond_timestamp()
    {
        var name = OsWindowCapture.BuildFileName("editor", new DateTime(2026, 9, 23, 12, 34, 56, 789));
        Assert.Equal("editor-20260923-123456789.png", name);
    }

    /// <summary>落盘目录固定在 %LOCALAPPDATA%\godot-mcp-daemon\screenshots(daemon 自有产物)。</summary>
    [Fact]
    public void screenshots_dir_lives_under_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "godot-mcp-daemon", "screenshots");
        Assert.Equal(expected, OsWindowCapture.ScreenshotsDir());
    }
}
