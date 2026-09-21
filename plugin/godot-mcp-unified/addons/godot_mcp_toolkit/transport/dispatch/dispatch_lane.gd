@tool
extends RefCounted
## 一条解析后的 JSON-RPC 请求可以经过的三条派发通道。server_request_router.gd
## 依据命令的注册表标志为每条请求选择"一条"通道,并调用 lane.drive(...);
## 每条通道持有其命令类别的并发纪律:
##   - ReadOnlyLane    — 立即执行,无锁(只读命令)。
##   - MutationLane     — 单飞 + FIFO 队列 + 变更看门狗,确保两个变更绝不在
##                        await 边界交错。
##   - SceneLeaseLane   — 经场景租约路由(排队直到该对端的亲和场景成为活动
##                        标签页),然后交给 MutationLane。
##
## 为什么是三个共享基类(_LaneBase)的通道对象,而是一个按标志分支的驱动器:
## 各通道拥有真正不同的状态与行为 — ReadOnlyLane 无状态,MutationLane 持有
## 执行中标志 + FIFO 队列 + 看门狗,SceneLeaseLane 只做委托 — 因此单一驱动器
## 会把变更队列字段拖进读取路径(低内聚),并长出一条第四通道必须去修改的
## if/elif 链(违反开闭原则)。多态的 drive() 让每条通道的状态保持局部,
## 并让派发器无需触碰路由即可新增通道。
##
## 按设计保持"编辑器纯净":本文件不引用任何 Editor* 符号,只 preload 共享
## 纯净的 Notifier。每个仅编辑器的步骤 — 场景亲和命令之前的编辑场景标签页
## 切换、租约簿记 — 都只经由注入的 Callable 触达(SceneLeaseLane 的
## _handle_scene_open / _try_queue_for_lease,编辑器把它们绑定到 scene_lease
## 子模块,后者再触达 open_scene_deferred;通道对它们一无所知地调用)。
## 在这里直接命名租约子模块或 EditorInterface 会重新污染本文件(#91713)。
## 模式 B 没有通道,绝不 preload 本文件;保持纯净既保留了这个选项,也让各
## 通道可无头单元测试。

const Notifier := preload("res://addons/godot_mcp_toolkit/transport/notifier.gd")


## 执行中请求在共享活动上下文映射中所用的按 peer 限定的键 —
## [code]_LaneBase.context_key[/code] 的公共再导出,供派发器的 _cancel 查找
## 使用(内部类无法跨文件命名;各通道经继承触达同一实现,因此该形态只有
## 一个归属)。
static func context_key(peer: WebSocketPeer, id) -> String:
	return _LaneBase.context_key(peer, id)


