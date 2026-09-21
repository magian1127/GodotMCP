using System.ComponentModel;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 实例清单查询工具(issue 05 起返回真实实例表:项目路径、短 id、引擎版本、端口、pid、连通性)。
/// 经 DI 注入实例管理器(与后台 reconcile 循环同一实例)。
/// </summary>
/// <param name="instances">实例管理器(依赖注入;与后台 reconcile 循环共享同一实例表)。</param>
[McpServerToolType]
public sealed class InstanceQueryTools(InstanceManager instances)
{
    /// <summary>list_instances:返回实例管理器的机器级实例表 JSON(项目路径、短 id、端口、pid、引擎版本、连通状态),原样透传。</summary>
    /// <returns>形如 {instances:[...]} 的 JSON 字符串。</returns>
    [McpServerTool(Name = "list_instances")]
    [Description("列出当前机器上全部活跃的 Godot 实例(含项目路径、短 id、引擎版本、端口、pid、连通状态)。")]
    public string ListInstances()
    {
        return instances.ListInstancesJson();
    }
}
