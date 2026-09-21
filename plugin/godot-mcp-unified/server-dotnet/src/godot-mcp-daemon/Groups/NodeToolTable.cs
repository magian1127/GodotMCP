using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Groups;

/// <summary>工具表条目(Node 桥导出;组工具含 group 字段,常驻工具无)。</summary>
/// <param name="Group">所属组名;null 表示常驻(eager)工具。</param>
/// <param name="Name">工具名(tools/list 与调用寻址标识)。</param>
/// <param name="Description">工具描述。</param>
/// <param name="Method">默认 wire 方法名;特例工具可由调用器按参数另选。</param>
/// <param name="InputSchema">入参 JSON 模式(Node zod 派生,未注入 instance)。</param>
/// <param name="Annotations">协议注解(readOnlyHint/destructiveHint 等)。</param>
/// <param name="Runtime">true 默认走运行时通道。</param>
/// <param name="SuccessHint">成功提示文本;非空时注入成功负载的 hint 键。</param>
/// <param name="DescriptionDynamic">描述是否运行期动态生成(如 discover_tools)。</param>
/// <param name="NoInstance">true 表示 schema 不注入 instance 寻址参数。</param>
/// <param name="GodotMinVersion">版本门控下限(含);null 无下限。</param>
/// <param name="GodotMaxVersion">版本门控上限(含);null 无上限。</param>
public sealed record NodeToolDef(
    string? Group,
    string Name,
    string? Description,
    string? Method,
    JsonElement InputSchema,
    ToolAnnotations? Annotations,
    bool Runtime,
    string? SuccessHint,
    bool DescriptionDynamic,
    bool NoInstance,
    string? GodotMinVersion,
    string? GodotMaxVersion);

/// <summary>
/// Node 工具表(嵌入资源 NodeToolTable.json):ALL_TOOL_DEFS 的 83 个工具 +
/// discover_tools 元工具。schema 与 Node tools/list 载荷同源转换
/// (MCP TS SDK 兼容层,draft-7 / io:'input')。
///
/// 两个用途:
/// ① 组工具(name 带 group)= daemon 按需注册的协议面与调用数据(issue 13);
/// ② schema 覆盖(ApplyProtocolSchemas)= 常驻工具经本表换上 Node 的 zod 派生
///    schema(仅注入 ADR-0003 的 instance 寻址参数),使全激活后的 tools/list
///    与 Node 桥逐字节对齐。
/// </summary>
public static class NodeToolTable
{
    /// <summary>嵌入资源名(与 Node 桥导出的工具表 JSON 同源)。</summary>
    private const string ResourceName = "GodotMcp.Daemon.Groups.NodeToolTable.json";
    /// <summary>完整性校验:期望条目总数(88 工具 + discover_tools)。</summary>
    private const int ExpectedEntryCount = 89;
    /// <summary>完整性校验:期望组工具条数(按需注册面)。</summary>
    private const int ExpectedGroupToolCount = 64;

    /// <summary>进程级共享的工具表 JSON 文档(启动期一次加载)。</summary>
    private static readonly JsonDocument Document = LoadDocument();

    /// <summary>全部条目(88 工具 + discover_tools;Node 目录顺序)。</summary>
    public static IReadOnlyList<NodeToolDef> AllEntries { get; } = LoadEntries();

    /// <summary>工具名 → 定义索引(序数比较)。</summary>
    private static readonly Dictionary<string, NodeToolDef> ByName =
        AllEntries.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>组工具(64 个按需注册的工具;Node GROUPS 装配顺序)。</summary>
    public static IReadOnlyList<NodeToolDef> GroupTools { get; } =
        AllEntries.Where(t => t.Group is not null).ToList();

    /// <summary>组工具名集合(激活报告的成员判定)。</summary>
    public static IReadOnlySet<string> GroupToolNames { get; } =
        GroupTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>带版本门控的工具(NodeToolDef 的 godotMin/MaxVersion)。</summary>
    /// <summary>带版本门控的工具名 → 门控(注册期可见性检查)。</summary>
    private static readonly Dictionary<string, VersionGate> GatesByTool = AllEntries
        .Where(e => e.GodotMinVersion is not null || e.GodotMaxVersion is not null)
        .ToDictionary(e => e.Name, e => new VersionGate(e.Name, e.GodotMinVersion, e.GodotMaxVersion), StringComparer.Ordinal);

    /// <summary>带版本门控的 wire 方法名 → 门控(调用期检查用)。</summary>
    private static readonly Dictionary<string, VersionGate> GatesByMethod = AllEntries
        .Where(e => e.Method is not null
            && (e.GodotMinVersion is not null || e.GodotMaxVersion is not null))
        .ToDictionary(e => e.Method!, e => new VersionGate(e.Name, e.GodotMinVersion, e.GodotMaxVersion), StringComparer.Ordinal);

    /// <summary>按名称查工具定义。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="def">命中的定义;未命中为 null。</param>
    /// <returns>命中为 true。</returns>
    public static bool TryGet(string toolName, out NodeToolDef? def) =>
        ByName.TryGetValue(toolName, out def);

    /// <summary>判定工具是否在表内(组激活报告的成员判定)。</summary>
    /// <param name="toolName">工具名。</param>
    /// <returns>在表内为 true。</returns>
    public static bool Contains(string toolName) => ByName.ContainsKey(toolName);

