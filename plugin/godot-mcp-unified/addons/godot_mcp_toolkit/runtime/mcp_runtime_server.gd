@tool
extends Node
## 模式 B — 仅运行时的 WebSocket 服务器,让上下文协议(MCP)桥接(bridge)能够深入
## 正在运行的游戏(LIVE game,而非正在编辑的场景)。由 plugin.gd 注册为
## `MCPRuntimeServer` 自动加载(autoload)。
##
## 同一个脚本文件会在编辑期加载(因为它位于启用 @tool 的插件中),
## 也会在运行期加载(因为它是自动加载)。我们在两种情况下自我销毁:
##   1. Engine.is_editor_hint() — 加载我们的是编辑器进程,而不是游戏。
##      编辑时在 6570 端口上再挂一个 WS 监听是个 bug。
##   2. not OS.has_feature("editor") — 任何导出模板(调试与发布均是)。
##      "editor" 仅在编辑器启动(F5/F6)时为 true,在任何导出中均为 false,
##      因此无论 export_strip 是否把自动加载清空,模式 B 都会在所有导出中
##      自我保护。这是安全攸关的;比 is_debug_build()(在调试导出中为
##      true)更强。

# 运行时依赖闭包 — 仅直接 preload 对导出友好的脚本。
# 刻意不使用 core/modules.gd:它静态引用了 EditorInterface/EditorPlugin,
# 会让本自动加载在导出模板中解析失败(godot#91713)— GDScript 在解析期
# 解析标识符,任何运行时守卫都来不及介入。
const Coerce := preload("res://addons/godot_mcp_toolkit/contract/coerce.gd")
const ExecuteHints := preload("res://addons/godot_mcp_toolkit/contract/execute_hints.gd")
const Pagination := preload("res://addons/godot_mcp_toolkit/contract/pagination.gd")
const PropertySetCheck := preload("res://addons/godot_mcp_toolkit/contract/property_set_check.gd")
const ScreenshotResponse := preload("res://addons/godot_mcp_toolkit/contract/screenshot_response.gd")
const Untrusted := preload("res://addons/godot_mcp_toolkit/security/untrusted.gd")
const MCPAuth := preload("res://addons/godot_mcp_toolkit/security/auth.gd")
const Scrubber := preload("res://addons/godot_mcp_toolkit/security/scrubber.gd")
const LogHelpers := preload("res://addons/godot_mcp_toolkit/logging/log_helpers.gd")
const RegistryClient := preload("res://addons/godot_mcp_toolkit/registry/registry_client.gd")
const LogBuffer := preload("res://addons/godot_mcp_toolkit/logging/log_buffer.gd")
const Notifier := preload("res://addons/godot_mcp_toolkit/transport/notifier.gd")
const WsTransport := preload("res://addons/godot_mcp_toolkit/transport/ws_transport.gd")
const SignalPairResolver := preload("res://addons/godot_mcp_toolkit/scene/signal_pair_resolver.gd")
const TextInputSynth := preload("res://addons/godot_mcp_toolkit/runtime/text_input_synth.gd")
# 对导出友好(仅核心 API)— 在这个运行时自动加载的 preload 闭包内是安全的。
const PortConfig := preload("res://addons/godot_mcp_toolkit/transport/port_config.gd")
const PlaytestTimeController := preload("res://addons/godot_mcp_toolkit/runtime/playtest_time_controller.gd")

# 默认的运行时(模式 B)扫描区间(含端点)。GODOT_MCP_RUNTIME_PORT 钉住
# 一个精确端口(必须精确绑定,否则失败);GODOT_MCP_RUNTIME_PORT_MIN/_MAX
# 重新安置该区间。由 PortConfig 解析;两种方式互斥。
const PORT_MIN := 6570
const PORT_MAX := 6585  # 6570..6585,含端点
const _ENV_PIN := "GODOT_MCP_RUNTIME_PORT"
const _ENV_PORT_MIN := "GODOT_MCP_RUNTIME_PORT_MIN"
const _ENV_PORT_MAX := "GODOT_MCP_RUNTIME_PORT_MAX"
const BIND := "127.0.0.1"
const JSONRPC_VERSION := "2.0"
# 对重监听重试做节流。与 mcp_server.gd 的编辑器侧循环保持一致。
# 每次游戏会话按 F5 都会重启运行时,因此典型的"绑定未成功"可恢复场景是
# 一个尚未释放 6570 的过期调试会话 — 通常 1-2 帧内就会清除。
const _RELISTEN_FRAME_INTERVAL := 60
const _AUTH_TIMEOUT_MS := 2000
# 调用方未传 limit 时 debugger.get_log 的默认条目数。
const _DEFAULT_LOG_LIMIT := 200
# execute.code 的控制台回显上限 — 过长的代码片段会在游戏日志中被截断。
const _EXECUTE_CODE_LOG_CAP := 256
# 在回退到最后一帧之前,等待新帧的时间上限,单位为秒。非关键路径,且远小于
# 服务器的 30 秒调用超时 — 一个按需重绘(redraw-on-demand)且空闲(未被
# 最小化)的游戏根本不会触发 frame_post_draw,因此等待必须能到期结束,而不是
# 永远挂起。
const _RUNTIME_FRAME_WAIT_SECONDS := 1.0

# 持有 TCP 监听器 + WS 对端 + 鉴权握手分帧 + 绑定的端口 + 会话令牌。
# 运行时只注入消息路由器(其命令匹配)和绑定发布回调;鉴权确认默认为
# {authed:true},且没有针对每次鉴权/关闭的副作用(运行中的游戏没有这些)。
# 在 _process 中内联驱动它(不用 call_deferred — 运行中的游戏没有
# EditorFileSystem 重入问题需要规避)。
var _transport: WsTransport = null
var _playtest_time_controller: RefCounted = PlaytestTimeController.new()


func _init() -> void:
	# 在游戏场景树暂停时(暂停菜单,或会暂停游戏玩法的退出确认对话框),
	# 让模式 B 的轮询循环保持存活。默认的 PROCESS_MODE_PAUSABLE 会在游戏
	# 一设置 get_tree().paused = true 的瞬间停掉 _process -> pump(),冻结所有
	# 运行时工具,直到游戏取消暂停。纯 Node API -- 无编辑器依赖。
	process_mode = Node.PROCESS_MODE_ALWAYS


func _ready() -> void:
	# 不要在编辑器进程中运行。
	#
	# 以惰性占位节点的身份留在 SceneTree 中,而不是 free 掉自己:
	# 自动加载跟踪器持有指向这个 Node 的指针,直到 `remove_autoload_singleton()`
	# 被触发(禁用插件时)。如果我们在这里 queue_free 自己,该指针就会悬空,
	# 禁用插件会在 Godot 内部触发 `root.remove_child(null)`。
	# `set_process(false)` + 没有 TCPServer 意味着每帧零开销、不绑定端口 —
	# 这个 Node 的存在纯粹是簿记用途。
	if Engine.is_editor_hint():
		set_process(false)
		return
	# --check-only 是一次仅解析的检查 — 不需要运行时服务器。
	if "--check-only" in OS.get_cmdline_args():
		set_process(false)
		return
	# 导出闸门:发行的游戏(调试或发布导出模板)不得监听 6570。
	# has_feature("editor") 仅在编辑器启动(F5/F6)时为 true,在任何导出模板中
	# 均为 false — 比 is_debug_build()(调试导出中为 true)更强,因此运行时
	# 服务器会在所有导出中自我保护,不依赖 export_strip 是否清空自动加载。
	# 同样采用静默方式 — 在场景树根保留一个空 Node,比在 SceneTree 搭建期间
	# 自动释放自身的各种微妙边界情况更省事。
	if not OS.has_feature("editor"):
		set_process(false)
		return
	_start_server()


func _exit_tree() -> void:
	_stop_server()


