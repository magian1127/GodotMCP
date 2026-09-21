@tool
extends Node
## 编辑器侧的上下文协议(MCP)服务器 — 传输编排门面。
##
## 构造、接线并驱动传输子系统,只保留生命周期与跨子系统接缝:
## ws_transport 持有 TCP 监听器 + WS 对端接受/轮询 + 鉴权握手分帧,notifier
## 持有通知分帧/发送,server_request_router + dispatch_lane 持有请求路由与
## 通道派发,而 scene_lease / mutation_watchdog / unfocused_sleep_controller /
## lsp_publisher 各自管辖自己的领域。所有命令逻辑位于 commands/ 下的
## 各领域模块中。

signal client_connected(peer_count: int)
signal client_disconnected(peer_count: int)
signal command_received(method: String)
## 当上下文协议服务器报告新的 GDScript LSP 判定(set_reported_lsp_status)时发出,
## 让停靠面板恰好在变化时刷新 — 无需轮询,即使状态稍后被重新评估
## (例如在一次 LSP 调用时)标签也不会过期。
signal lsp_status_changed
## 在会话令牌于用户路径变更后被重写到新的 user:// 路径时发出,携带新的令牌
## 路径。插件(注册表生命周期所有者)经由 ensure_registered 重新发布条目,
## 它会保留任何活动的 runtime_port/runtime_pid — register 会把它们置空,
## 破坏跨重命名仍存活的游戏的模式 B 发现。
signal token_rewritten(token_path: String)
## 在监听状态变化时发出 — 全新绑定、监听冲突(钉住端口被占用/扫描区间耗尽/
## 丢失的重绑定)被触发或解除,或端口配置错误。停靠面板重读 get_port_warning()
## 与监听状态并重绘。是停靠面板自身刷新拉取的即时对应物。
signal port_status_changed
## 在监听器全新绑定时发出(初次,或迟到 — 例如启动冲突后释放的钉住端口),
## 携带绑定的端口。组合根在它之上(重新)发布注册表条目,让实际绑定的端口
## 在每种模式下都落入 projects.json — 这是服务器失步交叉检查所读取的
## 地面真值。
signal port_bound(port: int)

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const MCPAuth := preload("res://addons/godot_mcp_toolkit/security/auth.gd")
const UndoRedoHelpers := preload("res://addons/godot_mcp_toolkit/scene/undo_redo_helpers.gd")
const Notifier := preload("res://addons/godot_mcp_toolkit/transport/notifier.gd")
const WsTransport := preload("res://addons/godot_mcp_toolkit/transport/ws_transport.gd")
const MutationWatchdog := preload("res://addons/godot_mcp_toolkit/transport/dispatch/mutation_watchdog.gd")
const SceneLease := preload("res://addons/godot_mcp_toolkit/scene/scene_lease.gd")
const ServerRequestRouter := preload("res://addons/godot_mcp_toolkit/transport/dispatch/server_request_router.gd")
const DispatchLane := preload("res://addons/godot_mcp_toolkit/transport/dispatch/dispatch_lane.gd")
const UnfocusedSleepController := preload("res://addons/godot_mcp_toolkit/core/unfocused_sleep_controller.gd")
const LspPublisher := preload("res://addons/godot_mcp_toolkit/paths/lsp_publisher.gd")
const PortConfig := preload("res://addons/godot_mcp_toolkit/transport/port_config.gd")

