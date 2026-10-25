using System.Collections.Concurrent;

namespace GodotMcp.Daemon.Sessions;

/// <summary>建键/加流失败的原因;调用方据此选择 HTTP 语义与错误码。</summary>
public enum SessionKeyRejection
{
    /// <summary>未拒绝。</summary>
    None,

    /// <summary>键格式非法(非 UUID 形状或超长)。对应 <c>INVALID_SESSION_KEY</c> / HTTP 400(listen)。</summary>
    InvalidKey,

    /// <summary>键数达到上限且没有可淘汰项。对应 <c>SESSION_LIMIT</c> / HTTP 503(listen)。</summary>
    CapacityExceeded,
}

/// <summary>
/// 会话键注册表 —— 无状态 HTTP 下唯一持有"会话身份"的地方(ADR-0006)。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>:MCP 2026-07-28 移除了会话握手与 <c>Mcp-Session-Id</c>,协议不提供
/// "这个请求属于哪个会话"。GodotMCP 的工具面、组、默认目标都需要会话级隔离,所以身份由 shim
/// 生成 UUID 并经 <c>x-godot-session</c> 头携带,daemon 在这里按键保存状态。</para>
/// <para><b>三态</b>:见 <see cref="SessionKeyState"/>。不变式 <c>Streams.Count &gt; 0 ⇒ Active</c>
/// 由 <see cref="AddStream"/> / <see cref="RemoveStream"/> 共同维护,是淘汰与过期判定的前提。</para>
/// <para><b>锁序</b>:本注册表的锁<b>先于</b> <c>GroupService._gate</c>。工具面填充开始时先取一份
/// 本注册表快照,<b>不得在 <c>_gate</c> 内回取本注册表</b>,否则与"激活/通知先 registry 后 gate"
/// 的路径形成 ABBA。</para>
/// <para><b>时钟</b>:用 <see cref="TimeProvider.GetTimestamp"/> 的单调刻度,不用墙上时间 ——
/// NTP 回跳会让键永不过期或瞬间过期。</para>
/// </remarks>
public sealed class SessionKeyRegistry(TimeProvider timeProvider, SessionKeyPolicy policy)
{
    /// <summary>键总表。用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 只为了让快照/枚举免锁,</summary>
    /// <remarks>所有状态迁移仍在 <see cref="_gate"/> 内完成,避免"读到半迁移状态"。</remarks>
    private readonly ConcurrentDictionary<string, SessionKeyEntry> _entries = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    /// <summary>成功读写的计数器,供测试断言"某请求没有建键"。</summary>
    private long _createdCount;

    /// <summary>当前键数(供 <c>--status</c> 与上限判定)。</summary>
    public int Count => _entries.Count;

    /// <summary>累计建键次数;测试用它验证"无 header 请求不建键"。</summary>
    public long CreatedCount => Interlocked.Read(ref _createdCount);

    /// <summary>当前策略(启动日志会打印实际值)。</summary>
    public SessionKeyPolicy Policy => policy;

    /// <summary>
    /// 校验键格式:UUID 形状且不超过 64 字符。<b>不接受任意字符串建键</b> —— 否则内存可被撑大,
    /// 且运维看到一堆无法归档的裸串。
    /// </summary>
    /// <param name="key">待校验的键。</param>
    /// <returns>合法时 true。</returns>
    public static bool IsValidKey(string? key)
    {
        return key is { Length: > 0 and <= 64 } && Guid.TryParse(key, out _);
    }

    /// <summary>
    /// 把会话键归一化成唯一文本形态(<c>D</c> 格式:小写 <c>8-4-4-4-12</c>)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须归一化</b>:<see cref="Guid.TryParse(string, out Guid)"/> 同时接受
    /// <c>{...}</c>、<c>(...)</c> 与 32 位无连字符形态,而键表是按<b>字符串</b>比较的。
    /// 不归一化的话,同一个 GUID 换个写法就是**另一个会话**:配额多占一个,而用户的
    /// 组与默认目标留在旧键上 —— 现场表现是"连接莫名其妙没了、组也没了",
    /// 且因为键仍然"合法",不会报任何错。</para>
    /// <para>归一化点只有两处(中间件与工具面组装器),下游一律用归一化后的值。</para>
    /// </remarks>
    /// <param name="key">原始键文本。</param>
    /// <returns>归一化后的键;非法时返回原值(让后续校验给出正确错误码)。</returns>
    public static string Normalize(string key)
        => Guid.TryParse(key, out var parsed) ? parsed.ToString("D") : key;

