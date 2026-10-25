namespace GodotMcp.Daemon;

/// <summary>
/// 本 daemon 进程的<b>唯一身份</b>（每次启动生成，进程生命周期内不变）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：会话键活在 daemon 内存里，daemon 一重启就清空。
/// shim 必须能可靠判断"我手里的会话状态还算数吗"。在它之前，唯一可用的判据是
/// "请求失败了"—— 那个判据两个方向都错：</para>
/// <list type="bullet">
/// <item><b>漏判</b>：token 跨重启稳定、端口固定，新 daemon 只要先就绪，下一条请求
/// 就是 200（未知键 → 只剩 <c>godot_connect</c>）。shim 察觉不到重启，
/// 用户静默丢掉所有已激活的组 —— 而这恰恰是本该被消灭的现场。
/// 典型时序：两个宿主共用一个 daemon，另一个宿主或 addon 先把 daemon 拉起来。</item>
/// <item><b>误判</b>：笔记本睡眠、杀毒卡顿、调试器暂停 30 秒都会让一次短请求超时，
/// 但 daemon 与键表其实完好。若据此重放，重放的 <c>godot_connect(groups)</c> 会
/// <b>替换</b>组集，把活会话的组静默裁掉。</item>
/// </list>
/// <para>有了 epoch，判据变成"daemon 身份变了没有"，与请求成功与否无关 —— 两个方向都准。</para>
/// <para>取值不落盘、不上报给客户端，只作为响应头给 shim 比对；它不是凭据，
/// 泄露也没有安全影响（只是一个随机标识）。</para>
/// </remarks>
public sealed class DaemonEpoch
{
    /// <summary>承载 epoch 的响应头名。</summary>
    public const string HeaderName = "x-godot-daemon-epoch";

    /// <summary>本进程的身份。</summary>
    public string Value { get; } = Guid.NewGuid().ToString("N");
}