# -- 共享通道基类 ----------------------------------------------------------------
# 持有每条通道都需要的协作者,以及两条通道共享的读取执行核心。
# 不直接实例化 — ReadOnlyLane / MutationLane / SceneLeaseLane 继承它。
class _LaneBase:
	extends RefCounted

	## 执行中请求在共享活动上下文映射中所用的按 peer 限定的键。JSON-RPC id 是
	## 每客户端的计数器,因此两个对端完全可能同时携带同一个 id — 裸的 str(id)
	## 键会让一个对端的注册覆盖另一个的(或让它成为对方的取消目标)。以对端
	## 实例 id 限定,让键在每个 (peer, request) 上唯一。每条通道都继承它;
	## 派发器经由脚本级再导出触达。
	static func context_key(peer: WebSocketPeer, id) -> String:
		return "%d:%s" % [peer.get_instance_id(), str(id)]

	# 命令派发表 — call_command 加上每命令标志。
	var _registry: MCPToolkitCommandRegistry = null
	# 以 context_key(peer, id) 为键的执行中可取消请求上下文 — 按 peer 限定,
	# 因为 JSON-RPC id 只在单个客户端内唯一。由派发器持有,并(字典是引用类型)
	# 共享,让派发器的 _cancel 处理器、两条执行路径,以及变更看门狗的恢复钩子
	# 看到同一个映射。通道只填充/擦除自己请求的条目,绝不读取别人的。
	var _active_contexts: Dictionary = {}
	# 再发射服务器的 command_received 信号。func(method: String) -> void。
	var _on_command: Callable = Callable()

	func _wire_base(registry: MCPToolkitCommandRegistry, active_contexts: Dictionary,
			on_command: Callable) -> void:
		_registry = registry
		_active_contexts = active_contexts
		_on_command = on_command

	## 在本通道上把一条已解析、已注册的请求驱动到响应。各通道覆写。走到这里时,
	## 派发器已处理了解析 / _cancel / echo / 注册表未命中,因此方法存在且
	## params 是字典。
	func drive(_peer: WebSocketPeer, _id, _method: String, _params: Dictionary) -> void:
		push_error("[MCPToolkit] _LaneBase.drive() called directly - a lane subclass must override it")

	## 插件拆卸期间释放注册表与活动上下文引用,在节点释放之前断开 Callable 链
	## (命令处理器、命令再发射)。
	func clear() -> void:
		_registry = null
		_active_contexts = {}
		_on_command = Callable()

	# 读取执行核心:若命令声明可取消则创建上下文,在调用期间以按 peer 限定的
	# 请求键跟踪它,再发射 command_received,并返回处理器的结果。由
	# ReadOnlyLane.drive 与 SceneLeaseLane 的排队读取路径(以 run_read 注入)
	# 共享,让上下文簿记只有一个归属,_active_contexts 在两个读取入口之间
	# 保持一致。
	func _execute_read(peer: WebSocketPeer, method: String, params: Dictionary, id) -> Dictionary:
		var ctx_key := context_key(peer, id)
		var ctx: MCPToolkitToolContext = null
		if _registry.is_cancellable(method):
			ctx = MCPToolkitToolContext.new()
			_active_contexts[ctx_key] = ctx
		_on_command.call(method)
		var result: Dictionary = await _registry.call_command(method, params, ctx)
		_active_contexts.erase(ctx_key)
		return result


# -- ReadOnlyLane ---------------------------------------------------------------
# 无状态:只读命令不持锁、永不排队,因此 drive() 只运行共享的读取核心并发送
# 结果。对应 mcp_server._dispatch_rpc 的只读路由。
class ReadOnlyLane:
	extends _LaneBase

	func configure(registry: MCPToolkitCommandRegistry, active_contexts: Dictionary,
			on_command: Callable) -> void:
		_wire_base(registry, active_contexts, on_command)

	func drive(peer: WebSocketPeer, id, method: String, params: Dictionary) -> void:
		var result: Dictionary = await _execute_read(peer, method, params, id)
		Notifier.send_result(peer, id, result, "[MCPServer]")

	## 运行读取核心并"返回"结果而不发送。场景租约通道的排队读取路径所需的
	## 接缝:它在发送之前向结果注入并发元数据,因此无法使用 drive()(它会
	## 立即发送)。与 drive() 相同的上下文簿记,让 _active_contexts 在两者之间
	## 保持一致。
	func run_returning(peer: WebSocketPeer, method: String, params: Dictionary, id) -> Dictionary:
		return await _execute_read(peer, method, params, id)


