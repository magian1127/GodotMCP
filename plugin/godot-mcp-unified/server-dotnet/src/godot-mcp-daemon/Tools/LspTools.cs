using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// LSP 通道工具(issue 11;tools/lsp.ts 的 C# 移植):诊断/符号/悬停/补全/导航,
/// 全部经 daemon 自持的 LSP 客户端直连 Godot 内置 GDScript LSP(端口经注册表发现,
/// 不经编辑器 WS 桥)。只读、幂等;多实例并存时按 instance 路由到各自端点。
/// </summary>
/// <param name="instances">实例管理器,提供按 instance 寻址的 GDScript LSP 客户端(惰性创建并连接)。</param>
[McpServerToolType]
public sealed partial class LspTools(InstanceManager instances)
{
    /// <summary>file 范围诊断遇着色器文件时的固定提示:LSP 不分析着色器,真实错误经 log_read 读编辑器日志。</summary>
    private const string ShaderDiagnosticsNote =
        "lsp_diagnostics does not validate shader files — Godot's LSP analyzes GDScript only. " +
        "Real shader errors surface when the editor imports/compiles the shader (open it, or run the game); " +
        "read them with log_read(channel:'editor', level_filter:['error']).";

    // ── 工具 ────────────────────────────────────────────────────

    /// <summary>
    /// lsp_diagnostics:对单个 .gd 文件执行 GDScript 诊断(diagnostics),或(scope="project")聚合扫描整个项目。
    /// <para>逻辑链:scope 缺省按"file"处理 → project 分支转入项目级扫描(见 ProjectDiagnosticsAsync)→
    /// file 分支缺 file_path 报 INVALID_PARAMS → .gdshader/.gdshaderinc 不经 LSP,直接返回空诊断并附
    /// 着色器说明 → 其余经 WithLspDocAsync 打开文档(前置失败透传其错误)→ 等待诊断推送:5 秒内无推送
    /// 返回 success 但 note 标明"状态未知,请重试";有推送则逐条格式化(1 基行号、严重级别标签)后返回。</para>
    /// </summary>
    /// <param name="scope">范围:"file"(默认,单文件)或"project"(全项目聚合,耗时可达数十秒)。</param>
    /// <param name="file_path">file 范围必填;res:// 下的 .gd/.gdshader/.gdshaderinc 路径。</param>
    /// <param name="include_addons">仅 project 范围生效:是否同时扫描 res://addons/。</param>
    /// <param name="include_warnings">仅 project 范围生效:是否包含非 Error 级别的诊断。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>JSON 信封:success + file_path + diagnostics(格式化后)+ count;寻址/读取失败时为错误信封。</returns>
    [McpServerTool(Name = "lsp_diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("对单个文件或整个项目执行详细的 GDScript 诊断。项目范围检查开销较大，可能需要数十秒。")]
    public async Task<CallToolResult> LspDiagnostics(
        string? scope = null,
        string? file_path = null,
        bool? include_addons = null,
        bool? include_warnings = null,
        string? instance = null)
    {
        if ((scope ?? "file") == "project")
        {
            return await ProjectDiagnosticsAsync(include_addons ?? false, include_warnings ?? false, instance);
        }
        if (string.IsNullOrEmpty(file_path))
        {
            return ToolResults.Error("INVALID_PARAMS", "lsp_diagnostics file scope requires file_path");
        }
        if (file_path.EndsWith(".gdshader", StringComparison.Ordinal)
            || file_path.EndsWith(".gdshaderinc", StringComparison.Ordinal))
        {
            return ToolResults.Json(new JsonObject
            {
                ["success"] = true,
                ["file_path"] = file_path,
                ["diagnostics"] = new JsonArray(),
                ["count"] = 0,
                ["note"] = ShaderDiagnosticsNote,
            }.ToJsonString());
        }

        var doc = await WithLspDocAsync(file_path, instance);
        if (doc.Error is not null)
        {
            return doc.Error;
        }
        var diagnostics = await doc.Client!.WaitForDiagnosticsAsync(doc.Uri!);
        if (diagnostics is null)
        {
            return ToolResults.Json(new JsonObject
            {
                ["success"] = true,
                ["file_path"] = file_path,
                ["diagnostics"] = new JsonArray(),
                ["count"] = 0,
                ["note"] = "no diagnostics notification within 5 s — status unknown, retry",
            }.ToJsonString());
        }
        var formatted = new JsonArray();
        foreach (var diagnostic in diagnostics.Value.EnumerateArray())
        {
            formatted.Add(FormatDiagnostic(diagnostic));
        }
        return ToolResults.Json(new JsonObject
        {
            ["success"] = true,
            ["file_path"] = file_path,
            ["diagnostics"] = formatted,
            ["count"] = formatted.Count,
        }.ToJsonString());
    }

    /// <summary>
    /// lsp_symbols:列出 .gd/.gdshader 文件的全部符号(symbol),返回结构树(比读全文省词元)。
    /// <para>逻辑链:经 WithLspDocAsync 打开文档(失败透传)→ 调 textDocument/documentSymbol
    /// (5 秒超时),异常报 LSP_UNAVAILABLE → 结果为数组时逐条递归格式化为 name/kind 标签/1 基起止行;
    /// 其他形态按空处理 → 返回 symbols + count。</para>
    /// </summary>
    /// <param name="file_path">res:// 下的 .gd/.gdshader/.gdshaderinc 路径。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>JSON 信封:success + file_path + symbols + count;寻址/读取/LSP 不可用时为错误信封。</returns>
    [McpServerTool(Name = "lsp_symbols", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出 .gd/.gdshader 文件中的全部符号（函数、变量、类、信号）。返回结构树，比读取完整源码开销更低。")]
    public async Task<CallToolResult> LspSymbols(string file_path, string? instance = null)
    {
        var doc = await WithLspDocAsync(file_path, instance);
        if (doc.Error is not null)
        {
            return doc.Error;
        }
        JsonElement result;
        try
        {
            result = await doc.Client!.SendRequestAsync(
                "textDocument/documentSymbol",
                new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = doc.Uri } },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ToolResults.Error("LSP_UNAVAILABLE", $"GDScript LSP unavailable: {ex.Message}.");
        }
        var symbols = new JsonArray();
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var symbol in result.EnumerateArray())
            {
                symbols.Add(FormatSymbol(symbol));
            }
        }
        return ToolResults.Json(new JsonObject
        {
            ["success"] = true,
            ["file_path"] = file_path,
            ["symbols"] = symbols,
            ["count"] = symbols.Count,
        }.ToJsonString());
    }

    /// <summary>
    /// lsp_hover:取指定位置的悬停(hover)类型签名与文档,适合针对性类型检查而非批量探索。
    /// <para>逻辑链:经 WithLspDocAsync 打开文档(失败透传)→ 调 textDocument/hover(5 秒超时),
    /// 异常报 LSP_UNAVAILABLE → contents 兼容四种形态(字符串/含 value 的对象/数组拼行/原始 JSON),
    /// 取不到文本时返回 contents:null → 命中则把文本内的项目内 file:// 链接改写回 res://
    /// (与 lsp_navigate 一致),再包进 untrusted 信封防提示注入后返回。</para>
    /// </summary>
    /// <param name="file_path">res:// 下的 .gd/.gdshader/.gdshaderinc 路径。</param>
    /// <param name="line">行号,从 0 开始。</param>
    /// <param name="column">列号,从 0 开始。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>JSON 信封:success + file_path + position + contents(null 或 untrusted 包裹的文本);失败时为错误信封。</returns>
    [McpServerTool(Name = "lsp_hover", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("获取指定位置符号的类型签名和文档。用于有针对性的类型检查，不宜用于批量探索。")]
    public async Task<CallToolResult> LspHover(string file_path, int line, int column, string? instance = null)
    {
        var doc = await WithLspDocAsync(file_path, instance);
        if (doc.Error is not null)
        {
            return doc.Error;
        }
        JsonElement result;
        try
        {
            result = await doc.Client!.SendRequestAsync("textDocument/hover", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = doc.Uri },
                ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
            }, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ToolResults.Error("LSP_UNAVAILABLE", $"GDScript LSP unavailable: {ex.Message}.");
        }

        string? hoverText = null;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("contents", out var contents))
        {
            hoverText = contents.ValueKind switch
            {
                JsonValueKind.String => contents.GetString(),
                JsonValueKind.Object => contents.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : contents.GetRawText(),
                JsonValueKind.Array => string.Join("\n", contents.EnumerateArray().Select(c =>
                    c.ValueKind == JsonValueKind.String ? c.GetString() : c.TryGetProperty("value", out var v) ? v.GetString() : "")),
                _ => contents.GetRawText(),
            };
        }

        if (hoverText is null)
        {
            return ToolResults.Json(new JsonObject
            {
                ["success"] = true,
                ["file_path"] = file_path,
                ["position"] = new JsonObject { ["line"] = line, ["column"] = column },
                ["contents"] = null,
            }.ToJsonString());
        }

        // 悬停 markdown 中的项目内 file:// 链接转回 res://(与 lsp_navigate 一致)。
        var projectPath = doc.Client!.ProjectPath;
        hoverText = FileUriInTextPattern().Replace(hoverText, match => FileUriToRes(match.Value, projectPath));

        return ToolResults.Json(new JsonObject
        {
            ["success"] = true,
            ["file_path"] = file_path,
            ["position"] = new JsonObject { ["line"] = line, ["column"] = column },
            ["contents"] = WrapUntrusted("hover", "godot-lsp", hoverText),
        }.ToJsonString());
    }

