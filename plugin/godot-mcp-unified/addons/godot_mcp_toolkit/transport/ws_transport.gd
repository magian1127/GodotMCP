@tool
extends RefCounted
## 持有一个已绑定的 TCP 监听器及其 WebSocket 对端:扫描/监听(包括
## ERR_ALREADY_IN_USE 的丢弃重建)、接受连接、鉴权握手分帧、对端生命周期,
## 以及鉴权超时 — 由外部调度的 pump() 原语驱动(节奏由调用方决定)。
## "何时"与各种副作用留在持有它的服务器里,以 Callable 注入;这个基类只
## 负责线路字节的收发和对端状态跟踪。
##
## 按约定对导出友好:模式 B 的运行时自动加载会 preload 本文件,而 GDScript
## 在解析期解析标识符(先于任何 is_editor_hint() 守卫),因此只要引用任何
## 编辑器专属类 — 或 preload 一个这样做的脚本 — 都会让自动加载在导出模板中
## 解析失败(godotengine/godot#91713,4.2–4.6 均未修复)。这里的全部标识符
## 集合是 TCPServer / WebSocketPeer / JSON / Time / ProjectSettings / MCPAuth,
## 且只 preload 对导出友好的文件。请保持这一状态:对 "Editor" 的 grep 必须
## 在非注释行中零命中。

const MCPAuth := preload("res://addons/godot_mcp_toolkit/security/auth.gd")

# 钉住模式下,对同一个被占用端口做这么多次节流重试(一段有界的"宽限期",
# 用以挺过重启时上一个实例尚未释放端口的情形),之后才记录精确错误。
# 此后它会继续监视该端口,所以之后的释放仍能恢复 — 上限只作用于响亮日志
# 阶段,不作用于恢复。
const _PIN_GRACE_ATTEMPTS := 5

# 由持有它的服务器在构造之后注入。基类只做机械动作;编辑器(模式 A)与
# 运行时(模式 B)之间所有不同的决策/副作用都经由这些接缝之一。
#
# _on_message:      func(peer: WebSocketPeer, text: String) -> void
#   服务器的编排者:解析帧并路由"鉴权 vs 派发"(鉴权分支调用 validate_auth(),
#   其余走自己的派发)。可以返回协程 — poll_peers() 会 await 它,因此编辑器
#   每 tick 顺序派发的行为得以保留(await 一个同步调用是空操作,所以运行时
#   的不 await 行为同样得以保留)。
# _build_auth_ack:  func(message: Dictionary) -> Dictionary
#   服务器提供鉴权确认载荷(编辑器附加 godot_version+version;运行时返回
#   {"authed": true})。纯函数 — 无副作用。
# _on_peer_authed:  func(count: int) -> void
#   鉴权成功后触发(编辑器 → 第一个对端触发失焦提速 + client_connected;
#   运行时 → 空操作)。
# _on_peer_closed:  func(peer: WebSocketPeer, was_authed: bool) -> void
#   清理期间每个关闭的对端触发一次(编辑器 → 租约清理 + 失焦恢复 +
#   client_disconnected;运行时 → 空操作)。
# _on_bound:        func(port: int) -> void
#   全新端口扫描完成绑定的那一刻触发(不是幂等的重复绑定),让服务器可以
#   发布新端口(运行时 → RegistryClient.set_runtime;编辑器 → 空操作)。
#   与抽取前运行时调用 set_runtime 的位置一致。
# _on_listen_conflict: func(port: int, active: bool) -> void
#   在监听冲突的"状态变化"时触发 — 监听器在其被允许的任何位置尝试绑定都
#   失败(钉住端口被占用、扫描区间耗尽,或丢失的重绑定),或该状况解除
#   (成功绑定时 active=false)。port 是钉住端口/区间基址。编辑器把它路由到
#   停靠面板警告 + 状态标签;运行时留空(子游戏进程无法触达停靠面板),
#   依赖传输层在游戏控制台里的 push_warning/push_error。
var _on_message: Callable = Callable()
var _build_auth_ack: Callable = Callable()
var _on_peer_authed: Callable = Callable()
var _on_peer_closed: Callable = Callable()
var _on_bound: Callable = Callable()
var _on_listen_conflict: Callable = Callable()

