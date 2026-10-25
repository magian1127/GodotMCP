using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon;

/// <summary>
/// <c>tools/list</c> 的缓存语义与目录元数据(2026-07-28 §2.4)。
/// </summary>
/// <remarks>
/// <para><b>为什么必须显式设</b>:SDK 对缺失的 <c>ttlMs</c>/<c>cacheScope</c> 只 warn 不 fail,
/// 客户端却会按自己的默认值缓存 —— 工具面会"该变的时候不变"。</para>
/// <para><b><c>ttlMs = 0</c></b>:本服务器的工具面<b>按会话键</b>且随 <c>godot_connect</c>/
/// <c>discover_tools</c> 即时变化,任何正值都意味着客户端会拿到过期目录。</para>
/// <para><b><c>cacheScope = Private</c></b>:目录含"本会话可见的工具",<b>绝不可跨会话共享</b>。
/// 用 <c>Public</c> 会让中间层缓存把 A 会话的工具面喂给 B 会话 —— 那是跨会话越权。</para>
/// <para><b>顺序稳定性</b>:集合填充顺序已由 <c>ToolFace.OrderForListing</c> 保证
/// (<c>godot_connect</c> 恒第一,其余按 Node 表序),所以这里不再重排。</para>
/// </remarks>
internal static class ToolListMetadata
{
    /// <summary>本服务器的目录缓存有效期:恒 0(工具面随时可能因连接/激活而变)。</summary>
    public static readonly TimeSpan NoCaching = TimeSpan.Zero;

    /// <summary>目录的缓存作用域:恒 <see cref="CacheScope.Private"/>。</summary>
    public const CacheScope Scope = CacheScope.Private;

    /// <summary>把缓存语义写进一次 <c>tools/list</c> 的结果。</summary>
    /// <param name="result">SDK 产出的结果(集合已填充完毕)。</param>
    public static void Apply(ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.TimeToLive = NoCaching;
        result.CacheScope = Scope;
    }

    /// <summary>
    /// 把工具列表排成"<c>godot_connect</c> 恒第一,其余按 Node 表序,表外的排在最后(按名字)"。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须在结果上重排</b>:<c>McpServerPrimitiveCollection.TryAdd</c> 与 SDK 的合并
    /// 都不保证顺序,实测出来是 <c>log_read, scene_query, list_instances, …</c> 这种字典序。
    /// 模型第一眼看不到入口工具,而 spec §2.3 要求目录顺序稳定且入口恒第一。</para>
    /// <para>表外工具(扩展工具)按名字排在最后 —— 它们不属于任何组,没有表序可用。</para>
    /// </remarks>
    /// <param name="result">SDK 产出的结果。</param>
    public static void Order(ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Tools is not { Count: > 1 } tools)
        {
            return;
        }

        var rank = Groups.ToolFace.TableOrderIndex;
        result.Tools = tools
            .OrderBy(t => t.Name == Groups.ToolFace.ConnectToolName ? -1 : 0)
            .ThenBy(t => rank.TryGetValue(t.Name, out var index) ? index : int.MaxValue)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 构造挂到 <c>Filters.Request.ListToolsFilters</c> 上的过滤器:补齐缓存语义并稳定排序。
    /// </summary>
    /// <returns>可直接加入过滤器列表的委托。</returns>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            Apply(result);
            Order(result);
            return result;
        };
    }
}