# -- MutationLane ---------------------------------------------------------------
# 单飞 + FIFO,确保两个变更绝不在 await 边界交错;外加变更看门狗,在执行中
# 协程中止/永不完成时恢复锁。持有执行中标志 + FIFO 队列;看门狗(一个独立的
# 纯净子模块)持有期限 + 代计数,并经注入的 force_clear 钩子回调。对应
# mcp_server 的变更路由 + _execute_mutation + _drain_mutation_queue。
class MutationLane:
	extends _LaneBase

	# 在单飞锁上排队的一条变更请求。
	class _QueueEntry:
		var peer: WebSocketPeer
		var id  # int 或 null(JSON-RPC id)
		var method: String
		var params: Dictionary
		var cancelled: bool = false
		var scene_queued_ms: int = 0  # 最初在场景队列排队时非零。

	# 任一时刻至多一个变更在执行;其余在 _queue(FIFO)中等待。
	var _in_flight := false
	var _queue: Array = []  # of _QueueEntry

	# 变更看门狗 — 执行中协程卡死时恢复锁。在构造时注入(set_handlers);
	# 执行开始时武装,完成时解除。
	var _watchdog = null  # MutationWatchdog(无类型:由派发器 preload)
	# 变更后的场景租约清理 + 排队等待的并发元数据 + 场景队列排水。注入,
	# 让租约子模块的状态留在租约子模块里。
	# _inject_concurrency_metadata: func(result: Dictionary, queued_ms: int) -> void
	# _post_mutation_cleanup:        func(peer, method, params, result) -> void
	# _drain_scene_queue:            func() -> void   (变更队列清空后调用)
	var _inject_concurrency_metadata: Callable = Callable()
	var _post_mutation_cleanup: Callable = Callable()
	var _drain_scene_queue: Callable = Callable()

	func configure(registry: MCPToolkitCommandRegistry, active_contexts: Dictionary,
			on_command: Callable) -> void:
		_wire_base(registry, active_contexts, on_command)

	## 接线变更通道的协作者:看门狗与三个租约侧接缝。在构造时、configure()
	## 之后调用一次。看门狗的 force_clear 钩子也在这里设置(它恢复的是本通道
	## 的锁),因此由通道负责这次接线。
	func set_handlers(watchdog, inject_concurrency_metadata: Callable,
			post_mutation_cleanup: Callable, drain_scene_queue: Callable) -> void:
		_watchdog = watchdog
		_watchdog.set_force_clear(_force_clear)
		_inject_concurrency_metadata = inject_concurrency_metadata
		_post_mutation_cleanup = post_mutation_cleanup
		_drain_scene_queue = drain_scene_queue

	func clear() -> void:
		super.clear()
		_watchdog = null
		_inject_concurrency_metadata = Callable()
		_post_mutation_cleanup = Callable()
		_drain_scene_queue = Callable()
		_queue.clear()

	func drive(peer: WebSocketPeer, id, method: String, params: Dictionary) -> void:
		# 单飞:若已有变更在运行,把这一个排队并告知对端;否则立即执行。
		# _execute 在其第一个 await 之前同步设置 _in_flight = true,因此检查与
		# 设置之间没有竞态窗口。
		if _in_flight:
			_enqueue(peer, id, method, params, 0)
		else:
			await _execute(peer, id, method, params)

	## 若有变更正在执行,把本命令追加到 FIFO、通知对端它已排队并返回 true;
	## 否则返回 false(调用方继续执行)。场景租约通道对场景排队的变更调用它,
	## 让变更通道状态留在本通道内。queued_ms 携带最初场景队列的等待时长,
	## 供元数据使用。
	func enqueue_if_busy(peer: WebSocketPeer, id, method: String, params: Dictionary,
			queued_ms: int) -> bool:
		if not _in_flight:
			return false
		_enqueue(peer, id, method, params, queued_ms)
		return true

	## 场景租约通道把一条已出队、标签页已激活的场景亲和变更交给的变更通道
	## 入口(它在完成时重新排水变更队列)。
	func execute(peer: WebSocketPeer, id, method: String, params: Dictionary,
			scene_queued_ms: int) -> void:
		await _execute(peer, id, method, params, scene_queued_ms)

	## 把一条排队(尚未执行)的变更标记为"排水时跳过" — 这是取消在队列一侧的
	## 回退路径,适用于在执行中上下文里找不到的目标。按[param peer] 与 id 同时
	## 匹配(id 只在单个客户端内唯一,仅按 id 匹配可能误取消另一个对端排队的
	## 变更)。找到匹配条目时返回 true。
	func cancel_queued(peer: WebSocketPeer, target_id: String) -> bool:
		for entry in _queue:
			if entry.peer == peer and str(entry.id) == target_id:
				entry.cancelled = true
				return true
		return false

	func _enqueue(peer: WebSocketPeer, id, method: String, params: Dictionary,
			queued_ms: int) -> void:
		var entry := _QueueEntry.new()
		entry.peer = peer
		entry.id = id
		entry.method = method
		entry.params = params
		entry.scene_queued_ms = queued_ms
		_queue.append(entry)
		Notifier.send_notification(peer, "_queued", {"request_id": id}, "[MCPServer]")

	func _execute(peer: WebSocketPeer, id, method: String, params: Dictionary,
			scene_queued_ms: int = 0) -> void:
		# 获取单飞锁,然后在"任何 await 之前"同步武装看门狗 — 让其期限只跟踪
		# 执行中的时间(绝不含排队等待)且不会竞态。我们在这里计算期限
		# (我们持有注册表与宽限设置)并把值交给看门狗;arm() 返回我们为
		# await 后守卫捕获的代计数。
		_in_flight = true
		var started_ms := Time.get_ticks_msec()
		var grace_ms: int = ProjectSettings.get_setting(
			"mcp_toolkit/concurrency/mutation_watchdog_grace_ms", 60000)
		# 期限依据:若命令声明了超时则用它(信任作者的契约 — 内置命令与谨慎的
		# 扩展获得紧凑、合适的恢复),未声明的方法用 _MAX_TIMEOUT_MS(30 秒默认
		# 不是对其时长的刻意陈述,所以不要提前强制清除)。两种情况都加上宽限;
		# 期限在执行开始时盖章。
		var deadline_ms := started_ms + _registry.get_watchdog_timeout_ms(method) + grace_ms
		Notifier.send_notification(peer, "_executing", {"request_id": id}, "[MCPServer]")
		var ctx_key := context_key(peer, id)
		var ctx: MCPToolkitToolContext = null
		if _registry.is_cancellable(method):
			ctx = MCPToolkitToolContext.new()
			_active_contexts[ctx_key] = ctx
		# 武装:把执行中身份 + 期限 + ctx 交给看门狗(让它能协作地取消一个
		# 缓慢但存活的处理器),并返回代计数。
		var my_generation: int = _watchdog.arm(peer, id, method, started_ms, deadline_ms, ctx)
		_on_command.call(method)
		var result: Dictionary = await _registry.call_command(method, params, ctx)
		# 拆卸守卫:当处理器在这里被挂起时(插件禁用/编辑器在变更中途退出),
		# clear() 会置空看门狗并清空接缝 Callable。一切已被拆除 — 放弃尾段,
		# 而不是在 null 上调用。
		if _watchdog == null:
			return
		# 代守卫:若看门狗在 await 中途强制清除了我们(代已递增),则一个后继
		# 变更现在持有锁。放弃"整段"尾段 — 看门狗已经响应过,并且已经擦除了
		# 本请求的活动上下文(它的 force_clear 钩子);在这里再擦除,可能在同一
		# 对端复用 id 时删掉后继者的存活 ctx。
		if _watchdog.current_generation() != my_generation:
			return
		_active_contexts.erase(ctx_key)
		if scene_queued_ms > 0:
			_inject_concurrency_metadata.call(result, scene_queued_ms)
		Notifier.send_result(peer, id, result, "[MCPServer]")
		_post_mutation_cleanup.call(peer, method, params, result)
		_in_flight = false
		_watchdog.disarm()
		drain()

	## 执行下一条有资格的排队变更(一次一条;下一次排水在完成时触发)。
	## 跳过已取消/已断开/未注册的条目。变更队列一旦清空,就交给场景队列排水
	## (同一租约的条目现在可以继续)。在变更完成时以及看门狗的 force_clear
	## 恢复时调用。
	func drain() -> void:
		while not _queue.is_empty():
			var entry: _QueueEntry = _queue.pop_front()
			# 跳过已取消的条目。
			if entry.cancelled:
				continue
			# 跳过已断开的对端。
			if entry.peer.get_ready_state() != WebSocketPeer.STATE_OPEN:
				continue
			# 跳过未注册的命令(热重载竞态)。
			if not _registry.has_command(entry.method):
				Notifier.send_error(entry.peer, entry.id, -32601,
					"Method unregistered while queued: %s" % entry.method)
				continue
			# 找到有效条目 — 执行它。_execute 在其第一个 await 之前同步设置
			# _in_flight = true,因此没有竞态窗口。
			_execute(entry.peer, entry.id, entry.method, entry.params, entry.scene_queued_ms)
			return  # _execute 完成时会调用 drain()。
		# 变更队列已完全排空 — 检查场景队列中同一租约的条目。
		_drain_scene_queue.call()

	# 看门狗的恢复钩子(在 set_handlers 中接线)。在触发时"最后"运行,此时
	# 看门狗已递增代计数并清除自己的身份,因此排水的同步后继重新武装不会被
	# 践踏:擦除被困请求的活动上下文(按 peer 限定的键 — 看门狗交还被困的
	# peer + id),清除单飞标志,并排水。
	func _force_clear(trapped_peer: WebSocketPeer, trapped_id) -> void:
		if trapped_peer != null:
			_active_contexts.erase(context_key(trapped_peer, trapped_id))
		_in_flight = false
		drain()


