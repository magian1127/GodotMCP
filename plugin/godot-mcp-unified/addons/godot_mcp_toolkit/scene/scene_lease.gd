@tool
extends RefCounted
## 协调按场景、限时生效的编辑租约(lease),以及防止并发对端各自瞄准不同
## 场景时互相污染"正在编辑标签页"的场景亲和队列。它持有租约状态
## (持有者/场景/续约时间戳)、每对端的场景亲和,以及等待租约的依赖标签页
## 命令 FIFO — 外加覆盖其上的完整机制:acquire / renew / release / steal
## (TTL 过期)/ drain,scene.open 争用处理,以及排队命令的执行路径。
## 派发层只是"路由"到这个子模块的公共 API;路由本身位于场景租约通道
## (dispatch_lane.gd)中,不在这里。
##
## 仅编辑器侧 — 并且按设计是"被污染的":它引用 EditorInterface(经由注入的
## 根解析器)并触达 Modules.CommandHelpers.open_scene_deferred。这是允许的,
## 因为模式 B 的运行时自动加载没有场景租约,且绝不能 preload 本文件
## (它没有亲和/标签页的概念 — 运行中的游戏永远独占自己的唯一 SceneTree)。
## 因此 #91713 不约束这个子模块,但运行时若引用它就会污染自动加载,
## 所以要严格保持仅编辑器可用:只有 mcp_server.gd(编辑器服务器)preload 它。
##
## 派发器/变更通道注入的接缝(让这个子模块不必回触及尚未抽出的服务器内部,
## 与 mutation_watchdog 的 force_clear 钩子呼应):根解析器 Callable(可测试性
## 接缝 — 编辑器注入 EditorInterface.get_edited_scene_root,单元测试注入桩)、
## command_received 的再发射、只读执行核心、变更忙时入队,以及变更通道入口。
## 注册表被直接持有(set_registry),与服务器持有它的方式一致,因为 drain 与
## scene.open 路径需要查询它并调用命令。Notifier / FileGuard / MCPToolkitError /
## open_scene_deferred 被直接触达(这个子模块已被污染,清洁闭包规则不约束它),
## 与服务器原先的 _send_* 包装完全一致。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Notifier := preload("res://addons/godot_mcp_toolkit/transport/notifier.gd")

# 等待租约的、依赖标签页的待处理命令(一条排队的 JSON-RPC 请求)。
class _SceneQueueEntry:
	var peer: WebSocketPeer
	var id  # int 或 null(JSON-RPC id)
	var method: String
	var params: Dictionary
	var queued_ms: int = 0
	var cancelled: bool = false

# 迟绑定的协作者。注册表由服务器的 set_registry 设置(它在 start() 构建
# 本子模块之前运行,随后在构造时在这里再次推送)。
var _registry: MCPToolkitCommandRegistry = null

# 注入的接缝(见类文档注释)。在 mcp_server._init_scene_lease 中一次性接线。
# _root_resolver: func() -> Node — 当前正在编辑的场景根(编辑器中是
#   EditorInterface;单元测试中是桩)。这是本子模块获知活动标签页的唯一途径。
var _root_resolver: Callable = Callable()
# _on_command: func(method: String) -> void — 再发射服务器的 command_received。
var _on_command: Callable = Callable()
# _run_read: func(peer, method: String, params: Dictionary, id) -> Dictionary (await)
#   — 派发器的只读执行核心(按 peer 记账上下文 + call_command);留在派发器中,
#   避免让 _active_contexts 跨越接缝共享。
var _run_read: Callable = Callable()
# _enqueue_mutation_if_busy: func(peer, id, method, params, queued_ms) -> bool —
#   若有变更正在执行,追加到变更 FIFO、发送 _queued 并返回 true;否则返回
#   false。变更通道状态(_mutation_in_flight / _mutation_queue)留在派发器中,
#   不进入租约子模块。
var _enqueue_mutation_if_busy: Callable = Callable()
# _execute_mutation: func(peer, id, method, params, scene_queued_ms) -> void —
#   变更通道入口(完成后它会重新抽取变更队列)。
var _execute_mutation: Callable = Callable()

# -- 租约状态 -------------------------------------------------------------------

# 每个对端的场景亲和:peer → 场景路径(无亲和则为 "")。
var _peer_scene_affinity: Dictionary = {}  # WebSocketPeer → String

# 场景租约状态。
var _lease_holder: WebSocketPeer = null
var _lease_scene: String = ""
var _lease_renewed_ms: int = 0

# 等待租约的、依赖标签页的待处理命令。
var _scene_queue: Array = []  # of _SceneQueueEntry