    /// <summary>
    /// 建键(或取回已有键)并记录一次活动。供 <c>godot_connect</c> 与 <c>subscriptions/listen</c> 两条
    /// 建键路径调用 —— <b>两者都必须能建键</b>:shim 启动的第一条请求就是内部 listen,
    /// 若流不能建键,通知与状态会落在不同键上。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="entry">成功时的条目。</param>
    /// <returns>未拒绝时返回 <see cref="SessionKeyRejection.None"/>。</returns>
    public SessionKeyRejection EnsureKey(string key, out SessionKeyEntry? entry)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        // 建键入口自己归一化:任何调用方都不该因为 GUID 的写法不同而多占一个会话,
        // 也不该把组与默认目标留在旧键上。
        key = Normalize(key);
        entry = null;
        if (!IsValidKey(key))
        {
            return SessionKeyRejection.InvalidKey;
        }

        string? evicted = null;
        SessionKeyRejection result;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                entry = existing;
                TouchLocked(existing);
                result = SessionKeyRejection.None;
            }
            else if (_entries.Count >= policy.MaxSessionKeys && !TryEvictLocked(out evicted))
            {
                result = SessionKeyRejection.CapacityExceeded;
            }
            else
            {
                var created = new SessionKeyEntry
                {
                    Key = key,
                    State = SessionKeyState.IdleNoStream,
                    LastSeenTimestamp = timeProvider.GetTimestamp(),
                };
                _entries[key] = created;
                Interlocked.Increment(ref _createdCount);
                entry = created;
                result = SessionKeyRejection.None;
            }
        }

        // 出锁后回调:容量淘汰掉的那个键也要清掉它的组桶,
        // 否则同一 UUID 重连时旧组会被一次"只浏览"的 discover_tools 静默复活。
        if (evicted is not null)
        {
            RaiseRecycled([evicted]);
        }

        return result;
    }

    /// <summary>
    /// 登记一条 listen 流。<b>建键与加流在同一临界区内完成</b> —— 分成两步的话,
    /// "建键成功、加流失败"会留下 <c>Active</c> 但零流的键,而 <c>Active</c> 永不淘汰也不过期。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="streamId">本条流的唯一标识(用于注销)。</param>
    /// <param name="entry">成功时的条目。</param>
    /// <returns>未拒绝时返回 <see cref="SessionKeyRejection.None"/>。</returns>
    public SessionKeyRejection AddStream(string key, long streamId, out SessionKeyEntry? entry)
    {
        // 与 EnsureKey 同一套归一化(两条建键路径必须一致)。
        key = Normalize(key);
        if (!IsValidKey(key))
        {
            entry = null;
            return SessionKeyRejection.InvalidKey;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                // 先声明再在条件里赋值:&& 短路会让 out 变量在 false 分支上"未赋值"。
                string? streamEvicted = null;
                if (_entries.Count >= policy.MaxSessionKeys && !TryEvictLocked(out streamEvicted))
                {
                    entry = null;
                    return SessionKeyRejection.CapacityExceeded;
                }

                // 容量淘汰掉的键同样要清桶(见 KeyRecycled)。此处仍在锁内,
                // 用 pending 列表延后到 SessionKeyReaper 的下一轮扫描再回调。
                if (streamEvicted is not null)
                {
                    lock (_pendingRecycled)
                    {
                        _pendingRecycled.Add(streamEvicted);
                    }
                }

                existing = new SessionKeyEntry
                {
                    Key = key,
                    LastSeenTimestamp = timeProvider.GetTimestamp(),
                };
                _entries[key] = existing;
                Interlocked.Increment(ref _createdCount);
            }

            existing.Streams.Add(streamId);
            // 不变式:有流必 Active。Dormant / IdleNoStream 只在无流时存在。
            existing.State = SessionKeyState.Active;
            existing.DormantSinceTimestamp = 0;
            TouchLocked(existing);
            entry = existing;
            return SessionKeyRejection.None;
        }
    }

    /// <summary>
    /// 注销一条 listen 流。最后一条流关闭时进入 <see cref="SessionKeyState.Dormant"/> 并开始计时。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="streamId">流标识。</param>
    public void RemoveStream(string key, long streamId)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.Streams.Remove(streamId);
            if (entry.Streams.Count == 0 && entry.State == SessionKeyState.Active)
            {
                entry.State = SessionKeyState.Dormant;
                entry.DormantSinceTimestamp = timeProvider.GetTimestamp();
            }

            TouchLocked(entry);
        }
    }

    /// <summary>登记一次在途请求开始。必须在 <c>Program.cs</c> 的中间件里按 header 调用 ——</summary>
    /// <remarks><c>ConfigureSessionOptions</c> 没有请求结束回调,不在这里驱动就永远判不出"无在途"。</remarks>
    /// <param name="key">会话键。</param>
    /// <returns>键不存在时返回 null(不建键:请求不该顺手创建会话)。</returns>
    public SessionKeyEntry? RequestStarted(string key)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.InFlight++;
            return entry;
        }
    }

    /// <summary>
    /// 登记一次请求结束。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="isListen">
    /// 该请求是否为 <c>subscriptions/listen</c>。<b>listen 例外</b>:它关闭的时序是
    /// "流移除 → Active→Dormant",紧接着跑本方法;若这里也做 Dormant→IdleNoStream,
    /// grace 就被完全绕过(见 ADR-0006)。
    /// </param>
    public void RequestCompleted(string key, bool isListen)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            if (!isListen)
            {
                if (entry.InFlight > 0)
                {
                    entry.InFlight--;
                }

                // 在途期间最后一条流关闭时,状态停在 Dormant;此处补上到 IdleNoStream 的迁移
                // (否则 grace 已过时 reaper 会删掉刚被使用的键)。
                if (entry.State == SessionKeyState.Dormant && entry.Streams.Count == 0)
                {
                    entry.State = SessionKeyState.IdleNoStream;
                    entry.DormantSinceTimestamp = 0;
                }
            }

            TouchLocked(entry);
        }
    }

    /// <summary>(重)设本键的组集合。调用方负责并集语义(工具重叠:node_set_property / signal_emit)。</summary>
    /// <param name="key">会话键。</param>
    /// <param name="groups">新组集合;为 null 时清空。</param>
    /// <returns>键存在时 true。</returns>
    public bool SetGroups(string key, IEnumerable<string>? groups)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            entry.Groups.Clear();
            if (groups is not null)
            {
                foreach (var group in groups)
                {
                    entry.Groups.Add(group);
                }
            }

            TouchLocked(entry);
            return true;
        }
    }

    /// <summary>写入本键默认目标(ADR-0008)。</summary>
    /// <param name="key">会话键。</param>
    /// <param name="target">规范化项目路径;null 表示清空。</param>
    /// <returns>键存在时 true。</returns>
    public bool SetDefaultTarget(string key, string? target)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            entry.DefaultTarget = target;
            TouchLocked(entry);
        }

        // 默认目标是 --status 里"这个会话连的是哪个项目"的唯一来源,同样要立刻反映。
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 标记本键"已真正连接"(由 <c>godot_connect</c> 调用)。
    /// </summary>
    /// <remarks>
    /// <para>键存在 ≠ 已连接:listen 长流与任意合法 UUID 都会建键,但它们不该看到 core 9。</para>
    /// <para>置位是不可逆的 —— 连接是会话的事实,没有"断开连接"这个动作(断由键回收表达)。</para>
    /// </remarks>
    /// <param name="key">会话键。</param>
    /// <returns>键不存在时为 false(调用方应先 <c>EnsureKey</c>)。</returns>
    public bool MarkConnected(string key)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            entry.Connected = true;
            TouchLocked(entry);
        }

        // 出锁后通知:会话从"未连接"变"已连接"是 sessions.json / --status 里
        // 最该立刻反映的状态转变。等 5s 周期会让排查的人看到旧值。
        RaiseChanged();
        return true;
    }

    /// <summary>记录 shim 上报的会话工作目录(空参 <c>godot_connect</c> 的候选发现依据)。</summary>
    /// <param name="key">会话键。</param>
    /// <param name="workingDirectory">shim 的 cwd;null 或空串表示未知。</param>
    /// <returns>键不存在时为 false。</returns>
    public bool SetWorkingDirectory(string key, string? workingDirectory)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                entry.WorkingDirectory = workingDirectory;
            }
            return true;
        }
    }

    /// <summary>按键取只读快照,供工具面填充使用(在 <c>_gate</c> 之外调用,避免锁序问题)。</summary>
    /// <param name="key">会话键。</param>
    /// <param name="snapshot">键存在时的不可变视图。</param>
    /// <returns>键存在时 true。</returns>
    public bool TryGetSnapshot(string key, out SessionSnapshot snapshot)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                snapshot = new SessionSnapshot(
                    entry.Key,
                    entry.ShortKey,
                    entry.State,
                    entry.Groups.ToArray(),
                    entry.DefaultTarget,
                    entry.Streams.Count,
                    entry.InFlight,
                    entry.Connected,
                    entry.WorkingDirectory,
                    entry.ShimVersion);
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    /// <summary>是否存在该键(不做活动刷新)。</summary>
    /// <param name="key">会话键。</param>
    /// <returns>存在时 true。</returns>
    public bool Contains(string key)
    {
        // 表达式体也得归一化:否则"这个键在不在"与"用哪个桶"会给出矛盾的答案。
        return _entries.ContainsKey(Normalize(key));
    }

    /// <summary>
    /// 键被回收后的回调(供 <c>GroupService</c> 清掉该键的组桶)。
    /// </summary>
    /// <remarks>
    /// <para>不清桶有两个后果:① 每个历史键永久留两个 <c>HashSet</c>,而 daemon 是常驻单例、
    /// shim 每次启动换 UUID —— 桶无界增长;② 同一 shim 用同一 UUID 重连时,一次**无参**
    /// <c>discover_tools()</c>(按规范是"只浏览")会把旧组写回注册表并静默展开,
    /// 模型从未激活过的工具自己出现。</para>
    /// <para>回调<b>必须在释放 <c>_gate</c> 之后</b>触发:<c>GroupService</c> 有自己的锁,
    /// 在注册表锁内回调会形成"注册表 → GroupService"的嵌套,而激活路径是
    /// "GroupService → 注册表",两者构成 ABBA。</para>
    /// </remarks>
    public event Action<string>? KeyRecycled;

    /// <summary>
    /// 容量淘汰路径产生的待回调键(锁内登记、由 <see cref="DrainPendingRecycled"/> 出锁回调)。
    /// </summary>
    /// <remarks><c>AddStream</c> 的容量分支在锁内,不能就地回调 GroupService(ABBA),所以走这里。</remarks>
    private readonly List<string> _pendingRecycled = [];

    /// <summary>取出并回调所有待通知的回收键(<b>必须在锁外调用</b>)。</summary>
    /// <returns>实际回调的键数。</returns>
    public int DrainPendingRecycled()
    {
        List<string> pending;
        lock (_pendingRecycled)
        {
            if (_pendingRecycled.Count == 0)
            {
                return 0;
            }

            pending = [.. _pendingRecycled];
            _pendingRecycled.Clear();
        }

        RaiseRecycled(pending);
        return pending.Count;
    }

    /// <summary>
    /// 会话表发生了值得刷新快照的变化(建键 / 连接 / 组 / 默认目标)。
    /// </summary>
    /// <remarks>
    /// 用于让 <see cref="SessionSnapshotWriter"/> 立刻落盘,而不是等周期定时器。
    /// 与 <see cref="KeyRecycled"/> 一样:**必须在出锁后触发**。
    /// </remarks>
    public event Action? Changed;

    /// <summary>通知订阅者会话表发生了变化(<b>必须在出锁后调用</b>)。</summary>
    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch
            {
                // 快照刷新失败不该影响会话本身,由 writer 自己记日志。
            }
        }
    }

    /// <summary>把本轮回收的键通知给订阅者(<b>必须在释放 <c>_gate</c> 之后调用</b>)。</summary>
    /// <param name="recycled">本轮回收的键。</param>
    /// <remarks>
    /// <b>逐个订阅者隔离异常</b>。<c>KeyRecycled</c> 是 public 事件,订阅者可以抛
    /// (例如日志后端在磁盘满时抛)。若不隔离,异常会从 <see cref="Sweep"/> 一路逃到
    /// <c>BackgroundService.ExecuteAsync</c>:.NET 默认 <c>StopHost</c> 会让**整个 daemon 退出**
    /// (退出码 0,宿主只看到"连接断了"),最轻也会让 reaper 永久停摆 —— 键表不再回收、
    /// 孤儿键很快占满 <c>MaxSessionKeys</c>,<c>godot_connect</c> 开始回 <c>SESSION_LIMIT</c>。
    /// 回收本身已经完成了,通知失败不该反过来影响回收。
    /// </remarks>
    private void RaiseRecycled(IReadOnlyList<string> recycled)
    {
        var handler = KeyRecycled;
        if (handler is null)
        {
            return;
        }

        foreach (var key in recycled)
        {
            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<string>)subscriber)(key);
                }
                catch
                {
                    // 通知失败不影响回收结果;这里没有 logger(注册表是纯数据结构),
                    // 由 SessionKeyReaper 的日志与 sweep 计数反映真实回收数。
                }
            }
        }
    }

    /// <summary>
    /// 回收过期键。由 <see cref="SessionKeyReaper"/> 周期调用,也可由测试直接调。
    /// </summary>
    /// <remarks>
    /// 两种过期都<b>要求 <c>InFlight == 0</c></b>:长工具(如 <c>editor_build_csharp_wait</c>)超过 TTL 时
    /// <c>LastSeen</c> 只在请求开始刷新,不等在途就会在请求进行中删键 —— 与"淘汰不删在途"是同一类错误。
    /// </remarks>
    /// <returns>本次删除的键数。</returns>
    public int Sweep()
    {
        var removed = 0;
        var removedKeys = new List<string>();
        lock (_gate)
        {
            var now = timeProvider.GetTimestamp();
            foreach (var (key, entry) in _entries)
            {
                if (entry.InFlight > 0 || entry.Streams.Count > 0)
                {
                    continue;
                }

                var expired = entry.State switch
                {
                    // **已连接的键不按 30s 宽限过期,而是与 IdleNoStream+Connected 同档。**
                    //
                    // 宽限期是为"最后一条流刚关、马上重连"这种交接瞬间不丢状态。但已连接
                    // (connect 过)的会话没有"交接失败"这回事 —— 宿主只是暂时把 listen 关掉
                    // (SDK 重连间隙、用户取消订阅)。此时按 30s 删键,用户看到的是
                    // "工具突然只剩 godot_connect",而且拿到的是**新**短键、对不上号。
                    // 判据与 TryEvictLocked 的"已连接的会话绝不淘汰"统一。
                    //
                    // 注意不能直接 `=> false`:那会造出永不回收的键。给它 ConnectedTtl,
                    // 与无流但已连接的会话完全同档,语义上就是"连接过的会话按连接态计时"。
                    SessionKeyState.Dormant when entry.Connected =>
                        timeProvider.GetElapsedTime(entry.LastSeenTimestamp, now) >= policy.ConnectedTtl,

                    SessionKeyState.Dormant =>
                        timeProvider.GetElapsedTime(entry.DormantSinceTimestamp, now) >= policy.HandoffGrace,

                    // **已连接的键按 ConnectedTtl,而不是 NoStreamTtl**。
                    // 传输层不保证有 listen 流:legacy 宿主(initialize 握手)根本不开流,
                    // shim 也只转发宿主自己发的 listen。若这类会话仍按 10 分钟 TTL 回收,
                    // 用户去读代码/思考一会儿回来,工具就全消失了 —— 与"没有流"这个实现细节无关,
                    // 用户看到的是"连着的会话自己断了"。
                    // Connected 就是 ADR-0006 里 Active 的等价物(在无流可用时)。
                    SessionKeyState.IdleNoStream when entry.Connected =>
                        timeProvider.GetElapsedTime(entry.LastSeenTimestamp, now) >= policy.ConnectedTtl,

                    SessionKeyState.IdleNoStream =>
                        timeProvider.GetElapsedTime(entry.LastSeenTimestamp, now) >= policy.NoStreamTtl,

                    _ => false,
                };

                if (expired && _entries.TryRemove(key, out _))
                {
                    removedKeys.Add(key);
                }
            }

            removed = removedKeys.Count;
        }

        // 出锁后再回调:GroupService 有自己的锁,锁内回调会形成 ABBA。
        RaiseRecycled(removedKeys);
        return removed;
    }

    /// <summary>把键表写成可运维的快照(供 <c>state/sessions.json</c> 与 <c>--status</c>)。</summary>
    /// <returns>按键排序的条目快照。</returns>
    public IReadOnlyList<SessionSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new SessionSnapshot(
                    e.Key,
                    e.ShortKey,
                    e.State,
                    e.Groups.ToArray(),
                    e.DefaultTarget,
                    e.Streams.Count,
                    e.InFlight,
                    e.Connected,
                    e.WorkingDirectory,
                    e.ShimVersion))
                .ToArray();
        }
    }

    /// <summary>写入 shim 上报的版本(取最后一次非空值)。</summary>
    /// <param name="key">会话键。</param>
    /// <param name="shimVersion">shim 版本;空串表示该请求没带该头。</param>
    /// <returns>写入成功时 true(键不存在或版本为空时为 false)。</returns>
    public bool SetShimVersion(string key, string? shimVersion)
    {
        // 纵深防御:入口已归一化,这里再兜一次 —— 任何调用方都不该因为
        // GUID 写法不同而落到另一个桶(那会静默分桶:组与默认目标留在旧键上)。
        key = Normalize(key);
        if (string.IsNullOrEmpty(shimVersion))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            entry.ShimVersion = shimVersion;
            return true;
        }
    }

    /// <summary>刷新活动时间戳。</summary>
    private void TouchLocked(SessionKeyEntry entry) => entry.LastSeenTimestamp = timeProvider.GetTimestamp();

    /// <summary>
    /// 上限腾位:只淘汰 <c>IdleNoStream</c> 与 grace 已过的 <c>Dormant</c>,且都要求在途为 0。
    /// <b>绝不淘汰 <c>Active</c></b>——那会让一个正在连的 shim 突然失去会话与组状态,
    /// 比拒绝新建键严重得多。
    /// </summary>
    /// <returns>腾出位置时 true。</returns>
    /// <param name="evicted">被淘汰的键;未淘汰时为 null。由调用方出锁后回调。</param>
    private bool TryEvictLocked(out string? evicted)
    {
        evicted = null;
        var now = timeProvider.GetTimestamp();
        SessionKeyEntry? victim = null;
        long victimSeen = long.MaxValue;

        foreach (var entry in _entries.Values)
        {
            if (entry.InFlight > 0 || entry.Streams.Count > 0)
            {
                continue;
            }

            var eligible = entry.State == SessionKeyState.IdleNoStream
                || (entry.State == SessionKeyState.Dormant
                    && timeProvider.GetElapsedTime(entry.DormantSinceTimestamp, now) >= policy.HandoffGrace);

            // **已连接的会话绝不淘汰**:容量压力不该把正在用的会话踢掉。
            // spec §1.2 的原话是"绝不挤掉活跃会话";在没有流的传输形态下,
            // Connected 就是"活跃"的判据(见 SessionKeyPolicy.ConnectedTtl 的说明)。
            if (!eligible || entry.Connected)
            {
                continue;
            }

            if (eligible && entry.LastSeenTimestamp < victimSeen)
            {
                victim = entry;
                victimSeen = entry.LastSeenTimestamp;
            }
        }

        if (victim is null || !_entries.TryRemove(victim.Key, out _))
        {
            return false;
        }

        // 记下被淘汰者,由调用方出锁后回调(锁内回调会与 GroupService 形成 ABBA)。
        evicted = victim.Key;
        return true;
    }
}