# -- SceneLeaseLane -------------------------------------------------------------
# 依赖标签页的命令:经场景租约路由,租约把请求排队,直到该对端的亲和场景成为
# 活动的编辑器标签页,然后把它重新派发到读取或变更通道。本通道是对(已抽出的)
# scene_lease 子模块的薄委托,经注入的 Callable 触达,因此本文件不命名任何
# 编辑器符号。对应 mcp_server 的场景租约路由(scene.open + try_queue_for_lease)。
class SceneLeaseLane:
	extends _LaneBase

	# 注入的场景租约接缝(租约子模块被编辑器污染;直接触达会污染本文件,
	# 因此藏在 Callable 之后):
	# _handle_scene_open:     func(peer, id, params) -> void (await) — 争用下的
	#   scene.open(不切换标签页;返回争用提示)。
	# _try_queue_for_lease:   func(peer, id, method, params) -> bool — 已排队则
	#   为 true(尚无响应),false 表示立即进入读取/变更通道。
	var _handle_scene_open: Callable = Callable()
	var _try_queue_for_lease: Callable = Callable()
	# 依赖标签页的命令"不需要"排队(亲和与活动标签页一致)时落到的通道。
	# 由派发器设置为变更通道或读取通道;SceneLeaseLane 自己从不执行命令。
	var _read_lane: ReadOnlyLane = null
	var _mutation_lane: MutationLane = null

	func configure(registry: MCPToolkitCommandRegistry, active_contexts: Dictionary,
			on_command: Callable) -> void:
		_wire_base(registry, active_contexts, on_command)

	## 接线场景租约接缝 + 落入通道。在构造时调用一次。
	func set_handlers(handle_scene_open: Callable, try_queue_for_lease: Callable,
			read_lane: ReadOnlyLane, mutation_lane: MutationLane) -> void:
		_handle_scene_open = handle_scene_open
		_try_queue_for_lease = try_queue_for_lease
		_read_lane = read_lane
		_mutation_lane = mutation_lane

	func clear() -> void:
		super.clear()
		_handle_scene_open = Callable()
		_try_queue_for_lease = Callable()
		_read_lane = null
		_mutation_lane = null

	func drive(peer: WebSocketPeer, id, method: String, params: Dictionary) -> void:
		# scene.open:拦截,因为存在争用时绝不能打开场景(标签页切换会干扰租约
		# 持有者)。租约子模块负责校验,并要么打开要么返回争用提示。
		if method == "scene.open":
			await _handle_scene_open.call(peer, id, params)
			return
		# 依赖标签页的命令:当目标场景与活动标签页不同时,经租约排队。
		# true → 已排队(尚无响应);false → 立即继续。
		if _try_queue_for_lease.call(peer, id, method, params):
			return
		# 亲和与活动标签页一致(或没有亲和)— 落到正确的通道。
		if _registry.needs_serialization(method):
			await _mutation_lane.drive(peer, id, method, params)
		else:
			await _read_lane.drive(peer, id, method, params)
