using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Extensions;
using GodotMcp.Daemon.Groups;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// discover_tools 元工具(issue 12;Node groups/groups.ts 处理器语义的 C# 移植;
/// issue 14:refresh_extensions 拉取扩展并投影):
/// 按名称/关键词查找并激活工具组;激活动作在当前请求会话的工具集合上批量进行
/// (DeferChangedEvents → 恰好一次 tools/list_changed);激活状态为 daemon 全局,
/// 每个新请求会话在 ConfigureSession 时对齐(无隐藏状态)。
/// </summary>
/// <param name="groups">工具组服务:激活/停用/模糊匹配/目录与工具面同步。</param>
/// <param name="extensions">扩展服务:从已连接实例拉取扩展并投影为工具组。</param>
[McpServerToolType]
public sealed class DiscoverTools(GroupService groups, ExtensionService extensions)
{
    /// <summary>
    /// discover_tools:按名称或关键词查找并激活工具组,也是唯一的组停用(reset)与扩展组刷新入口。
    /// <para>逻辑链:refresh_extensions=true 时先对全部已连接实例拉取扩展(编辑器不可达不算错误,
    /// 登记 0 条)→ 进入 DeferChangedEvents 批:阶段 1 处理 reset(true 全停/数组按名停用)→ 阶段 2
    /// 处理请求:已知组名精确处理(activate=false 时仅报告不激活),未知元素逐词模糊匹配并截断
    /// (超额以 hint 提示,已入列的组跳过)→ 批内同步工具面并刷新 discover_tools 描述,出批时
    /// 真实变化恰好触发一次 tools/list_changed → 无参数(或空数组)且未 reset 时改返回完整目录
    /// (只浏览,不激活)→ 组装响应:groups 逐组状态与工具元数据(include_schemas 控制是否附参数
    /// schema)、deactivated 清单与重激活提示、扩展刷新摘要、已加载组超过 5 个时的 warning。</para>
    /// </summary>
    /// <param name="request">组名或领域关键词;字符串/数组均可,缺省返回完整目录。</param>
    /// <param name="activate">是否激活匹配到的组(默认 true;false 时仅浏览不激活)。</param>
    /// <param name="include_schemas">响应的工具元数据是否附完整参数结构,默认 false。</param>
    /// <param name="reset">true 停用全部工具组;字符串数组仅停用列出的组。</param>
    /// <param name="refresh_extensions">添加/编辑/删除项目扩展后置 true:扫描编辑器文件系统并同步扩展工具组。</param>
    /// <returns>JSON 信封:success + groups(逐组状态与工具列表),可附 hint/warning/extension_refresh/deactivated/deactivated_tools。</returns>
    [McpServerTool(Name = "discover_tools", ReadOnly = true, OpenWorld = false)]
    [Description(
        "按名称或领域关键词查找并激活工具组。仅激活当前任务需要的组，建议不超过约 5 个；同时加载过多工具组会挤满工具列表并降低回答质量。不传参数时返回完整目录。reset: true 停用全部工具组；reset: ['group_a'] 仅停用 group_a。添加、编辑或删除项目扩展后，请设置 refresh_extensions:true。")]
    public async Task<CallToolResult> Discover(
        JsonElement? request = null,
        bool? activate = null,
        bool? include_schemas = null,
        JsonElement? reset = null,
        bool? refresh_extensions = null)
    {
        var effectiveActivate = activate != false; // Node zod default(true)
        var includeSchemas = include_schemas == true;

        // refresh_extensions:对全部已连接实例拉取扩展(Node refreshExtensions 回调;
        // 编辑器不可达不算错误 —— 继续执行,登记 0 条)。
        ExtensionRefreshSummary? refreshSummary = null;
        if (refresh_extensions == true)
        {
            refreshSummary = await extensions.RefreshAllAsync(CancellationToken.None);
        }

        var groupResults = new List<(GroupStatus Status, string? Match)>();
        var deactivated = new List<string>();
        string? fuzzyHint = null;
        var resetActive = reset is { } resetEl
            && resetEl.ValueKind is JsonValueKind.True or JsonValueKind.Array;
        var requestIsEmpty = request is { ValueKind: JsonValueKind.Array } requestArrayProbe
            && requestArrayProbe.GetArrayLength() == 0;

        var collection = groups.CurrentSessionCollection;
        // 单批:激活/停用与工具面同步在同一 DeferChangedEvents 内完成 → 恰好一次 list_changed。
        using (collection?.DeferChangedEvents())
        {
            // 阶段 1:重置/停用(false 与省略等效)。
            if (resetActive && reset!.Value.ValueKind == JsonValueKind.True)
            {
                deactivated.AddRange(groups.Deactivate(names: null));
            }
            else if (resetActive)
            {
                var names = reset!.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                deactivated.AddRange(groups.Deactivate(names));
            }

            // 阶段 2:请求——精确名称直接处理,未识别元素走模糊关键词(含扩展组)。
            if (request is { } requestEl && !requestIsEmpty)
            {
                var elements = GroupService.CoerceRequest(requestEl);
                var exactNames = elements.Where(groups.IsKnownGroup).ToList();
                var fuzzyNames = elements.Where(e => !groups.IsKnownGroup(e)).ToList();

                foreach (var name in exactNames)
                {
                    var status = effectiveActivate ? groups.Activate(name) : groups.Report(name);
                    groupResults.Add((status, "exact_name"));
                }

                if (fuzzyNames.Count > 0)
                {
                    var perKeyword = new Dictionary<string, List<(string Name, int Score)>>(StringComparer.Ordinal);
                    foreach (var keyword in fuzzyNames)
                    {
                        perKeyword[keyword] = groups.FindMatchesSingle(keyword);
                    }
                    var (selected, additionalCount) = GroupService.CapFuzzyResults(perKeyword);
                    foreach (var name in selected)
                    {
                        if (groupResults.Any(r => r.Status.Name == name))
                        {
                            continue;
                        }
                        var status = effectiveActivate ? groups.Activate(name) : groups.Report(name);
                        groupResults.Add((status, "loose_keyword"));
                    }
                    if (additionalCount > 0)
                    {
                        fuzzyHint = $"另有 {additionalCount} 个匹配工具组未激活，请缩小请求范围或传入准确的组名。";
                    }
                }
            }

            // 工具面对齐 + discover_tools 描述刷新(都在批内 → 与通知原子)。
            if (collection is not null)
            {
                groups.SyncCollection(collection);
                if (collection["discover_tools"] is { } discoverTool)
                {
                    discoverTool.ProtocolTool.Description = groups.BuildDiscoverToolsDesc();
                }
            }
        }

        // 真实变化才通报(幂等激活/空重置不触发):一批至多一次,与 Node batchToolRegistration 同纪律。
        // 该信号由挂起的 subscriptions/listen 长流投递为 tools/list_changed(issue 12);
        // 扩展刷新带来的登记同样计入(issue 14)。
        if (deactivated.Count > 0
            || groupResults.Any(r => r.Status.Status == "activated")
            || refreshSummary is { Registered: > 0 } or { Deferred: > 0 })
        {
            groups.NotifyToolSurfaceChanged();
        }

        // 无参数(或空数组且未带 reset)→ 完整目录(不激活)。
        var catalogRequested = request is null || requestIsEmpty;
        if (catalogRequested && !resetActive)
        {
            groupResults = groups.Catalog().Select(s => (s, (string?)null)).ToList();
        }
        if (requestIsEmpty && resetActive && fuzzyHint is null)
        {
            fuzzyHint = "指定的工具组已重置。不传参数调用 discover_tools() 可浏览完整目录。";
        }

        // 响应组装(Node 同形)。
        var groupsArray = new JsonArray();
        foreach (var (status, match) in groupResults)
        {
            var entry = new JsonObject
            {
                ["name"] = status.Name,
                ["status"] = status.Status,
            };
            var toolsArray = new JsonArray();
            foreach (var toolName in status.Tools)
            {
                toolsArray.Add(
                    groups.TryGetToolObject(toolName, out var toolObject)
                        ? GroupService.BuildToolMeta(toolObject, includeSchemas)
                        : new JsonObject { ["name"] = toolName });
            }
            entry["tools"] = toolsArray;
            if (status.Description is not null)
            {
                entry["description"] = status.Description;
            }
            if (match is not null)
            {
                entry["match"] = match;
            }
            groupsArray.Add(entry);
        }

        var response = new JsonObject
        {
            ["success"] = true,
            ["groups"] = groupsArray,
        };
        if (fuzzyHint is not null)
        {
            response["hint"] = fuzzyHint;
        }
        if (refreshSummary is not null)
        {
            // Node createExtensionDiscovery 结算摘要同形:{registered, deferred, commands, hint?}。
            var summary = new JsonObject
            {
                ["registered"] = refreshSummary.Registered,
                ["deferred"] = refreshSummary.Deferred,
                ["commands"] = refreshSummary.Commands,
            };
            if (refreshSummary.Hint is not null)
            {
                summary["hint"] = refreshSummary.Hint;
            }
            response["extensions_refreshed"] = true;
            response["extension_refresh"] = summary;
        }
        if (deactivated.Count > 0)
        {
            response["deactivated"] = new JsonArray(deactivated.Select(d => JsonValue.Create(d)).ToArray());
            if (resetActive && reset!.Value.ValueKind == JsonValueKind.True)
            {
                response["reset_all"] = true;
            }
            var deactivatedTools = new JsonArray();
            foreach (var groupName in deactivated)
            {
                if (GroupCatalogue.Find(groupName) is { } groupDef)
                {
                    foreach (var toolName in groupDef.ToolNames.Where(NodeToolTable.Contains))
                    {
                        deactivatedTools.Add(toolName);
                    }
                }
                else if (extensions.TryGetGroup(groupName) is { } extGroup)
                {
                    foreach (var toolName in extGroup.ToolNames)
                    {
                        deactivatedTools.Add(toolName);
                    }
                }
            }
            if (deactivatedTools.Count > 0)
            {
                response["deactivated_tools"] = deactivatedTools;
            }
            response["hint"] ??= "已停用的工具无法调用。使用前请调用 discover_tools(request=[...]) 重新激活。";
        }

        var totalLoaded = groups.LoadedCount;
        if (totalLoaded > 5)
        {
            response["warning"] =
                $"当前已加载 {totalLoaded} 个工具组，较多工具会占用上下文并可能降低回答质量。" +
                "请优先仅激活当前任务需要的组，使用 reset 停用不再需要的组。";
        }

        return ToolResults.Json(response.ToJsonString());
    }
}