# 默认的编辑器扫描区间(含端点)。GODOT_MCP_EDITOR_PORT 钉住一个精确端口
# (必须精确绑定,否则失败);GODOT_MCP_EDITOR_PORT_MIN/_MAX 重新安置该区间。
# 由 PortConfig 解析;两种方式互斥(钉住会忽略区间)。
const PORT_MIN := 6550
const PORT_MAX := 6560  # 6550..6560,含端点
const _ENV_PIN := "GODOT_MCP_EDITOR_PORT"
const _ENV_PORT_MIN := "GODOT_MCP_EDITOR_PORT_MIN"
const _ENV_PORT_MAX := "GODOT_MCP_EDITOR_PORT_MAX"
const BIND := "127.0.0.1"
const JSONRPC_VERSION := "2.0"
# 对重监听重试做节流,避免端口被其他进程短暂占住时刷屏日志(例如第二个
# 编辑器实例、一个过期的调试器)。60 帧 ≈ 60fps 下的 1 秒;桥接的重连退避
# 也是同一量级,避免重试在桥接的退避之上继续堆积。
const _RELISTEN_FRAME_INTERVAL := 60
# 每 N 帧轮询一次 TCPServer/WebSocket 对端,而不是每帧都轮询。
# Godot 存在一个竞态:插件的 _process 工作与由文件系统停靠面板交互触发的
# 主循环工作之间(经由 Main::iteration 的重入 — 参见
# godotengine/godot#46893、#54864、#110891)。
# 两个缓解手段叠加:
#   1. 跳帧:以约 15Hz(4 帧)而不是 60Hz 轮询,把碰撞窗口缩小约 4 倍。
#   2. 延迟派发:_process 经由 call_deferred 调度轮询体而不是内联执行,
#      把我们的 I/O 移出重入最危险的 _process 调用栈。
# 副作用:命令在延迟调用上下文中运行,因此使用进度对话框的 Godot API
# (例如 save_scene)会记录无害的 progress_dialog.cpp 错误。这是可以接受的 —
# 另一种选择(内联 _process 轮询)会造成可复现的编辑器崩溃。
# 截至 Godot 4.5/4.6-dev,上游没有结构性修复。
# 曾考虑过冷/热睡眠模式(无客户端时 set_process(false))但被否决:不存在
# 可以唤醒的 TCPServer accept 信号,而且门控这个循环会与延迟派发 +
# 始终运行的 _mutation_watchdog.tick() 相互掣肘,收益却很小。
const _POLL_FRAME_INTERVAL := 4
# 鉴权超时。在此窗口内未发送有效鉴权消息的对端,将以 WS 关闭代码 1008
# (策略违规)关闭。
const _AUTH_TIMEOUT_MS := 2000

# 持有 TCP 监听器 + WS 对端 + 鉴权握手分帧 + 绑定的端口 + 会话令牌。
# 本文件以 Callable 注入编辑器侧的决策/副作用(鉴权确认载荷、鉴权成功时
# 的提速、关闭时的租约清理),并经由 _poll_connections → transport.pump()
# 驱动它。
var _transport: WsTransport = null
var _poll_frame_counter := 0
# 解析出的监听端口配置(PortConfig.resolve 结果)。非空的 "error" 是致命的 —
# 编辑器服务器不监听,停靠面板会显示原因。
var _port_config: Dictionary = {}
# 在 start() 时捕获,让 editor.get_console 的日志文件选择启发式可以优先
# 选择启动之后的日志而不是过期的轮转日志。
var _plugin_boot_time: int = 0
var _registry: MCPToolkitCommandRegistry = null
# 把解析后的 JSON-RPC 请求路由到其注册表策略选择的通道(读取/变更/场景租约)
# 并驱动它。持有执行中可取消上下文映射 + 三条通道;本文件只保留生命周期接线,
# 以及各通道需要注入的跨子系统接缝(场景租约处理器、看门狗、command_received)。
# 在 start() 中构建;_handle_message 把每个已鉴权帧交给它。参见
# server_request_router.gd / dispatch_lane.gd。
var _router: ServerRequestRouter = null
# LSP 端点发布器(解析本编辑器的 GDScript-LSP 端点、发布到注册表、在设置
# 变更时防抖地重新发布)+ 服务器报告的 LSP 存活镜像,位于 lsp_publisher.gd。
# 本文件只保留跨子系统的触发点(start() → 连接监视;stop() → 断开监视;
# 服务器报告判定 → set_reported_lsp_status)、停靠面板与命令触达的公共 LSP
# 获取器(委托给这个子模块),以及 lsp_status_changed 信号(子模块报告变化时
# 在这里再发射 — 停靠面板在服务器上绑定它)。在 start() 中构造,注入绑定
# 端口来源与状态变化的再发射。参见 lsp_publisher.gd。
var _lsp: LspPublisher = null
# 失焦响应机制(调低/恢复/自愈机器级的失焦睡眠 EditorSetting 及其
# 先写者胜备份)位于 unfocused_sleep_controller.gd。本文件只保留跨子系统的
# 触发点(首个已鉴权连接 → 调低;最后一次断开 → 恢复;start() → 先自愈),
# 以及停靠面板读取的公共获取器,各自委托给这个子模块。在 start() 中构造,
# 注入 EditorSettings 访问器。参见 unfocused_sleep_controller.gd。
var _unfocused: UnfocusedSleepController = null
## 由 plugin.gd 设置,让领域命令可以调用 EditorPlugin API
## (例如 add_autoload_singleton 以立即刷新编辑器缓存)。
var editor_plugin: EditorPlugin = null

## 持有 UndoRedo 辅助方法的 Node,领域命令按字符串名称引用它们。
## 在 start() 中填充;命令闭包经 server.undo_helpers 访问。
var undo_helpers: Node = null