# 监听/接受连接警告的标签前缀,由持有它的服务器提供,让编辑器与运行时
# 保持各自不同的控制台标签。
var _log_prefix: String = "[MCPServer]"
# 整个端口范围耗尽时记录的警告。由服务器逐字提供,因为编辑器与运行时的
# 措辞不同 — 一个 "%d/%d" 格式,由基类填入低/高端口。会自动加上前缀。
var _exhausted_warning: String = "no free port in %d-%d; will retry every ~1s"
# 端口扫描范围与绑定地址,在构造时提供(编辑器与运行时监听不同的端口范围)。
var _port_base: int = 6550
var _port_range: int = 11
var _bind: String = "127.0.0.1"
# 对重监听重试做节流,避免端口被其他进程短暂占住时刷屏日志。60fps 下约
# 1 秒;桥接的重连退避也是同一量级,避免重试在退避之上继续堆积。
var _relisten_frame_interval: int = 60
# 在此窗口内未发送有效鉴权消息的对端,将以 WS 代码 1008(策略违规)关闭。
var _auth_timeout_ms: int = 2000
# poll_peers() 是否"等待"每一条 _on_message 投递。编辑器(模式 A)await,
# 让派发在单个轮询 tick 内保持严格顺序(其变更串行化依赖于此);运行时
# (模式 B)不 await — 发出即忘,协程处理器(例如等待 frame_post_draw 的
# runtime.screenshot)独立运行,轮询循环完成本轮。与各服务器抽取前的行为
# 完全一致。
var _await_messages: bool = true
# 钉住模式:绑定确切的 _port_base,否则失败 — 绝不扫描到其他端口
# (确定性的多实例)。被钉住但被占用的端口会立即暴露,并在有界的
# _PIN_GRACE_ATTEMPTS 宽限内重试同一端口。扫描模式(默认)保持不变。
var _pinned: bool = false
# 服务器提供的"如何解决"尾注,附加到精确的钉住冲突错误之后,让编辑器与
# 运行时针对各自的环境变量措辞。
var _pin_conflict_hint: String = ""

var _tcp_server: TCPServer = null
var _peers: Array[WebSocketPeer] = []
var _peer_authed: Dictionary = {}       # WebSocketPeer -> true(仅已鉴权对端)
var _peer_connect_ms: Dictionary = {}   # WebSocketPeer -> int(接受时的 ticks_msec)
var _relisten_countdown: int = 0
# 跟踪当前连续监听失败的过程,以便记录第一次(带提示)、重试期间保持安静,
# 并在恢复时带上尝试次数宣布恢复。每次监听成功时重置。
var _consecutive_failures: int = 0
# -1 = 从未绑定。
var _bound_port: int = -1
var _session_token: String = ""
# 为 true 表示监听器在其被允许的任何位置尝试绑定都失败了 — 钉住端口被占用、
# 扫描区间耗尽,或丢失的重绑定。驱动编辑器停靠面板警告 + 状态标签(经由
# _on_listen_conflict)。任何一次成功绑定都会清除它。
var _listen_conflict: bool = false
# 记录精确错误之前剩余的同端口有界宽限尝试次数。
var _pin_grace_remaining: int = 0
# 一次性闩锁,让宽限期后的精确错误只记录一次,而不是每次重试都记录。
var _pin_error_logged: bool = false


