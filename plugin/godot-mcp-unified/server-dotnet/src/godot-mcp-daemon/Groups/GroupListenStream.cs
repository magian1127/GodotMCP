using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Groups;

/// <summary>
/// subscriptions/listen 的 daemon 侧实现(SEP-2575,2026-07-28 修订):无状态 HTTP 下
/// 服务端无法主动推送,tools/list_changed 只能沿某个已挂起的 listen 长请求的响应流投递。
/// 本类独占该长流 —— 先发唯一一条 acknowledged(声明授予的订阅类型),再把
/// GroupService 的工具面变更逐条标注订阅 id 写入,直到客户端断开。
/// </summary>
internal static class GroupListenStream
{
    /// <summary>服务一条 listen 请求;仅当授予了通知类型时才挂起长流。</summary>
    /// <para>逻辑链:按请求裁剪授予集(仅 toolsListChanged,能力旗标与实际投递一致)
    /// → 先发唯一一条 acknowledged(声明授予的订阅类型)→ 未授予任何类型 → 立即返回
    /// EmptyResult 不占流;已授予 → 订阅 GroupService.ToolSurfaceChanged,把每条变更
    /// 经本请求响应流投递,并挂起等待客户端断开(cancellationToken 触发收尾退订)。</para>
    /// <param name="groups">组服务,提供工具面变更事件源。</param>
    /// <param name="server">请求绑定的 McpServer(消息自动路由回本请求的响应流)。</param>
    /// <param name="subscriptionId">listen 请求的 JSON-RPC id,标注在通知 _meta 供多路分解。</param>
    /// <param name="requested">客户端请求订阅的通知类型;null 视为未订阅任何类型。</param>
    /// <param name="cancellationToken">客户端断开时取消,驱动长流收尾退订。</param>
    /// <returns>EmptyResult;长流挂起时仅在断开后返回。</returns>
    public static async ValueTask<EmptyResult> ServeAsync(
        GroupService groups,
        McpServer server,
        RequestId subscriptionId,
        SubscriptionsListenNotifications? requested,
        CancellationToken cancellationToken)
    {
        // 只授予本 daemon 确实会投递的类型(能力旗标与实际投递一致,issue 12)。
        var granted = new SubscriptionsListenNotifications
        {
            ToolsListChanged = requested?.ToolsListChanged == true ? true : null,
        };
        await SendAsync(server, NotificationMethods.SubscriptionsAcknowledgedNotification,
            AckParams(granted), subscriptionId, cancellationToken).ConfigureAwait(false);
        if (granted.ToolsListChanged != true)
        {
            // 无任何可投递类型:不挂起连接(与 SDK 无状态内建行为一致,避免空占请求与响应流)。
            return new EmptyResult();
        }

        // 订阅工具面变更 —— 每条变更经此长流投递;客户端断开(cancellationToken)即退订收尾。
        using (groups.SubscribeToolSurface(() => _ = SendListChangedAsync(server, subscriptionId)))
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), closed))
            {
                await closed.Task.ConfigureAwait(false);
            }
        }
        return new EmptyResult();
    }

    /// <summary>发送一条 tools/list_changed;投递失败只可能源于该流已关闭,不影响其他监听者。</summary>
    /// <param name="server">请求绑定的 McpServer(路由回本请求响应流)。</param>
    /// <param name="subscriptionId">订阅 id,写入通知 _meta 供客户端多路分解。</param>
    private static async Task SendListChangedAsync(McpServer server, RequestId subscriptionId)
    {
        try
        {
            await SendAsync(server, NotificationMethods.ToolListChangedNotification,
                paramsNode: null, subscriptionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 客户端已断开/流已废弃:与 SDK 逐订阅扇出同策略,单流失败静默跳过。
        }
    }

    /// <summary>
    /// 构造并发送一条订阅通知:params/_meta 标注订阅 id,供共享通道的客户端多路分解(SEP-2575)。
    /// 目标 McpServer 为请求绑定的 DestinationBoundMcpServer —— 消息自动路由回本请求的响应流。
    /// </summary>
    /// <param name="server">目标 McpServer(请求绑定,消息路由回对应响应流)。</param>
    /// <param name="method">通知方法名(acknowledged 或 tools/list_changed)。</param>
    /// <param name="paramsNode">通知 params;null 时建空对象,统一补 _meta。</param>
    /// <param name="subscriptionId">订阅 id(id 为字符串或 long,均原样写入 _meta)。</param>
    /// <param name="cancellationToken">发送取消令牌。</param>
    /// <returns>发送任务。</returns>
    private static Task SendAsync(
        McpServer server,
        string method,
        JsonNode? paramsNode,
        RequestId subscriptionId,
        CancellationToken cancellationToken)
    {
        var paramsObject = paramsNode as JsonObject ?? new JsonObject();
        if (paramsObject["_meta"] is not JsonObject meta)
        {
            meta = new JsonObject();
            paramsObject["_meta"] = meta;
        }
        meta[MetaKeys.SubscriptionId] = subscriptionId.Id switch
        {
            string stringId => JsonValue.Create(stringId),
            long longId => JsonValue.Create(longId),
            _ => null,
        };

        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = paramsObject,
        };
        return server.SendMessageAsync(notification, cancellationToken);
    }

    /// <summary>acknowledged 的线上参数形:{notifications:{toolsListChanged:true}}(协议固定字段名)。</summary>
    /// <param name="granted">实际授予的通知类型集合。</param>
    /// <returns>序列化后的 acknowledged params 节点。</returns>
    private static JsonNode? AckParams(SubscriptionsListenNotifications granted)
    {
        return JsonSerializer.SerializeToNode(
            new SubscriptionsAcknowledgedNotificationParams { Notifications = granted },
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(SubscriptionsAcknowledgedNotificationParams)));
    }
}