# -- 变更串行化 -----------------------------------------------------------------
# 多个 WebSocket 对端同时连接时,变更命令不得在 await 边界交错。单飞标志 +
# FIFO 队列确保任一时刻至多一个变更在执行。只读命令完全绕过锁。
#
# 单飞标志 + FIFO 队列 + 变更的执行/排水位于 dispatch_lane.gd 的
# MutationLane(由派发器构造)。本文件只保留看门狗实例(它必须在每个
# _process 帧运行,与通道状态无关),并把它交给派发器,接进变更通道的
# 恢复钩子。

# 变更看门狗 — 当执行中的变更协程中止或永不完成时恢复锁
# (否则所有变更会被永久卡死)。它持有执行中身份 + 自适应期限 + 代计数器。
# 在 start() 中构造并交给派发器,由派发器把它的 force_clear 钩子接进变更
# 通道;由该通道在执行开始时武装,并在"这里"每个 _process 帧 tick
# (无论通道状态如何它都必须运行)。参见 mutation_watchdog.gd。
var _mutation_watchdog: MutationWatchdog = null

# -- 场景租约 -------------------------------------------------------------------
# 多个对端瞄准不同场景时,限时租约防止跨场景污染。依赖标签页的命令会排队,
# 直到该对端的亲和场景与活动标签页一致。
#
# 租约机制(状态 + acquire/renew/release/steal/drain + 场景亲和队列 +
# scene.open 争用)位于 scene_lease.gd;进入它的派发"路由"位于场景租约通道
# (dispatch_lane.gd),经由派发器触达。在 start() 中构造,注入编辑器的
# 根解析器与各通道接缝;场景租约通道经 _scene_lease.try_queue_for_lease /
# handle_scene_open 路由,_process tick _scene_lease.check_expiry。
# 参见 scene_lease.gd。
var _scene_lease: SceneLease = null


func set_registry(registry: MCPToolkitCommandRegistry) -> void:
	_registry = registry
	# 若子模块已存在则推送给它们(set_registry 通常在 start() 构建它们之前
	# 运行,那种情况下由 _init_scene_lease / _init_router 播种)。
	if _scene_lease != null:
		_scene_lease.set_registry(registry)
	if _router != null:
		_router.set_registry(registry)


## 释放命令注册表及其全部 Callable 引用。
## 在插件拆卸期间调用,在节点删除之前断开引用链。
func clear_registry() -> void:
	if _registry != null:
		_registry.clear()
		_registry = null
	# 同时断开子模块的注册表与接缝 Callable 链(它们持有注册表,以及绑回
	# 本服务器的接缝)。
	if _scene_lease != null:
		_scene_lease.clear_registry()
	if _router != null:
		_router.clear()


func get_plugin_boot_time() -> int:
	return _plugin_boot_time


func is_listening() -> bool:
	return _transport != null and _transport.is_listening()


func get_authed_peer_count() -> int:
	return _transport.get_authed_count() if _transport != null else 0


func get_bound_port() -> int:
	return _transport.get_bound_port() if _transport != null else -1


## 本编辑器设置所指向的 GDScript LSP 端点(默认 127.0.0.1:6005)。指向
## lsp_publisher.gd 的薄静态委托(C9)— 保留在这里是因为 plugin.gd 与注册表
## 调用方静态调用 MCPServer.resolve_lsp_endpoint();解析出的主机/端口被传入
## register()/ensure_registered(),让 registry_client.gd 对模式 B 运行时
## 自动加载保持编辑器纯净。
static func resolve_lsp_endpoint() -> Dictionary:
	return LspPublisher.resolve_lsp_endpoint()


## 上下文协议服务器在这里报告权威的 LSP 判定(editor.set_lsp_status);委托给 LSP
## 发布器,后者存储判定并经注入的处理器再发射 lsp_status_changed,让停靠面板
## 刷新。信号保留在本对象上(停靠面板在这里绑定它)。参见 lsp_publisher.gd。
func set_reported_lsp_status(status: Dictionary) -> void:
	if _lsp != null:
		_lsp.set_reported_lsp_status(status)


func get_reported_lsp_status() -> Dictionary:
	return _lsp.get_reported_lsp_status() if _lsp != null else {}


func get_command_methods() -> Array:
	if _registry == null:
		return []
	return _registry.get_all_methods()


## 向所有已鉴权的 WebSocket 对端发送通知。
## 无需重启即可触发服务器侧的工具列表重载
## (例如工具面发生变化之后,如组激活或新增扩展)。
func broadcast_notification(notification_type: String, params: Dictionary = {}) -> void:
	var authed: Array = _transport.get_authed_peers() if _transport != null else []
	var count := Notifier.broadcast(authed, notification_type, params, "[MCPServer]")
	print("[MCPServer] broadcasting %s to %d authed peer%s" % [
		notification_type, count, "" if count == 1 else "s"])


