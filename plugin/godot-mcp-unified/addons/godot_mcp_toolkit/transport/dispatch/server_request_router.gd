@tool
extends RefCounted
## 把解析后的 JSON-RPC 请求路由到其每命令策略选择的派发通道,并驱动该通道
## 直到响应。保留派发器层面的事务 — id/method/params 解析、_cancel 与 echo
## 通知、注册表未命中(-32601)应答 — 然后依据命令的注册表标志从三条通道
## (只读/变更/场景租约)中选择"一条",调用 lane.drive(...)。各通道
## (dispatch_lane.gd)持有各自命令类别的并发纪律;本文件只持有路由。
##
## 线路契约是冻结的:通知方法 _queued / _executing / _cancel / echo、JSON-RPC
## 错误码,以及信封键是 TS 桥接所说的契约 — 不要在这里重命名或改形。
##
## 按设计保持"编辑器纯净":不引用任何 Editor* 符号,只 preload 共享纯净的
## Notifier 与(纯净的)派发通道。一切仅编辑器的步骤(场景标签页切换、
## 租约)都由通道经由注入的 Callable 触达,绝不在这里命名。模式 B 没有派发器,
## 绝不 preload 本文件;保持纯净既保留了这个选项,也让路由可无头测试。

const Notifier := preload("res://addons/godot_mcp_toolkit/transport/notifier.gd")
const DispatchLane := preload("res://addons/godot_mcp_toolkit/transport/dispatch/dispatch_lane.gd")

# 命令派发表 — has_command 加上选择通道所需的各标志。
var _registry: MCPToolkitCommandRegistry = null
# 以 DispatchLane.context_key(peer, id) 为键的执行中可取消请求上下文
# (按 peer 限定 — id 只在单个客户端内唯一)。在这里持有,并与通道及变更
# 看门狗(按引用)共享,让 _cancel 能取消一条执行中的请求,同时各通道
# 填充/擦除自己的条目。参见 dispatch_lane.gd。
var _active_contexts: Dictionary = {}

# 三条通道,在 build_lanes() 中构造并接线。每条请求按其注册表标志被路由到
# 其中恰好一条。无类型(具体类型是 DispatchLane 的内部类
# ReadOnlyLane / MutationLane / SceneLeaseLane):GDScript 在 .new() 时经由
# DispatchLane 常量解析它们,但"preload 的内部类"型注解在本代码库中没有
# 先例,因此保持无类型以避免解析歧义。
var _read_lane = null  # DispatchLane.ReadOnlyLane
var _mutation_lane = null  # DispatchLane.MutationLane
var _scene_lease_lane = null  # DispatchLane.SceneLeaseLane

# 取消一条排队(尚未执行)的场景队列命令。注入(场景队列位于被编辑器污染的
# 租约子模块):func(peer, target_id: String) -> bool — 与另外两个取消扫描一样
# 按 peer 限定(id 只在单个客户端内唯一)。
var _cancel_scene_queued: Callable = Callable()


func set_registry(registry: MCPToolkitCommandRegistry) -> void:
	_registry = registry


## 构造并接线三条通道。调用方(mcp_server)提供注册表、command_received 的
## 再发射、看门狗,以及场景租约接缝(每个都藏在 Callable 之后,让本文件与
## 通道保持编辑器纯净)。在 start() 中、set_registry 之后调用一次。各通道
## 按引用持有共享的 _active_contexts。
func build_lanes(on_command: Callable, watchdog,
		inject_concurrency_metadata: Callable, post_mutation_cleanup: Callable,
		drain_scene_queue: Callable, handle_scene_open: Callable,
		try_queue_for_lease: Callable, cancel_scene_queued: Callable) -> void:
	_cancel_scene_queued = cancel_scene_queued

	_read_lane = DispatchLane.ReadOnlyLane.new()
	_read_lane.configure(_registry, _active_contexts, on_command)

	_mutation_lane = DispatchLane.MutationLane.new()
	_mutation_lane.configure(_registry, _active_contexts, on_command)
	_mutation_lane.set_handlers(watchdog, inject_concurrency_metadata,
		post_mutation_cleanup, drain_scene_queue)

	_scene_lease_lane = DispatchLane.SceneLeaseLane.new()
	_scene_lease_lane.configure(_registry, _active_contexts, on_command)
	_scene_lease_lane.set_handlers(handle_scene_open, try_queue_for_lease,
		_read_lane, _mutation_lane)


## 变更通道(DispatchLane.MutationLane)— 服务器把它的 enqueue_if_busy + execute
## 入口注入场景租约子模块(场景排队的变更会重进入变更通道)。由于前述
## "preload 内部类"的原因,返回值保持无类型。
func mutation_lane():
	return _mutation_lane


## 读取通道(DispatchLane.ReadOnlyLane)— 服务器把它的读取核心注入场景租约
## 子模块(排队读取路径与直接只读命令执行同样的上下文跟踪读取)。同样因
## "preload 内部类"的原因,返回值保持无类型。
func read_lane():
	return _read_lane


