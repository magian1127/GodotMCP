using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Sessions;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// 按<b>本会话键</b>解析调用目标(ADR-0008 / spec §8)。
/// </summary>
/// <remarks>
/// <para><b>为什么独立成服务</b>:<see cref="Groups.GroupService"/> 构造时 <c>new</c> 了
/// <see cref="Groups.GroupToolInvoker"/>,若让 invoker 回调 GroupService 取默认目标,
/// 会形成构造环。本类只依赖 <see cref="SessionKeyAccessor"/> 与
/// <see cref="SessionKeyRegistry"/>,不碰 GroupService —— 环自然消失。</para>
///
/// <para><b>为什么读 <see cref="SessionKeyAccessor"/> 而不是裸 AsyncLocal</b>:
/// 实测在工具 handler 里 AsyncLocal 写入的值<b>读不到</b>(SDK 处理请求时建立了新的执行上下文),
/// 而 <c>HttpContext</c> 随请求传递、通过 <c>IHttpContextAccessor</c> 读得到。
/// <see cref="SessionKeyAccessor"/> 正是"先 HttpContext、再 AsyncLocal"的双载体,
/// 也是唯一被验证在两条路径上都可用的读法。</para>
///
/// <para><b>它不重新实现寻址规则</b>:把"本键默认目标"交给
/// <see cref="InstanceManager.ResolveConnection(string?, string?, out InstanceCallException?)"/>,
/// 规则仍归 ADR-0003 的既有归属。两处各写一套必然漂移。</para>
/// </remarks>
/// <param name="instances">实例管理器(持有连接表与寻址规则)。</param>
/// <param name="keys">会话键注册表(读本键默认目标)。</param>
/// <param name="scope">会话态访问器(双载体读法)。</param>
public sealed class SessionTargetResolver(
    InstanceManager instances,
    SessionKeyRegistry keys,
    SessionKeyAccessor scope)
{
    /// <summary>按"显式 instance &gt; 本键默认目标 &gt; 唯一活跃实例"解析目标。</summary>
    /// <param name="instance">显式实例标识;null/空表示不指定。</param>
    /// <param name="error">失败时的寻址错误(含 <c>TARGET_NOT_RUNNING</c>);成功为 null。</param>
    /// <returns>目标连接;失败为 null。</returns>
    internal InstanceConnection? Resolve(string? instance, out InstanceCallException? error)
        => instances.ResolveConnection(instance, DefaultTargetOfCurrentSession(), out error);

    /// <summary>取当前会话键的默认目标;无键、键不存在或未设置时为 null。</summary>
    /// <returns>规范化项目路径或 null。</returns>
    public string? DefaultTargetOfCurrentSession()
    {
        var key = scope.CurrentKey;

        // 无键时没有默认目标概念 —— 回滚面与调试面都走这条。
        if (string.IsNullOrEmpty(key) || !keys.TryGetSnapshot(key, out var snapshot))
        {
            return null;
        }

        return snapshot.DefaultTarget;
    }
}
