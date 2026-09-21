using System.Net;
using System.Text;
using System.Text.Json;
using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// 2026-07-28 修订的裸 HTTP 面(不经 SDK 客户端;后者存在已知探测回退竞态,
/// 见 SessionConnector 注释):① server/discover 昭示 supportedVersions 含
/// 2026-07-28 与 tools.listChanged;② per-request metadata 信封校验(缺 Mcp-Method →
/// -32020);③ 头体不一致的 initialize 按 SEP-2575 拒收(带 2026-07-28 头 + 2025-11-25
/// 体 → -32020)—— 锁住 daemon 的服务端语义(修复测试侧竞态时不可退化为宽松接收)。
/// </summary>
public class DiscoveryFaceTests
{
    /// <summary>裸 HTTP 面:server/discover 昭示版本与能力,信封校验与头体一致性按 SEP-2575 强制。</summary>
    /// <para>
    /// Arrange:经 DaemonProcess 拉起 daemon,不经 SDK 客户端、以裸 HttpClient 构造请求。
    /// Assert ①正路:带 2026-07-28 协议头 + Mcp-Method 头探测得 200,supportedVersions 含
    /// "2026-07-28",capabilities.tools.listChanged 为 true;Assert ②信封:缺 Mcp-Method 头得
    /// 400,错误码 -32020,message 为 "Missing required Mcp-Method header.";
    /// Assert ③头体不一致:2026-07-28 头 + 2025-11-25 体的 initialize 得 400、错误码 -32020,
    /// message 含 "does not match body params.protocolVersion" —— daemon 不宽松接收。
    /// </para>
    [Fact]
    public async Task server_discover_advertises_july2026_and_enforces_envelope()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var discoverBody =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{\"_meta\":{" +
            "\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"," +
            "\"io.modelcontextprotocol/clientCapabilities\":{}," +
            "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"raw-discovery\",\"version\":\"1\"}}}}";

        // ① 正路:2026-07-28 探测成功,能力如实昭示(含 tools.listChanged,listen 长流的前提)。
        var (status, payload) = await PostRawAsync(http, daemon.Port, daemon.Token, discoverBody, new()
        {
            ["MCP-Protocol-Version"] = "2026-07-28",
            ["Mcp-Method"] = "server/discover",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var versions = payload.GetProperty("result").GetProperty("supportedVersions")
            .EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("2026-07-28", versions);
        Assert.True(payload.GetProperty("result").GetProperty("capabilities")
            .GetProperty("tools").GetProperty("listChanged").GetBoolean());

        // ② 信封校验:缺 Mcp-Method → -32020(SEP-2575 标准头强制)。
        var (missingMethodStatus, missingMethodPayload) = await PostRawAsync(
            http, daemon.Port, daemon.Token, discoverBody, new() { ["MCP-Protocol-Version"] = "2026-07-28" });
        Assert.Equal(HttpStatusCode.BadRequest, missingMethodStatus);
        Assert.Equal(-32020, missingMethodPayload.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            "Missing required Mcp-Method header.",
            missingMethodPayload.GetProperty("error").GetProperty("message").GetString());

        // ③ 头体不一致的 initialize 拒收(SDK 客户端探测竞态的失败形态即此;daemon 不得宽松接收)。
        var initializeBody =
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"," +
            "\"capabilities\":{},\"clientInfo\":{\"name\":\"raw-discovery\",\"version\":\"1\"}}}";
        var (mismatchStatus, mismatchPayload) = await PostRawAsync(
            http, daemon.Port, daemon.Token, initializeBody, new() { ["MCP-Protocol-Version"] = "2026-07-28" });
        Assert.Equal(HttpStatusCode.BadRequest, mismatchStatus);
        Assert.Equal(-32020, mismatchPayload.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(
            "does not match body params.protocolVersion",
            mismatchPayload.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>裸 POST:头按给定集合、body 原样;解析 SSE data 行或 JSON 体的 JSON-RPC 负载。</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Payload)> PostRawAsync(
        HttpClient http, int port, string token, string body, Dictionary<string, string> headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // 裸探测逐条独立连接(Connection: close):规避 keep-alive 复用把上一响应残余
        // 当状态行解析的偶发读帧错位 —— 每条断言只关心本轮请求的语义。
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var dataLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        var json = dataLine is not null ? dataLine["data:".Length..].Trim() : text;
        return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }
}