## 配置监听器参数与控制台标签。由持有它的服务器在构造之后、第一次 pump()
## 之前调用一次。await_messages 选择派发语义(见字段文档):编辑器为 true,
## 运行时为 false。exhausted_warning 是服务器逐字提供的端口范围耗尽消息
## (一个 "%d/%d" 格式,对应低/高端口;自动加前缀)。pinned 为 true 时,
## port_range 必须为 1 且 port_base 是精确钉住值 — 监听器要么绑定它要么失败
## (精确绑定,否则失败),绝不扫描别处;pin_conflict_hint 是服务器提供的
## "如何解决"尾注,附加到精确冲突错误之后。
func configure(log_prefix: String, port_base: int, port_range: int, bind: String,
		relisten_frame_interval: int, auth_timeout_ms: int,
		await_messages: bool, exhausted_warning: String,
		pinned: bool = false, pin_conflict_hint: String = "") -> void:
	_log_prefix = log_prefix
	_port_base = port_base
	_port_range = port_range
	_bind = bind
	_relisten_frame_interval = relisten_frame_interval
	_auth_timeout_ms = auth_timeout_ms
	_await_messages = await_messages
	_exhausted_warning = exhausted_warning
	_pinned = pinned
	_pin_conflict_hint = pin_conflict_hint
	if pinned:
		_pin_grace_remaining = _PIN_GRACE_ATTEMPTS


## 注入每服务器的接缝(见上方字段文档)。由持有它的服务器在 configure() 之后
## 立即调用一次。用不到某个接缝的服务器(例如运行时不需要鉴权确认覆盖或
## 关闭/鉴权副作用)传入空 Callable,基类会回退到它的机械默认行为。
func set_handlers(on_message: Callable, build_auth_ack: Callable,
		on_peer_authed: Callable, on_peer_closed: Callable,
		on_bound: Callable = Callable(),
		on_listen_conflict: Callable = Callable()) -> void:
	_on_message = on_message
	_build_auth_ack = build_auth_ack
	_on_peer_authed = on_peer_authed
	_on_peer_closed = on_peer_closed
	_on_bound = on_bound
	_on_listen_conflict = on_listen_conflict


func set_token(token: String) -> void:
	_session_token = token


## 当前会话令牌,供必须把它重写到新 user:// 路径(配置/名称变更之后)的
## 服务器读取,而无需自持一份副本。
func get_token() -> String:
	return _session_token


func is_listening() -> bool:
	return _tcp_server != null and _tcp_server.is_listening()


func get_bound_port() -> int:
	return _bound_port


## 为 true 表示监听器在其被允许的任何位置尝试绑定都失败了(钉住端口被占用、
## 扫描区间耗尽,或丢失的重绑定)。编辑器服务器把它作为停靠面板警告 +
## 状态标签状态暴露出来。
func is_listen_conflict() -> bool:
	return _listen_conflict


## 钉住模式下本传输层必须绑定的精确端口,否则为 -1。
func get_pinned_port() -> int:
	return _port_base if _pinned else -1


func is_authed(peer: WebSocketPeer) -> bool:
	return _peer_authed.has(peer)


func get_authed_count() -> int:
	return _peer_authed.size()


## 已鉴权的对端(用于广播)。按 Notifier.broadcast 辅助方法期望的原始键数组
## 返回;调用方不得经由它修改映射。
func get_authed_peers() -> Array:
	return _peer_authed.keys()


# -- 泵原语 ---------------------------------------------------------------------


## 驱动一轮"监听维护 + 接受连接 + 轮询对端 + 清理"循环。节奏由调用方决定
## (编辑器:call_deferred @ ~15 Hz,受 is_dispatching() 门控;运行时:每帧
## 内联)。执行一轮后返回;绝不自行循环。若本轮有已鉴权对端关闭则返回 true,
## 让调用方每 tick 发出一次聚合的断开(编辑器使用;运行时忽略)。
##
## 顺序与抽取前的服务器完全一致:监听器不可用时,尝试(重新)监听并返回,
## 本 tick 不接受/不轮询;否则接受待处理连接、轮询对端,并清理已关闭者。
func pump() -> bool:
	if _tcp_server == null or not _tcp_server.is_listening():
		ensure_listening()
		return false
	accept_pending()
	var closed := await poll_peers()
	return cleanup(closed)


# -- 监听 / 重监听 --------------------------------------------------------------