    /// <summary>
    /// lsp_completion:取指定位置的代码补全(completion)条目,仅在需要了解可用 API 时调用。
    /// <para>逻辑链:经 WithLspDocAsync 打开文档(失败透传)→ 调 textDocument/completion(5 秒超时),
    /// 异常报 LSP_UNAVAILABLE → 结果兼容数组与 {items:[...]} 两种形态 → 按 limit(缺省 10,Node zod
    /// 默认)截取,逐条投影为 label/kind 标签/detail/documentation(存在才带)→ 返回 completions +
    /// count(截取后)+ total(全量)。</para>
    /// </summary>
    /// <param name="file_path">res:// 下的 .gd/.gdshader/.gdshaderinc 路径。</param>
    /// <param name="line">行号,从 0 开始。</param>
    /// <param name="column">列号,从 0 开始。</param>
    /// <param name="limit">最多返回的补全条目数,缺省 10;针对性查询可设 5 以节省词元。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>JSON 信封:success + completions + count + total;寻址/读取/LSP 不可用时为错误信封。</returns>
    [McpServerTool(Name = "lsp_completion", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("获取指定位置的代码补全。针对性查询可设 limit=5 以节省词元。仅在需要了解可用 API 时调用。")]
    public async Task<CallToolResult> LspCompletion(
        string file_path, int line, int column, int? limit = null, string? instance = null)
    {
        var doc = await WithLspDocAsync(file_path, instance);
        if (doc.Error is not null)
        {
            return doc.Error;
        }
        JsonElement result;
        try
        {
            result = await doc.Client!.SendRequestAsync("textDocument/completion", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = doc.Uri },
                ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
            }, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ToolResults.Error("LSP_UNAVAILABLE", $"GDScript LSP unavailable: {ex.Message}.");
        }

        JsonElement.ArrayEnumerator items = default;
        var hasItems = false;
        if (result.ValueKind == JsonValueKind.Array)
        {
            items = result.EnumerateArray();
            hasItems = true;
        }
        else if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            items = itemsEl.EnumerateArray();
            hasItems = true;
        }
        var all = hasItems ? items.ToArray() : Array.Empty<JsonElement>();

