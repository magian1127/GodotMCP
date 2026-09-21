@tool
extends VBoxContainer
## 停靠面板(dock)的“服务器状态”子面板——实时服务器/运行时/LSP/活动标签。
##
## 一个停靠面板子面板。由 dock.gd 构建并持有；加入停靠面板的状态卡片
## （因此编辑器会随停靠面板一起释放它）。只构建一次状态标签，
## 并拥有其更新逻辑：监听地址 + 来源（或未监听的警告状态）、连接数、
## 运行时端口（试玩测试(playtest)期间轮询，带有“无法连接”升级提示）、
## LSP 端点/冲突，以及最近活动行。每个值都读取自绑定的服务器；
## 每次刷新都就地修改现有标签——绝不重建
## （它会在停靠面板的 1 秒定时器和每个服务器事件上重绘，
## 因此“只构建一次”很重要）。
##
## 编排(orchestration)仍由停靠面板负责：它把服务器信号路由到这里以更新标签
## （refresh / refresh_runtime / refresh_lsp / set_peer_count / set_activity），
## 同时自己持有连接/断开的弹出提示(toast)
## 以及对其他子面板（.mcp.json + 失焦响应）的扇出(fan-out)。
## 共享的 .mcp.json 警告面板在视觉上位于状态行与运行时标签之间，
## 因此停靠面板通过 insert_warning_panel() 把它挂到这里
## ——放置位置归本面板，但警告的行为仍归停靠面板 + .mcp.json 面板。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const RegistryClient = Modules.RegistryClient
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 正在运行的试玩测试(playtest)在多长时间内没有发布运行时端口后，
# 运行时标签会从“等待中”升级为“无法连接”。覆盖游戏启动 + 自动加载(Autoload)
# 绑定 + 注册表写入的时间，外加固定端口的宽限期（约 5 秒）——
# 编辑器无法直接看到子进程的绑定失败（没有 IPC），
# 因此宽限期之后的缺席就是这种廉价的替代判断依据。
const _RUNTIME_REACH_GRACE_MS := 10000

# 每个标签（监听/端口/连接/运行时/LSP）都读取服务器；
# 持有它是为了让各刷新方法无需停靠面板再次传入即可重绘。
var _server: Node = null

# 状态标签——在 _init() 中一次性构建，由下方各刷新方法就地修改。
var _status_label: Label = null
var _peer_label: Label = null
var _activity_label: Label = null
var _runtime_label: Label = null
var _lsp_label: Label = null
# 未监听警告——固定端口冲突、扫描区间耗尽或端口配置错误。
# 由 refresh() 依据服务器的 get_port_warning() 重绘；激活前保持隐藏。
# 首次观察到当前试玩测试正在运行的时刻（毫秒）——驱动运行时标签
var _port_warning_label: Label = null
# 从“等待中”升级为“无法连接”。没有试玩测试运行时为 -1。
# 未监听警告——放置在状态行正下方，使绑定失败
var _playtest_seen_ms: int = -1


func _init(server: Node) -> void:
	_server = server

	var status_row := HBoxContainer.new()
	add_child(status_row)

	_status_label = Label.new()
	_status_label.text = EditorLocale.pick("... starting", "……正在启动")
	_status_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	status_row.add_child(_status_label)

	_peer_label = Label.new()
	_peer_label.text = EditorLocale.pick("0 peers", "0 个连接")
	status_row.add_child(_peer_label)

	# （固定端口被占用、扫描区间耗尽或端口环境变量有误）
	# 无需滚动即可见。
	# .mcp.json 警告面板由停靠面板通过 insert_warning_panel() 插入到此处
	_port_warning_label = Label.new()
	_port_warning_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_port_warning_label.add_theme_color_override("font_color", EditorLocale.warning_color())
	_port_warning_label.add_theme_font_size_override("font_size", 12)
	_port_warning_label.visible = false
	add_child(_port_warning_label)

	# （索引 1），使其位于状态行与下方各标签之间
	# ——停靠面板拥有其行为，本面板只承载其放置位置。
	# 服务器为该编辑器发现的 GDScript LSP 端点（尽力而为；

	_runtime_label = Label.new()
	_runtime_label.text = EditorLocale.pick("Runtime: not running", "运行时：未运行")
	_runtime_label.add_theme_font_size_override("font_size", 13)
	add_child(_runtime_label)

	# 冲突判定以服务器为准）。
	# 将停靠面板的共享 .mcp.json 警告面板托管在状态行与运行时标签之间
	_lsp_label = Label.new()
	_lsp_label.text = EditorLocale.pick("LSP: —", "LSP：—")
	_lsp_label.add_theme_font_size_override("font_size", 12)
	_lsp_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	add_child(_lsp_label)

	_activity_label = Label.new()
	_activity_label.text = EditorLocale.pick("Last activity: —", "最近活动：—")
	_activity_label.add_theme_font_size_override("font_size", 12)
	_activity_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	add_child(_activity_label)


