@tool
extends RefCounted
## 试玩测试(playtest)会话控制(编辑器侧):启动或停止编辑器试玩测试,
## 并确认活动运行时的就绪状态。
##
## 负责 game.start / game.stop 处理器(模式 A — 通过
## EditorInterface.play_*/stop_playing_scene 启动/停止游玩会话)、运行时就绪探测
## (对活动游戏的模式 B 服务器执行 WebSocket 连接→鉴权→ping 健康检查),
## 以及启动失败诊断(扫描 LogBuffer,解释游戏为何从未启动)。
## game.start 通过调用日志读取器的公开 mark_session_started(),
## 把调试日志会话标记为已开始 — 这是向调试日志获取子模块
## 的单项数据交接。
## 试玩测试命令组抽取出的子模块,经由 `preload` 别名访问。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const RegistryClient = Modules.RegistryClient
const Helpers = Modules.CommandHelpers
const MCPAuth := preload("res://addons/godot_mcp_toolkit/security/auth.gd")
const _LogReader := preload("res://addons/godot_mcp_toolkit/commands/playtest_log_reader.gd")

const RUNTIME_HOST := "127.0.0.1"
const RUNTIME_POLL_TIMEOUT_MS := 5000
const _REGISTRY_POLL_INTERVAL_MS := 100


# -- 命令 ---------------------------------------------------------------------