func _start_server() -> void:
	LogBuffer.setup()
	var config := PortConfig.resolve(_ENV_PIN, _ENV_PORT_MIN, _ENV_PORT_MAX, PORT_MIN, PORT_MAX)
	_log_port_config(config)
	if not str(config.get("error", "")).is_empty():
		# 致命的配置错误 — 本次会话禁用模式 B 工具(已在上面记录日志)。
		# 不会构建传输层,因此也要停掉每帧泵:否则 _process 每帧都会解引用
		# 空传输对象。
		set_process(false)
		return
	_transport = WsTransport.new()
	var pinned: bool = config["mode"] == PortConfig.MODE_PINNED
	var base: int = int(config["port"]) if pinned else int(config["port_min"])
	var count: int = 1 if pinned else int(config["port_max"]) - int(config["port_min"]) + 1
	# await_messages = false:运行时每帧"发出即忘"(fire-and-forget),让协程
	# 处理器(如 runtime.screenshot)独立于轮询循环运行,与之前一致。
	# 钉住的端口被占用时,会在游戏控制台发出响亮的 push_error(由控制台工具
	# 捕获)— 子游戏进程无法触达编辑器停靠面板,因此传输层的
	# push_warning/push_error 就是运行时侧的暴露面。
	_transport.configure("[MCPRuntimeServer]", base, count, BIND,
		_RELISTEN_FRAME_INTERVAL, _AUTH_TIMEOUT_MS, false,
		"no free port in %d-%d; Mode B tools disabled this session", pinned,
		"Free the port, or change %s (or unset it to use the scanned band). Mode B tools are disabled this session." % _ENV_PIN)
	# 运行时接缝:只有消息路由器(其命令匹配)和绑定发布回调(set_runtime)。
	# 无鉴权确认覆盖({authed:true} 为默认),无针对每次鉴权/关闭的副作用,
	# 也没有端口冲突处理器(传输层自己的 push_error 就是响亮的运行时暴露面 —
	# 没有停靠面板可以触达)。
	_transport.set_handlers(_handle_message, Callable(), Callable(), Callable(),
		_on_bound)
	# 运行时与编辑器服务器使用同一个令牌文件,这样桥接读一次就能对两者鉴权。
	# 编辑器服务器先写入(插件启用先于游戏启动)。如果文件尚不存在
	# (边界情况:插件未启用时独立启动游戏),则生成我们自己的令牌。
	var token := ""
	var token_path := MCPAuth.get_token_path()
	var file := FileAccess.open(token_path, FileAccess.READ)
	if file != null:
		token = file.get_as_text().strip_edges()
		file.close()
	if token.is_empty():
		token = MCPAuth.generate_token()
		MCPAuth.write_token(token)
	_transport.set_token(token)
	_transport.ensure_listening()


# 在启动时记录解析出的模式 B 监听端口配置(让游戏控制台说明"为什么"
# 选择这个端口):端口 + 来源;当钉住端口使区间失去意义时给出单行说明;
# 配置错误时发出响亮的 push_error — 绝不静默回退到默认值。
func _log_port_config(config: Dictionary) -> void:
	var config_error := str(config.get("error", ""))
	if not config_error.is_empty():
		push_error("[MCPRuntimeServer] Invalid port config: %s - Mode B tools disabled this session." % config_error)
		return
	if bool(config.get("band_ignored", false)):
		print("[MCPRuntimeServer] note: %s pins an exact port, so the %s/%s band is ignored." % [
			_ENV_PIN, _ENV_PORT_MIN, _ENV_PORT_MAX])
	match str(config.get("source", "")):
		"env-pin":
			print("[MCPRuntimeServer] port %d (pinned via %s)" % [int(config["port"]), _ENV_PIN])
		"env-band":
			print("[MCPRuntimeServer] port: scanning %d-%d (band via %s/%s)" % [
				int(config["port_min"]), int(config["port_max"]), _ENV_PORT_MIN, _ENV_PORT_MAX])
		_:
			print("[MCPRuntimeServer] port: scanning %d-%d (default)" % [
				int(config["port_min"]), int(config["port_max"])])


func _stop_server() -> void:
	set_process(false)
	if _transport != null:
		_transport.close_all(1000, "")
		_transport.shutdown_listener()
	# 尽力而为的注册表清理(游戏可能被强制杀死)。
	RegistryClient.clear_runtime()


# 由传输层在全新绑定时触发(初次或迟到,两种模式皆然):发布端口,让桥接
# 能够发现模式 B。编辑器侧会镜像这一行为 — 其全新绑定接缝会经由组合根
# 重新发布注册表条目。
func _on_bound(port: int) -> void:
	RegistryClient.set_runtime(port)


func _process(_delta: float) -> void:
	# 配置错误启动不会留下传输对象(set_process(false) 已停止本循环);
	# 这个守卫让漏网的一次 tick 成为无害空操作,而不是空指针解引用 —
	# 与编辑器侧 _poll_connections 中的空值守卫互为镜像。
	if _transport == null:
		return
	LogBuffer.poll()
	# 让监听器在短暂的套接字丢失后保持存活。_ready 中的编辑器/发布闸门
	# 阻止了 _process 在不该运行的地方运行(set_process(false)),因此能走到
	# 这里就等于调试构建的运行时。内联泵(不用 call_deferred — 运行中的游戏
	# 没有 EditorFileSystem 重入问题)。
	_transport.pump()


func _handle_message(peer: WebSocketPeer, text: String) -> void:
	var parser := JSON.new()
	var parse_err := parser.parse(text)
	if parse_err != OK:
		_send_error(peer, null, -32700, "Parse error: %s" % parser.get_error_message())
		return

	var msg = parser.data
	if typeof(msg) != TYPE_DICTIONARY:
		_send_error(peer, null, -32600, "Invalid Request: top-level must be an object")
		return

	# 鉴权握手 — 传输层负责校验、标记已鉴权,并发送默认的 {authed:true}
	# 确认(运行时不提供确认覆盖)。
	if not _transport.is_authed(peer):
		_transport.validate_auth(peer, msg)
		return

	var id = msg.get("id", null)
	if typeof(id) == TYPE_FLOAT and int(id) == id:
		id = int(id)
	var method := str(msg.get("method", ""))
	var params = msg.get("params", null)

	if method.is_empty():
		_send_error(peer, id, -32600, "Invalid Request: missing method")
		return

	match method:
		"echo":
			_send_result(peer, id, params)
		"ping":
			_send_result(peer, id, {"success": true})
		"runtime.screenshot":
			_cmd_runtime_screenshot(peer, id, params)
		"runtime.get_node_state":
			_cmd_runtime_get_node_state(peer, id, params)
		"runtime.set_property":
			_cmd_runtime_set_property(peer, id, params)
		"debugger.get_log":
			_cmd_debugger_get_log(peer, id, params)
		"signal.list":
			_cmd_signal_list(peer, id, params)
		"signal.connect":
			_cmd_signal_connect(peer, id, params)
		"signal.disconnect":
			_cmd_signal_disconnect(peer, id, params)
		"signal.emit":
			_cmd_signal_emit(peer, id, params)
		"input.simulate":
			_cmd_input_simulate(peer, id, params)
		"runtime.time_control":
			_cmd_runtime_time_control(peer, id, params)
		"animation_player.control":
			_cmd_animation_player_control(peer, id, params)
		"runtime.get_script_vars":
			_cmd_runtime_get_script_vars(peer, id, params)
		"execute.code":
			_cmd_execute_code(peer, id, params)
		_:
			_send_error(peer, id, -32601, "Method not found: %s" % method)


func _send_result(peer: WebSocketPeer, id, result) -> void:
	Notifier.send_result(peer, id, result, "[MCPRuntimeServer]")


func _send_error(peer: WebSocketPeer, id, code: int, message: String) -> void:
	Notifier.send_error(peer, id, code, message)



# ---- 运行时命令辅助方法 -----------------------------------------------------