## 首次端口扫描或幂等的重监听,与抽取前的拆分一致:尚无绑定端口时扫描
## 区间;已知端口时(节流地)重试它。重试失败时丢弃已闩住的 TCPServer 并在
## 下次尝试时重建(调用 listen() 失败过的 TCPServer 若被复用会闩住
## ERR_ALREADY_IN_USE)。
func ensure_listening() -> void:
	# 已在监听 → 无需确保。没有这个守卫,在存活期间调用会停止并重新监听
	# 同一个套接字(对已打开的 TCPServer 调用 listen() 会失败
	# ERR_ALREADY_IN_USE),短暂掉线并让冲突状态抖动。pump() 以
	# is_listening() 门控所以不会走到这里;这个守卫让方法对任何调用方都安全。
	if _tcp_server != null and _tcp_server.is_listening():
		return
	# 先做重试节流(与抽取前的 _try_listen 顺序一致),让已武装的倒计时同时
	# 门控重新扫描(尚未绑定端口)与重绑定。初次 start() 调用时倒计时为 0,
	# 因此第一次扫描是立即的。
	if _relisten_countdown > 0:
		_relisten_countdown -= 1
		return
	if _bound_port < 0:
		if _pinned:
			_pin_listen()
		else:
			_scan_and_listen()
		return
	if _tcp_server == null:
		_tcp_server = TCPServer.new()
	var error := _tcp_server.listen(_bound_port, _bind)
	if error == OK:
		_note_listen_success(true)
		return
	var hint := ""
	if error == ERR_ALREADY_IN_USE:
		hint = " (ERR_ALREADY_IN_USE — will retry silently every ~1s)"
	_tcp_server.stop()
	_note_listen_failure("%s rebind %s:%d failed (err %d)%s" % [
		_log_prefix, _bind, _bound_port, error, hint])


# 依次尝试 _port_base.._port_base+_port_range-1,绑定第一个空闲端口。成功时
# 设置 _bound_port。若全部占用,则武装节流重试。
func _scan_and_listen() -> void:
	for offset in range(_port_range):
		var candidate := _port_base + offset
		var server := TCPServer.new()
		var err := server.listen(candidate, _bind)
		if err == OK:
			_tcp_server = server
			_bound_port = candidate
			_note_listen_success(false)
			print("%s listening on %s:%d" % [_log_prefix, _bind, _bound_port])
			if _on_bound.is_valid():
				_on_bound.call(_bound_port)
			return
		server.stop()
	# 服务器逐字提供的消息(编辑器与运行时措辞不同)。
	_note_listen_failure("%s %s" % [
		_log_prefix, _exhausted_warning % [_port_base, _port_base + _port_range - 1]])


# 钉住模式:绑定确切的 _port_base,否则失败 — 绝不扫描到其他端口。
# 第一次冲突立即暴露(经 _on_listen_conflict 的停靠面板警告 + 一条
# push_warning);同一端口在有界的宽限内重试 — 每次尝试都使用全新的
# TCPServer,因为调用 listen() 失败过的实例若被复用会闩住
# ERR_ALREADY_IN_USE — 以挺过上一个仍在释放端口的实例;宽限耗尽 → 一条
# 精确的 push_error;之后的成功会清除冲突并重新绑定。响亮且确定 — 绝不
# 静默回退到其他端口,绝不静默挂起。
func _pin_listen() -> void:
	if _tcp_server == null:
		_tcp_server = TCPServer.new()
	var error := _tcp_server.listen(_port_base, _bind)
	if error == OK:
		_bound_port = _port_base
		_consecutive_failures = 0
		_relisten_countdown = 0
		print("%s listening on %s:%d" % [_log_prefix, _bind, _bound_port])
		if _on_bound.is_valid():
			_on_bound.call(_bound_port)
		if _listen_conflict:
			print("%s pinned port %d is now free - bound after the conflict cleared" % [
				_log_prefix, _port_base])
			_set_listen_conflict(false)
		_pin_grace_remaining = _PIN_GRACE_ATTEMPTS
		_pin_error_logged = false
		return
	# 被占用(或其他绑定失败)— 丢弃失败的服务器,让下一次尝试从全新实例
	# 开始(复用的实例会因闩住的 ERR_ALREADY_IN_USE 持续失败),然后武装
	# 节流重试。
	_tcp_server.stop()
	_tcp_server = null
	if not _listen_conflict:
		push_warning("%s pinned port %d in use - retrying the same port briefly in case a prior instance is still releasing it" % [
			_log_prefix, _port_base])
		_set_listen_conflict(true)
	if _pin_grace_remaining > 0:
		_pin_grace_remaining -= 1
		if _pin_grace_remaining == 0 and not _pin_error_logged:
			_pin_error_logged = true
			push_error("%s could not bind pinned port %d - it is still in use. %s" % [
				_log_prefix, _port_base, _pin_conflict_hint])
	_relisten_countdown = _relisten_frame_interval