static func cmd_game_start(parameters: Dictionary) -> Dictionary:
	# 无头(headless)编辑器无法启动游戏进程,因此运行时(模式 B)永远不会
	# 起来 — 前置守卫返回的 success:true 曾是假成功(没有任何东西在运行)。
	# 像 editor.screenshot 一样确定性地失败,让 LLM 走分支,而不是轮询
	# 一个永远不会连接的运行时。守卫之下的显示路径逐字节相同。
	if Modules.VersionUtils.is_headless():
		return MCPToolkitError.fail("HEADLESS_UNSUPPORTED",
			"playtest requires a display server (the game process cannot be launched headless) — use script_check, scene/node inspection, or log_read(channel:'editor') for verification.")

	var target := str(parameters.get("scene_path", "current"))
	var wait_for_runtime_raw = parameters.get("wait_for_runtime", true)
	var wait_for_runtime := bool(wait_for_runtime_raw) \
		if typeof(wait_for_runtime_raw) == TYPE_BOOL else true
	var runtime_poll_raw = parameters.get("runtime_poll", false)
	var runtime_poll := bool(runtime_poll_raw) \
		if typeof(runtime_poll_raw) == TYPE_BOOL else false
	var if_running := str(parameters.get("if_running", "fail"))

	if not (if_running in ["return", "fail"]):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"if_running must be 'return' or 'fail' (got %s); default is 'fail'" % if_running)

	if runtime_poll:
		if not EditorInterface.is_playing_scene():
			# runtime_poll 从不启动游戏(仅探测),因此走到这里意味着
			# 没有任何正在运行的东西可供探测。空缓冲区不能证明编译失败,
			# 因此按捕获到的证据诊断,而不是断言原因。
			var comp := _scan_compilation_errors()
			var diag := _diagnose_startup(comp, false, Modules.VersionUtils.get_engine_version_pair())
			return MCPToolkitError.fail(diag["code"], diag["message"], diag["hint"])
	else:
		if EditorInterface.is_playing_scene():
			if if_running == "return":
				var runtime_port := RegistryClient.get_runtime_port()
				return MCPToolkitSuccess.ok({"status": "already_running",
					"runtime_port": runtime_port if runtime_port > 0 else null})
			return MCPToolkitError.fail("ALREADY_PLAYING",
				"a game is already running; call game.stop first, or use runtime_poll:true to re-probe the runtime connection")

		# 标记游玩会话已开始,使编辑器侧的 debugger.get_log
		# 缓存读取游戏的日志。引擎每次启动都会把 godot.log 重新清空,
		# 因此整个文件都属于本次会话(读取器从字节 0 开始读)。
		_LogReader.mark_session_started()

		# 处理器本身由 MCP 的 call_deferred 派发；播放场景会创建 Godot
		# 进度任务，必须先让出一帧离开消息队列刷新上下文。
		await (Engine.get_main_loop() as SceneTree).process_frame
		match target:
			"main":
				# 前置守卫:未定义主场景时,play_main_scene() 会弹出
				# 无法关闭的"No main scene has ever been defined"模态对话框并返回,
				# 使插件虚假地报告成功(随后的 runtime_poll 会
				# 把它误判为 COMPILATION_FAILED)。改为确定性失败 —
				# 与 "current" 分支的 NO_SCENE 守卫一致。
				if _main_scene_missing():
					return MCPToolkitError.fail("NO_SCENE",
						"no main scene set — set one in Project Settings > Application > Run > Main Scene "
						+ "(or project_set_setting application/run/main_scene='res://YourScene.tscn'), "
						+ "or use target:'current' or a res:// scene path.")
				EditorInterface.play_main_scene()
			"current":
				if Helpers.get_edited_root() == null:
					return MCPToolkitError.fail("NO_SCENE",
						"no currently-edited scene; use target:'main' or target:<res://path>, or scene.open first")
				EditorInterface.play_current_scene()
			_:
				var guard := FileGuard.resolve_safe(target)
				if guard["error"] != null:
					return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
				if target.get_extension().to_lower() != "tscn":
					return MCPToolkitError.fail("INVALID_PATH",
						"game.start only plays .tscn files (got %s)" % target)
				if not FileAccess.file_exists(target):
					return MCPToolkitError.fail("NOT_FOUND",
						"no scene file at %s; use scene.create first" % target, MCPToolkitError.HINT_FILE_PATH)
				EditorInterface.play_custom_scene(target)

	# 运行时就绪检查。
	var runtime_port := -1
	var runtime_ready := false
	var runtime_failure := ""
	var bridge_discovery := false

	if runtime_poll:
		# 显式重新探测:完整轮询循环(runtime_poll:true 路径)。
		var deadline := Time.get_ticks_msec() + RUNTIME_POLL_TIMEOUT_MS
		while Time.get_ticks_msec() < deadline:
			runtime_port = RegistryClient.get_runtime_port()
			if runtime_port > 0:
				break
			await Engine.get_main_loop().create_timer(_REGISTRY_POLL_INTERVAL_MS / 1000.0).timeout
		if runtime_port > 0:
			var remaining := maxi(500, deadline - Time.get_ticks_msec())
			var probe := await _poll_runtime_ready(
				RUNTIME_HOST, runtime_port, remaining)
			runtime_ready = probe["ready"]
			if not runtime_ready:
				runtime_failure = str(probe.get("failure", "unknown"))
		else:
			runtime_failure = "registry_timeout"
	elif wait_for_runtime:
		# Godot 的编辑器是单线程的:play_custom_scene() 把
		# 游戏启动推迟到事件循环,因此阻塞式轮询在这里永远
		# 不会成功(主线程被阻塞 → 游戏无法生成)。
		# 代理(agent)必须跟进:
		#   game_start(if_running:"return", runtime_poll:true)
		bridge_discovery = true

	var response := MCPToolkitSuccess.ok({
		"target": target,
		"runtime_port": runtime_port if runtime_port > 0 else null,
		"runtime_ready": runtime_ready,
	})
	if bridge_discovery:
		response["runtime_discovery"] = "bridge"
		# wait_for_runtime=true 时抑制提示文本 — 上下文协议(MCP)服务器
		# 会吸收这段异步间隙并返回单一的合并响应。
		# 非服务器客户端可以依据 runtime_discovery:"bridge" 跟进
		# 调用 game_start(if_running:'return', runtime_poll:true)。
		if not wait_for_runtime:
			response["hint"] = (
				"Game launched but runtime not yet connected (Godot defers the "
				+ "game process — it cannot start during a blocking call). "
				+ "Follow up with game_start(if_running:'return', runtime_poll:true) "
				+ "to wait for runtime readiness."
			)
	if runtime_poll:
		response["runtime_poll"] = true
	if (wait_for_runtime or runtime_poll) and not runtime_ready and not bridge_discovery:
		response["runtime_failure"] = runtime_failure
		match runtime_failure:
			"registry_timeout":
				if not EditorInterface.is_playing_scene():
					# 轮询开始时游戏还在运行,但现在已经停止 —
					# 属于"先运行后死掉"的软性路径,按运行时崩溃定性(它
					# 已开始运行,因此编译错误可能性不大)。runtime_ready:false +
					# runtime_failure:"registry_timeout" 是如实反映状况的结构化信号。
					var comp := _scan_compilation_errors()
					var diag := _diagnose_startup(comp, true, Modules.VersionUtils.get_engine_version_pair())
					response["hint"] = diag["hint"]
					if not (diag["errors"] as Array).is_empty():
						response["compilation_errors"] = diag["errors"]
				else:
					response["hint"] = "Runtime port never appeared in registry within the timeout. The game may need more time to start. Try game_start with runtime_poll:true to re-probe, or check log_read(channel:'editor') for startup errors."
			"token_read_failed":
				response["hint"] = "Could not read auth token — the token file may be missing or empty. Re-enable the plugin in Project Settings > Plugins."
			"ws_connect_timeout":
				response["hint"] = "Port %d found but WebSocket connection failed — the runtime server may have crashed on startup. Check log_read(channel:'editor') for errors." % runtime_port
			"auth_timeout":
				response["hint"] = "WebSocket connected but auth handshake timed out — the token may be stale. Re-enable the plugin in Project Settings > Plugins to regenerate it."
			"ping_timeout":
				response["hint"] = "Authenticated but ping/pong timed out — the runtime server may be overloaded. Use runtime_poll:true to retry without restarting the game."
			_:
				response["hint"] = "Runtime not ready (%s). Checklist: (1) Is the MCP Runtime autoload enabled? (2) Is the runtime port (default 6570) available? (3) Check log_read(channel:'editor') for errors. (4) If runtime tools aren't loaded yet, call discover_tools({request: 'runtime'})." % runtime_failure
	# P-006:未请求轮询且运行时未就绪时,警告运行时
	# 工具将阻塞/失败。防止代理对未连接的游戏调用
	# capture_screenshot(target:'runtime') / input_simulate。
	elif not runtime_ready and not wait_for_runtime and not runtime_poll:
		response["hint"] = (
			"runtime_ready is false — runtime tools (capture_screenshot(target:'runtime'), input_simulate, "
			+ "execute_code, etc.) will NOT work until the runtime connects. "
			+ "Call game_start with wait_for_runtime:true to wait for connection, "
			+ "or use runtime_poll:true to re-probe. "
			+ "Check log_read(channel:'auto') for startup errors."
		)
	return response