func _cmd_runtime_screenshot(peer: WebSocketPeer, id, params) -> void:
	var viewport := get_viewport()
	if viewport == null:
		_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "no viewport available"))
		return

	var params_dict: Dictionary = params if typeof(params) == TYPE_DICTIONARY else {}

	# 提前拒绝非法的 image_detail,让拼错值无法以全分辨率溜过去(纯尺寸
	# 计算器把未知值当作原生尺寸处理,所以守卫要放在这里)。
	var detail := ScreenshotResponse.detail_of(params_dict)
	if detail.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS",
			"image_detail must be one of [full, mid, low] (got %s)"
			% str(params_dict.get("image_detail", ""))))
		return

	var force_foreground := bool(params_dict.get("force_foreground_game", false))

	var remediation: Array[String] = []
	var foreground_hint := ""

	# 按请求尊重 force_foreground_game,而不看最小化读取结果:嵌入式游戏
	# (编辑器的 Game 视图)是所有者关联的顶层窗口,被硬性限定为 WINDOWED,
	# 即使编辑器被最小化也不会被报告为最小化,因此若用 _window_is_minimized()
	# 来门控这个开关,就会静默跳过它。
	if force_foreground:
		foreground_hint = _foreground_game_if_requested(remediation)

	# 不要因窗口状态而短路,先尝试捕获:仍在合成画面的窗口(嵌入式游戏,
	# 或只是失焦的浮动游戏)会产生新帧。对等待设上限,让被挂起的窗口 — 或
	# 从不触发 frame_post_draw 的空闲按需重绘游戏 — 不会一直停到服务器的
	# 调用超时。
	var frame_arrived := await _await_frame_or_timeout()

	# 等待已到期且窗口无法绘制:渲染确实被挂起(顶层游戏被最小化,或在
	# macOS 上被完全遮挡),因此不可能产生任何帧 — 返回诊断信号而不是过期的
	# 最后一帧。若等待到期但窗口仍可绘制,则说明是空闲的按需重绘游戏,此时
	# 最后一帧就是当前状态 — 继续往下走并返回它。
	if _should_signal_minimized(frame_arrived, not DisplayServer.window_can_draw(0)):
		_send_result(peer, id, MCPToolkitError.fail("RUNTIME_WINDOW_MINIMIZED",
			"the running game window can't render — it is minimized or fully occluded, so rendering is suspended",
			"The running game window can't render (minimized or fully occluded), so no frame is produced — not a crash or timeout. Retry with force_foreground_game:true to raise and capture the game window, or restore/reveal it yourself."))
		return

	var image := viewport.get_texture().get_image()
	if image == null:
		_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "viewport texture unavailable"))
		return

	# 总是按全分辨率编码 — 这才是磁盘副本要持久化的内容,不受 image_detail
	# 影响(该上限只作用于内联路径)。
	var png_bytes := image.save_png_to_buffer()
	if png_bytes.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "save_png_to_buffer returned empty"))
		return

	var inline := _downscale_inline_png(image, params_dict, detail, png_bytes)

	# 运行时的保存路径允许列表只有 user://screenshots/ — 发行游戏没有
	# res:// 写权限,且 res:// 目标由编辑器服务器管辖。内联载荷的传输缓冲
	# 自适应由 ScreenshotResponse.build 负责(逐级降采样已在此前发生,超限
	# 兜底为磁盘形态),所以这里无需再做字节检查。
	var response := ScreenshotResponse.build(
		params_dict, png_bytes, image.get_width(), image.get_height(),
		["user://screenshots/"],
		inline["png_bytes"], int(inline["width"]), int(inline["height"]),
		str(inline["detail"]))
	if response.get("success") == false:
		_send_result(peer, id, response)
		return
	if not remediation.is_empty():
		response["remediation"] = remediation
	# 嵌入式游戏无法被前置(所有者关联、由编辑器控制),因此在那里发起
	# force_foreground_game 请求不会生效 — 要说明窗口为什么没动,而不是给
	# 调用方留下一次静默的空操作。采用追加而非覆盖,以免清掉整形器(shaper)
	# 可能已设置的磁盘全分辨率提示。
	if not foreground_hint.is_empty():
		if response.has("hint"):
			response["hint"] = "%s %s" % [response["hint"], foreground_hint]
		else:
			response["hint"] = foreground_hint
	_send_result(peer, id, response)


# 在 image_detail 级别适用时编码内联 PNG,并按传输缓冲自适应:从请求级别
# 逐级下降(full→mid→low),返回第一个放得进 ws_buffer_kb 的级别;全部超限
# 时返回空载荷,ScreenshotResponse.build 兜底为磁盘形态 — 绝不让超限帧上升
# 为 RESPONSE_TOO_LARGE。仅在确实要返回内联图像(inline/both 模式)时执行 —
# disk 模式返回的是全分辨率文件,不需要内联缓冲。原生尺寸(该级别无需缩放)
# 复用 [param png_bytes](调用方已编码的全分辨率缓冲),不重复编码。返回
# {png_bytes, width, height, detail}:detail 是实际应用的级别,供 build 披露
# 降级;png_bytes 为空(尺寸为 -1)表示无内联候选 — build 解读为"内联使用
# 全分辨率缓冲、未降级"。
func _downscale_inline_png(image: Image, params_dict: Dictionary, detail: String,
		png_bytes: PackedByteArray) -> Dictionary:
	var empty := {"png_bytes": PackedByteArray(), "width": -1, "height": -1, "detail": ""}
	if ScreenshotResponse.mode_of(params_dict) == "disk":
		return empty
	var native := Vector2i(image.get_width(), image.get_height())
	var last_dims := Vector2i(-1, -1)
	for level in ScreenshotResponse.progressive_details(detail):
		var target := ScreenshotResponse.image_detail_dims(native.x, native.y, level)
		if target == last_dims:
			continue  # 图像本就小于该级别上限 — 与上一候选同尺寸,无需重复编码
		last_dims = target
		var level_bytes := png_bytes
		if target != native:
			# duplicate() 的返回类型是 Ref<Resource>;显式的 Image 局部变量完成强转
			# (类型不匹配时会响亮报错,不像 `as` 那样静默)。对副本执行缩放,让
			# 全分辨率缓冲保持完好以供磁盘保存。
			var inline_image: Image = image.duplicate()
			inline_image.resize(target.x, target.y, Image.INTERPOLATE_LANCZOS)
			level_bytes = inline_image.save_png_to_buffer()
		if ScreenshotResponse.inline_fits_transport(level_bytes):
			return {
				"png_bytes": level_bytes,
				"width": target.x,
				"height": target.y,
				"detail": level,
			}
	return empty


# 按调用方的显式请求拉起游戏窗口。嵌入式游戏(编辑器的 Game 视图)是
# 所有者关联、由编辑器控制的:引擎把它硬性限定为 WINDOWED 并每帧重申其
# z 顺序,因此 window_set_mode / window_move_to_foreground 在那里不起作用 —
# 这个开关无法生效,所以不要谎称成功。顶层游戏则会响应:取消最小化
# (仅当确实最小化时,避免对普通窗口化游戏做多余的重设)+ 移到前台 +
# 获取焦点,是真实可见的副作用 — 无论是取消最小化还是从后台拉起,都声明
# "foregrounded_game"。返回一条提示(无则为空字符串),解释嵌入式场景下的
# 空操作,免得调用方面对一次悄无声息的无效请求。
func _foreground_game_if_requested(remediation: Array[String]) -> String:
	if _is_embedded_in_editor():
		return ("The game runs embedded in the editor's Game view and can't be independently "
			+ "foregrounded — it is already composited in the Game dock. Raise or restore the "
			+ "editor window to view it.")
	if _window_is_minimized():
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED, 0)
	_ensure_game_focus()
	remediation.append("foregrounded_game")
	return ""


# 游戏是否以嵌入方式运行在编辑器的 Game 视图中。该 API 是 4.4+ 的
# (嵌入功能落地的那个版本);在 4.2/4.3 的最低支持版本上该方法不存在,
# 嵌入不可能发生,因此方法缺失可以正确地解读为顶层运行。
func _is_embedded_in_editor() -> bool:
	return Engine.has_method("is_embedded_in_editor") and bool(Engine.call("is_embedded_in_editor"))


# 决定在带时限的等帧结束后,是否返回"渲染被挂起"的信号。只有当新帧
# 无法产生且窗口无法绘制时才发信号 — 即真正的挂起场景(顶层游戏被最小化,
# 或在 macOS 上被完全遮挡,合成器停止呈现)。若帧已到达,或等待到期但窗口
# 仍可绘制(空闲按需重绘,最后一帧即当前帧),都继续执行捕获。纯决策逻辑,
# 单独抽出以便在没有真实游戏窗口的情况下进行单元测试。
static func _should_signal_minimized(frame_arrived: bool, window_suspended: bool) -> bool:
	return not frame_arrived and window_suspended