# 翻转监听冲突标志,且只在状态"变化"时通知持有者(编辑器暴露/清除停靠面板
# 警告 + 状态标签;运行时不传处理器)。保持幂等,状态未变时的重复失败不会
# 重复触发接缝。
func _set_listen_conflict(active: bool) -> void:
	if _listen_conflict == active:
		return
	_listen_conflict = active
	if _on_listen_conflict.is_valid():
		_on_listen_conflict.call(_port_base, active)


# 记录一次失败的监听(扫描区间耗尽,或丢失的重绑定):累加失败计数,在
# 一连串失败中的第一次发出警告,丢弃死掉的服务器(调用 listen() 失败过的
# TCPServer 会闩住 ERR_ALREADY_IN_USE — 下一次尝试需要全新实例),提升
# 停靠面板的监听冲突状态,并武装节流重试。
func _note_listen_failure(warning: String) -> void:
	_consecutive_failures += 1
	if _consecutive_failures == 1:
		push_warning(warning)
	_tcp_server = null
	_set_listen_conflict(true)
	_relisten_countdown = _relisten_frame_interval


# 记录一次成功的监听:清除失败连击、重试闩锁和监听冲突状态。从先前失败
# 过程中恢复时,记录恢复日志(首次启动的全新扫描传 false,跳过该行)。
func _note_listen_success(emit_recovery_log: bool) -> void:
	if emit_recovery_log and _consecutive_failures > 0:
		print("%s listening on %s:%d (recovered after %d failed attempts)" % [
			_log_prefix, _bind, _bound_port, _consecutive_failures])
	_consecutive_failures = 0
	_relisten_countdown = 0
	_set_listen_conflict(false)


# -- 接受 / 轮询 / 清理 ---------------------------------------------------------


## 接受每个待处理连接:取走流,包装成 WebSocketPeer,其入站/出站缓冲大小
## 取自 ws_buffer_kb(在这里只读取一次 — 这是该 ProjectSetting 读取的唯一
## 归属),并跟踪对端及其连接时间。ProjectSettings 是核心单例(运行时也存在),
## 因此读取它不会破坏基类的导出友好性。
func accept_pending() -> void:
	while _tcp_server.is_connection_available():
		var stream := _tcp_server.take_connection()
		var peer := WebSocketPeer.new()
		var buffer_kb: int = ProjectSettings.get_setting("mcp_toolkit/limits/ws_buffer_kb", 1024)
		peer.inbound_buffer_size = buffer_kb * 1024
		peer.outbound_buffer_size = buffer_kb * 1024
		var accept_error := peer.accept_stream(stream)
		if accept_error != OK:
			push_warning("%s accept_stream failed (%d)" % [_log_prefix, accept_error])
			continue
		_peers.append(peer)
		_peer_connect_ms[peer] = Time.get_ticks_msec()


