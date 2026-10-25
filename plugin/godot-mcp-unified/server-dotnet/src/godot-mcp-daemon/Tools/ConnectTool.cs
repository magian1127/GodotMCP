using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Sessions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// <c>godot_connect</c> —— 会话的唯一入口工具(ADR-0007)。未连接时它是工具面上<b>唯一</b>
/// 可见的工具;连接后工具面展开为 core 9 ∪ 所选组。
/// </summary>
/// <remarks>
/// <para><b>它是自救路径,因此不能报错</b>:会话键不存在时它<b>自行建键</b>并正常完成连接
/// (进入 <c>IdleNoStream</c>)。否则降级期会出现"连上了、有一个工具、一调就错",模型无从自愈。</para>
/// <para><b>响应只带组名与工具名</b>,不带 schema/description —— 那正是本次改造要削减的协议面。</para>
/// <para><b>无流键没有 <c>list_changed</c> 可推</b>:响应里直接带组与工具清单;生产 shim 由内部流
/// 在 ack 后推通知;不经 shim 的直连客户端必须显式再取一次 <c>tools/list</c>。</para>
/// </remarks>
/// <param name="keys">会话键注册表。</param>
/// <param name="accessor">当前请求的会话态(读键与 cwd)。</param>
/// <param name="groupService">组服务(激活组、通知工具面变更)。</param>
/// <param name="instances">实例管理器(解析目标、判在线)。</param>
[McpServerToolType]
public sealed class ConnectTool(
    SessionKeyRegistry keys,
    SessionKeyAccessor accessor,
    GroupService groupService,
    InstanceManager instances)
{
    /// <summary>未带会话键时的错误码。</summary>
    public const string MissingHeaderCode = "SESSION_HEADER_MISSING";

    /// <summary>会话键格式非法时的错误码。</summary>
    public const string InvalidKeyCode = "INVALID_SESSION_KEY";

    /// <summary>键数达到上限且无可淘汰项时的错误码。</summary>
    public const string SessionLimitCode = "SESSION_LIMIT";

    /// <summary>空参且找不到候选项目时的错误码。</summary>
    public const string NoCandidateProjectCode = "NO_CANDIDATE_PROJECT";

    /// <summary>目标已写入但当前无活跃实例时的提示码(连接本身仍成功)。</summary>
    public const string TargetNotRunningCode = "TARGET_NOT_RUNNING";

    /// <summary>
    /// 连接 Godot 项目并展开工具面。
    /// </summary>
    /// <param name="project">项目路径;省略时依次尝试本键默认目标、cwd 下的 project.godot、唯一活跃实例。</param>
    /// <param name="instance">实例短 id;与 project 二选一。</param>
    /// <param name="groups">要激活的组名;省略时保留现组(新键即空组 → 只得到 core 9)。</param>
    /// <returns>JSON 信封:success + project + session + groups + tools。</returns>
    [McpServerTool(Name = "godot_connect", ReadOnly = false, OpenWorld = false)]
    [Description(
        "连接到一个 Godot 项目并展开工具面。必须先调用本工具——未连接时其它工具都不存在。" +
        "project 省略时依次尝试：本会话上次连接的目标、当前工作目录下的 project.godot、唯一的活跃实例。" +
        "groups 省略时保留本会话已激活的组（新会话即空组，只得到 9 个只读工具）；传 groups 则替换为这些组。" +
        "工具名不带 godot_ 前缀（例如 capture_screenshot，不是 godot_capture_screenshot）。")]
    public Task<CallToolResult> Connect(
        string? project = null,
        string? instance = null,
        JsonElement? groups = null)
    {
        var context = accessor.Current;
        // 会话键优先从 GroupService 的 AsyncLocal 读:它由 ConfigureSessionOptions 写入,
        // 与工具 handler 共享同一执行上下文(已由 DiscoverTools 长期验证)。
        // accessor 的 HttpContext 路径在工具 handler 里读不到(不同上下文),仅作兜底。
        var key = groupService.CurrentSessionKey ?? context?.Key;
        if (string.IsNullOrEmpty(key))
        {
            // 无 header:走"未连接",且**不建键** —— 它建的键没人会再用,只会留一个孤儿键。
            return Task.FromResult(Error(MissingHeaderCode,
                "缺少会话标识（x-godot-session）。请通过 MCP 服务器（shim）连接，或设置 GODOT_MCP_DEBUG_UNKEYED=1 使用调试面。"));
        }

        // cwd 从 GroupService 的会话态读(与键**同载体**):ConfigureSessionOptions 每请求写入,
        // 首次 connect 也能拿到。注册表只是跨请求兜底 —— 首次 connect 时它还不会有这个键,
        // 从注册表读会静默拿到 null,让这条自救路径恰好在最该生效的时刻失效。
        var workingDirectory =
            groupService.CurrentWorkingDirectory
            ?? (keys.TryGetSnapshot(key, out var cwdSnapshot) ? cwdSnapshot.WorkingDirectory : null)
            ?? context?.WorkingDirectory;
        if (!SessionKeyRegistry.IsValidKey(key))
        {
            return Task.FromResult(Error(InvalidKeyCode, "会话标识格式非法（需 UUID 形状、不超过 64 字符）。"));
        }

        // 建键(幂等)。这同时是"键不存在时的自救路径"。
        var rejection = keys.EnsureKey(key, out _);
        if (rejection == SessionKeyRejection.CapacityExceeded)
        {
            var summary = string.Join("; ", keys.Snapshot()
                .Select(s => $"{s.ShortKey}:{s.State}/streams={s.StreamCount}/inflight={s.InFlight}"));
            return Task.FromResult(Error(SessionLimitCode,
                $"会话数已达上限（{keys.Policy.MaxSessionKeys}），且没有可回收的空闲会话。重启 daemon 可清空。当前：{summary}"));
        }
        if (rejection == SessionKeyRejection.InvalidKey)
        {
            return Task.FromResult(Error(InvalidKeyCode, "会话标识格式非法。"));
        }

        // 组与目标是两件独立的事:**先处理组,再解析目标**。
        // 否则目标解析失败(例如测试里没有活跃实例、cwd 也没有 project.godot)时提前返回,
        // 组就永远不会被设置 —— 模型会看到"连接失败"却又无法激活任何组,彻底卡死。
        if (groups is { ValueKind: JsonValueKind.Array } groupsArray)
        {
            var names = groupsArray.EnumerateArray()
                .Select(e => e.GetString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
            keys.SetGroups(key, names);
        }

        // 解析目标。
        var resolution = ResolveTarget(project, instance, key, workingDirectory);

        // 写入本键默认目标(ADR-0008)。目标解析失败时不清空已有默认目标。
        if (resolution.ProjectPath is not null)
        {
            keys.SetDefaultTarget(key, resolution.ProjectPath);
        }

        // 把本键的组同步进组服务(激活状态是每键的)。
        // 只记录**真正激活成功**的组:unsafe 组在未设 GODOT_MCP_UNSAFE=1 时会被门控拒绝,
        // 若把它也写进 keys,可见集就会包含一个 GroupService 从未建过工具对象的组,
        // 结果是"工具面里有名字、一调就 GROUP_NOT_ACTIVATED"。
        var requestedGroups = keys.TryGetSnapshot(key, out var snapshot) ? snapshot.Groups : [];
        var activated = new List<string>(requestedGroups.Length);
        foreach (var groupName in requestedGroups)
        {
            var status = groupService.Activate(groupName);
            if (status.Status is "activated" or "already_loaded")
            {
                activated.Add(groupName);
            }
        }
        if (activated.Count != requestedGroups.Length)
        {
            keys.SetGroups(key, activated);
            snapshot = keys.TryGetSnapshot(key, out var updated) ? updated : snapshot;
        }

        // **置位"已连接"必须放在组激活之后**:工具面的判据是它(见 SessionKeyEntry.Connected),
        // 而 Activate 在目录与表不一致时会抛异常。先置位就会留下"已连接但组只应用了一半"的键 ——
        // 会话既不是"未连接"也不是"可用",工具面出现 core 9 却与 discover_tools 报告不一致。
        //
        // 但它必须**无条件**执行且在任何返回路径之前:目标解析失败(NO_CANDIDATE_PROJECT /
        // INSTANCE_NOT_FOUND)只是"还不知道连哪个项目",会话本身已经完成连接。若那次失败吞掉置位,
        // 模型重试一个带 project 的 connect 之前会一直只看到 1 个工具,而它连 discover_tools 都没有。
        keys.MarkConnected(key);

        var activeGroups = activated.ToArray();

        // **必须走 groupService.VisibleToolNamesFor** —— 那是可见集的唯一入口
        // (core ∪ 内置组 ∪ 扩展组 ∪ 未分组扩展),也正好是 ToolFaceComposer 与
        // ToolCallFallback 用的那一个。
        //
        // 用 ToolFace.VisibleToolNames 会漏掉扩展组与未分组扩展:它只认 GroupCatalogue。
        // 后果是项目装了 MCP 扩展时,响应里的 tools 只有 core 9,而真实面已经包含扩展工具 ——
        // 模型据此判断"连上了但没拿到新工具",于是反复 discover_tools 或直接放弃。
        var visible = groupService.VisibleToolNamesFor(activeGroups);
        var ordered = ToolFace.OrderForListing(visible);

        // 工具面可能变了 → 通知(有流时由 listen 长流投递)。
        groupService.NotifyToolSurfaceChanged(key);

        var payload = new JsonObject
        {
            ["success"] = resolution.ErrorCode is null,
            ["session"] = snapshot.ShortKey ?? key[..Math.Min(8, key.Length)],
            ["groups"] = new JsonArray(activeGroups.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
            ["tools"] = new JsonArray(ordered.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["is_default"] = resolution.ProjectPath is not null,
        };
        if (resolution.ProjectPath is not null)
        {
            payload["project"] = resolution.ProjectPath;
        }

        // 目标解析失败不等于连接失败:组已经设好、工具面已经展开,只是"还没有具体项目"。
        // 这种情况回 isError + 可行动文案(模型据此补一个 project 参数),而不是让整个 connect 失败。
        if (resolution.ErrorCode is not null)
        {
            payload["code"] = resolution.ErrorCode;
            payload["message"] = resolution.ErrorMessage;
            return Task.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = payload.ToJsonString() }],
            });
        }

        if (resolution.TargetNotRunning)
        {
            payload["code"] = TargetNotRunningCode;
            payload["hint"] =
                $"目标项目当前没有活跃实例。请先启动 Godot：godot --headless --editor --path \"{resolution.ProjectPath}\"";
        }

        return Task.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = payload.ToJsonString() }],
        });
    }

    /// <summary>解析目标项目;失败时给出错误码与可行动文案。</summary>
    /// <param name="project">显式项目路径。</param>
    /// <param name="instance">显式实例短 id。</param>
    /// <param name="key">会话键(读默认目标)。</param>
    /// <param name="workingDirectory">shim 上报的 cwd。</param>
    /// <returns>解析结果。</returns>
    private Resolution ResolveTarget(string? project, string? instance, string key, string? workingDirectory)
    {
        var snapshot = instances.Snapshot();

        // ① 显式 project。
        if (!string.IsNullOrWhiteSpace(project))
        {
            var normalized = RegistryReader.Canonical(project);
            var online = snapshot.Any(c => string.Equals(c.Key, normalized, StringComparison.OrdinalIgnoreCase));
            return new Resolution(normalized, null, null, TargetNotRunning: !online);
        }

        // ② 显式 instance(12 位短 id 或项目路径)。
        if (!string.IsNullOrWhiteSpace(instance))
        {
            var byPath = snapshot.FirstOrDefault(c =>
                string.Equals(c.Key, RegistryReader.Canonical(instance), StringComparison.OrdinalIgnoreCase));
            var match = byPath ?? snapshot.FirstOrDefault(c =>
                string.Equals(RegistryReader.HashOf(c.Key), instance, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new Resolution(match.Key, null, null, TargetNotRunning: false);
            }
            return new Resolution(null, "INSTANCE_NOT_FOUND",
                $"找不到实例 {instance}。已连接：{DescribeInstances(snapshot)}", TargetNotRunning: false);
        }

        // ③ 本键默认目标。
        if (keys.TryGetSnapshot(key, out var session) && session.DefaultTarget is { } target)
        {
            var online = snapshot.Any(c => string.Equals(c.Key, target, StringComparison.OrdinalIgnoreCase));
            return new Resolution(target, null, null, TargetNotRunning: !online);
        }

        // ④ cwd 下的 project.godot(只做存在性校验,绝不递归扫描 —— 大工作区会卡)。
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var candidate = Path.Combine(workingDirectory, "project.godot");
            if (File.Exists(candidate))
            {
                return new Resolution(RegistryReader.Canonical(workingDirectory), null, null, TargetNotRunning: false);
            }
        }

        // ⑤ 唯一活跃实例。
        if (snapshot.Count == 1)
        {
            return new Resolution(snapshot[0].Key, null, null, TargetNotRunning: false);
        }

        // ⑥ 无候选。
        return new Resolution(null, NoCandidateProjectCode,
            "没有可连接的项目。可以：① 传 project 绝对路径；② 先让 Godot 打开该项目；③ 确认会话工作目录下有 project.godot。" +
            (snapshot.Count > 1 ? $" 当前活跃实例：{DescribeInstances(snapshot)}" : " 当前没有活跃实例。"),
            TargetNotRunning: false);
    }

    /// <summary>列出活跃实例的简短描述(错误文案用)。</summary>
    /// <param name="snapshot">实例快照。</param>
    /// <returns>可读清单。</returns>
    private static string DescribeInstances(List<InstanceConnection> snapshot)
    {
        return snapshot.Count == 0
            ? "(无)"
            : string.Join(", ", snapshot.Select(c => $"{RegistryReader.HashOf(c.Key)}={c.Key}"));
    }

    /// <summary>构造 <c>isError</c> 结果(绝不抛异常 —— 抛异常会变成 JSON-RPC 错误)。</summary>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">可行动文案。</param>
    /// <returns>错误结果。</returns>
    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = new JsonObject
                {
                    ["success"] = false,
                    ["code"] = code,
                    ["message"] = message,
                }.ToJsonString(),
            },
        ],
    };

    /// <summary>目标解析结果。</summary>
    /// <param name="ProjectPath">解析出的项目路径;失败为 null。</param>
    /// <param name="ErrorCode">失败时的错误码。</param>
    /// <param name="ErrorMessage">失败时的可行动文案。</param>
    /// <param name="TargetNotRunning">目标已写入但当前无活跃实例(连接本身仍算成功)。</param>
    private readonly record struct Resolution(
        string? ProjectPath,
        string? ErrorCode,
        string? ErrorMessage,
        bool TargetNotRunning);
}