# 游戏主窗口是否被最小化(渲染被挂起)。
func _window_is_minimized() -> bool:
	return DisplayServer.window_get_mode(0) == DisplayServer.WINDOW_MODE_MINIMIZED


# 等待下一次 frame_post_draw,但在 _RUNTIME_FRAME_WAIT_SECONDS 之后放弃,
# 让空闲的按需重绘窗口(从不触发该信号)不会把处理器挂死。一旦有帧绘制
# 立即返回 true;期限先到则返回 false — 调用方据此区分"新鲜捕获"与
# "窗口被挂起"。每个 process frame 轮询一次帧标志,而它即使在空闲窗口上
# 也会跳动,因此期限总是可以到达。
func _await_frame_or_timeout() -> bool:
	var got_frame := [false]
	var on_frame := func() -> void:
		got_frame[0] = true
	RenderingServer.frame_post_draw.connect(on_frame, CONNECT_ONE_SHOT)
	var deadline := Time.get_ticks_msec() + int(_RUNTIME_FRAME_WAIT_SECONDS * 1000.0)
	while not got_frame[0] and Time.get_ticks_msec() < deadline:
		await get_tree().process_frame
	if not got_frame[0] and RenderingServer.frame_post_draw.is_connected(on_frame):
		RenderingServer.frame_post_draw.disconnect(on_frame)
	return got_frame[0]


func _cmd_runtime_get_node_state(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var path := str(params.get("node_path", ""))
	if path.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "missing node_path"))
		return

	var tree := get_tree()
	if tree == null or tree.root == null:
		_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "scene tree unavailable"))
		return

	var node := tree.root.get_node_or_null(path)
	if node == null:
		var hint := _build_not_found_hint(tree.root, path)
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "node not found: %s%s" % [path, hint]))
		return

	var props := {}
	for prop in node.get_property_list():
		var usage: int = int(prop.get("usage", 0))
		# 仅检查器可见的属性 — 避开引擎内部状态和分类标题。
		if not (usage & PROPERTY_USAGE_EDITOR):
			continue
		var pname := str(prop.get("name", ""))
		if pname.is_empty() or pname.begins_with("_"):
			continue
		props[pname] = Coerce.serialize_value(node.get(pname))

	_send_result(peer, id, {
		"name": String(node.name),
		"class": node.get_class(),
		"path": path,
		"properties": props,
	})


func _cmd_runtime_get_script_vars(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var path := str(params.get("node_path", ""))
	if path.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "missing node_path"))
		return

	var tree := get_tree()
	if tree == null or tree.root == null:
		_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "scene tree unavailable"))
		return

	var node := tree.root.get_node_or_null(path)
	if node == null:
		var hint := _build_not_found_hint(tree.root, path)
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "node not found: %s%s" % [path, hint]))
		return

	var script = node.get_script()
	if script == null:
		_send_result(peer, id, {
			"name": String(node.name),
			"class": node.get_class(),
			"script_path": "",
			"path": path,
			"variables": [],
			"count": 0,
		})
		return

	var visibility_filter := str(params.get("visibility", "all"))
	if not (visibility_filter in ["public", "private", "all"]):
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS",
			"visibility must be 'public', 'private', or 'all' (got '%s')" % visibility_filter))
		return

	var variables: Array = []
	for prop in node.get_property_list():
		var usage: int = int(prop.get("usage", 0))
		if not (usage & PROPERTY_USAGE_SCRIPT_VARIABLE):
			continue
		var pname := str(prop.get("name", ""))
		if pname.is_empty():
			continue
		var vis := "private" if pname.begins_with("_") else "public"
		if visibility_filter != "all" and vis != visibility_filter:
			continue
		variables.append({
			"name": pname,
			"value": Coerce.serialize_value(node.get(pname)),
			"visibility": vis,
		})
	_send_result(peer, id, {
		"name": String(node.name),
		"class": node.get_class(),
		"script_path": script.resource_path,
		"path": path,
		"variables": variables,
		"count": variables.size(),
	})


## 逐段遍历路径,报告解析在哪一步失败,并列出同级节点。
func _build_not_found_hint(root: Node, path: String) -> String:
	var segments := path.split("/")
	var current := root
	# 跳过绝对路径开头产生的空段(例如 "/root/World" → ["", "root", "World"])
	var start := 1 if segments.size() > 0 and segments[0] == "" else 0
	# 对于绝对路径,第一个真实段是 "root" — 我们从 tree.root 出发,因此跳过它
	if start < segments.size() and segments[start] == "root":
		start += 1
	for i in range(start, segments.size()):
		var seg: String = segments[i]
		var child := current.get_node_or_null(seg)
		if child == null:
			var siblings: Array = []
			for c in current.get_children():
				siblings.append(str(c.name))
			if siblings.is_empty():
				return ". '%s' has no children." % str(current.get_path())
			return ". '%s' has children: %s" % [str(current.get_path()), str(siblings)]
		current = child
	return ""


func _cmd_runtime_set_property(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var node_path: String = str(params.get("node_path", ""))
	var property: String = str(params.get("property", ""))
	var value = params.get("value")

	if node_path.is_empty() or property.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS",
			"node_path and property are required"))
		return

	var tree := get_tree()
	if tree == null or tree.root == null:
		_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "scene tree unavailable"))
		return

	var node := tree.root.get_node_or_null(node_path)
	if node == null:
		# 逐段遍历路径,并在失败的那一层列出同级节点。
		var hint := _build_not_found_hint(tree.root, node_path)
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND",
			"node not found: %s%s" % [node_path, hint]))
		return

	# 校验属性存在 — 对于 "position:x" 这类复合路径,用基础属性名对照
	# 节点的属性列表检查。
	var current = node.get(property)
	if current == null:
		var base_prop := property.split(":")[0] if ":" in property else property
		var found := false
		for p in node.get_property_list():
			if str(p.get("name", "")) == base_prop:
				found = true
				break
		if not found:
			_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND",
				"property '%s' not found on %s" % [property, node_path]))
			return

	var coerced = Coerce.coerce_value(value)
	if typeof(coerced) == TYPE_DICTIONARY and (coerced as Dictionary).has("_coerce_error"):
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", str(coerced["_coerce_error"])))
		return
	# 当属性期望 NodePath 时,把字符串自动强转为 NodePath。
	if typeof(current) == TYPE_NODE_PATH and typeof(coerced) == TYPE_STRING:
		coerced = NodePath(str(coerced))

	node.set(property, coerced)

	# 回读以确认
	var new_value = node.get(property)
	# 对标量(不含冒号)路径分类这次写入:DROPPED(静默的错误类型,Godot 的
	# set() 会丢弃不可赋值的 Variant)→ SET_FAILED;ADJUSTED(引擎重塑了值,
	# 例如 7.9→7 的截断)→ 成功并在下方附加警告。冒号子路径(如 "position:x")
	# 无法通过 node.get() 回读(前后都是 null),因此保持尽力而为 — 与编辑器侧
	# 的拆分保持一致。
	var adjusted_warning := ""
	if ":" not in property:
		var outcome := PropertySetCheck.describe_set_drop(current, new_value, coerced, property)
		if outcome.get("status", "") == "dropped":
			# 绑定的 setter(position/modulate)可能已把错误类型经 Variant 转换
			# 成零值并存储;恢复先前的值,让 SET_FAILED 对运行中的节点无破坏。
			node.set(property, current)
			_send_result(peer, id, MCPToolkitError.fail("SET_FAILED", str(outcome.get("error", ""))))
			return
		if outcome.get("status", "") == "adjusted":
			adjusted_warning = str(outcome.get("warning", ""))
	var result := {
		"node_path": node_path,
		"property": property,
		"old_value": Coerce.serialize_value(current),
		"new_value": Coerce.serialize_value(new_value),
	}

	# 自动加载提示:/root 的直接子节点都是自动加载 — 它们会跨越场景切换
	# 持续存在。提醒:除非游戏自身的重置逻辑显式还原该属性,否则改动会
	# 带到重启/关卡切换之后。
	if node.get_parent() == tree.root:
		result["warning"] = (
			"'%s' is an autoload — it persists across scene transitions. " % node.name +
			"This change will carry forward through restarts/level changes " +
			"unless the game explicitly resets '%s'." % property)

	# "已接受但被调整"的说明(引擎重塑了值)。若与自动加载警告同时适用,
	# 则合并两者,确保任何一项告诫都不丢失。
	if adjusted_warning != "":
		if result.has("warning"):
			result["warning"] = str(result["warning"]) + " " + adjusted_warning
		else:
			result["warning"] = adjusted_warning

	_send_result(peer, id, result)