## 接线迟绑定的协作者。在构造时调用一次,先于任何派发。
## 每个接缝的契约见上方各字段的文档注释。
func set_handlers(root_resolver: Callable, on_command: Callable,
		run_read: Callable, enqueue_mutation_if_busy: Callable,
		execute_mutation: Callable) -> void:
	_root_resolver = root_resolver
	_on_command = on_command
	_run_read = run_read
	_enqueue_mutation_if_busy = enqueue_mutation_if_busy
	_execute_mutation = execute_mutation


func set_registry(registry: MCPToolkitCommandRegistry) -> void:
	_registry = registry


## 插件拆卸期间释放注册表与处理器引用,让本子模块持有的 Callable → GDScript
## 链(注册表的命令处理器,以及绑回服务器的各接缝)在服务器节点被释放之前
## 断开。
func clear_registry() -> void:
	_registry = null
	_root_resolver = Callable()
	_on_command = Callable()
	_run_read = Callable()
	_enqueue_mutation_if_busy = Callable()
	_execute_mutation = Callable()


# -- 派发入口(派发器路由到这些方法)--------------------------------------------


## scene.open:在派发层拦截,因为存在争用时绝不能调用 open_scene_from_path —
## 标签页切换会干扰租约持有者。校验逻辑与 _cmd_scene_open 一致。
func handle_scene_open(peer: WebSocketPeer, id, params: Dictionary) -> void:
	# 校验 file_path — 与 _cmd_scene_open 的检查一致。在这里拦截,是因为存在
	# 争用时绝不能调用 open_scene_from_path(标签页切换会干扰租约持有者)。
	var err = MCPToolkitError.require(params, ["file_path"])
	if err != null:
		Notifier.send_result(peer, id, err, "[MCPServer]")
		return
	var file_path := str(params.get("file_path", ""))
	var guard := Modules.FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		Notifier.send_result(peer, id,
			MCPToolkitError.fail("PATH_DENIED", str(guard["reason"])), "[MCPServer]")
		return
	if not FileAccess.file_exists(file_path):
		Notifier.send_result(peer, id, MCPToolkitError.fail("NOT_FOUND",
			"scene not found: %s" % file_path, MCPToolkitError.HINT_FILE_PATH), "[MCPServer]")
		return

	# 设置亲和。
	_peer_scene_affinity[peer] = file_path

	# 尝试获取租约。
	var acquired := try_acquire(peer, file_path)
	_on_command.call("scene.open")
	if acquired:
		# 已获取租约 — 调用真正的处理器(打开场景)。
		var result: Dictionary = await _registry.call_command("scene.open", params)
		Notifier.send_result(peer, id, result, "[MCPServer]")
	else:
		# 存在争用 — 不打开场景,返回 success + 争用提示。
		var hint := _build_contention_hint()
		var result := {"success": true, "path": file_path}
		if not hint.is_empty():
			result["hint"] = hint
		Notifier.send_result(peer, id, result, "[MCPServer]")


## 让依赖标签页的命令经过场景租约路由。已入队时返回 true(调用方必须随即
## 返回 — 尚无响应);命令应当立即进入变更/读取通道时返回 false。若本对端
## 持有租约且已在活动标签页上,则续约租约。不要求活动场景的命令直接短路
## 返回 false。
func try_queue_for_lease(peer: WebSocketPeer, id, method: String,
		params: Dictionary) -> bool:
	if not _registry.is_active_scene_required(method):
		return false
	var peer_scene := str(_peer_scene_affinity.get(peer, ""))
	var active_scene := _active_scene_path()
	if not peer_scene.is_empty() and peer_scene != active_scene:
		# 目标与活动标签页不符 — 为租约排队。
		var entry := _SceneQueueEntry.new()
		entry.peer = peer
		entry.id = id
		entry.method = method
		entry.params = params
		entry.queued_ms = Time.get_ticks_msec()
		_scene_queue.append(entry)
		Notifier.send_notification(peer, "_queued", {"request_id": id}, "[MCPServer]")
		return true
	# 目标与活动标签页一致(或没有亲和)— 正常执行。
	# 若本对端持有租约则续约。
	if _lease_holder == peer:
		_lease_renewed_ms = Time.get_ticks_msec()
	return false


## 把一条已排队(尚未执行)的场景队列命令标记为"排水时跳过" — 这是取消在
## 场景队列中的回退路径,适用于在执行中上下文或变更队列里找不到的目标。
## 按[param peer] 与 id 同时匹配(id 只在单个客户端内唯一,仅按 id 匹配可能
## 误取消另一个对端排队的命令)。找到匹配条目时返回 true。
func cancel_queued(peer: WebSocketPeer, target_id: String) -> bool:
	for entry in _scene_queue:
		if entry.peer == peer and str(entry.id) == target_id:
			entry.cancelled = true
			return true
	return false


