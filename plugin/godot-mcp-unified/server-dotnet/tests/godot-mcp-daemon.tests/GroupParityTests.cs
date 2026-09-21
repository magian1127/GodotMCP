using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 13 验收:全量 parity —— 全激活(含 unsafe)后 tools/list 与 Node 桥对比为空集:
/// ① 名称:Node 表内 89 个条目(88 工具 + discover_tools)全部在;daemon 附加工具
///    ⊆ {list_instances, list_operations}(状态面对 Node 的已记录扩展);
/// ② schema:逐工具与 Node 表一致(仅剥离 daemon 注入的 instance 寻址参数与 $schema 键;
///    Node 表由 server/scripts/dump-group-tools.ts 以 SDK 同源转换导出);
/// ③ 描述:除 discover_tools(动态目录)外逐字一致。
/// 另:unsafe 组门控 —— 默认隐藏且拒绝激活,显式启用(GODOT_MCP_UNSAFE=1)后进入全量面
/// (Node isGroupEnabled 同规)。
/// </summary>
public class GroupParityTests
{
    /// <summary>允许 daemon 在 Node 表之外附加的工具白名单(状态面对 Node 的已记录扩展)。</summary>
    private static readonly string[] DaemonOnlyExtras = ["list_instances", "list_operations"];