func _cmd_debugger_get_log(peer: WebSocketPeer, id, params) -> void:
	var limit := _DEFAULT_LOG_LIMIT
	if typeof(params) == TYPE_DICTIONARY and params.has("limit"):
		limit = max(1, int(params.get("limit", _DEFAULT_LOG_LIMIT)))
	var source: String = "buffer"
	if typeof(params) == TYPE_DICTIONARY and params.has("source"):
		source = str(params.get("source", "buffer"))
	if not (source in ["buffer", "file"]):
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS",
			"source must be 'buffer' or 'file' (got %s)" % source))
		return

	var text_filter: String = ""
	var text_regex: RegEx = null
	if typeof(params) == TYPE_DICTIONARY and params.has("text_filter"):
		text_filter = str(params.get("text_filter", ""))
	if text_filter != "" and typeof(params) == TYPE_DICTIONARY:
		var is_regex: bool = bool(params.get("is_regex", false))
		if is_regex:
			text_regex = RegEx.new()
			if text_regex.compile("(?i)" + text_filter) != OK:
				_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS",
					"text_filter is not a valid regex (is_regex=true). "
					+ "To search for literal text, omit is_regex or set it to false. "
					+ "For regex, check for unbalanced groups () [] or unescaped metacharacters."))
				return

	var regex_warning := _detect_double_escaped_regex(text_filter) if text_regex != null else ""

	if source == "buffer":
		var buf_result: Dictionary = LogBuffer.get_entries(limit, [], -1, text_filter, text_regex)
		var entries: Array = buf_result["entries"]
		for entry in entries:
			var scrubbed := Scrubber.scrub(str(entry["message"]), "debugger.get_log")
			entry["message"] = scrubbed["text"]
		var has_more: bool = buf_result["has_more"]
		# 封顶尾部分页(最旧的行先被丢弃,无游标):若还有更多行,引导调用方
		# 提高 limit 或用 text_filter 收窄。
		var hint := ""
		if has_more:
			hint = "more lines remain — raise limit or narrow with text_filter (capped tail: the oldest lines drop first, no cursor)."
		var response := Pagination.build(
			{"lines": Untrusted.wrap("game_log", "buffer", JSON.stringify(entries))},
			"lines", int(buf_result["total_lines"]), int(buf_result["returned"]), has_more,
			"", 0, hint, {"next_id": buf_result["next_id"], "source": "buffer"})
		# 在 4.2-4.4 上,缓冲依靠文件尾随实现 — 若为空且文件日志关闭则发出警告。
		if entries.is_empty() and not LogBuffer.uses_logger_api():
			if not LogHelpers.is_file_logging_enabled():
				response["warning"] = "On Godot 4.2-4.4 the log buffer captures output by tailing the log file. Enable debug/file_logging/enable_file_logging in ProjectSettings and restart the editor for output capture to work. Logs from before enabling will not be available."
		if not regex_warning.is_empty():
			response["warning"] = regex_warning
		_send_result(peer, id, response)
		return

	# source == "file" — 原始日志文件读取路径。
	# 使用未全局化的(raw,un-globalized)已配置路径:既尊重被重定位过的
	# debug/file_logging/log_path,又让默认情况下响应中的路径元数据保持
	# 逐字节一致(FileAccess 接受 user:// 路径)。
	var log_path := LogHelpers.configured_log_path()
	if not FileAccess.file_exists(log_path):
		var file_logging_enabled: bool = LogHelpers.is_file_logging_enabled()
		if not file_logging_enabled:
			var _hint := "file logging is disabled — enable it in ProjectSettings → Debug → File Logging → Enable File Logging, then restart"
			if LogBuffer.uses_logger_api():
				_hint += "; alternatively use source=\"buffer\" (default) which captures all output in real-time"
			else:
				_hint += ". On Godot 4.2-4.4 source=\"buffer\" also depends on file logging, so both sources require this setting"
			_send_result(peer, id, MCPToolkitError.fail("LOG_UNAVAILABLE", _hint,
				MCPToolkitError.log_unavailable_hint(LogBuffer.uses_logger_api())))
		else:
			_send_result(peer, id, Pagination.build(
				{"lines": []}, "lines", 0, 0, false, "", 0, "",
				{
					"path": log_path,
					"source": "file",
					"note": "log file not yet written — new game with no prints, or file flush pending",
				}))
		return

	var file := FileAccess.open(log_path, FileAccess.READ)
	if file == null:
		var open_err := FileAccess.get_open_error()
		if FileAccess.file_exists(log_path):
			_send_result(peer, id, MCPToolkitError.fail("LOG_BUSY",
				"log file exists but could not be read (err %d)" % open_err,
				MCPToolkitError.log_busy_hint(LogBuffer.uses_logger_api())))
		else:
			var _gone_hint := "log file disappeared at %s — possible log rotation; retry" % log_path
			if LogBuffer.uses_logger_api():
				_gone_hint += " or use source=\"buffer\""
			_send_result(peer, id, MCPToolkitError.fail("LOG_UNAVAILABLE", _gone_hint,
				MCPToolkitError.log_unavailable_hint(LogBuffer.uses_logger_api())))
		return
	var text := file.get_as_text()
	file.close()

	# 先过滤后切片 — 与缓冲来源(LogBuffer.get_entries)保持一致:先去掉
	# ANSI 序列并对所有行应用 text_filter,然后取最后 `limit` 条匹配。
	# total_lines 统计的是匹配行数(封顶前);truncated 表示匹配数超过
	# limit,较旧的匹配被丢弃。封顶尾部,无游标。
	var all_lines := text.split("\n", false)
	var filtered: Array = []
	for raw_line in all_lines:
		var line: String = LogHelpers.strip_ansi(str(raw_line))
		if text_filter != "":
			if text_regex != null:
				if not text_regex.search(line):
					continue
			else:
				if line.findn(text_filter) < 0:
					continue
		filtered.append(line)

	var total := filtered.size()
	var file_truncated: bool = filtered.size() > limit
	if file_truncated:
		filtered = filtered.slice(filtered.size() - limit)
	var slice: Array = filtered

	var json_slice := JSON.stringify(slice)
	var scrubbed := Scrubber.scrub(json_slice, "debugger.get_log")
	# 封顶尾部分页(最旧的行先被丢弃,无游标)。
	var file_hint := ""
	if file_truncated:
		file_hint = "more lines remain — raise limit or narrow with text_filter (capped tail: the oldest lines drop first, no cursor)."
	var file_response := Pagination.build(
		{"lines": Untrusted.wrap("game_log", "godot", scrubbed["text"])},
		"lines", total, slice.size(), file_truncated, "", 0, file_hint,
		{"path": log_path, "source": "file"})
	if not regex_warning.is_empty():
		file_response["warning"] = regex_warning
	_send_result(peer, id, file_response)


## 检测疑似被双重转义的正则元字符(与 editor_commands.gd 相同)。
func _detect_double_escaped_regex(pattern: String) -> String:
	for letter in ["d", "D", "w", "W", "s", "S", "b", "B"]:
		if pattern.find("\\\\" + letter) >= 0:
			return (
				"Pattern contains '\\\\%s' (literal backslash + '%s'). "
				+ "If you meant the regex metacharacter \\%s, your backslash "
				+ "is likely double-escaped. In JSON, use \"\\\\%s\" (one escaped "
				+ "backslash), not \"\\\\\\\\%s\" (two)."
			) % [letter, letter, letter, letter, letter]
	return ""


# ---- 信号命令(编辑器处理器的模式 B 镜像)-----------------------------------


# 注入到共享 SignalPairResolver 的根解析器:返回运行中的 SceneTree 根
# (编辑器侧注入的是 EditorInterface.get_edited_scene_root())。当场景树
# 尚未就绪时返回 null,解析器会把它当作"没有节点"处理。
func _runtime_root() -> Node:
	var tree := get_tree()
	return tree.root if tree != null else null