        var maxItems = limit ?? 10; // Node zod default(10)
        var completions = new JsonArray();
        foreach (var item in all.Take(maxItems))
        {
            var entry = new JsonObject { ["label"] = GetString(item, "label") ?? "" };
            entry["kind"] = CompletionKindLabel(GetInt(item, "kind"));
            if (GetString(item, "detail") is { } detail)
            {
                entry["detail"] = detail;
            }
            if (item.TryGetProperty("documentation", out var documentation) && documentation.ValueKind != JsonValueKind.Null)
            {
                entry["documentation"] = documentation.ValueKind == JsonValueKind.String
                    ? documentation.GetString()
                    : documentation.TryGetProperty("value", out var value) ? value.GetString() : documentation.GetRawText();
            }
            completions.Add(entry);
        }
        return ToolResults.Json(new JsonObject
        {
            ["success"] = true,
            ["file_path"] = file_path,
            ["position"] = new JsonObject { ["line"] = line, ["column"] = column },
            ["completions"] = completions,
            ["count"] = completions.Count,
            ["total"] = all.Length,
        }.ToJsonString());
    }

    /// <summary>
    /// lsp_navigate:从源码位置做定义/引用的导航(navigation)查询。
    /// <para>逻辑链:经 WithLspDocAsync 打开文档(失败透传)→ mode=references 走 textDocument/references
    /// (含声明,30 秒超时 —— 引用查询同步扫全工程),否则 textDocument/definition(5 秒超时);
    /// 异常报 LSP_UNAVAILABLE → 结果兼容数组与单对象,统一换算为 res:// 路径 + 1 基 line/column →
    /// references 返回 references + count;definition 按命中数收拢:0 个为 null、1 个为单对象、
    /// 多个为数组。</para>
    /// </summary>
    /// <param name="mode">"references"(查全部引用)或"definition"(查定义)。</param>
    /// <param name="file_path">res:// 下的 .gd/.gdshader/.gdshaderinc 路径。</param>
    /// <param name="line">行号,从 0 开始。</param>
    /// <param name="column">列号,从 0 开始。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>JSON 信封:references 分支含 references + count;definition 分支含 definition(单对象/数组/null);失败时为错误信封。</returns>
    [McpServerTool(Name = "lsp_navigate", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("从一个源码位置查找符号定义或项目中的全部引用。")]
    public async Task<CallToolResult> LspNavigate(
        string mode, string file_path, int line, int column, string? instance = null)
    {
        var doc = await WithLspDocAsync(file_path, instance);
        if (doc.Error is not null)
        {
            return doc.Error;
        }

        var isReferences = mode == "references";
        JsonElement result;
        try
        {
            result = isReferences
                ? await doc.Client!.SendRequestAsync("textDocument/references", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = doc.Uri },
                    ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
                    ["context"] = new JsonObject { ["includeDeclaration"] = true },
                }, TimeSpan.FromSeconds(30), CancellationToken.None) // 引用查询同步扫全工程(实测约 18s)。
                : await doc.Client!.SendRequestAsync("textDocument/definition", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = doc.Uri },
                    ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
                }, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ToolResults.Error("LSP_UNAVAILABLE", $"GDScript LSP unavailable: {ex.Message}.");
        }

        var locations = new JsonArray();
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in result.EnumerateArray())
            {
                locations.Add(FormatLocation(location, doc.Client!.ProjectPath));
            }
        }
        else if (result.ValueKind == JsonValueKind.Object)
        {
            locations.Add(FormatLocation(result, doc.Client!.ProjectPath));
        }

        if (isReferences)
        {
            return ToolResults.Json(new JsonObject
            {
                ["success"] = true,
                ["file_path"] = file_path,
                ["position"] = new JsonObject { ["line"] = line, ["column"] = column },
                ["references"] = locations,
                ["count"] = locations.Count,
            }.ToJsonString());
        }

        JsonNode? definition = locations.Count switch
        {
            0 => null,
            1 => locations[0]!.DeepClone(),
            _ => locations.DeepClone(),
        };
        return ToolResults.Json(new JsonObject
        {
            ["success"] = true,
            ["file_path"] = file_path,
            ["position"] = new JsonObject { ["line"] = line, ["column"] = column },
            ["definition"] = definition,
        }.ToJsonString());
    }

    // ── 项目级扫描(Node lspProjectScan 语义) ────────────────────

    /// <summary>
    /// 项目级诊断扫描(lsp_diagnostics 的 scope="project" 分支):遍历全部 .gd 并分块开文档收集诊断。
    /// <para>逻辑链:按 instance 解析连接取项目根(寻址失败透传错误信封)→ 取该实例的 LSP 客户端
    /// (连接失败透传 LSP_UNAVAILABLE 等)→ 枚举 .gd 文件,空集直接返回空聚合 → 按 20 文件/块、
    /// 每文件至多等 10 秒逐块扫描,任一块整体失败即中止 → 全部结果折叠为项目级信封返回。</para>
    /// </summary>
    /// <param name="includeAddons">是否同时扫描顶层 res://addons/。</param>
    /// <param name="includeWarnings">是否保留非 Error 级别诊断(否则仅统计 Error)。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>聚合 JSON 信封(见 AggregateScan);寻址/连接/扫描失败时为错误信封。</returns>
    private async Task<CallToolResult> ProjectDiagnosticsAsync(bool includeAddons, bool includeWarnings, string? instance)
    {
        var connection = instances.ResolveConnection(instance, out var resolveError);
        if (resolveError is not null || connection is null)
        {
            return ToolResults.FromException(resolveError!);
        }
        var projectRoot = connection.Key.Replace('/', Path.DirectorySeparatorChar);

        LspClient client;
        try
        {
            client = await instances.GetLspClientAsync(instance, CancellationToken.None);
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }

        var files = EnumerateGdFiles(projectRoot, includeAddons);
        if (files.Count == 0)
        {
            return ToolResults.Json(AggregateScan([]));
        }

        const int chunkSize = 20;
        const int waitMs = 10_000;
        var results = new List<(string FilePath, string Kind, JsonArray? Diagnostics)>();
        for (var i = 0; i < files.Count; i += chunkSize)
        {
            var chunk = files.Skip(i).Take(chunkSize).ToList();
            var scanned = await ScanChunkAsync(client, chunk, projectRoot, includeWarnings, waitMs);
            if (scanned.Error is not null)
            {
                return scanned.Error;
            }
            results.AddRange(scanned.Results!);
        }
        return ToolResults.Json(AggregateScan(results));
    }

    /// <summary>
    /// 扫描一个文件块:批量打开文档、统一等待诊断、再全部关闭。
    /// <para>逻辑链:逐个读文件(读不到记 read_failed,是唯一的非 URI 结果)→ didOpen/didChange
    /// 推送内容 → 在关闭任何文件之前等待全部诊断(先关闭会丢失迟到的推送)→ 全部关闭 →
    /// 若块内有文件且无一收到诊断,整体判 LSP_UNAVAILABLE(编辑器可能忙或卡死,建议稍后重试)→
    /// 其余按文件归类:超时记 timed_out;按 includeWarnings 过滤严重级别大于 1 的条目后,
    /// 空则 clean,非空则 diagnostics。</para>
    /// </summary>
    /// <param name="client">目标实例的 LSP 客户端。</param>
    /// <param name="chunk">本块的 res:// 文件路径列表(不超过 20 个)。</param>
    /// <param name="projectRoot">项目根绝对路径,用于 res:// → 绝对路径换算。</param>
    /// <param name="includeWarnings">是否保留非 Error 级别诊断。</param>
    /// <param name="waitMs">单文件等待诊断推送的上限(毫秒)。</param>
    /// <returns>Results:逐文件 (路径, 类别, 诊断数组);Error:整块判失败时的错误信封。二者互斥。</returns>
    private static async Task<(List<(string, string, JsonArray?)>? Results, CallToolResult? Error)> ScanChunkAsync(
        LspClient client, List<string> chunk, string projectRoot, bool includeWarnings, int waitMs)
    {
        var opened = new List<(string FilePath, string Uri)>();
        var results = new List<(string, string, JsonArray?)>();
        foreach (var resPath in chunk)
        {
            var absolute = ResToAbsolute(resPath, projectRoot);
            string content;
            try
            {
                content = await File.ReadAllTextAsync(absolute);
            }
            catch (Exception)
            {
                // 唯一的非 URI 结果:READ_FAILED(文件在遍历之后消失)。
                results.Add((resPath, "read_failed", null));
                continue;
            }
            var uri = LspClient.AbsoluteToFileUri(absolute);
            await client.OpenDocumentAsync(uri, content);
            opened.Add((resPath, uri));
        }

        // 在关闭任何文件之前先等待全部诊断(关闭会丢失迟到的推送);等待段无论成败
        // 都尽力关闭已打开的文档,避免异常路径把打开残留丢给后续分块。
        (string FilePath, JsonElement? Diagnostics)[] waits = [];
        try
        {
            waits = await Task.WhenAll(opened.Select(async o =>
                (o.FilePath, Diagnostics: await client.WaitForDiagnosticsAsync(o.Uri, waitMs))));
        }
        finally
        {
            foreach (var (_, uri) in opened)
            {
                try
                {
                    await client.CloseDocumentAsync(uri);
                }
                catch (Exception)
                {
                    // 收尾尽力而为:连接已断时关闭失败,交由上层的错误路径上报。
                }
            }
        }

        if (opened.Count > 0 && waits.All(w => w.Diagnostics is null))
        {
            return (null, ToolResults.Error(
                "LSP_UNAVAILABLE",
                $"Connected to the GDScript LSP but received no diagnostics within {waitMs / 1000}s across " +
                $"{opened.Count} files — the editor may be busy or wedged. Retry shortly."));
        }

        foreach (var (filePath, diagnostics) in waits)
        {
            if (diagnostics is null)
            {
                results.Add((filePath, "timed_out", null));
                continue;
            }
            var filtered = new JsonArray();
            foreach (var diagnostic in diagnostics.Value.EnumerateArray())
            {
                var severity = GetInt(diagnostic, "severity") ?? 0;
                if (!includeWarnings && severity > 1)
                {
                    continue; // 未请求警告时仅保留 Error(严重级别 1)。
                }
                filtered.Add(diagnostic.Clone());
            }
            results.Add(filtered.Count == 0 ? (filePath, "clean", null) : (filePath, "diagnostics", filtered));
        }
        return (results, null);
    }

    /// <summary>
    /// 把逐文件扫描结果折叠为项目级信封:scanned/clean 计数、files_with_diagnostics(逐条格式化)、
    /// total_diagnostics,以及可选的 timed_out(附"状态未知,不等于干净"的 note)与 read_failed 列表。
    /// </summary>
    /// <param name="results">ScanChunkAsync 累积的逐文件结果(警告过滤已在 ScanChunkAsync 内完成,聚合层无需再筛)。</param>
    /// <returns>聚合结果 JSON 字符串。</returns>
    private static string AggregateScan(
        List<(string FilePath, string Kind, JsonArray? Diagnostics)> results)
    {
        var filesWithDiagnostics = new JsonArray();
        var timedOut = new List<string>();
        var readFailed = new List<string>();
        var clean = 0;
        var totalDiagnostics = 0;
        foreach (var (filePath, kind, diagnostics) in results)
        {
            switch (kind)
            {
                case "clean":
                    clean++;
                    break;
                case "diagnostics":
                    var formatted = new JsonArray();
                    foreach (var diagnostic in diagnostics!)
                    {
                        formatted.Add(FormatDiagnostic(JsonDocument.Parse(diagnostic!.ToJsonString()).RootElement));
                        totalDiagnostics++;
                    }
                    filesWithDiagnostics.Add(new JsonObject
                    {
                        ["file_path"] = filePath,
                        ["diagnostics"] = formatted,
                    });
                    break;
                case "timed_out":
                    timedOut.Add(filePath);
                    break;
                case "read_failed":
                    readFailed.Add(filePath);
                    break;
            }
        }

        var payload = new JsonObject
        {
            ["success"] = true,
            ["scanned"] = results.Count,
            ["clean"] = clean,
            ["files_with_diagnostics"] = filesWithDiagnostics,
            ["total_diagnostics"] = totalDiagnostics,
        };
        if (timedOut.Count > 0)
        {
            payload["timed_out"] = new JsonArray(timedOut.Select(s => JsonValue.Create(s)).ToArray());
            payload["note"] =
                $"{timedOut.Count} file(s) produced no diagnostics notification within 10s — status unknown, NOT clean.";
        }
        if (readFailed.Count > 0)
        {
            payload["read_failed"] = new JsonArray(readFailed.Select(s => JsonValue.Create(s)).ToArray());
        }
        return payload.ToJsonString();
    }

    /// <summary>从项目根递归枚举全部 .gd 文件,返回 res:// 路径列表(供项目级扫描分块)。</summary>
    /// <param name="projectRoot">项目根绝对路径。</param>
    /// <param name="includeAddons">是否包含顶层 addons 目录。</param>
    /// <returns>res:// 路径列表;项目根不存在时为空列表。</returns>
    private static List<string> EnumerateGdFiles(string projectRoot, bool includeAddons)
    {
        var found = new List<string>();
        Walk(projectRoot, "", found, includeAddons, true);
        return found;
    }

    /// <summary>
    /// 深度优先遍历目录收集 .gd 文件:跳过点开头目录(.godot 等);顶层 addons 仅在未请求
    /// includeAddons 时排除(Node walkDir 同规,更深层出现的 addons 目录不受影响)。
    /// </summary>
    /// <param name="dirAbs">当前目录绝对路径。</param>
    /// <param name="relPrefix">相对项目根的路径前缀(以 / 结尾,根为空串)。</param>
    /// <param name="found">结果收集器(res:// 路径)。</param>
    /// <param name="includeAddons">是否包含顶层 addons。</param>
    /// <param name="atRoot">当前是否处于项目根(仅根层应用 addons 排除)。</param>
    private static void Walk(string dirAbs, string relPrefix, List<string> found, bool includeAddons, bool atRoot)
    {
        if (!Directory.Exists(dirAbs))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dirAbs, "*.gd"))
        {
            found.Add("res://" + relPrefix + Path.GetFileName(file));
        }
        foreach (var dir in Directory.EnumerateDirectories(dirAbs))
        {
            var name = Path.GetFileName(dir);
            if (name == ".godot" || name.StartsWith('.'))
            {
                continue;
            }
            if (atRoot && !includeAddons && name == "addons")
            {
                continue; // addons 排除只作用于顶层目录(Node walkDir 同规)。
            }
            Walk(dir, relPrefix + name + "/", found, includeAddons, false);
        }
    }

    // ── 共享:文档打开 / 标签 / URI / 信封 ──────────────────────

    /// <summary>单文件 LSP 调用的前置结果:成功携带客户端与文档 URI,失败携带现成错误信封,二者互斥。</summary>
    /// <param name="Client">目标实例的 LSP 客户端(仅成功时非 null)。</param>
    /// <param name="Uri">已打开文档的 file:// URI(仅成功时非 null)。</param>
    /// <param name="Error">前置校验/寻址/读盘失败时可直接返回的错误信封。</param>
    private sealed record LspDoc(LspClient? Client, string? Uri, CallToolResult? Error);

    /// <summary>
    /// 打开(或复用)目标文档,是全部单文件 LSP 工具的公共前置入口。
    /// <para>逻辑链:file_path 必须 res:// 前缀,否则 INVALID_PATH → 扩展名白名单校验
    /// (.gd/.gdshader/.gdshaderinc;.cs 单独提示改用 IDE 的 .NET 语言服务器),其余报
    /// UNSUPPORTED_FILE_TYPE → 按 instance 取 LSP 客户端(寻址/连接失败透传)→ 读盘失败报
    /// READ_FAILED → didOpen(已打开则为 didChange)推送最新内容 → 返回可用的 (client, uri)。</para>
    /// </summary>
    /// <param name="filePath">res:// 下的 GDScript/着色器文件路径。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>成功:Client/Uri 就绪且 Error 为 null;失败:Error 为现成错误信封。</returns>
    private async Task<LspDoc> WithLspDocAsync(string filePath, string? instance)
    {
        if (!filePath.StartsWith("res://", StringComparison.Ordinal))
        {
            return new LspDoc(null, null, ToolResults.Error("INVALID_PATH", "file_path must start with res://"));
        }
        if (!filePath.EndsWith(".gd", StringComparison.Ordinal)
            && !filePath.EndsWith(".gdshader", StringComparison.Ordinal)
            && !filePath.EndsWith(".gdshaderinc", StringComparison.Ordinal))
        {
            return new LspDoc(null, null, ToolResults.Error(
                "UNSUPPORTED_FILE_TYPE",
                filePath.EndsWith(".cs", StringComparison.Ordinal)
                    ? "Godot's built-in LSP only covers GDScript (.gd) and shaders (.gdshader). " +
                      "C# (.cs) diagnostics come from the .NET language server in your IDE (VS Code, Rider)."
                    : "Godot's built-in LSP only covers .gd and .gdshader/.gdshaderinc files. " +
                      "Other languages (C++, Rust, Python via GDExtension) use external toolchains with no Godot LSP integration."));
        }

        LspClient client;
        try
        {
            client = await instances.GetLspClientAsync(instance, CancellationToken.None);
        }
        catch (InstanceCallException ex)
        {
            return new LspDoc(null, null, ToolResults.FromException(ex));
        }

        var absolute = ResToAbsolute(filePath, client.ProjectPath);
        string content;
        try
        {
            content = await File.ReadAllTextAsync(absolute);
        }
        catch (Exception ex)
        {
            return new LspDoc(null, null, ToolResults.Error("READ_FAILED", $"Cannot read {filePath}: {ex.Message}"));
        }
        var uri = LspClient.AbsoluteToFileUri(absolute);
        await client.OpenDocumentAsync(uri, content);
        return new LspDoc(client, uri, null);
    }

    /// <summary>把 LSP 诊断条目格式化为 1 基 line/character、严重级别标签与 message,存在 code 时附带。</summary>
    /// <param name="diagnostic">publishDiagnostics 数组中的原始条目。</param>
    /// <returns>格式化后的 JSON 对象。</returns>
    private static JsonObject FormatDiagnostic(JsonElement diagnostic)
    {
        var start = diagnostic.GetProperty("range").GetProperty("start");
        var formatted = new JsonObject
        {
            ["line"] = start.GetProperty("line").GetInt32() + 1,
            ["character"] = start.GetProperty("character").GetInt32() + 1,
            ["severity"] = SeverityLabel(GetInt(diagnostic, "severity") ?? 0),
            ["message"] = GetString(diagnostic, "message") ?? "",
        };
        if (diagnostic.TryGetProperty("code", out var code) && code.ValueKind != JsonValueKind.Null)
        {
            formatted["code"] = JsonNode.Parse(code.GetRawText());
        }
        return formatted;
    }

    /// <summary>把 documentSymbol 条目格式化为 name/kind 标签/1 基起止行,children 非空时递归格式化。</summary>
    /// <param name="symbol">documentSymbol 结果中的原始条目。</param>
    /// <returns>格式化后的 JSON 对象(叶子节点不含 children 键)。</returns>
    private static JsonObject FormatSymbol(JsonElement symbol)
    {
        var result = new JsonObject
        {
            ["name"] = GetString(symbol, "name") ?? "",
            ["kind"] = SymbolKindLabel(GetInt(symbol, "kind")),
            ["start_line"] = (symbol.TryGetProperty("range", out var range) && range.TryGetProperty("start", out var start)
                ? GetInt(start, "line") ?? 0
                : 0) + 1,
            ["end_line"] = (symbol.TryGetProperty("range", out var range2) && range2.TryGetProperty("end", out var end)
                ? GetInt(end, "line") ?? 0
                : 0) + 1,
        };
        if (symbol.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            && children.GetArrayLength() > 0)
        {
            var formattedChildren = new JsonArray();
            foreach (var child in children.EnumerateArray())
            {
                formattedChildren.Add(FormatSymbol(child));
            }
            result["children"] = formattedChildren;
        }
        return result;
    }

    /// <summary>把 LSP 位置(uri/targetUri + range/targetRange)换算为 res:// 路径 + 1 基 line/column。</summary>
    /// <param name="location">definition/references 返回的位置条目(两种键名形态都兼容)。</param>
    /// <param name="projectPath">项目根绝对路径,用于 file:// → res:// 换算。</param>
    /// <returns>格式化后的 JSON 对象。</returns>
    private static JsonObject FormatLocation(JsonElement location, string projectPath)
    {
        var uri = GetString(location, "uri") ?? GetString(location, "targetUri") ?? "";
        var range = location.TryGetProperty("range", out var r)
            ? r
            : location.TryGetProperty("targetRange", out var tr) ? tr : default;
        var line = range.ValueKind == JsonValueKind.Object && range.TryGetProperty("start", out var start)
            ? GetInt(start, "line") ?? 0
            : 0;
        var character = range.ValueKind == JsonValueKind.Object && range.TryGetProperty("start", out var start2)
            ? GetInt(start2, "character") ?? 0
            : 0;
        return new JsonObject
        {
            ["file_path"] = FileUriToRes(uri, projectPath),
            ["line"] = line + 1,
            ["column"] = character + 1,
        };
    }

    /// <summary>LSP 诊断严重级别数字编码 → 标签:1 Error / 2 Warning / 3 Information / 4 Hint,其余 Unknown。</summary>
    /// <param name="severity">LSP severity 编号(缺失时由调用方传 0)。</param>
    /// <returns>严重级别标签。</returns>
    private static string SeverityLabel(int severity) => severity switch
    {
        1 => "Error",
        2 => "Warning",
        3 => "Information",
        4 => "Hint",
        _ => "Unknown",
    };

    /// <summary>LSP SymbolKind 数字编码(1–26)→ 标签,缺失或越界为 Unknown。</summary>
    /// <param name="kind">SymbolKind 编号。</param>
    /// <returns>符号类别标签。</returns>
    private static string SymbolKindLabel(int? kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package", 5 => "Class", 6 => "Method",
        7 => "Property", 8 => "Field", 9 => "Constructor", 10 => "Enum", 11 => "Interface", 12 => "Function",
        13 => "Variable", 14 => "Constant", 15 => "String", 16 => "Number", 17 => "Boolean", 18 => "Array",
        19 => "Object", 20 => "Key", 21 => "Null", 22 => "EnumMember", 23 => "Struct", 24 => "Event",
        25 => "Operator", 26 => "TypeParameter", _ => "Unknown",
    };

    /// <summary>LSP CompletionItemKind 数字编码(1–25)→ 标签,缺失或越界为 Unknown。</summary>
    /// <param name="kind">CompletionItemKind 编号。</param>
    /// <returns>补全条目类别标签。</returns>
    private static string CompletionKindLabel(int? kind) => kind switch
    {
        1 => "Text", 2 => "Method", 3 => "Function", 4 => "Constructor", 5 => "Field", 6 => "Variable",
        7 => "Class", 8 => "Interface", 9 => "Module", 10 => "Property", 11 => "Unit", 12 => "Value",
        13 => "Enum", 14 => "Keyword", 15 => "Snippet", 16 => "Color", 17 => "File", 18 => "Reference",
        19 => "Folder", 20 => "EnumMember", 21 => "Constant", 22 => "Struct", 23 => "Event",
        24 => "Operator", 25 => "TypeParameter", _ => "Unknown",
    };

    // ── URI(security/untrusted.ts 的 TS 镜像) ───────────────────

    /// <summary>
    /// 把 LSP 返回的文本包进一次性 untrusted 信封(随机 nonce),并先洗掉文本中已有的 untrusted
    /// 标签 —— 防止 LSP 内容伪造成可信信封注入对话(security/untrusted.ts 的 TS 镜像)。
    /// </summary>
    /// <param name="kind">内容类别标记(如 hover)。</param>
    /// <param name="source">来源标记(如 godot-lsp)。</param>
    /// <param name="body">待包裹的原始文本。</param>
    /// <returns>清洗并包裹后的文本。</returns>
    private static string WrapUntrusted(string kind, string source, string body)
    {
        var nonce = RandomNumberGeneratorHex8();
        var scrubbed = EnvelopeTagPattern().Replace(body, "[scrubbed-envelope-tag]");
        return $"<untrusted-{nonce} kind=\"{kind}\" source=\"{source}\">\n{scrubbed}\n</untrusted-{nonce}>";
    }

    /// <summary>生成 8 位十六进制随机串(4 随机字节),用作 untrusted 信封的一次性 nonce。</summary>
    /// <returns>8 个十六进制字符。</returns>
    private static string RandomNumberGeneratorHex8()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>匹配文本中的 untrusted 信封标签(开/闭、任意 nonce,大小写不敏感),供 WrapUntrusted 清洗。</summary>
    [GeneratedRegex("<\\s*/?\\s*untrusted(?:-[0-9a-f]*)?(?:\\s[^>]*)?\\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex EnvelopeTagPattern();

    /// <summary>匹配散文文本中的 file:// 链接(排除空白与常见收尾符号),供悬停文本的 res:// 改写。</summary>
    [GeneratedRegex("file://[^\\s)<>\"'`\\]]+")]
    private static partial Regex FileUriInTextPattern();

    // ── URI 转换(lsp/lspUri.ts 语义) ───────────────────────────

    /// <summary>res:// 路径 → 项目内绝对路径(分隔符按平台换算;非 res:// 前缀的输入去掉前缀逻辑不生效,原样拼接)。</summary>
    /// <param name="resPath">res:// 路径(或裸相对路径)。</param>
    /// <param name="projectPath">项目根绝对路径。</param>
    /// <returns>拼接后的绝对路径。</returns>
    public static string ResToAbsolute(string resPath, string projectPath)
    {
        var relative = resPath.StartsWith("res://", StringComparison.Ordinal) ? resPath[6..] : resPath;
        return Path.Combine(projectPath, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// file:// URI → res:// 路径:仅当路径位于项目根之内时改写(前缀比较忽略大小写与分隔符差异);
    /// Windows 盘符形式(/C:/...)先去掉前导斜杠;项目外或非 file:// 输入原样返回。
    /// </summary>
    /// <param name="uri">LSP 返回的 URI。</param>
    /// <param name="projectPath">项目根绝对路径。</param>
    /// <returns>res:// 路径,或原 URI。</returns>
    public static string FileUriToRes(string uri, string projectPath)
    {
        if (!uri.StartsWith("file://", StringComparison.Ordinal))
        {
            return uri;
        }
        var absolutePath = Uri.UnescapeDataString(uri[7..]);
        if (absolutePath.Length >= 2 && absolutePath[0] == '/' && char.IsLetter(absolutePath[1]) && absolutePath[2..].StartsWith(':'))
        {
            absolutePath = absolutePath[1..]; // Windows 盘符形式去掉前导斜杠。
        }
        var normalizedProject = projectPath.Replace('\\', '/').TrimEnd('/');
        var normalizedPath = absolutePath.Replace('\\', '/');
        if (normalizedPath.StartsWith(normalizedProject, StringComparison.OrdinalIgnoreCase))
        {
            return "res:/" + normalizedPath[normalizedProject.Length..];
        }
        return uri; // 项目外 —— 原样返回。
    }

    /// <summary>从 JSON 对象安全取字符串属性:非对象、缺键或类型不符返回 null。</summary>
    /// <param name="element">源 JSON 元素。</param>
    /// <param name="key">属性名。</param>
    /// <returns>字符串值或 null。</returns>
    private static string? GetString(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>从 JSON 对象安全取整数属性:非对象、缺键或非数字返回 null。</summary>
    /// <param name="element">源 JSON 元素。</param>
    /// <param name="key">属性名。</param>
    /// <returns>整数值或 null。</returns>
    private static int? GetInt(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
    }
}