## 断开连接时按对端清理:丢弃亲和,若该对端持有租约则释放(从而排水下一个
## 等待者),并移除该对端排队的场景命令。传输层已先清除自己的对端映射。
func on_peer_closed(peer: WebSocketPeer) -> void:
	_peer_scene_affinity.erase(peer)
	if _lease_holder == peer:
		release()  # 立即释放 → 排水队列。
	# 移除该对端排队的场景命令。
	_scene_queue = _scene_queue.filter(func(entry: _SceneQueueEntry):
		return entry.peer != peer)


# -- 租约机制 -------------------------------------------------------------------


# 经由注入的根解析器获取当前正在编辑的场景路径(编辑器中是 EditorInterface;
# 测试中是桩)。没有正在编辑的场景时返回 ""。
func _active_scene_path() -> String:
	var root: Node = _root_resolver.call()
	if root == null:
		return ""
	return root.scene_file_path


func _build_contention_hint() -> String:
	if _lease_holder == null or _lease_scene.is_empty():
		return ""
	return (
		"Note: another session is currently editing %s. "
		+ "Work that doesn't target this scene executes immediately "
		+ "without waiting — for example, reading or writing scripts "
		+ "for your own scenes, managing your files, or querying project "
		+ "info. Work that modifies nodes or reads scene trees will queue "
		+ "until the editor tab becomes available — wait times are variable."
	) % _lease_scene


## 为本对端的亲和场景获取(或续约)租约。纯簿记 — 刻意不在这里做原生的
## open_scene_from_path(原生打开违反 #75669 的延迟打开规则,且对扫描没有
## 防护);标签页激活发生在带防护的 _execute_scene_queued_* 路径中,经由
## _switch_to_affinity_scene。获取/续约返回 true;被其他对端持有(或场景已
## 被删除)返回 false。
func try_acquire(peer: WebSocketPeer, scene: String) -> bool:
	if _lease_holder == null:
		# 当前无持有者 — 获取。
		if not scene.is_empty() and not FileAccess.file_exists(scene):
			_peer_scene_affinity.erase(peer)
			return false  # 场景已被删除。
		_lease_holder = peer
		_lease_scene = scene
		_lease_renewed_ms = Time.get_ticks_msec()
		# 获取租约保持纯簿记 — 不做原生的 open_scene_from_path
		# (那会违反 #75669 的延迟打开规则,且对扫描没有防护)。
		# 标签页激活属于带防护的 _execute_scene_queued_* 路径,经由
		# _switch_to_affinity_scene。
		return true
	if _lease_holder == peer:
		# 同一对端 — 续约。
		_lease_renewed_ms = Time.get_ticks_msec()
		return true
	return false  # 租约被其他对端持有。


## 释放租约并排水下一个等待者。公开是为了让单元测试能演练释放分支;
## 生产路径经由过期/断开/scene.close 到达这里。
func release() -> void:
	_lease_holder = null
	_lease_scene = ""
	_lease_renewed_ms = 0
	drain()


## TTL 抢占:若有等待者排队且持有者已超过租约 TTL,则释放租约,让下一个
## 等待者能够获取。由服务器的 _process 每次轮询 tick 调用。
func check_expiry() -> void:
	# 在保存的重入期间不要抢占/排水(租约抢占会在保存中途排掉一条场景
	# 排队的命令)。
	if MCPToolkitSafeSceneOps.is_dispatching():
		return
	if _lease_holder == null:
		return
	if _scene_queue.is_empty():
		return  # 没有等待者 — 不让租约过期。
	var ttl_ms: int = ProjectSettings.get_setting(
		"mcp_toolkit/concurrency/scene_lease_ttl_ms", 8000)
	var elapsed := Time.get_ticks_msec() - _lease_renewed_ms
	if elapsed >= ttl_ms:
		# 抢占:已有对端等待,且租约超过了 TTL。
		release()  # 触发排水 → 下一个等待者获得租约。