# 编辑器 _resolve_scene_node 的运行时等价物 — 通过共享解析器针对运行中的
# SceneTree 根解析路径。裸 "" / "." 解析为树根,这样只想要顶层信号的调用方
# 无需提供完整路径。保留这层薄包装,是为了让其他需要解析节点的运行时命令
# (signal.emit、animation_player.control、execute.code 等)共享同一个接缝。
func _resolve_runtime_node(path: String):
	return SignalPairResolver.resolve_node(path, _runtime_root())


func _cmd_signal_list(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var path := str(params.get("node_path", ""))
	var node = _resolve_runtime_node(path)
	if node == null:
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % path))
		return
	_send_result(peer, id, {"path": path, "signals": SignalPairResolver.list_signals_of(node)})


# 通过共享解析器,针对运行中的树校验 connect/disconnect 四元组。返回的形态
# 与编辑器的 _resolve_signal_pair 相同(编辑器包装了同一个解析器,并叠加了
# 自己的提示增强);运行时路径刻意更精简 — 没有实例化场景/编译错误提示。
func _resolve_runtime_signal_pair(params) -> Dictionary:
	return SignalPairResolver.resolve_pair(params, _runtime_root())


func _cmd_signal_connect(peer: WebSocketPeer, id, params) -> void:
	var r := _resolve_runtime_signal_pair(params)
	if r.has("error"):
		_send_result(peer, id, MCPToolkitError.fail(str(r["code"]), str(r["error"])))
		return
	var source = r["source"]
	var callable: Callable = r["callable"]
	var signal_name: String = str(r["signal_name"])
	var source_path: String = str(r["source_path"])
	var target_path: String = str(r["target_path"])
	var method_name: String = str(r["method_name"])
	# 幂等性 — 与 SignalCommands 中编辑器侧副本保持一致。
	if source.is_connected(signal_name, callable):
		_send_result(peer, id, {
			"success": true,
			"status": "returned",
			"source_path": source_path,
			"signal": signal_name,
			"target_path": target_path,
			"method": method_name,
		})
		return
	# 运行时没有 UndoRedo — 连接对本次游戏会话是临时的,玩家退出即消失。
	# 直接 connect;失败码会向上暴露。显式的 int 注解:因为 `source` 是
	# Variant(字典值),类型推断无法穿透到 Object.connect 的 Error 返回值。
	var err: int = source.connect(signal_name, callable)
	if err != OK:
		_send_result(peer, id, MCPToolkitError.fail("CONNECT_FAILED", "connect returned %d" % err))
		return
	_send_result(peer, id, {
		"success": true,
		"status": "created",
		"source_path": source_path,
		"signal": signal_name,
		"target_path": target_path,
		"method": method_name,
	})


func _cmd_signal_disconnect(peer: WebSocketPeer, id, params) -> void:
	var r := _resolve_runtime_signal_pair(params)
	if r.has("error"):
		_send_result(peer, id, MCPToolkitError.fail(str(r["code"]), str(r["error"])))
		return
	var source = r["source"]
	var callable: Callable = r["callable"]
	var signal_name: String = str(r["signal_name"])
	if not source.is_connected(signal_name, callable):
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "no connection to disconnect"))
		return
	source.disconnect(signal_name, callable)
	_send_result(peer, id, {"success": true})



func _cmd_signal_emit(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var path := str(params.get("node_path", ""))
	var signal_name := str(params.get("signal_name", ""))
	if signal_name.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "missing signal_name"))
		return
	var node = _resolve_runtime_node(path)
	if node == null:
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % path))
		return
	if not node.has_signal(signal_name):
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "signal %s not on %s" % [signal_name, path]))
		return
	var raw_args = params.get("args", [])
	if typeof(raw_args) != TYPE_ARRAY:
		raw_args = []
	var coerced: Array = [signal_name]
	for a in raw_args:
		var coerced_arg = Coerce.coerce_value(a)
		if typeof(coerced_arg) == TYPE_DICTIONARY and (coerced_arg as Dictionary).has("_coerce_error"):
			_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", str(coerced_arg["_coerce_error"])))
			return
		coerced.append(coerced_arg)
	node.callv("emit_signal", coerced)
	_send_result(peer, id, {"success": true})


# ---- 试玩测试命令 ------------------------------------------------------------


