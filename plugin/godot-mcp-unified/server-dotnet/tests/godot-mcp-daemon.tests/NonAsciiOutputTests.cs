using System.Text.Json;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tests.Infrastructure;
using GodotMcp.Daemon.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// daemon 重建 JSON 时的非 ASCII 保真回归(实测线上症:中文项目路径到达模型时是
/// 字面量 \uXXXX 转义文本)。
/// <para>缺陷机理:System.Text.Json 的默认 encoder 把每个非 ASCII 字符转成 \uXXXX,
/// 而 Node/GDScript 的 JSON.stringify 不转义。daemon 从 Node 桥移植时,
/// 「重新序列化」路径(JsonNode.ToJsonString / JsonSerializer.Serialize)因此静默改变了
/// 线上形态:模型读到 <c>\u91CD\u751F...</c> 而非 <c>重生...</c> —— 每个汉字从 1 字符
/// 涨到 6 字符(数倍放大词元占用),且模型拿到的路径无法直接用于读文件。</para>
/// <para>本文件钉住 <see cref="ToolResults.Output"/> 的 encoder 选择。断言必须落在
/// <b>原始文本</b>上:先 JsonDocument.Parse 再取值的写法会解码 \uXXXX,
/// 修复前后都通过,无法充当回归护栏。</para>
/// </summary>
public class NonAsciiOutputTests
{
    /// <summary>含中文的项目路径(缺陷报告中的真实形态)。</summary>
    private const string ChineseProjectPath = @"D:\proj\重生之战之秒";

    /// <summary>路径中「重生」的转义形态 —— 出现即表示 encoder 回退到了默认行为。</summary>
    private const string EscapedMarker = @"\u91CD";