## （其原有位置）。停靠面板创建并驱动该面板；本方法只负责放置它（索引 1），
## 使编辑器会随停靠面板一起释放它。
## 刷新——状态 + 连接 + 运行时 + LSP（全部读取服务器、就地修改标签）
func insert_warning_panel(panel: Control) -> void:
	add_child(panel)
	move_child(panel, 1)


# ---------------------------------------------------------------------------
# 依据当前服务器/注册表状态重绘所有状态标签——包括主状态行的
# ---------------------------------------------------------------------------


## “监听中/未监听”样式，以及未监听警告
## （二者都源自服务器的实时监听状态，因此任何未绑定阶段都会显示）。
## 幂等重绘——足够廉价，可在每次刷新触发时运行。
## 面板尚未进入场景树时调用也安全——遇到 null 会提前返回。
## 未监听——警告样式，并在原因已知时显示简明原因
func refresh() -> void:
	if _server == null or _status_label == null:
		return
	if _server.is_listening():
		var port: int = _server.get_bound_port()
		var source: String = _server.get_port_source()
		var localized_source := _localized_port_source(source)
		var source_suffix := " (%s)" % localized_source if not localized_source.is_empty() else ""
		_status_label.text = EditorLocale.pick(
			"Listening on 127.0.0.1:%d%s",
			"正在监听 127.0.0.1:%d%s") % [port, source_suffix]
		_status_label.remove_theme_color_override("font_color")
	else:
		# （固定端口被占用 / 扫描区间耗尽 / 配置无效）。
		# 依据试玩测试(playtest)/运行时端口状态重绘运行时标签。
		var warning: Dictionary = _server.get_port_warning()
		if bool(warning.get("active", false)):
			_status_label.text = EditorLocale.pick(
				"Not listening — %s", "未监听 — %s") % str(warning.get("label", ""))
		else:
			_status_label.text = EditorLocale.pick("Not listening", "未监听")
		_status_label.add_theme_color_override("font_color", EditorLocale.warning_color())
	_update_port_warning()
	var count: int = _server.get_authed_peer_count()
	_peer_label.text = (
		"%d 个连接" % count
		if EditorLocale.is_chinese_editor()
		else "%d peer%s" % [count, "" if count == 1 else "s"]
	)
	refresh_runtime()
	refresh_lsp()


## 在停靠面板的 1 秒定时器上调用，使其在试玩测试期间无需服务器事件即可更新。
## 编辑器无法看到子游戏的绑定失败（没有 IPC），
## 因此超过宽限期仍未发布运行时端口的试玩测试会升级为“无法连接”警告，
## 引导用户查看游戏控制台
## （子进程自身的 push_error 输出就在那里）。
## LSP 状态指示器。上下文协议(MCP)服务器上报权威判定
func refresh_runtime() -> void:
	if _runtime_label == null:
		return
	if EditorInterface.is_playing_scene():
		if _playtest_seen_ms < 0:
			_playtest_seen_ms = Time.get_ticks_msec()
		var rt_port := RegistryClient.get_runtime_port()
		if rt_port > 0:
			_runtime_label.text = EditorLocale.pick(
				"Runtime: listening on 127.0.0.1:%d",
				"运行时：正在监听 127.0.0.1:%d") % rt_port
			_runtime_label.add_theme_color_override("font_color", EditorLocale.success_color())
		elif Time.get_ticks_msec() - _playtest_seen_ms > _RUNTIME_REACH_GRACE_MS:
			_runtime_label.text = EditorLocale.pick(
				"Runtime: not reachable — check the game console",
				"运行时：无法连接 — 请检查游戏控制台")
			_runtime_label.add_theme_color_override("font_color", EditorLocale.warning_color())
		else:
			_runtime_label.text = EditorLocale.pick(
				"Runtime: game running, waiting for port...",
				"运行时：游戏正在运行，等待端口……")
			_runtime_label.add_theme_color_override("font_color", EditorLocale.warning_color())
	else:
		_playtest_seen_ms = -1
		_runtime_label.text = EditorLocale.pick(
			"Runtime: not running (start playtest with F5)",
			"运行时：未运行（按 F5 开始运行项目）")
		_runtime_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())