func _cmd_runtime_time_control(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var result: Dictionary = await _playtest_time_controller.control(get_tree(), params)
	_send_result(peer, id, result)


# input.simulate:按顺序处理 {event_type, event_data, delay_ms?} 数组,
# 可带可选延迟。key/action 事件经由 Input.parse_input_event 送入 Input
# 单例;mouse 与 send_text 事件则经由 Viewport.push_input 路由
# (驱动 gui_input/焦点)。
func _cmd_input_simulate(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return

	var events = params.get("events", null)
	if typeof(events) != TYPE_ARRAY or events.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS",
			"events array is required and must not be empty"))
		return

	var summary_mode: bool = bool(params.get("summary", true))
	var total: int = events.size()
	var results: Array = []
	var processed := 0

	for i in total:
		var event = events[i]
		if typeof(event) != TYPE_DICTIONARY:
			var err := MCPToolkitError.fail("INVALID_PARAMS", "event at index %d must be an object" % i)
			err["events_processed"] = processed
			if not summary_mode:
				err["results"] = results
			_send_result(peer, id, err)
			return
		var et := str(event.get("event_type", ""))
		var ed: Dictionary = {}
		var raw = event.get("event_data", null)
		if typeof(raw) == TYPE_DICTIONARY:
			ed = raw
		var delay_before_ms := int(event.get("delay_before_ms", 0))
		var delay_after_ms := int(event.get("delay_after_ms", 0))
		if delay_before_ms > 0:
			await get_tree().create_timer(delay_before_ms / 1000.0).timeout
		var event_result := {"index": i, "total": total, "type": et, "dispatched": true}
		# 诊断信息:gui_has_focus = 有 GUI Control 持有焦点(不是操作系统窗口焦点)。
		var vp := get_viewport()
		if vp != null:
			event_result["gui_has_focus"] = vp.gui_get_focus_owner() != null
		event_result["tree_paused"] = get_tree().paused if get_tree() != null else false
		if et == "click":
			var click_delay := int(ed.get("click_delay_ms", 50))
			await _dispatch_click(ed)
			event_result["click_delay_ms"] = click_delay
		elif et == "click_node":
			var click_result := _dispatch_click_node(ed)
			event_result.merge(click_result, true)
			if not bool(click_result.get("dispatched", false)):
				# click_node 找不到目标时不能以批次 success:true 返回；
				# 调用方必须看到可重试的参数错误，而不是把未点击误当成成功。
				var click_err := MCPToolkitError.fail("NOT_FOUND", str(click_result.get("error", "click_node dispatch failed")))
				click_err["events_processed"] = processed
				click_err["failed_at"] = event_result
				if not summary_mode:
					click_err["results"] = results
				_send_result(peer, id, click_err)
				return
		elif et == "action":
			# 混合派发:用 action_press/release 维持持续的 Input.is_action_pressed()
			# 状态(移动),同时用 parse_input_event(InputEventAction) 设置
			# is_action_just_pressed() 所需的帧间过渡跟踪(跳跃)。
			var action_name := StringName(str(ed.get("action", "")))
			# 拒绝未在 InputMap 中注册的动作:派发它什么也匹配不到(一个本会静默
			# 报告 success:true、dispatched:true 的空操作)。像畸形事件一样中止
			# 整个批次,并指名未知的动作。只有 action 路径使用 InputMap —
			# key/text/send_text 模式不使用。
			if not InputMap.has_action(action_name):
				var action_err := MCPToolkitError.fail("INVALID_PARAMS",
					"input action '%s' is not registered in the InputMap" % str(action_name),
					"Register it in Project Settings → Input Map (or check the name — "
					+ "action strings are case-sensitive). List actions with InputMap.get_actions().")
				action_err["events_processed"] = processed
				if not summary_mode:
					action_err["results"] = results
				_send_result(peer, id, action_err)
				return
			var is_pressed := bool(ed.get("pressed", true))
			var strength := float(ed.get("strength", 1.0))
			if is_pressed:
				Input.action_press(action_name, strength)
			else:
				Input.action_release(action_name)
			var act := InputEventAction.new()
			act.action = action_name
			act.pressed = is_pressed
			act.strength = strength
			Input.parse_input_event(act)
			event_result["action"] = str(action_name)
			event_result["pressed"] = is_pressed
		elif et == "send_text":
			# 通过焦点持有者向文本界面输入字符串,让真实的
			# gui_input → text_changed/text_submitted 得以触发
			# (set_property(.text) 会跳过它们)。event_data 的值是 Variant —
			# 需要逐一显式强转。
			var text_val: String = str(ed.get("text", ""))
			var node_path_str: String = str(ed.get("node_path", ""))
			var do_submit: bool = bool(ed.get("submit", false))

			# 焦点优先级:显式的 Control node_path 优先;否则向当前持有 GUI 焦点的
			# 控件输入。自定义 _input 读取器无论如何都能收到事件,因此 "none"
			# 只是提示,不是失败。
			var target: Node = null
			var focus_source := "none"
			var node_path_problem := ""
			if not node_path_str.is_empty():
				# 针对 /root 解析(不要用 click_node 的 current_scene 解析器),
				# 让绝对路径 /root/... 和自动加载都能按文档说明解析。
				var resolved = _resolve_runtime_node(node_path_str)
				if resolved == null:
					node_path_problem = "not found"
				elif resolved is Control:
					target = resolved
					(resolved as Control).grab_focus()
					# 输入前留一帧,让焦点变化稳定下来。
					await get_tree().process_frame
					focus_source = "node_path"
				else:
					node_path_problem = "is not a Control (%s)" % resolved.get_class()
			if target == null:
				target = vp.gui_get_focus_owner() if vp != null else null
				focus_source = "existing" if target != null else "none"

			# 仅当目标暴露可读的 String `text` 时才捕获前后值;自定义读取器没有
			# → text_changed 保持 null。
			var has_text := false
			var text_before := ""
			if target != null:
				var tb: Variant = target.get("text")
				if typeof(tb) == TYPE_STRING:
					has_text = true
					text_before = tb
			# 当目标的 `secret` 为 true 时抹除回显(LineEdit.secret);
			# 下面的变化比较仍读取真实值 — 只有回显被遮蔽。
			var is_secret := false
			if target != null:
				var sec: Variant = target.get("secret")
				is_secret = typeof(sec) == TYPE_BOOL and bool(sec)

			# 经由 push_input 投递(把按键事件路由到 gui.key_focus);没有视口时,
			# 与鼠标路径一致地回退到 parse_input_event。
			for key_ev in TextInputSynth.synthesize_text_events(text_val):
				if vp != null:
					vp.push_input(key_ev)
				else:
					Input.parse_input_event(key_ev)
			if do_submit:
				for enter_ev in TextInputSynth.synthesize_enter():
					if vp != null:
						vp.push_input(enter_ev)
					else:
						Input.parse_input_event(enter_ev)
			var chars_sent := text_val.length()

			var text_changed: Variant = null
			if has_text:
				var ta: Variant = target.get("text")
				var raw_after: String = ta if typeof(ta) == TYPE_STRING else ""
				text_changed = raw_after != text_before
				event_result["text_after"] = TextInputSynth.format_text_after(raw_after, is_secret)
			event_result["chars_sent"] = chars_sent
			event_result["focus_source"] = focus_source
			if target != null:
				event_result["focus_target"] = {"path": str(target.get_path()), "class": target.get_class()}
			else:
				event_result["focus_target"] = null
			event_result["text_changed"] = text_changed

			if node_path_problem != "":
				event_result["hint"] = "node_path '%s' %s; pass a valid Control path or omit node_path to type into the focused field." % [node_path_str, node_path_problem]
			else:
				var focus_path := str(target.get_path()) if target != null else ""
				var focus_class := target.get_class() if target != null else ""
				var paused := bool(event_result.get("tree_paused", false))
				var send_text_hint := TextInputSynth.build_hint(
					focus_source, text_changed, focus_path, focus_class, chars_sent, paused)
				if send_text_hint != "":
					event_result["hint"] = send_text_hint
		else:
			var ev := _build_input_event(et, ed)
			if ev == null:
				event_result["dispatched"] = false
				event_result["error"] = "unknown event_type (expected key|mouse_button|mouse_motion|action|click|click_node|send_text)"
				results.append(event_result)
				var err := MCPToolkitError.fail("INVALID_PARAMS",
					"unknown event_type at index %d: %s" % [i, et])
				err["events_processed"] = processed
				if summary_mode:
					err["failed_at"] = event_result
				else:
					err["results"] = results
				_send_result(peer, id, err)
				return
			# 鼠标事件使用 push_input,以获得正确的 GUI/CanvasLayer 路由。
			# 不做操作系统级聚焦 — position + global_position 字段足以完成
			# 视口命中测试,且并行安全。
			if ev is InputEventMouse:
				if get_viewport() != null:
					get_viewport().push_input(ev)
				else:
					Input.parse_input_event(ev)
			else:
				Input.parse_input_event(ev)
		results.append(event_result)
		processed += 1
		if delay_after_ms > 0:
			await get_tree().create_timer(delay_after_ms / 1000.0).timeout

	if summary_mode:
		_send_result(peer, id, {"success": true, "events_processed": processed,
			"total": total, "last_event": results.back()})
	else:
		_send_result(peer, id, {"success": true, "events_processed": processed,
			"total": total, "results": results})


## 从 event_data 解析鼠标位置。同时接受扁平的 {x, y} 与嵌套的
## {position: {x, y}} 两种格式,以提高对 LLM 的容错性。
func _parse_mouse_position(event_data: Dictionary) -> Vector2:
	var pos = event_data.get("position", null)
	if pos == null and event_data.has("x"):
		pos = {"x": event_data.get("x", 0.0), "y": event_data.get("y", 0.0)}
	if typeof(pos) == TYPE_DICTIONARY:
		return Vector2(float(pos.get("x", 0.0)), float(pos.get("y", 0.0)))
	return Vector2.ZERO


## 拉起并聚焦游戏的操作系统窗口。仅用于显式选择加入
## (force_foreground_game):输入合成并不需要它 — 只要 position +
## global_position 正确,push_input() 就足以完成视口命中测试,无需操作系统
## 焦点。风险:并行多实例运行时,这会让多个会话争抢操作系统鼠标与窗口
## 焦点,因此必须保持选择加入。
func _ensure_game_focus() -> void:
	DisplayServer.window_move_to_foreground()
	var win := get_window()
	if win != null:
		win.grab_focus()


func _build_input_event(event_type: String, event_data: Dictionary) -> InputEvent:
	match event_type:
		"key":
			var key_ev := InputEventKey.new()
			key_ev.keycode = int(event_data.get("keycode", 0))
			key_ev.pressed = bool(event_data.get("pressed", true))
			if event_data.has("physical_keycode"):
				key_ev.physical_keycode = int(event_data.get("physical_keycode", 0))
			if event_data.has("unicode"):
				key_ev.unicode = int(event_data.get("unicode", 0))
			key_ev.shift_pressed = bool(event_data.get("shift", false))
			key_ev.ctrl_pressed = bool(event_data.get("ctrl", false))
			key_ev.alt_pressed = bool(event_data.get("alt", false))
			key_ev.meta_pressed = bool(event_data.get("meta", false))
			return key_ev
		"mouse_button":
			var mb := InputEventMouseButton.new()
			mb.button_index = int(event_data.get("button_index", MOUSE_BUTTON_LEFT))
			mb.pressed = bool(event_data.get("pressed", true))
			var mb_vec := _parse_mouse_position(event_data)
			mb.position = mb_vec
			mb.global_position = mb_vec
			if event_data.has("world_position"):
				var wp := _parse_mouse_position(
					{"position": event_data["world_position"]})
				var vp_pos: Vector2 = get_viewport().get_canvas_transform() * wp
				mb.position = vp_pos
				mb.global_position = vp_pos
			mb.shift_pressed = bool(event_data.get("shift", false))
			mb.ctrl_pressed = bool(event_data.get("ctrl", false))
			mb.alt_pressed = bool(event_data.get("alt", false))
			mb.meta_pressed = bool(event_data.get("meta", false))
			return mb
		"mouse_motion":
			var mm := InputEventMouseMotion.new()
			var mm_vec := _parse_mouse_position(event_data)
			mm.position = mm_vec
			mm.global_position = mm_vec
			if event_data.has("world_position"):
				var wp := _parse_mouse_position(
					{"position": event_data["world_position"]})
				var vp_pos: Vector2 = get_viewport().get_canvas_transform() * wp
				mm.position = vp_pos
				mm.global_position = vp_pos
			var rel = event_data.get("relative", null)
			if typeof(rel) == TYPE_DICTIONARY:
				mm.relative = Vector2(float(rel.get("x", 0.0)), float(rel.get("y", 0.0)))
			return mm
		"action":
			var act := InputEventAction.new()
			act.action = StringName(str(event_data.get("action", "")))
			act.pressed = bool(event_data.get("pressed", true))
			if event_data.has("strength"):
				act.strength = float(event_data.get("strength", 1.0))
			return act
		_:
			return null