## 未配置主场景(application/run/main_scene 未设置或为空)时为真。
## 每次都从 ProjectSettings 全新读取 — 而非缓存值 — 因此刚写入的设置
## 也会被遵守。支撑 game.start "main" 的前置守卫:确定性失败,而不是
## 让 play_main_scene() 弹出引擎无法关闭的"No main scene has ever been
## defined"模态对话框(它静默返回 → 造成假成功)。纯查询;已做单元测试。
static func _main_scene_missing() -> bool:
	return str(ProjectSettings.get_setting("application/run/main_scene", "")).strip_edges().is_empty()


static func cmd_game_stop(_parameters: Dictionary) -> Dictionary:
	var was_running := EditorInterface.is_playing_scene()
	EditorInterface.stop_playing_scene()
	return MCPToolkitSuccess.ok({"was_running": was_running})


# -- 运行时探测 ------------------------------------------------------------------


## WebSocket 健康检查:连接、鉴权、发送 ping、等待响应。
## 只有完整的 JSON-RPC 层可运作时才报告 true(不会出现
## 仅 TCP 探测带来的假阳性)。
## 成功时返回 {"ready": true},否则返回 {"ready": false, "failure": <stage>},
## 其中 stage 是以下之一:token_read_failed, ws_connect_timeout, auth_timeout,
## ping_timeout。
static func _poll_runtime_ready(
	host: String, port: int, timeout_ms: int,
) -> Dictionary:
	var token_path := MCPAuth.get_token_path()
	var token_file := FileAccess.open(token_path, FileAccess.READ)
	if token_file == null:
		return {"ready": false, "failure": "token_read_failed"}
	var token := token_file.get_as_text().strip_edges()
	token_file.close()
	if token.is_empty():
		return {"ready": false, "failure": "token_read_failed"}

	var furthest := "ws_connect_timeout"
	var deadline := Time.get_ticks_msec() + timeout_ms
	while Time.get_ticks_msec() < deadline:
		var ws := WebSocketPeer.new()
		var err := ws.connect_to_url("ws://%s:%d" % [host, port])
		if err != OK:
			await Engine.get_main_loop().create_timer(0.1).timeout
			continue

		var auth_sent := false
		var ping_sent := false
		while Time.get_ticks_msec() < deadline:
			ws.poll()
			var state := ws.get_ready_state()
			if state == WebSocketPeer.STATE_CLOSED \
					or state == WebSocketPeer.STATE_CLOSING:
				break
			if state != WebSocketPeer.STATE_OPEN:
				await Engine.get_main_loop().create_timer(0.01).timeout
				continue

			if furthest == "ws_connect_timeout":
				furthest = "auth_timeout"

			if not auth_sent:
				ws.send_text(JSON.stringify({"auth": token}))
				auth_sent = true
				await Engine.get_main_loop().create_timer(0.01).timeout
				continue

			while ws.get_available_packet_count() > 0:
				var text := ws.get_packet().get_string_from_utf8()
				var parser := JSON.new()
				if parser.parse(text) != OK:
					continue
				var msg = parser.data
				if typeof(msg) != TYPE_DICTIONARY:
					continue
				if not ping_sent:
					if msg.get("authed", false) == true:
						ws.send_text(JSON.stringify({
							"jsonrpc": "2.0", "method": "ping", "id": 0}))
						ping_sent = true
						furthest = "ping_timeout"
				else:
					if msg.has("result"):
						ws.close(1000)
						return {"ready": true}

			await Engine.get_main_loop().create_timer(0.01).timeout

		ws.close()
		await Engine.get_main_loop().create_timer(0.1).timeout
	return {"ready": false, "failure": furthest}