    /// <summary>全激活(含 unsafe)后 tools/list 与 Node 桥导出表对齐:名称、schema、描述三方 parity。</summary>
    /// <para>
    /// Arrange:UnsafeEnabled 拉起 daemon,起 4.7.0 假实例使版本门控工具进入并集面,
    /// 经 discover_tools 激活 GroupCatalogue 全部 31 组,取 tools/list 按名建字典。
    /// Assert ①名称:Node 表全部条目都在(daemon 附加工具恰为 DaemonOnlyExtras);
    /// ②schema:逐工具剥离 $schema 与 instance 参数(见 Strip)、对象键序规整(见 Canonical)后
    /// 与 Node 表 DeepEquals;③描述:除动态目录 discover_tools(DescriptionDynamic)外逐字相等。
    /// </para>
    [Fact]
    public async Task full_activation_tools_list_matches_node_surface()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 120,
            UnsafeEnabled = true, // 全量面 = 含 unsafe(Node GODOT_MCP_UNSAFE=1 同名语义)。
        });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);
        // 4.7.0 实例:使含版本门控的工具(scene_close min 4.5)进入并集面 ——
        // 无实例/旧版本时门控工具隐藏(Node 注册门控同规,见 ExtensionAndGateTests)。
        using var fake = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = @"D:\proj\parity",
            StateDir = stateDir,
            AuthAckJson = """{"authed":true,"godot_version":"4.7.0","version":"1.0.0","headless":false}""",
        });
        await WaitConnectedAsync(client, @"D:\proj\parity");

        // 激活全部 31 组。
        var discover = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = GroupCatalogue.Groups.Select(g => g.Name).ToList(),
        });
        Assert.True(discover.IsError is null or false, TextOf(discover));

        var tools = await client.ListToolsAsync();
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // ① 名称:缺失为空;daemon 附加工具恰好为白名单。
        var missing = NodeToolTable.AllEntries
            .Where(e => !byName.ContainsKey(e.Name))
            .Select(e => e.Name)
            .ToList();
        Assert.True(missing.Count == 0, $"缺失工具:{string.Join(", ", missing)}");
        var extras = byName.Keys
            .Where(n => NodeToolTable.AllEntries.All(e => e.Name != n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(DaemonOnlyExtras.OrderBy(n => n, StringComparer.Ordinal).ToList(), extras);

        // ② schema 逐工具一致(剥离 instance/$schema);③ 描述(动态目录除外)。
        foreach (var def in NodeToolTable.AllEntries)
        {
            var tool = byName[def.Name];
            var actual = Canonical(Strip(JsonNode.Parse(tool.JsonSchema.GetRawText())!.AsObject(), stripInstance: true));
            var expected = Canonical(Strip(JsonNode.Parse(def.InputSchema.GetRawText())!.AsObject(), stripInstance: !def.NoInstance));
            Assert.True(
                JsonNode.DeepEquals(actual, expected),
                $"schema 不一致:{def.Name}\n actual  :{actual?.ToJsonString()}\n expected:{expected?.ToJsonString()}");
            if (!def.DescriptionDynamic)
            {
                Assert.Equal(def.Description, tool.Description);
            }
        }
    }

    /// <summary>unsafe 组门控:默认隐藏且拒绝激活,工具与 discover_tools 描述目录均不出现。</summary>
    /// <para>
    /// Arrange:默认配置(未启用 unsafe)拉起 daemon 接入。
    /// Assert 目录:discover_tools 返回 31 组,unsafe 行 status 为 "available"、description 为
    /// UnsafeDisabledMessage、tools 为空;Act:请求激活 "unsafe"。
    /// Assert 拒绝:unsafe 行仍为 "available"+禁用说明,tools/list 不含 execute_code/node_call_method,
    /// discover_tools 自身描述的目录同样跳过禁用组(不含 "unsafe [" 行)。
    /// </para>
    [Fact]
    public async Task unsafe_group_is_hidden_by_default_and_gated()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions { StateDir = stateDir, IdleSeconds = 120 });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        // 目录:unsafe 行 available + 禁用说明(其余 30 组不变)。
        var catalog = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>());
        using (var payload = JsonDocument.Parse(TextOf(catalog)))
        {
            var groups = payload.RootElement.GetProperty("groups");
            Assert.Equal(31, groups.GetArrayLength());
            var unsafeRow = groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "unsafe");
            Assert.Equal("available", unsafeRow.GetProperty("status").GetString());
            Assert.Equal(GroupService.UnsafeDisabledMessage, unsafeRow.GetProperty("description").GetString());
            Assert.Equal(0, unsafeRow.GetProperty("tools").GetArrayLength());
        }

        // 激活请求被拒:仍 available + 禁用说明,工具不上线。
        var activate = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = "unsafe",
        });
        using (var payload = JsonDocument.Parse(TextOf(activate)))
        {
            var row = payload.RootElement.GetProperty("groups").EnumerateArray()
                .Single(g => g.GetProperty("name").GetString() == "unsafe");
            Assert.Equal("available", row.GetProperty("status").GetString());
            Assert.Equal(GroupService.UnsafeDisabledMessage, row.GetProperty("description").GetString());
        }
        var after = await client.ListToolsAsync();
        Assert.DoesNotContain(after, t => t.Name is "execute_code" or "node_call_method");

        // discover_tools 的描述目录同样跳过禁用组(Node buildDiscoverToolsDesc 同规矩)。
        var discoverTool = after.Single(t => t.Name == "discover_tools");
        Assert.DoesNotContain("unsafe [", discoverTool.Description);
    }

    // ── schema 规整 ──────────────────────────────────────────────

    /// <summary>剥离 $schema;按需剥离 daemon 注入的 instance 参数(记入差异对账)。</summary>
    private static JsonObject Strip(JsonObject schema, bool stripInstance)
    {
        schema.Remove("$schema");
        if (stripInstance && schema["properties"] is JsonObject properties)
        {
            properties.Remove("instance");
            if (schema["required"] is JsonArray required)
            {
                var kept = required
                    .Where(r => r is not null && r.GetValue<string>() != "instance")
                    .Select(r => JsonValue.Create(r!.GetValue<string>()))
                    .ToArray();
                if (kept.Length == 0)
                {
                    schema.Remove("required");
                }
                else
                {
                    schema["required"] = new JsonArray(kept);
                }
            }
        }
        return schema;
    }

    /// <summary>对象键递归排序,消除键序差异(数组顺序保持)。</summary>
    private static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    sorted[key] = Canonical(obj[key]);
                }
                return sorted;
            }
            case JsonArray arr:
                return new JsonArray(arr.Select(Canonical).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>取工具响应唯一文本块的内容。</summary>
    /// <param name="result">工具调用结果。</param>
    /// <returns>文本内容(由调用方自行解析)。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    /// <summary>轮询 list_instances 直至指定项目出现已连接行;20 秒未连接抛 TimeoutException。</summary>
    /// <param name="client">已接入 daemon 的 SDK 客户端。</param>
    /// <param name="projectPath">项目路径(内部先规范化再与行 path 比对)。</param>
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