## （editor.set_lsp_status）——编辑器无法读取自己的 LSP 绑定状态，
## 且引擎内的跨进程存活检测在 Windows 上不可靠，
## 因此停靠面板渲染服务器判定的结果。
## 在上下文协议服务器连接之前，回退(fallback)显示已配置（已发布）的端点。
## 停靠面板把 server.lsp_status_changed 路由到这里。
func refresh_lsp() -> void:
	if _lsp_label == null:
		return
	var st: Dictionary = {}
	if _server != null and _server.has_method("get_reported_lsp_status"):
		st = _server.get_reported_lsp_status()
	if not st.is_empty() and st.has("state"):
		var host := str(st.get("host", "127.0.0.1"))
		var port := int(st.get("port", 6005))
		match str(st.get("state", "")):
			"active":
				_lsp_label.text = EditorLocale.pick(
					"LSP: %s:%d · active", "LSP：%s:%d · 活跃") % [host, port]
				_lsp_label.add_theme_color_override("font_color", EditorLocale.success_color())
				_lsp_label.tooltip_text = EditorLocale.pick(
					"This editor owns the GDScript LSP port (reported by the MCP server).",
					"此编辑器占用该 GDScript LSP 端口（由 MCP 服务器报告）。")
			"conflict":
				_lsp_label.text = EditorLocale.pick(
					"LSP: %d ⚠ conflict — another editor owns this port",
					"LSP：%d ⚠ 冲突 — 该端口已被另一编辑器占用") % port
				_lsp_label.add_theme_color_override("font_color", EditorLocale.warning_color())
				_lsp_label.tooltip_text = EditorLocale.pick(
					"Another editor owns the machine-wide GDScript LSP port, so this editor's "
						+ "LSP tools are unavailable. Give each editor a distinct --lsp-port + "
						+ "GODOT_MCP_LSP_PORT. See docs/multi-instance.md.",
					"另一编辑器占用了全局 GDScript LSP 端口，因此本编辑器的 LSP 工具不可用。"
						+ "请为每个编辑器设置不同的 --lsp-port 和 GODOT_MCP_LSP_PORT；"
						+ "详情参阅 docs/multi-instance.zh-CN.md。")
			_:  # "unavailable" / unknown
				_lsp_label.text = EditorLocale.pick(
					"LSP: %d ⚠ unavailable", "LSP：%d ⚠ 不可用") % port
				_lsp_label.add_theme_color_override("font_color", EditorLocale.warning_color())
				_lsp_label.tooltip_text = str(st.get(
					"detail", EditorLocale.pick(
						"GDScript LSP not reachable.", "无法连接 GDScript LSP。")))
		return
	# 尚无服务器上报——显示已配置（已发布）的端点。
	var ep := RegistryClient.get_lsp_endpoint()
	if ep.is_empty():
		_lsp_label.text = EditorLocale.pick("LSP: —", "LSP：—")
		_lsp_label.tooltip_text = ""
		_lsp_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())
		return
	_lsp_label.text = EditorLocale.pick(
		"LSP: %s:%d (editor setting · awaiting MCP server)",
		"LSP：%s:%d（编辑器设置 · 等待 MCP 服务器）") % [ep["host"], ep["port"]]
	_lsp_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	_lsp_label.tooltip_text = EditorLocale.pick(
		"Configured GDScript LSP port (the editor setting). If this editor was launched "
			+ "with --lsp-port the actual port differs — Godot doesn't expose it to the plugin, "
			+ "so the MCP server reports the real port (and owner/conflict status) on connect.",
		"已配置的 GDScript LSP 端口（编辑器设置）。如果编辑器使用 --lsp-port 启动，"
			+ "实际端口可能不同；Godot 不会把它暴露给插件，因此 MCP 服务器会在连接时"
			+ "报告真实端口及其占用/冲突状态。")


# ---------------------------------------------------------------------------
# 定向标签更新——停靠面板把特定服务器信号路由到这里，
# 使连接/断开/命令无需完整刷新即可更新连接数 + 活动
# （保留现有信号驱动行为）。弹出提示(toast)仍由停靠面板持有。
# ---------------------------------------------------------------------------


## 设置连接数标签（停靠面板把 client_connected/disconnected 路由到这里）。
func set_peer_count(peer_count: int) -> void:
	if _peer_label != null:
		_peer_label.text = (
			"%d 个连接" % peer_count
			if EditorLocale.is_chinese_editor()
			else "%d peer%s" % [peer_count, "" if peer_count == 1 else "s"]
		)


## 设置最近活动标签（停靠面板把连接/断开/命令路由到这里）。
func set_activity(text: String) -> void:
	if _activity_label != null:
		_activity_label.text = text


# 依据服务器的 get_port_warning() 结果显示或隐藏未监听警告
# ——固定端口冲突、扫描区间耗尽或端口配置错误。
# 作为 refresh() 的一部分，使警告与状态行样式
# 总是在同一次处理中源自相同的服务器状态。
func _update_port_warning() -> void:
	if _port_warning_label == null or _server == null:
		return
	var state: Dictionary = _server.get_port_warning()
	var active := bool(state.get("active", false))
	_port_warning_label.visible = active
	if active:
		_port_warning_label.text = "⚠️ " + str(state.get("message", ""))


func _localized_port_source(source: String) -> String:
	if not EditorLocale.is_chinese_editor():
		return source
	match source:
		"default":
			return "默认"
		"pinned":
			return "固定端口"
		"band":
			return "扫描范围"
		_:
			return source