    /// <summary>工具的版本门控;无门槛返回 null。</summary>
    public static VersionGate? GateForTool(string toolName) =>
        GatesByTool.GetValueOrDefault(toolName);

    /// <summary>按 wire 方法查版本门控(调用期检查的零改写入口)。</summary>
    public static VersionGate? GateForMethod(string method) =>
        GatesByMethod.GetValueOrDefault(method);

    /// <summary>
    /// 对外 schema:表内 schema 原文 + 注入可选 <c>instance</c> 参数(ADR-0003
    /// 实例寻址;Node 无此参数,parity 对比按已记录的差异忽略该属性)。
    /// </summary>
    /// <param name="def">工具表定义。</param>
    /// <returns>注入 instance 后的 schema;NoInstance 条目原样返回。</returns>
    public static JsonElement SchemaFor(NodeToolDef def)
    {
        return def.NoInstance ? def.InputSchema : InjectInstance(def.InputSchema);
    }

    /// <summary>向任意工具 schema(含扩展命令的 input_schema)注入 instance 寻址参数。</summary>
    /// <param name="schema">原始 JSON 模式(需含 properties 对象;否则原样往返)。</param>
    /// <returns>properties 中追加了可选 instance 参数的新 schema。</returns>
    public static JsonElement InjectInstance(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is JsonObject properties)
        {
            properties["instance"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。",
            };
        }
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    /// <summary>
    /// 把会话工具集合里的常驻工具换到表内 schema(仅当确有差异;组工具经
    /// JsonSchemaGroupTool 已带表内 schema,跳过)。逐请求调用幂等;共享 Tool 对象
    /// 的重复写入为同值,多请求并发安全。
    /// </summary>
    /// <param name="collection">当前请求会话的工具集合。</param>
    public static void ApplyProtocolSchemas(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        foreach (var tool in collection)
        {
            if (tool is JsonSchemaGroupTool || !ByName.TryGetValue(tool.ProtocolTool.Name, out var def))
            {
                continue;
            }
            var schema = SchemaFor(def);
            if (!JsonNode.DeepEquals(
                    JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()),
                    JsonNode.Parse(schema.GetRawText())))
            {
                tool.ProtocolTool.InputSchema = schema;
            }
        }
    }

    /// <summary>加载嵌入资源中的工具表 JSON;资源缺失即抛(启动期快速失败)。</summary>
    /// <returns>解析完成的 JsonDocument(进程级共享,常驻)。</returns>
    private static JsonDocument LoadDocument()
    {
        using var stream = typeof(NodeToolTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入资源:{ResourceName}");
        return JsonDocument.Parse(stream);
    }

    /// <summary>解析工具表条目并做完整性校验(条目数/组工具数不符即抛)。</summary>
    /// <para>逻辑链:遍历 tools 数组逐条映射为 NodeToolDef(可选字段缺省、annotations
    /// 反序列化、inputSchema 克隆)→ 条目数不等于 84 或组工具数不等于 64 时抛
    /// InvalidOperationException(工具表损坏,启动期快速失败)。</para>
    /// <returns>全部工具定义列表(Node 目录顺序)。</returns>
    private static List<NodeToolDef> LoadEntries()
    {
        var entries = new List<NodeToolDef>();
        foreach (var entry in Document.RootElement.GetProperty("tools").EnumerateArray())
        {
            var annotations = entry.TryGetProperty("annotations", out var annotationsEl)
                ? annotationsEl.Deserialize<ToolAnnotations>(McpJsonUtilities.DefaultOptions)
                : null;
            entries.Add(new NodeToolDef(
                Group: entry.TryGetProperty("group", out var groupEl) ? groupEl.GetString() : null,
                Name: entry.GetProperty("name").GetString()!,
                Description: entry.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                Method: entry.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null,
                InputSchema: entry.GetProperty("inputSchema").Clone(),
                Annotations: annotations,
                Runtime: entry.TryGetProperty("runtime", out var runtimeEl) && runtimeEl.ValueKind == JsonValueKind.True,
                SuccessHint: entry.TryGetProperty("successHint", out var hintEl) ? hintEl.GetString() : null,
                DescriptionDynamic: entry.TryGetProperty("descriptionDynamic", out var dynamicEl) && dynamicEl.ValueKind == JsonValueKind.True,
                NoInstance: entry.TryGetProperty("noInstance", out var noInstanceEl) && noInstanceEl.ValueKind == JsonValueKind.True,
                GodotMinVersion: entry.TryGetProperty("godotMinVersion", out var minEl) ? minEl.GetString() : null,
                GodotMaxVersion: entry.TryGetProperty("godotMaxVersion", out var maxEl) ? maxEl.GetString() : null));
        }
        if (entries.Count != ExpectedEntryCount)
        {
            throw new InvalidOperationException(
                $"工具表损坏:{ResourceName} 含 {entries.Count} 条,期望 {ExpectedEntryCount}");
        }
        var groupTools = entries.Count(e => e.Group is not null);
        if (groupTools != ExpectedGroupToolCount)
        {
            throw new InvalidOperationException(
                $"工具表损坏:{ResourceName} 组工具 {groupTools} 个,期望 {ExpectedGroupToolCount}");
        }
        return entries;
    }
}
