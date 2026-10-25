using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Sessions;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon;

/// <summary>
/// 每个请求的工具面组装器(ADR-0007)。把 <c>ToolCollection</c> 裁剪到"本会话键可见的集合"。
/// </summary>
/// <remarks>
/// <para><b>实现方式不是 deferred eager 包装</b>:工具类一个都不动,34 个 <c>[McpServerTool]</c>
/// 照旧由 <c>WithToolsFromAssembly</c> 注册;这里每请求把不在可见集里的从该请求的 collection 移除。
/// 在 stateless 下 <c>ConfigureSessionOptions</c> 每请求收到新的 options 与新的 collection,
/// 所以移除只影响本请求。</para>
/// <para><b>但工具对象是 DI 单例</b>:collection 是每请求新的,里面的工具对象是同一批。
/// 因此<b>禁止按会话改写</b> Description/schema —— 那会串到别的会话。
/// <c>NodeToolTable.ApplyProtocolSchemas</c> 写的是对所有会话相同的协议 schema(幂等且必需),
/// 不算违规;判据是"写入内容是否与会话/键相关"。</para>
/// <para><b>锁序</b>:先取会话键注册表快照,再进 <c>GroupService._gate</c> 组装。
/// 不得在 <c>_gate</c> 内回取注册表,否则与"激活/通知先注册表后 gate"形成 ABBA。</para>
/// </remarks>
/// <param name="keys">会话键注册表。</param>
/// <param name="groups">组服务(激活状态、可见集与工具对象)。</param>
/// <param name="diagnostics">会话诊断日志(工具面尺寸)。</param>
internal sealed class ToolFaceComposer(
    SessionKeyRegistry keys,
    GroupService groups,
    SessionKeyDiagnostics diagnostics)
{
    /// <summary>
    /// 组装本请求的工具面。
    /// </summary>
    /// <param name="httpContext">当前请求(读 header 判定调试面与路由方法)。</param>
    /// <param name="collection">本请求的工具集合(由 SDK 每请求新建)。</param>
    public void Compose(HttpContext httpContext, McpServerPrimitiveCollection<McpServerTool> collection)
    {
        // 与中间件同一套归一化:两处必须得到同一个键,否则工具面按 A 裁、鉴权按 B 判。
        var rawKey = httpContext.Request.Headers[SessionKeyMiddleware.HeaderName].ToString();
        var key = string.IsNullOrEmpty(rawKey) ? rawKey : SessionKeyRegistry.Normalize(rawKey);
        var hasValidKey = SessionKeyRegistry.IsValidKey(key);
        var debugEnabled = string.Equals(
            Environment.GetEnvironmentVariable("GODOT_MCP_DEBUG_UNKEYED"),
            "1",
            StringComparison.Ordinal);

        // 调试面:仅对"无 header"的请求生效,且需要显式 env。带合法 header 的请求不受它影响 ——
        // 否则一个 env 开关就能把已连接会话的工具面也放大到全量并跳过 unsafe 门控。
        var debugFace = debugEnabled && !hasValidKey;

        // 回滚开关(spec §6.1):GODOT_MCP_TOOLFACE=eager 退回"全局组激活"的旧工具面。
        // 只回滚**工具面**,不回滚会话键、寻址、通知 —— 那三者是数据面,回滚会破坏契约。
        var eagerRollback = string.Equals(
            Environment.GetEnvironmentVariable("GODOT_MCP_TOOLFACE"),
            "eager",
            StringComparison.Ordinal);

        // ① 先取注册表快照(锁序:注册表 → _gate)。
        //    **"已连接"的判据是 entry.Connected,不是"键存在"**:
        //    listen 长流与任意合法 UUID 都会建键,若按"键存在"判定,它们会直接看到 core 9,
        //    "未连接只看到 godot_connect"这条核心需求立刻失效。
        var hasSession = false;
        string[]? activeGroups = null;
        if (hasValidKey && keys.TryGetSnapshot(key, out var snapshot))
        {
            hasSession = snapshot.Connected;
            activeGroups = hasSession ? snapshot.Groups : null;
        }

        // cwd 与键**同载体**(GroupService 的 AsyncLocal),这样首次 godot_connect 也能拿到它 ——
        // 注册表要等到 handler 里的 EnsureKey 才有这个键,从注册表读会静默拿到 null。
        var cwd = SessionKeyMiddleware.ReadWorkingDirectory(httpContext);

        // ①b 回滚:不看会话键,直接按组服务的**全局**激活状态同步(旧行为)。
        //     必须显式传 DefaultKey(空串)而不是会话键 —— 传会话键会让 LoadedLocked 仍读
        //     `_loadedByKey[key]` 这个每键桶,两键各激活各的,等于没回滚。
        //     `x-godot-session` 仍被接受(否则 shim 无法工作),只是不参与裁剪。
        if (eagerRollback)
        {
            groups.ConfigureSession(collection, GroupService.DefaultKey, cwd);
            groups.SyncCollection(collection);
            NodeToolTable.ApplyProtocolSchemas(collection);
            groups.ApplyVersionGates(collection);
            return;
        }

        // ② 全量:调试面。此时不裁剪、不跳过任何工具,并让 unsafe 组可见(它是"能测任何工具"的调试面)。
        if (debugFace)
        {
            groups.ConfigureSession(collection, null, cwd);
            NodeToolTable.ApplyProtocolSchemas(collection);
            return;
        }

        // ③ 计算可见集。**必须走 GroupService.VisibleToolNamesFor** ——
        //    那是可见集的唯一入口(core ∪ 内置组 ∪ 扩展组 ∪ 未分组扩展)。
        //    这里自己拼一遍会让未命中兜底(ToolCallFallback)拿到另一份集合,
        //    后果是"工具在面上、一调却说它不存在/组未激活"。
        var visible = groups.VisibleToolNamesFor(hasSession ? activeGroups : null);

        var order = ToolFace.OrderForListing(visible);

        // ④ 在 _gate 内:确保组工具对象存在、按可见集填集合、换协议 schema。
        //    同时把会话键写进 GroupService 的 AsyncLocal —— 工具实现靠它知道"我是哪个键",
        //    这条路径与 _sessionCollection 相同(已验证工具里读得到)。
        groups.ComposeFace(collection, hasSession, visible, order, hasValidKey ? key : null, cwd);

        // ④b 常驻工具的 schema 换用表内 zod 派生版(issue 13 parity)。
        //     组工具构造时已带表内 schema,这一步只覆盖 eager 面;漏了它会让
        //     attribute 派生 schema(含 nullable 形态)留在面上,与表内定义不一致。
        NodeToolTable.ApplyProtocolSchemas(collection);

        // ⑤ 版本门控(已连接时才有意义):实例集无法提供的门控工具从本请求隐藏。
        if (hasSession)
        {
            groups.ApplyVersionGates(collection);
        }

        // ⑥ 工具面尺寸落 Debug 日志。默认级别不输出,但一旦把日志调高,
        //    "面塌了"就有据可查 —— 这是可观测性里唯一能回答
        //    "本请求究竟看见了几个工具"的一条(其余日志只讲键的状态与回收)。
        diagnostics.ToolFace(
            hasValidKey ? (key.Length <= 8 ? key : key[..8]) : null,
            hasSession,
            collection.Count);
    }
}