func bind_user_path_monitor(monitor: RefCounted) -> void:
	monitor.user_path_changed.connect(_on_user_path_changed)


func _on_user_path_changed() -> void:
	_rewrite_token_after_rename()


## 在配置/名称变更之后,把当前内存中的令牌重写到新的 user:// 路径。
## 不会生成新令牌 — 现有连接保持已鉴权状态。广播新令牌路径,让注册表
## 所有者(plugin.gd)以保留运行时信息的方式重新发布条目。
func _rewrite_token_after_rename() -> void:
	if _transport == null:
		return  # 没有运行中的传输层 → 没有令牌可重写,也没有绑定。
	var write_err := MCPAuth.write_token(_transport.get_token())
	if write_err != OK:
		push_warning("[MCPServer] failed to re-write token after rename (err %d)" % write_err)
	else:
		print("[MCPServer] token re-written to %s" % MCPAuth.get_token_path())
	# 广播新令牌路径;插件经 ensure_registered 重新发布,让活跃游戏的
	# runtime_port/runtime_pid 在重命名中幸存(register 会把它们置空)。
	# 服务器不从重命名路径伸入注册表。
	if _transport.get_bound_port() > 0:
		token_rewritten.emit(MCPAuth.get_token_path())


func regenerate_token() -> void:
	var token := MCPAuth.generate_token()
	if _transport != null:
		_transport.set_token(token)
	var write_err := MCPAuth.write_token(token)
	if write_err != OK:
		push_warning("[MCPServer] failed to write rotated token (err %d)" % write_err)
	else:
		print("[MCPServer] token rotated, written to %s" % MCPAuth.get_token_path())
	# 关闭所有现有对端 — 它们必须用新令牌重新鉴权。
	if _transport != null:
		_transport.close_all(1008, "token rotated")


func start() -> void:
	# 在监听之前自愈一次残留的提速(来自崩溃或并发实例),让全局键绝不会
	# 在没有存活连接的情况下持续存在。
	# 先构建子模块(self_heal 是它的方法);此时不可能有已鉴权的对端。
	_init_unfocused_sleep()
	_unfocused.self_heal(get_authed_peer_count())
	if undo_helpers == null:
		undo_helpers = UndoRedoHelpers.new()
		undo_helpers.name = "UndoRedoHelpers"
		add_child(undo_helpers)
	_plugin_boot_time = int(Time.get_unix_time_from_system())
	_init_transport()
	_init_mutation_watchdog()
	# 构建 scene_lease 与派发器,然后交叉接线。二者存在相互的接缝依赖
	# (场景租约通道路由进租约子模块;租约子模块的排队路径会重进入读取/
	# 变更通道),因此两者都"先"构造,"后"接线:_init_router 把各通道绑定到
	# (此刻已存在的)租约方法,_init_scene_lease 把租约接缝绑定到
	# (此刻已存在的)通道方法。
	_init_scene_lease()
	_init_router()
	_wire_scene_lease_to_lanes()
	_init_lsp_publisher()
	var token := MCPAuth.generate_token()
	# 端口配置错误会让 _transport 保持 null(start() 仍会接线其余部分,让
	# 停靠面板 + LSP 监视启动并能暴露错误);跳过监听路径。
	if _transport != null:
		_transport.set_token(token)
	var write_err := MCPAuth.write_token(token)
	if write_err != OK:
		push_warning("[MCPServer] failed to write token (err %d); auth will still be enforced but bridge may not find the file" % write_err)
	else:
		var token_path := MCPAuth.get_token_path()
		print("[MCPServer] session token written to %s" % token_path)
	if _transport != null:
		_transport.ensure_listening()
	else:
		port_status_changed.emit()
	_connect_lsp_settings_watch()