## 轮询每个对端;执行鉴权超时(对端超时未鉴权则以 1008 关闭);排空可用的
## 数据包,把每帧投递给 _on_message(有 await,保持编辑器每 tick 的顺序
## 派发)。返回已关闭的对端,供 cleanup() 使用。
func poll_peers() -> Array[WebSocketPeer]:
	var closed: Array[WebSocketPeer] = []
	var now_ms := Time.get_ticks_msec()
	for peer in _peers:
		peer.poll()
		var state := peer.get_ready_state()
		if state == WebSocketPeer.STATE_CLOSED:
			closed.append(peer)
			continue
		if state != WebSocketPeer.STATE_OPEN:
			continue
		# 鉴权超时 — 关闭超时未鉴权的对端。
		if not _peer_authed.has(peer):
			if now_ms - int(_peer_connect_ms.get(peer, 0)) > _auth_timeout_ms:
				peer.close(1008, "auth timeout")
				closed.append(peer)
				continue
		while peer.get_available_packet_count() > 0:
			var text := peer.get_packet().get_string_from_utf8()
			# 编辑器 await(本 tick 内顺序派发);运行时发出即忘(协程处理器独立
			# 运行)。见 _await_messages。
			if _await_messages:
				await _on_message.call(peer, text)
			else:
				_on_message.call(peer, text)
	return closed


## 为已关闭的对端清除对端映射,并为每个对端触发 _on_peer_closed(服务器在
## 那里做租约/失焦/信号清理)。was_authed 在清除之前读取,让服务器能区分
## 掉线的是否为已鉴权对端。只要有任一关闭的对端是已鉴权的就返回 true,让
## 服务器按批次发出一次聚合的断开(抽取前的形态),并用重入安全的局部变量。
func cleanup(closed: Array[WebSocketPeer]) -> bool:
	var had_authed_disconnect := false
	for peer in closed:
		var was_authed := _peer_authed.has(peer)
		if was_authed:
			had_authed_disconnect = true
		_peers.erase(peer)
		_peer_authed.erase(peer)
		_peer_connect_ms.erase(peer)
		if _on_peer_closed.is_valid():
			_on_peer_closed.call(peer, was_authed)
	return had_authed_disconnect


# -- 鉴权握手 -------------------------------------------------------------------


## 用会话令牌校验解析后的鉴权消息。成功:标记对端已鉴权,发送服务器提供的
## 鉴权确认帧,以新的已鉴权数触发 _on_peer_authed,并返回 true。失败:以
## 1008 关闭并返回 false。确认载荷由服务器提供(编辑器与运行时不同);
## 分帧由基类负责。
func validate_auth(peer: WebSocketPeer, message: Dictionary) -> bool:
	if not MCPAuth.validate(message, _session_token):
		peer.close(1008, "invalid token")
		return false
	_peer_authed[peer] = true
	var ack: Dictionary = {"authed": true}
	if _build_auth_ack.is_valid():
		ack = _build_auth_ack.call(message)
	peer.send_text(JSON.stringify(ack))
	if _on_peer_authed.is_valid():
		_on_peer_authed.call(_peer_authed.size())
	return true


# -- 拆除 -----------------------------------------------------------------------


## 以给定代码/原因关闭每个对端并清空对端映射。供服务器的 stop()(1000)与
## regenerate_token()(1008 — 对端必须重新鉴权)使用。不触碰 TCPServer;
## 那归服务器的 stop() 管。
func close_all(code: int, reason: String) -> void:
	for peer in _peers:
		if peer != null:
			if reason.is_empty():
				peer.close(code)
			else:
				peer.close(code, reason)
	_peers.clear()
	_peer_authed.clear()
	_peer_connect_ms.clear()


## 停止并丢弃 TCPServer,重置监听簿记 — 最终的拆除步骤,用于 close_all()
## 关闭全部对端之后。
func shutdown_listener() -> void:
	if _tcp_server != null:
		_tcp_server.stop()
		_tcp_server = null
	_relisten_countdown = 0
	_consecutive_failures = 0
	_bound_port = -1
	_set_listen_conflict(false)
	_pin_grace_remaining = _PIN_GRACE_ATTEMPTS if _pinned else 0
	_pin_error_logged = false