## 把租约交给下一个有资格的等待者并执行其命令(一次一条;下一次排水在
## 完成时触发)。跳过已取消/已断开/未注册/亲和已清除的条目。由派发器在
## 变更队列排水后调用,并在释放/读取完成时内部调用。
func drain() -> void:
	while not _scene_queue.is_empty():
		var entry: _SceneQueueEntry = _scene_queue.pop_front()
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

		# 为该对端的亲和场景获取租约。
		var peer_scene := str(_peer_scene_affinity.get(entry.peer, ""))
		if peer_scene.is_empty():
			# 对端在排队期间清除了亲和 — 跳过。
			continue
		if not try_acquire(entry.peer, peer_scene):
			# 租约被其他对端持有 — 把条目放回并停止。
			_scene_queue.push_front(entry)
			return

		# 计算该条目排队了多久。
		var queued_ms := Time.get_ticks_msec() - entry.queued_ms

		# 按类型派发:变更 → 变更通道(遵守变更锁);
		# 读取 → 直接执行(无需锁)。
		if _registry.needs_serialization(entry.method):
			_execute_scene_queued_mutation(entry, queued_ms)
		else:
			_execute_scene_queued_read(entry, queued_ms)
		return  # 一次一条;下一次排水在完成时触发。
	# 场景队列已完全排空 — 没有剩余。


# 在执行场景排队命令之前,把编辑器切换到该对端的亲和场景
# (try_acquire 刻意不做打开)。针对活动的 EditorFileSystem 扫描有防护。
# 无法切换时返回 false 并向对端发送 TIMEOUT,调用方随即中止这一条
# (队列其余部分留给下一次排水触发)。
func _switch_to_affinity_scene(peer: WebSocketPeer, id) -> bool:
	var scene := str(_peer_scene_affinity.get(peer, ""))
	if scene.is_empty() or _active_scene_path() == scene:
		return true
	if await Modules.CommandHelpers.open_scene_deferred(scene):
		return true
	Notifier.send_result(peer, id, MCPToolkitError.fail("TIMEOUT",
		"could not switch to %s — EditorFileSystem still scanning" % scene), "[MCPServer]")
	return false


func _execute_scene_queued_mutation(entry: _SceneQueueEntry,
		queued_ms: int) -> void:
	# 在激活标签页之前重新入队(不切换标签页),避免变更锁忙碌时做一次
	# 多余的切换。
	if _enqueue_mutation_if_busy.call(entry.peer, entry.id, entry.method,
			entry.params, queued_ms):
		return
	# 在这里激活该对端的亲和场景,针对扫描有防护。
	if not await _switch_to_affinity_scene(entry.peer, entry.id):
		return
	_execute_mutation.call(entry.peer, entry.id, entry.method, entry.params, queued_ms)


func _execute_scene_queued_read(entry: _SceneQueueEntry,
		queued_ms: int) -> void:
	# 读取之前激活该对端的亲和场景(带防护)。
	if not await _switch_to_affinity_scene(entry.peer, entry.id):
		return
	var result: Dictionary = await _run_read.call(entry.peer, entry.method, entry.params, entry.id)
	if queued_ms > 0:
		inject_concurrency_metadata(result, queued_ms)
	Notifier.send_result(entry.peer, entry.id, result, "[MCPServer]")
	# 检查同一对端/场景是否还有更多条目。
	drain()


## scene.close 成功之后:丢弃该对端对该场景的亲和;若该对端在该场景上持有
## 租约则释放。由变更通道在 scene.close 变更完成后调用。
func post_mutation_cleanup(peer: WebSocketPeer, method: String,
		params: Dictionary, result: Dictionary) -> void:
	if method != "scene.close":
		return
	if not result.get("success", false):
		return
	var file_path := str(params.get("file_path", ""))
	if _peer_scene_affinity.get(peer, "") == file_path:
		_peer_scene_affinity.erase(peer)
	if _lease_holder == peer and _lease_scene == file_path:
		release()


## 在结果上标注它在场景租约上等待了多久:始终附加一个零令牌的 _meta 块,
## 超过 3 秒阈值再附一句正文。由变更通道与排队读取路径为等待过的命令调用。
## queued_ms <= 0 时为空操作。
func inject_concurrency_metadata(result: Dictionary, queued_ms: int) -> void:
	if queued_ms <= 0:
		return
	# 第 1 层:_meta(零 LLM 令牌 — 桥接可提取到上下文协议(MCP) _meta)。
	result["_meta"] = {
		"concurrency": {
			"queued_ms": queued_ms,
			"reason": "scene_lease_wait",
			"lease_holder_scene": _lease_scene,
		}
	}
	# 第 3 层:按阈值门控的正文句子(仅 >3 秒)。
	if queued_ms > 3000:
		var note := (
			"(Scene access waited %.1fs — another session holds the "
			+ "active tab. Scene-independent tools execute without waiting.)"
		) % (queued_ms / 1000.0)
		var existing_hint := str(result.get("hint", ""))
		if not existing_hint.is_empty():
			result["hint"] = existing_hint + " " + note
		else:
			result["hint"] = note


## 当前租约持有者(或 null)。供单元测试的 acquire/release 断言使用的只读
## 访问器;生产调用方经由上面的方法路由。
func lease_holder() -> WebSocketPeer:
	return _lease_holder