# 构建 WS 传输层并注入编辑器侧接缝:鉴权 vs 派发的路由器、鉴权确认载荷
# (模式 A 附加 godot_version + version)、首个已鉴权对端的提速回调、关闭时的
# 租约/失焦/信号清理、全新绑定通告(注册表重新发布 + 停靠面板重绘),以及
# 监听冲突 → 停靠面板警告。先解析监听端口配置;配置错误会让 _transport 保持
# null,start() 跳过监听(停靠面板显示原因)。
func _init_transport() -> void:
	_port_config = PortConfig.resolve(_ENV_PIN, _ENV_PORT_MIN, _ENV_PORT_MAX, PORT_MIN, PORT_MAX)
	_log_port_config()
	if not str(_port_config.get("error", "")).is_empty():
		return
	_transport = WsTransport.new()
	var pinned: bool = _port_config["mode"] == PortConfig.MODE_PINNED
	var base: int = int(_port_config["port"]) if pinned else int(_port_config["port_min"])
	var count: int = 1 if pinned else int(_port_config["port_max"]) - int(_port_config["port_min"]) + 1
	# await_messages = true:编辑器在单个轮询 tick 内顺序派发
	# (其变更串行化依赖于此)。
	_transport.configure("[MCPServer]", base, count, BIND,
		_RELISTEN_FRAME_INTERVAL, _AUTH_TIMEOUT_MS, true,
		"no free port in %d-%d; will retry every ~1s", pinned,
		"Free the port, or change %s (or unset it to use the scanned band)." % _ENV_PIN)
	_transport.set_handlers(_handle_message, _build_auth_ack, _on_peer_authed,
		_on_peer_closed, _on_transport_bound, _on_listen_conflict_changed)


# 在启动时记录解析出的监听端口配置:端口 + 来源(让编辑器控制台与停靠面板
# 都能说明"为什么"选择这个端口 — 钉住、区间还是默认),钉住使区间失去意义时
# 给出单行说明,配置错误(坏钉住 / MIN > MAX)时发出响亮的 push_error —
# 绝不静默回退到默认值。
func _log_port_config() -> void:
	var config_error := str(_port_config.get("error", ""))
	if not config_error.is_empty():
		push_error("[MCPServer] Invalid port config: %s - the editor MCP server did not start. Fix the environment variable and restart the editor (env is read at editor launch; re-enabling the plugin keeps the old value)." % config_error)
		return
	if bool(_port_config.get("band_ignored", false)):
		print("[MCPServer] note: %s pins an exact port, so the %s/%s band is ignored." % [
			_ENV_PIN, _ENV_PORT_MIN, _ENV_PORT_MAX])
	match str(_port_config.get("source", "")):
		"env-pin":
			print("[MCPServer] port %d (pinned via %s)" % [int(_port_config["port"]), _ENV_PIN])
		"env-band":
			print("[MCPServer] port: scanning %d-%d (band via %s/%s)" % [
				int(_port_config["port_min"]), int(_port_config["port_max"]),
				_ENV_PORT_MIN, _ENV_PORT_MAX])
		_:
			print("[MCPServer] port: scanning %d-%d (default)" % [
				int(_port_config["port_min"]), int(_port_config["port_max"])])


# 全新的监听器绑定(初次或迟到,两种模式皆然)。通告端口,让组合根(重新)
# 发布注册表条目 — 正是这一点让迟到的钉住绑定仍可被发现 — 并重绘停靠面板的
# 监听状态。
func _on_transport_bound(port: int) -> void:
	port_bound.emit(port)
	port_status_changed.emit()


# 传输层的监听冲突状态发生变化(不可绑定 ⇄ 已绑定)。再发射,让停靠面板
# 重绘;停靠面板在自己的刷新时也会拉取该状态,因此一次漏发的信号
# (例如在 start() 期间、停靠面板绑定之前)也仍会被捕捉到。
func _on_listen_conflict_changed(_port: int, _active: bool) -> void:
	port_status_changed.emit()


## 供停靠面板状态标签使用的解析后的监听端口来源:"pinned" /
## "band" / "default",配置解析失败时为 ""。
func get_port_source() -> String:
	match str(_port_config.get("source", "")):
		"env-pin":
			return "pinned"
		"env-band":
			return "band"
		"default":
			return "default"
		_:
			return ""


## 停靠面板当前的"未监听"警告:致命的端口配置错误、钉住端口冲突,或扫描
## 区间耗尽冲突。返回 { "active": bool, "message": String, "label": String } —
## message 是完整的警告面板文本,label 是状态行的简明原因;服务器正常绑定时
## active 为 false。
func get_port_warning() -> Dictionary:
	var config_error := str(_port_config.get("error", ""))
	if not config_error.is_empty():
		return {
			"active": true,
			"message": "Invalid port config: %s — the MCP editor server did not start. Fix the environment variable and restart the editor (env is read at editor launch; re-enabling the plugin keeps the old value)." % config_error,
			"label": "invalid port config",
		}
	if _transport != null and _transport.is_listen_conflict():
		if _port_config.get("mode", "") == PortConfig.MODE_PINNED:
			var pinned_port: int = int(_port_config["port"])
			return {
				"active": true,
				"message": "Pinned port: %d not available — the MCP editor server did not bind. Free the port, or change %s (or unset it to use the scanned band)." % [pinned_port, _ENV_PIN],
				"label": "pinned port %d not available" % pinned_port,
			}
		# 丢失的监听套接字只在"同一"端口上重试(get_bound_port() 保持已设置),
		# 因此给出全范围建议是无效的 — 指名那个端口。
		var lost_port: int = _transport.get_bound_port()
		if lost_port > 0:
			return {
				"active": true,
				"message": "Port: %d not available — the MCP editor server lost its listen socket and is retrying that port. Free port %d to recover." % [lost_port, lost_port],
				"label": "port %d not available" % lost_port,
			}
		var low: int = int(_port_config.get("port_min", PORT_MIN))
		var high: int = int(_port_config.get("port_max", PORT_MAX))
		return {
			"active": true,
			"message": "Range of ports: %d-%d not available — the MCP editor server did not bind. Free a port in the range, or move it with %s/%s." % [low, high, _ENV_PORT_MIN, _ENV_PORT_MAX],
			"label": "ports %d-%d not available" % [low, high],
		}
	return {"active": false, "message": "", "label": ""}


