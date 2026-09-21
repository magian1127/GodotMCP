using System.ComponentModel;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 只读状态工具(issue 15;spec US18):操作队列可见性(排队/执行中)与场景租约持有情况。
/// 数据源限定为 daemon 可观测信号(在途请求表 + toolkit 的 _queued/_executing 进度通知);
/// 租约不可查询之处如实标注,不伪造精确数据。全部只读,无互锁、无变异副作用。
/// </summary>
/// <param name="instances">实例管理器:在途请求表数据源与实例寻址入口。</param>
[McpServerToolType]
public sealed class StateTools(InstanceManager instances)
{
    [McpServerTool(Name = "list_operations")]
    [Description(
        "列出经本 daemon 观测到的在途操作(在飞/排队/执行中)。数据来源:daemon 在途请求表 + " +
        "toolkit 的 _queued/_executing 进度通知;只读、无副作用。局限:仅涵盖经本 daemon 发起的调用;" +
        "场景租约状态不可查询(线路契约 C1-C22 冻结且无查询面)——排队中的请求是否在等租约无法区分," +
        "lease 字段如实标注。省略 instance 时返回全部实例(daemon 全局只读视图);给定 instance 时" +
        "按寻址规则严格解析。")]
    /// <summary>列出经本 daemon 观测到的在途操作(在飞/排队/执行中),只读无副作用。</summary>
    /// <para>逻辑链:委托 InstanceManager.ListOperationsJson 汇总在途请求表与 toolkit
    /// 进度通知 → error 为 null 时原样返回 JSON;否则把 InstanceCallException 映射为
    /// isError 错误信封。</para>
    /// <param name="instance">给定实例时按寻址规则严格解析;省略时返回全部实例(daemon 全局只读视图)。</param>
    /// <returns>在途操作 JSON(含 lease 字段的如实标注)或错误信封。</returns>
    public Task<CallToolResult> ListOperations(string? instance = null)
    {
        var json = instances.ListOperationsJson(instance, out var error);
        return Task.FromResult(error is null ? ToolResults.Json(json!) : ToolResults.FromException(error));
    }
}
