using ModelContextProtocol.Client;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 工具清单断言扩展(官方 SDK McpClientTool 列表上的断言糖):
/// DaemonMcpFaceTests / AcceptanceHarnessTests 等反复使用"清单须含某工具"的同一形状。
/// </summary>
internal static class McpToolListExtensions
{
    /// <summary>断言工具清单包含指定名称(测试中反复出现的同一形状)。</summary>
    public static void AssertContainsTool(this IEnumerable<McpClientTool> tools, string name)
    {
        Assert.Contains(tools, t => t.Name == name);
    }
}