# 构建变更看门狗。它的 force_clear 恢复钩子由变更通道(在派发器中)接线 —
# 通道持有该钩子要清除的单飞标志,所以钩子应属于通道。本文件只构造看门狗,
# 并在每个 _process 帧 tick() 它(无论通道状态如何它都必须运行)。
func _init_mutation_watchdog() -> void:
	_mutation_watchdog = MutationWatchdog.new()


# 构建失焦睡眠控制器,注入 EditorSettings 访问器(子模块唯一的编辑器触点 —
# 藏在 Callable 之后,使其保持为可测试性接缝,并让 EditorInterface 名称留在
# 这个编辑器文件里,与 scene_lease 的根解析器呼应)。编排者保留触发点
# (首个已鉴权时提速、最后一次断开时恢复、启动时自愈);子模块拥有机制。
func _init_unfocused_sleep() -> void:
	_unfocused = UnfocusedSleepController.new()
	_unfocused.set_settings_accessor(func() -> EditorSettings:
		return EditorInterface.get_editor_settings())


# 构造场景租约协调器并播种注册表。它的接缝在 _wire_scene_lease_to_lanes 中
# 接线(在派发器的各通道存在之后 — 它们相互依赖)。set_registry 通常在
# start() 构建它之前运行,因此在这里播种注册表。
func _init_scene_lease() -> void:
	_scene_lease = SceneLease.new()
	_scene_lease.set_registry(_registry)


# 构建派发器及其三条通道,注入各通道需要的跨子系统接缝(每个都藏在 Callable
# 之后,让派发器与通道保持编辑器纯净):command_received 的再发射、变更
# 看门狗,以及绑定到(已构造的)租约子模块的场景租约通道接缝 — 用于路由的
# handle_scene_open / try_queue_for_lease / cancel_queued,以及变更通道完成
# 路径所需的 inject_concurrency_metadata / post_mutation_cleanup / drain。
# set_registry 通常在 start() 之前运行,因此也在这里播种派发器的注册表。
func _init_router() -> void:
	_router = ServerRequestRouter.new()
	_router.set_registry(_registry)
	_router.build_lanes(
		func(method: String) -> void: command_received.emit(method),
		_mutation_watchdog,
		_scene_lease.inject_concurrency_metadata,
		_scene_lease.post_mutation_cleanup,
		_scene_lease.drain,
		_scene_lease.handle_scene_open,
		_scene_lease.try_queue_for_lease,
		_scene_lease.cancel_queued,
	)


# 把通道方法注入场景租约子模块(相互接线的后半段):编辑器的根解析器
# (唯一的 EditorInterface 触点 — 藏在 Callable 之后,使其成为可测试性接缝)、
# command_received 的再发射、读取通道返回结果的执行核心(排队读取路径在
# 发送之前要注入并发元数据,因此它需要拿到结果而不是直接发送),以及变更
# 通道的忙时入队 + 执行入口。在 _init_router 构建通道之后运行。
func _wire_scene_lease_to_lanes() -> void:
	var mutation_lane = _router.mutation_lane()
	_scene_lease.set_handlers(
		func() -> Node: return EditorInterface.get_edited_scene_root(),
		func(method: String) -> void: command_received.emit(method),
		_router.read_lane().run_returning,
		mutation_lane.enqueue_if_busy,
		mutation_lane.execute,
	)