    /// <summary>
    /// 错误信封非 ASCII 保真:daemon 侧重建的错误负载(全部工具的失败路径都经此)不得把
    /// 中文错误消息转义。直接单测 ToolResults.Error,不经 daemon 进程。
    /// </summary>
    [Fact]
    public void error_envelope_keeps_non_ascii_verbatim()
    {
        var result = ToolResults.Error("INVALID_PARAMS", "节点不存在：根/玩家", hint: "改用 node_path");

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.Contains("节点不存在：根/玩家", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u8282", text, StringComparison.Ordinal);
        // 信封形状不变:小驼峰键仍生效(encoder 修复不得回退命名策略)。
        Assert.Contains("\"success\":false", text, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"INVALID_PARAMS\"", text, StringComparison.Ordinal);
        Assert.Contains("\"hint\":", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 错误信封中的非 ASCII <b>路径</b>同样保真:多实例歧义场景把候选实例路径带进 instances,
    /// 中文项目路径必须原样可达。
    /// </summary>
    [Fact]
    public void error_envelope_keeps_non_ascii_paths_verbatim()
    {
        var result = ToolResults.Error(
            "AMBIGUOUS_INSTANCE",
            "multiple instances connected",
            instances:
            [
                new InstanceSummary(ChineseProjectPath, "abc123def456", 9080, true),
            ]);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        // 反斜杠是 JSON 必需字符,任何 encoder 都会转义成 \\ —— 按 JSON 转义形态断言;
        // 中文本身必须原样出现(这才是本测试的护栏目标)。
        Assert.Contains(@"D:\\proj\\重生之战之秒", text, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapedMarker, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// list_instances 端到端非 ASCII 保真:中文项目路径经注册表 → 实例表 →
    /// InstanceManager.ListInstancesJson(JsonSerializer.Serialize + ToolResults.Output)→
    /// MCP 会话回到调用方,模型读到的必须是真中文。
    /// <para>断言链:Arrange —— daemon+会话;编辑器替身以中文项目路径注册。
    /// Act —— list_instances。Assert —— ① 原始文本含中文路径且不含 \u 转义标记;
    /// ② 解析后路径等于规范化键(snake_case 键不受命名策略影响,形状不变)。</para>
    /// </summary>
    [Fact]
    public async Task list_instances_keeps_non_ascii_project_path_verbatim()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ChineseProjectPath,
            StateDir = stateDir,
        });
        await WaitConnectedAsync(client, ChineseProjectPath);

        var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
        var text = TextOf(result);

        // 原始文本护栏:转义形态出现即回归。
        Assert.Contains("重生之战之秒", text, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapedMarker, text, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(text);
        var canonical = JsonMatch.CanonicalProjectKey(ChineseProjectPath);
        Assert.Contains(
            payload.RootElement.GetProperty("instances").EnumerateArray(),
            i => i.GetProperty("path").GetString() == canonical && i.GetProperty("connected").GetBoolean());
    }

    /// <summary>
    /// capture_screenshot 磁盘信封非 ASCII 保真:这是缺陷报告的直接场景 ——
    /// BuildScreenshotResult 重建信封(JsonObject → ToJsonString)时不得转义中文保存路径,
    /// 否则模型拿到的是无法直接读文件的字面量转义文本。
    /// <para>断言链:Arrange —— daemon+会话;编辑器与运行时替身以中文项目路径注册,
    /// runtime.screenshot 回帧携带含中文的磁盘形态 path。Act —— capture_screenshot(target=runtime)
    /// (GAME_NOT_RUNNING 轮询重试)。Assert —— ① 纯文本信封无图像块;② 原始文本含中文路径
    /// 且不含 \u 转义标记;③ 解析后 path 与替身回帧一致。</para>
    /// </summary>
    [Fact]
    public async Task screenshot_disk_envelope_keeps_non_ascii_path_verbatim()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        // 占位符路径(发布前检查:仓库内不得出现本机绝对路径)。
        const string shotPath = @"C:\Users\<user>\AppData\Roaming\godot\app_userdata\重生之战之秒\screenshots\after-restart.png";

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ChineseProjectPath,
            StateDir = stateDir,
        });
        using var runtime = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ChineseProjectPath,
            StateDir = null,
            Token = editor.Token,
            AuthAckJson = """{"authed":true}""",
            Scripts =
            {
                new FakeScript
                {
                    Method = "runtime.screenshot",
                    Reusable = true,
                    Frames =
                    {
                        new FakeFrame { Json = ResultFrame(
                            $$"""{"success":true,"path":"{{shotPath.Replace(@"\", @"\\")}}","width":1920,"height":1080,"bytes":1615667,"mime_type":"image/png","returned":"1920x1080"}""") },
                    },
                },
            },
        });
        await WaitConnectedAsync(client, ChineseProjectPath);
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, ChineseProjectPath, runtime.Port, Environment.ProcessId);

        // 注册表变更异步消化:GAME_NOT_RUNNING 视作"通道尚未就绪",轮询重试(15s 上限)。
        CallToolResult shot;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            shot = await client.CallToolAsync("capture_screenshot", new Dictionary<string, object?>
            {
                ["target"] = "runtime",
            });
            var shotText = shot.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
            if (shot.IsError is null or false
                || !shotText.Contains("GAME_NOT_RUNNING", StringComparison.Ordinal)
                || DateTime.UtcNow >= deadline)
            {
                break;
            }
            await Task.Delay(250);
        }
        Assert.True(shot.IsError is null or false, TextOf(shot));

        // 磁盘信封为纯文本:path 随行,绝无图像内容块。
        Assert.DoesNotContain(shot.Content, block => block is ImageContentBlock);
        var envelopeText = TextOf(shot);

        // 原始文本护栏:中文路径必须原样可见(转义形态出现即回归)。
        Assert.Contains("重生之战之秒", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapedMarker, envelopeText, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(envelopeText);
        Assert.Equal(shotPath, payload.RootElement.GetProperty("path").GetString());
        Assert.Equal("1920x1080", payload.RootElement.GetProperty("returned").GetString());
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>把 result JSON 包成标准 JSON-RPC 结果帧($request_id 占位符由替身回填为真实请求 id)。</summary>
    /// <param name="resultJson">工具 result 载荷的 JSON 文本。</param>
    /// <returns>完整 wire 帧字符串。</returns>
    private static string ResultFrame(string resultJson)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":" + resultJson + "}";
    }

    /// <summary>取工具结果唯一文本内容块的文本(顺带断言内容块恰有一个)。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>文本内容块承载的字符串。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    /// <summary>轮询 list_instances 直到目标项目(规范化路径)显示 connected;20s 超时抛 TimeoutException。</summary>
    /// <param name="client">MCP 会话客户端。</param>
    /// <param name="projectPath">目标项目路径(内部先做规范化)。</param>
    /// <returns>实例连接就绪后完成的任务。</returns>
    private static async Task WaitConnectedAsync(McpClient client, string projectPath)
    {
        var canonical = JsonMatch.CanonicalProjectKey(projectPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
            using var payload = JsonDocument.Parse(TextOf(result));
            var connected = payload.RootElement.GetProperty("instances").EnumerateArray()
                .Any(i => i.GetProperty("path").GetString() == canonical && i.GetProperty("connected").GetBoolean());
            if (connected)
            {
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"实例 {canonical} 未在 20s 内连接");
    }
}