## click:在指定位置按下 + 延迟 + 释放(内部默认延迟 50 毫秒)。
## 使用 push_input 并携带 position + global_position 以完成 GUI 命中测试。
## 不做操作系统级的 warp_mouse 或窗口聚焦 — 对并行多实例运行安全。
func _dispatch_click(event_data: Dictionary) -> void:
	var mb_press := InputEventMouseButton.new()
	mb_press.button_index = int(event_data.get("button_index", MOUSE_BUTTON_LEFT))
	mb_press.pressed = true
	var vec := _parse_mouse_position(event_data)
	if event_data.has("world_position"):
		var wp := _parse_mouse_position(
			{"position": event_data["world_position"]})
		vec = get_viewport().get_canvas_transform() * wp
	mb_press.position = vec
	mb_press.global_position = vec
	mb_press.shift_pressed = bool(event_data.get("shift", false))
	mb_press.ctrl_pressed = bool(event_data.get("ctrl", false))
	mb_press.alt_pressed = bool(event_data.get("alt", false))
	mb_press.meta_pressed = bool(event_data.get("meta", false))
	var vp := get_viewport()
	if vp != null:
		vp.push_input(mb_press)
	else:
		Input.parse_input_event(mb_press)
	var click_delay := int(event_data.get("click_delay_ms", 50))
	await get_tree().create_timer(click_delay / 1000.0).timeout
	var mb_release := InputEventMouseButton.new()
	mb_release.button_index = mb_press.button_index
	mb_release.pressed = false
	mb_release.position = vec
	mb_release.global_position = vec
	mb_release.shift_pressed = mb_press.shift_pressed
	mb_release.ctrl_pressed = mb_press.ctrl_pressed
	mb_release.alt_pressed = mb_press.alt_pressed
	mb_release.meta_pressed = mb_press.meta_pressed
	if vp != null:
		vp.push_input(mb_release)
	else:
		Input.parse_input_event(mb_release)


## click_node:按路径对节点进行程序化点击。对 Control 调用 grab_focus(),
## 对 BaseButton 发出 pressed 信号。无需猜测坐标。
func _dispatch_click_node(event_data: Dictionary) -> Dictionary:
	var node_path_str := str(event_data.get("node_path", ""))
	if node_path_str.is_empty():
		return {"dispatched": false, "error": "node_path is required for click_node"}
	var root := get_tree().current_scene if get_tree() != null else null
	if root == null:
		return {"dispatched": false, "error": "no current scene"}
	var target := root.get_node_or_null(NodePath(node_path_str))
	if target == null:
		return {"dispatched": false, "error": "node not found: %s" % node_path_str}
	var result := {"dispatched": true, "node_path": node_path_str, "node_class": target.get_class()}
	if target is Control:
		target.grab_focus()
		result["focused"] = true
	if target is BaseButton:
		if target.toggle_mode:
			target.button_pressed = not target.button_pressed
			result["toggled_to"] = target.button_pressed
		target.emit_signal("pressed")
		result["pressed_emitted"] = true
	else:
		result["pressed_emitted"] = false
		result["note"] = "node is not a BaseButton; grab_focus applied if Control"
	return result


# animation_player.control:驱动运行中 SceneTree 里的 AnimationPlayer。
# 返回操作后的状态,让调用方无需额外一次往返即可确认 seek/play 已生效。
func _cmd_animation_player_control(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var path := str(params.get("node_path", ""))
	if path.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "missing node_path"))
		return
	var node = _resolve_runtime_node(path)
	if node == null:
		_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % path))
		return
	if not (node is AnimationPlayer):
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "node is not AnimationPlayer: %s (got %s)" % [path, node.get_class()]))
		return
	var ap: AnimationPlayer = node
	var op := str(params.get("operation", ""))
	match op:
		"play":
			var anim := str(params.get("animation_name", ""))
			if anim.is_empty():
				ap.play()
			else:
				if not ap.has_animation(anim):
					_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "animation not found: %s" % anim))
					return
				ap.play(anim)
		"pause":
			ap.pause()
		"stop":
			ap.stop()
		"seek":
			ap.seek(float(params.get("time", 0.0)), true)
		_:
			_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "unknown op: %s (expected play|pause|stop|seek)" % op))
			return
	_send_result(peer, id, {
		"success": true,
		"current_animation": String(ap.current_animation),
		"current_animation_position": ap.current_animation_position,
	})


# execute.code:在运行中的游戏上下文里通过 Expression 求值 GDScript。
# 风险已通过上下文协议注解(destructiveHint: true)和 security-recommendations.md
# 告知;代理侧的工具过滤才是执行层。
func _cmd_execute_code(peer: WebSocketPeer, id, params) -> void:
	if typeof(params) != TYPE_DICTIONARY:
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "params must be an object"))
		return
	var code := str(params.get("code", ""))
	if code.is_empty():
		_send_result(peer, id, MCPToolkitError.fail("INVALID_PARAMS", "missing code"))
		return

	var truncated := code.substr(0, _EXECUTE_CODE_LOG_CAP)
	if code.length() > _EXECUTE_CODE_LOG_CAP:
		truncated += "...[+%d chars]" % (code.length() - _EXECUTE_CODE_LOG_CAP)
	print("[MCPTools] execute.code: %s" % truncated)

	var scope_node: Node = null
	var scope_path := str(params.get("scope_path", ""))
	if scope_path.is_empty():
		var tree := get_tree()
		if tree == null or tree.root == null:
			_send_result(peer, id, MCPToolkitError.fail("INTERNAL", "scene tree unavailable"))
			return
		scope_node = tree.root
	else:
		scope_node = _resolve_runtime_node(scope_path)
		if scope_node == null:
			_send_result(peer, id, MCPToolkitError.fail("NOT_FOUND", "scope node not found: %s" % scope_path))
			return

	# Expression 只支持表达式,不支持语句。提前拦截常见的语句关键字,
	# 给出清晰的错误,而不是 Expression.parse() 那句晦涩的
	# "Invalid named index 'var'"。
	var _trimmed := code.strip_edges()
	for _kw in ["var", "return", "func", "if", "for", "while", "class", "const", "match"]:
		if _trimmed == _kw or _trimmed.begins_with(_kw + " ") or _trimmed.begins_with(_kw + "\t") or _trimmed.begins_with(_kw + "\n"):
			_send_result(peer, id, MCPToolkitError.fail("PARSE_ERROR",
				"execute_code only supports expressions, not statements. '%s' is a statement keyword. " % _kw +
				"Use method calls (node.method()), property access (node.property), or arithmetic instead."))
			return

	var expr := Expression.new()
	var parse_err := expr.parse(code, PackedStringArray())
	if parse_err != OK:
		_send_result(peer, id, MCPToolkitError.fail("PARSE_ERROR", expr.get_error_text()))
		return
	var result = expr.execute([], scope_node, false)
	if expr.has_execute_failed():
		var err_text := expr.get_error_text()
		# 经由共享辅助方法补充提示,让编辑器与运行时给出完全一致的指引。
		err_text += ExecuteHints.build_hint(err_text, code)
		_send_result(peer, id, MCPToolkitError.fail("EXECUTE_FAILED", err_text))
		return
	_send_result(peer, id, {"result": Coerce.serialize_value(result)})