# 构建 LSP 发布器,并注入它从本编排者需要的两个接缝:绑定的 WS 端口来源
# (端口归传输层所有;监视读取它,以便在服务器尚未监听时跳过重新发布)与
# lsp_status_changed 的再发射(信号保留在本对象上,因为停靠面板在这里绑定
# 它)。resolve_lsp_endpoint 是子模块上的静态方法 — 它直接引用 EditorInterface,
# 对这个仅编辑器文件来说没有问题(绝不会被运行时 preload)。在 start() 中、
# 连接监视之前构造。
func _init_lsp_publisher() -> void:
	_lsp = LspPublisher.new()
	_lsp.set_bound_port_provider(func() -> int:
		return _transport.get_bound_port() if _transport != null else -1)
	_lsp.set_status_changed_handler(func() -> void: lsp_status_changed.emit())


func stop() -> void:
	set_process(false)
	_disconnect_lsp_settings_watch()
	if _transport != null:
		_transport.close_all(1000, "")
	if _unfocused != null:
		_unfocused.restore()
	if _transport != null:
		_transport.shutdown_listener()
	print("[MCPServer] stopped")


# LSP 设置监视的触发点(机制位于 lsp_publisher.gd,C9)。
# start() 连接监视(解析基线,并在 EditorSettings.settings_changed 上监听
# 会话中途的 GDScript LSP 端口/主机变更 → 防抖的注册表重新发布);
# stop() 断开它(I12 对称性)。薄的空值防护委托,让生命周期排序留在这里,
# 而子模块拥有监视 + 防抖 + 重新发布。
func _connect_lsp_settings_watch() -> void:
	if _lsp != null:
		_lsp.connect_settings_watch()


func _disconnect_lsp_settings_watch() -> void:
	if _lsp != null:
		_lsp.disconnect_settings_watch()


# -- 帧循环 ---------------------------------------------------------------------


func _process(_delta: float) -> void:
	# 变更看门狗必须始终运行,独立于轮询节奏与租约状态 — 它是卡死的变更锁的
	# 唯一恢复手段。(空值守卫与 _poll_connections 一致:start() 会在任何
	# _process 之前于 _enter_tree 中同步构建它,但一次漏网的启动前 tick
	# 仍应保持为空操作。)
	if _mutation_watchdog != null:
		_mutation_watchdog.tick()
	Modules.LogBuffer.poll()
	_poll_frame_counter += 1
	if _poll_frame_counter < _POLL_FRAME_INTERVAL:
		return
	_poll_frame_counter = 0
	if _scene_lease != null:
		_scene_lease.check_expiry()
	# 经由 call_deferred 派发,把网络 I/O 移出 _process 调用栈,缩小与 Godot
	# 的 EditorFileSystem 扫描/导入工作之间的重入碰撞面
	# (见 _POLL_FRAME_INTERVAL 的注释)。
	call_deferred("_poll_connections")


func _poll_connections() -> void:
	# 防御:start() 只会在构建传输层之后才可能让 _process 触发(两者都在插件
	# _enter_tree 中同步运行),但仍然加守卫,让漏网的启动前 tick 保持为空操作
	# 而不是空指针解引用(抽取前的循环以同样的方式容忍空监听器)。
	if _transport == null:
		return
	# 在一次保存的 Main::iteration() 重入进行期间跳过这个重入 tick —
	# 保存中途不得派发任何命令。
	if MCPToolkitSafeSceneOps.is_dispatching():
		return
	# pump() 返回本轮是否有已鉴权对端关闭。每个 tick 用"最终的"已鉴权数聚合
	# 一次断开(抽取前的形态),使用局部变量,让变更 await 期间重入的延迟
	# 轮询无法互相践踏。
	var had_authed_disconnect: bool = await _transport.pump()
	if had_authed_disconnect:
		var authed_now := _transport.get_authed_count()
		if authed_now == 0:
			_unfocused.restore()
		client_disconnected.emit(authed_now)


# -- 消息处理 -------------------------------------------------------------------


# 传输层把每个原始帧投递到这里。我们先解析,再路由:未鉴权的对端走传输层的
# 鉴权握手(我们额外运行仅面向人类的版本不匹配检查);已鉴权的对端进入派发。
# 传输层会 await 本方法,因此派发在单个轮询 tick 内保持顺序。
func _handle_message(peer: WebSocketPeer, text: String) -> void:
	var parser := JSON.new()
	var parse_error := parser.parse(text)
	if parse_error != OK:
		_send_error(peer, null, -32700, "Parse error: %s" % parser.get_error_message())
		return

	var message = parser.data
	if typeof(message) != TYPE_DICTIONARY:
		_send_error(peer, null, -32600, "Invalid Request: top-level must be an object")
		return

	if not _transport.is_authed(peer):
		if _transport.validate_auth(peer, message):
			# 版本不匹配检查 — 仅面向人类(编辑器控制台),上下文协议线路上没有输出。
			# 只在鉴权成功之后运行,与抽取前的握手一致。预握手的服务器不发送
			# 版本 → 跳过。
			var server_ver: String = str(message.get("version", ""))
			if not server_ver.is_empty():
				_check_version_mismatch(Modules.VersionUtils.read_plugin_version(), server_ver)
		return

	await _router.route_request(peer, message)