## 扫描 LogBuffer 中的近期错误 — 用于在游戏
## 启动失败时检测编译失败。
static func _scan_compilation_errors() -> Dictionary:
	var buf := Modules.LogBuffer.get_entries(10, ["error"])
	var errors: Array = []
	for entry in buf.get("entries", []):
		errors.append(str(entry.get("message", "")))
	return {
		"found": errors.size() > 0,
		"errors": errors,
		"source": "logger" if Modules.LogBuffer.uses_logger_api() else "file_tail",
	}


## 结合日志扫描结果以及是否观察到游戏在运行,分阶段诊断
## 运行时探测为何没发现活动游戏。
##
## 对其输入是纯函数,因此整个决策可在无头下做单元测试。接收:
## 来自 [method _scan_compilation_errors] 的 [param comp] 扫描字典
## ([code]{"found", "errors", "source"}[/code])、[param was_running](本次调用中
## 观察到游戏在运行时为 true — 即软性的"先运行后死掉"路径;从未确认有任何东西
## 在运行时为 false — 即硬性的"从未启动"路径),以及运行中引擎的
## [param version_pair](如 [code]"4.5"[/code]),用于文件尾随注意事项。
## 返回 [code]{"code", "message", "hint", "errors"}[/code]。[br]
## [br]
## 该定性只在实际掌握错误([code]found[/code])且游戏从未运行时才声称是编译失败:
## 空缓冲区不能证明任何关于编译的事(冷启动的解析错误可能尚未落盘,
## 且 4.2–4.4 的文件尾随模式可能完全漏掉解析错误),因此空缓冲区只陈述
## [code]GAME_NOT_RUNNING[/code] 这一事实,并同时给出两种假设,而不是断言
## 原因。运行后停止的游戏被定性为运行时崩溃,而非编译
## 错误
## (它已开始运行)。
static func _diagnose_startup(
	comp: Dictionary, was_running: bool, version_pair: String,
) -> Dictionary:
	# 在 JSON 形状的扫描边界处 comp[...] 是 Variant → 一律强转,绝不推断。
	var found := bool(comp.get("found", false))
	var errors: Array = comp.get("errors", [])
	var is_file_tail := str(comp.get("source", "")) == "file_tail"
	# 文件尾随信号按版本门控(4.2–4.4);只在该情形点名版本,让代理
	# 知道空缓冲区可能是捕获能力所限,而非干净的启动。
	var file_tail_caveat := (
		" (Godot %s tails the log file and can miss compile errors.)" % version_pair
		if is_file_tail else ""
	)

	if found:
		var joined := "\n".join(errors)
		if was_running:
			return {
				"code": "GAME_NOT_RUNNING",
				"message": "Game stopped mid-probe with errors captured — likely a runtime crash:\n" + joined,
				"hint": "The game began running, so a compile error is unlikely. Check log_read(channel:'auto') for the crash." + file_tail_caveat,
				"errors": errors,
			}
		return {
			"code": "COMPILATION_FAILED",
			"message": "Game failed to start — likely a startup or parse error. Recent errors:\n" + joined,
			"hint": "Fix the errors shown, then call game_start again." + file_tail_caveat,
			"errors": errors,
		}

	if was_running:
		return {
			"code": "GAME_NOT_RUNNING",
			"message": "Game started then stopped before the probe completed.",
			"hint": "Likely a runtime crash or early exit (it began running, so a compile error is unlikely). Check log_read(channel:'auto')." + file_tail_caveat,
			"errors": errors,
		}
	return {
		"code": "GAME_NOT_RUNNING",
		"message": "No running game to probe.",
		"hint": (
			"Either (a) nothing launched yet / still cold-starting — call game_start (without runtime_poll), "
			+ "or retry runtime_poll:true shortly; or (b) it failed to compile. To surface a compile error not "
			+ "in the buffer, call editor_sync then log_read(channel:'editor')." + file_tail_caveat
		),
		"errors": errors,
	}