## 插件拆卸期间释放各通道的注册表 + Callable 引用,在服务器节点释放之前
## 断开引用链。
func clear() -> void:
	_registry = null
	_cancel_scene_queued = Callable()
	if _read_lane != null:
		_read_lane.clear()
	if _mutation_lane != null:
		_mutation_lane.clear()
	if _scene_lease_lane != null:
		_scene_lease_lane.clear()


## 解析原始 JSON-RPC 请求,处理派发器层面的事务(_cancel、echo、缺失方法、
## 注册表未命中),然后把其余请求路由到其注册表标志选择的通道。传输层会
## await 本方法,因此派发在单个轮询 tick 内保持顺序。
func route_request(peer: WebSocketPeer, message: Dictionary) -> void:
	var id = message.get("id", null)
	# Godot 的 JSON 解析器把所有数字都返回为 float;把整数 float 的 id 强转回
	# int,让 {"id": 1} 往返后仍是 {"id": 1},而不是 {"id": 1.0}。
	if typeof(id) == TYPE_FLOAT and int(id) == id:
		id = int(id)
	var method := str(message.get("method", ""))
	var parameters = message.get("params", null)

	if method.is_empty():
		Notifier.send_error(peer, id, -32600, "Invalid Request: missing method")
		return

	# _cancel 是桥接发出的"发出即忘"通知 — 无响应。它触发目标请求的
	# MCPToolkitToolContext 上的协作取消。会在变更队列与场景队列中扫描排队
	# (尚未执行)的命令,并标记为"排水时跳过"。限定在"发起请求的对端"内 —
	# id 只在单个客户端内唯一,全局 id 匹配可能误取消另一个对端的请求。
	if method == "_cancel":
		_handle_cancel(peer, parameters)
		return

	# echo 是传输层诊断,不是领域命令。
	if method == "echo":
		Notifier.send_result(peer, id, parameters, "[MCPServer]")
		return

	if _registry == null or not _registry.has_command(method):
		Notifier.send_error(peer, id, -32601, "Method not found: %s" % method)
		return

	var safe_parameters: Dictionary = parameters \
		if typeof(parameters) == TYPE_DICTIONARY else {}

	await _select_lane(method).drive(peer, id, method, safe_parameters)


# 通道类别判别器(数据→路由映射的词汇表)。由 lane_kind_for 返回;由
# _select_lane 映射到构造好的通道实例。
const LANE_READ := "read"
const LANE_MUTATION := "mutation"
const LANE_SCENE_LEASE := "scene_lease"


## `method` 依据其注册表标志应路由到的通道"类别" — 纯粹的数据→路由映射,
## 是这个抽象的核心不变量(直接单元测试,不需要活通道):
##   - scene.open 与要求活动场景的命令 → scene_lease(争用时该通道排队,否则
##     在内部落到变更/读取通道 — 与旧的 handle_scene_open /
##     try_queue_for_lease 块完全一致);
##   - 其他变更 → mutation;其余 → read。
## 非场景必需的命令跳过租约通道,等价于旧逻辑中 try_queue_for_lease 在做出
## 相同的变更/读取决策之前立即返回 false(不续约租约)。与抽取前 _dispatch_rpc
## 的顺序路由一致。
func lane_kind_for(method: String) -> String:
	if method == "scene.open" or _registry.is_active_scene_required(method):
		return LANE_SCENE_LEASE
	if _registry.needs_serialization(method):
		return LANE_MUTATION
	return LANE_READ


# 把 `method` 的通道类别映射到其构造好的通道实例。
func _select_lane(method: String):
	var kind := lane_kind_for(method)
	if kind == LANE_SCENE_LEASE:
		return _scene_lease_lane
	if kind == LANE_MUTATION:
		return _mutation_lane
	return _read_lane


# _cancel 路由:执行中 → 经跟踪的上下文取消;否则先在变更队列中标记排队
# 条目,再到场景队列。发出即忘 — 两种结果都不响应。执行中查找与变更队列
# 扫描都限定在发起请求的对端(上下文映射以 context_key(peer, id) 为键 —
# id 只在单个客户端内唯一)。
func _handle_cancel(peer: WebSocketPeer, parameters) -> void:
	var safe_params: Dictionary = parameters \
		if typeof(parameters) == TYPE_DICTIONARY else {}
	var target_id := str(safe_params.get("request_id", ""))
	# 执行中:经上下文取消(仅该对端对该 id 的注册)。
	var target_key := DispatchLane.context_key(peer, target_id)
	if _active_contexts.has(target_key):
		_active_contexts[target_key].cancel()
		return
	# 在变更队列中排队(按 peer 限定匹配):
	if _mutation_lane.cancel_queued(peer, target_id):
		return
	# 在场景队列中排队(归租约子模块所有;按 peer 限定匹配):
	_cancel_scene_queued.call(peer, target_id)