# 向传输层提供模式 A 的鉴权确认载荷:裸的 {authed:true} 加上本编辑器的
# Godot 与插件版本及其显示模式(运行时只发送 {authed:true})。`headless` 是
# 服务器读取的线路信号,用于分支其无头降级的工具断言。纯函数 — 无副作用;
# 提速/信号发生在 _on_peer_authed 中。
func _build_auth_ack(_message: Dictionary) -> Dictionary:
	var vi := Engine.get_version_info()
	return {
		"authed": true,
		"godot_version": "%d.%d.%d" % [vi["major"], vi["minor"], vi["patch"]],
		"version": Modules.VersionUtils.read_plugin_version(),
		"headless": Modules.VersionUtils.is_headless(),
	}


# 由传输层在一个对端完成鉴权后触发。在第一个已鉴权对端时,提升失焦响应性;
# 始终为停靠面板再发射 client_connected。
func _on_peer_authed(count: int) -> void:
	if count == 1:
		_unfocused.lower()
	client_connected.emit(count)


# 由传输层对每个关闭的对端触发一次(传输层已清除自己的对端映射)。做仅
# 编辑器的"按对端"清理:丢弃场景亲和,若该对端持有租约则释放,移除该对端
# 排队的场景命令。聚合的 client_disconnected 发射 + 失焦恢复在每个 tick 于
# _poll_connections 中发生"一次",依据是 pump() 的返回 — 不在这里。
# was_authed 在这里未使用(聚合使用传输层的批次结果)。
func _on_peer_closed(peer: WebSocketPeer, _was_authed: bool) -> void:
	_scene_lease.on_peer_closed(peer)


func _check_version_mismatch(local: String, remote: String) -> void:
	var local_parts := local.split(".")
	var remote_parts := remote.split(".")
	if local_parts.size() != 3 or remote_parts.size() != 3:
		return  # 非 semver — 跳过比较。
	if not local_parts[0].is_valid_int() or not remote_parts[0].is_valid_int():
		return
	if int(local_parts[0]) != int(remote_parts[0]):
		push_error("[MCPServer] Major version mismatch - plugin %s, server %s. Update both to the same major version." % [local, remote])
	elif local != remote:
		push_warning("[MCPServer] Version mismatch - plugin %s, server %s. Consider updating." % [local, remote])


# 派发路由、变更通道以及读取/场景租约路由位于 server_request_router.gd +
# dispatch_lane.gd;_handle_message 把每个已鉴权帧交给 _router.route_request。
# 下面的分帧辅助方法保留 — 编排者自己在 _handle_message 中发送派发前的
# 解析错误(-32700 / -32600)。


func _send_error(peer: WebSocketPeer, id, code: int, error_message: String) -> void:
	Notifier.send_error(peer, id, code, error_message)


# -- 失焦睡眠管理 ----------------------------------------------------------------
# 调低/恢复/自愈机制 + 先写者胜备份位于 unfocused_sleep_controller.gd。
# 本文件保留停靠面板读取的公共获取器并逐个委托给 _unfocused;跨子系统的
# 触发点(首个已鉴权连接时提速、最后一次断开时恢复、启动时自愈)位于
# _on_peer_authed / _poll_connections / start()。空值守卫让停靠面板启动前的
# 指示器刷新保持诚实(子模块在 start() 中构建),保留抽取前的默认值。


## 用户已选择加入时为 true(默认 true)。设置缺失/不可用时回退到默认值,
## 让选择加入绝不因设置不可用而被阻塞。
func is_unfocused_responsive_enabled() -> bool:
	return _unfocused.is_unfocused_responsive_enabled() if _unfocused != null else true


## 由配置的提升值隐含的 fps,供停靠面板指示器与日志使用。
func get_unfocused_responsive_fps() -> int:
	return _unfocused.get_unfocused_responsive_fps() if _unfocused != null else 60


## 本实例是否持有激活的提速 — 委托;语义见 unfocused_sleep_controller.gd 的
## is_unfocused_boost_active。
func is_unfocused_boost_active() -> bool:
	return _unfocused.is_unfocused_boost_active() if _unfocused != null else false


## 选择加入设置变化时,立即应用或恢复失焦响应提速,
## 而不是等待下一次连接/断开。
func notify_unfocused_responsive_setting_changed() -> void:
	if _unfocused != null:
		_unfocused.notify_unfocused_responsive_setting_changed(get_authed_peer_count())
