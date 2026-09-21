@tool
extends VBoxContainer
## 上下文协议(MCP)底部停靠面板(dock)——信号驱动的状态 + 轮询式运行时标签。
##
## 由 plugin.gd 创建并绑定。服务器状态是信号驱动的（没有轮询延迟）；
## 一个轻量定时器在试玩测试(playtest)期间轮询运行时标签，
## 使其无需服务器事件即可更新。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const NodejsCheck = Modules.NodejsCheck
const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
const ToolkitDialogPresenter := preload("res://addons/godot_mcp_toolkit/ui/toolkit_dialog_presenter.gd")
const DockSectionCard := preload("res://addons/godot_mcp_toolkit/ui/dock/dock_section_card.gd")
const DockStatusPanel := preload("res://addons/godot_mcp_toolkit/ui/dock/status/dock_status_panel.gd")
const DockLimitsSection := preload("res://addons/godot_mcp_toolkit/ui/dock/limits/dock_limits_section.gd")
const DockUnfocusedControl := preload("res://addons/godot_mcp_toolkit/ui/dock/limits/dock_unfocused_control.gd")
const DockAuditSection := preload("res://addons/godot_mcp_toolkit/ui/dock/security/dock_audit_section.gd")
const DockMcpJsonPanel := preload("res://addons/godot_mcp_toolkit/ui/dock/mcp/dock_mcp_json_panel.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 弹出提示(toast)严重级别常量（与 EditorToaster.Severity 一致）。
const _TOAST_INFO := 0
const _TOAST_WARNING := 1
const _TOAST_ERROR := 2

var _server: Node = null
var _audit_path: String = ""

# 服务器状态分区——监听/连接/运行时/LSP/活动标签（独立子面板）。
var _status_panel: DockStatusPanel = null

# .mcp.json 健康状态入口——共享的缺失/格式错误/只读警告面板 +
# 三态底栏按钮 + 只读缓存 + 写入/打开/修复流程（独立子面板）。
var _mcp_json_panel: DockMcpJsonPanel = null

# 失焦响应模式——可选开启的开关 + 三态指示器（独立子面板）。
var _unfocused_control: DockUnfocusedControl = null

# 审计日志分区——设置 + 查看/清空 + 延迟创建的日志查看器（独立子面板）。
var _audit_section: DockAuditSection = null

# Node.js 警告。
var _nodejs_status_warning: Label = null

# 注入的横切协作者（由组装器(composer)持有）：共享的 .mcp.json 写入流程
# 与编辑器全局对话框呈现器(presenter)。停靠面板只是消费它们——
# 底栏按钮 + .mcp.json 面板——与工具菜单和引导向导完全一样；
# 它从不拥有这些行为。
var _write_flow: MCPJsonWriteFlow = null
var _dialog_presenter: ToolkitDialogPresenter = null

# 试玩测试(playtest)期间用于轮询运行时状态的轻量定时器。
var _runtime_timer: Timer = null


func bind(
		server: Node, audit_path: String,
		write_flow: MCPJsonWriteFlow, dialog_presenter: ToolkitDialogPresenter) -> void:
	_server = server
	_audit_path = audit_path
	_write_flow = write_flow
	_dialog_presenter = dialog_presenter
	_server.client_connected.connect(_on_client_connected)
	_server.client_disconnected.connect(_on_client_disconnected)
	_server.command_received.connect(_on_command_received)
	# LSP 判定经由 editor.set_lsp_status 到达（一条命令，而不是停靠面板信号）；
	# 在服务器设置它的那一刻刷新，使标签永不过期。
	_server.lsp_status_changed.connect(_on_lsp_status_changed)
	# 监听状态（新绑定、固定端口/扫描区间冲突、端口配置错误）——
	# 除下方初次拉取外，在服务器状态变化的瞬间重绘。
	_server.port_status_changed.connect(_on_port_status_changed)
	_refresh_status()


func _ready() -> void:
	_build_ui()
	# bind() 在 _ready 之前运行（节点尚未进入场景树），
	# 因此其中的刷新调用会因控件为 null 而提前返回。现在界面已存在，重新执行一次。
	_refresh_status()
	# 单个 1 秒定时器，在 _on_runtime_timer_timeout 中扇出(fan-out)到运行时标签
	# （试玩测试端口发现 / 试玩测试结束）与 .mcp.json 面板的文件有效性刷新；
	# 只读始终保持服务器同步（见 _on_client_connected），绝不轮询。
	_runtime_timer = Timer.new()
	_runtime_timer.wait_time = 1.0
	_runtime_timer.timeout.connect(_on_runtime_timer_timeout)
	add_child(_runtime_timer)
	_runtime_timer.start()


# ---------------------------------------------------------------------------
# 界面(UI)构建（纯代码——全部为动态内容）
# ---------------------------------------------------------------------------


func _build_ui() -> void:
	var scale := EditorInterface.get_editor_scale()
	add_theme_constant_override("separation", int(4 * scale))

	# == 状态分区（紧凑，固定在顶部） ==============================
	var status_section := DockSectionCard.make_section(EditorLocale.pick("Server Status", "服务器状态"))
	status_section.size_flags_vertical = 0  # 固定高度
	add_child(status_section)
	var sc: VBoxContainer = status_section.get_meta("content")

	# 服务器状态标签（监听/连接/运行时/LSP/活动）——独立子面板。
	# 停靠面板把服务器信号路由给它以更新标签；它自行读取服务器。
	_status_panel = DockStatusPanel.new(_server)
	sc.add_child(_status_panel)

	# .mcp.json 健康面板——共享的缺失/格式错误/只读警告位于服务器状态行之后
	# （位置醒目）；它的三态按钮放在底栏（放置 = 编排者），
	# 但由本面板驱动（含义）。现在就创建按钮以便面板绑定；
	# 底栏稍后为其定位。警告面板托管在状态面板内部
	# （状态行与运行时标签之间）——放置归状态面板，
	# 行为仍留在这里。
	var mcp_json_btn := Button.new()
	mcp_json_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_mcp_json_panel = DockMcpJsonPanel.new(mcp_json_btn, Callable(self, "_toast"), _write_flow)
	_status_panel.insert_warning_panel(_mcp_json_panel)

	# Node.js 可用性——共享检测。
	var node_check := NodejsCheck.check()
	var nodejs_msg := ""
	if not node_check["found"]:
		var _path_hint := ""
		if OS.get_name() == "Windows":
			_path_hint = EditorLocale.pick(
				"\nIf Node.js is installed, ensure it is on your system PATH.",
				"\n如果已经安装 Node.js，请确认它已加入系统 PATH。")
		elif OS.get_name() == "macOS":
			# 从 Finder/Dock 启动的应用可能找不到版本管理器安装的 Node
			# （不在 PATH 中）；从终端启动则会继承 shell 的 PATH。
			_path_hint = EditorLocale.pick(
				"\nIf Node.js is installed, ensure it is on your PATH. A "
					+ "version-manager Node (nvm/fnm/Homebrew) may need you to launch the "
					+ "editor and MCP client from a terminal. See the bundled local "
					+ "advanced-configuration guide.",
				"\n如果已经安装 Node.js，请确认它已加入 PATH。通过 nvm、fnm 或 Homebrew "
					+ "安装的 Node 可能需要从终端启动编辑器和 MCP 客户端。"
					+ "详情见随插件提供的本地高级配置文档。")
		nodejs_msg = EditorLocale.pick(
			"Node.js not found — the local MCP server bridge requires Node.js 22+. "
				+ "See the bundled advanced-configuration guide.",
			"未找到 Node.js — 本地 MCP 服务器桥接需要 Node.js 22 或更高版本。"
				+ "请查看随插件提供的高级配置文档。") + _path_hint
	elif not node_check["meets_minimum"]:
		nodejs_msg = EditorLocale.pick(
			"Node.js %s found but 22+ is required. See the bundled local setup guide.",
			"检测到 Node.js %s，但需要 22 或更高版本。请查看随插件提供的本地安装说明。"
		) % str(node_check["version"])
	_nodejs_status_warning = Label.new()
	_nodejs_status_warning.text = nodejs_msg
	_nodejs_status_warning.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_nodejs_status_warning.add_theme_color_override("font_color", EditorLocale.warning_color())
	_nodejs_status_warning.add_theme_font_size_override("font_size", 11)
	_nodejs_status_warning.visible = nodejs_msg != ""
	sc.add_child(_nodejs_status_warning)

	# 失焦响应模式——可选开启的开关 + 三态指示器子面板。
	# （位于编辑器设置而非项目设置——这是一项按用户、全机器范围的偏好，
	# 绝不能写进 project.godot / 版本控制(VCS)。）
	# 该面板拥有设置的读写 + 指示器；停靠面板把刷新路由给它。
	# 应用设置（具备冲突感知/崩溃安全的恢复）在服务器端完成；
	# 面板直接调用绑定的服务器，因此无需向停靠面板回发信号。
	_unfocused_control = DockUnfocusedControl.new(_server)
	sc.add_child(_unfocused_control)

	# == 可折叠分区（标题始终可见） =========================
	var sections_vbox := VBoxContainer.new()
	sections_vbox.size_flags_vertical = Control.SIZE_EXPAND_FILL
	sections_vbox.add_theme_constant_override("separation", int(2 * scale))
	add_child(sections_vbox)

	# -- 审计日志分区（默认折叠） -----------------------------
	# 该面板拥有审计设置 + 查看/清空按钮 + 延迟创建的日志查看器；
	# 停靠面板注入自己的弹出提示(toast)，使面板无需依赖 toaster。
	var ac := DockSectionCard.make_collapsible(
		sections_vbox, EditorLocale.pick("Audit Log", "审计日志"), false)
	_audit_section = DockAuditSection.new(_audit_path, Callable(self, "_toast"))
	ac.add_child(_audit_section)

	# -- 安全与响应上限分区（默认折叠） ------------
	var lc := DockSectionCard.make_collapsible(
		sections_vbox, EditorLocale.pick("Security & Response Limits", "安全与响应上限"), false)
	var limits_section := DockLimitsSection.new()
	limits_section.regenerate_token_requested.connect(_on_regen_token)
	lc.add_child(limits_section)

	# == 底栏（固定在底部） ============================================
	var footer := PanelContainer.new()
	footer.add_theme_stylebox_override("panel", DockSectionCard.make_section_style())
	footer.size_flags_vertical = Control.SIZE_SHRINK_END
	add_child(footer)

	var footer_row := HBoxContainer.new()
	footer.add_child(footer_row)

	var skills_btn := Button.new()
	skills_btn.text = EditorLocale.pick("Companion Skills", "配套技能")
	skills_btn.tooltip_text = EditorLocale.pick(
		"Open Companion Skills folder", "打开配套技能文件夹")
	skills_btn.pressed.connect(_open_companion_skills)
	skills_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	footer_row.add_child(skills_btn)

	var extensions_btn := Button.new()
	extensions_btn.text = EditorLocale.pick("Extensions", "扩展")
	extensions_btn.tooltip_text = EditorLocale.pick(
		"Browse Godot MCP Unified extensions", "浏览 Godot MCP Unified 扩展")
	extensions_btn.pressed.connect(_on_extensions_pressed)
	extensions_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	footer_row.add_child(extensions_btn)

	# 在上方（状态分区）创建，以便 _mcp_json_panel 绑定它；底栏只负责放置。
	# 标签/提示(tooltip)/颜色/动作均由面板驱动。
	footer_row.add_child(mcp_json_btn)

	var info_btn := Button.new()
	info_btn.text = EditorLocale.pick("Info / Help", "信息 / 帮助")
	info_btn.pressed.connect(_on_info_pressed)
	info_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	footer_row.add_child(info_btn)



# ---------------------------------------------------------------------------
# 信号处理器——服务器事件（无轮询）
# ---------------------------------------------------------------------------

func _on_client_connected(peer_count: int) -> void:
	# 状态标签位于状态面板中；停靠面板保留弹出提示(toast) +
	# 对其他子面板的扇出(fan-out)（它知道哪个面板响应哪个事件）。
	if _status_panel != null:
		_status_panel.set_peer_count(peer_count)
		_status_panel.set_activity(EditorLocale.pick(
			"Last activity: client connected", "最近活动：客户端已连接"))
	_toast(
		"MCP 客户端已连接（%d 个连接）" % peer_count
		if EditorLocale.is_chinese_editor()
		else "MCP client connected (%d peer%s)" % [
			peer_count, "" if peer_count == 1 else "s"])
	# （重新）连接的上下文协议服务器刚刚从其环境变量中读取了 GODOT_MCP_READ_ONLY，
	# 因此这正是只读生效的时刻——同步徽标(badge)使其一致。
	# 注意：已连接的服务器会保留其启动时的只读设定，
	# 不会因之后编辑 .mcp.json 而切换（服务器在启动时只读取一次环境变量）。
	# 徽标反映最近一次连接的 .mcp.json；仍在运行的更早服务器
	# （多客户端）要等它们自己重连后才会切换。
	if _mcp_json_panel != null:
		_mcp_json_panel.sync_read_only_state()
	if _unfocused_control != null:
		_unfocused_control.refresh()


func _on_client_disconnected(peer_count: int) -> void:
	if _status_panel != null:
		_status_panel.set_peer_count(peer_count)
		_status_panel.set_activity(EditorLocale.pick(
			"Last activity: client disconnected", "最近活动：客户端已断开"))
	if peer_count == 0:
		_toast(EditorLocale.pick("MCP client disconnected", "MCP 客户端已断开"))
	if _unfocused_control != null:
		_unfocused_control.refresh()


func _on_command_received(method: String) -> void:
	if _status_panel != null:
		_status_panel.set_activity(EditorLocale.pick(
			"Last activity: %s", "最近活动：%s") % method)


# LSP 判定信号——路由到状态面板的 LSP 标签（服务器驱动，永不过期）。
# 独立的处理器使 bind() 能在 _build_ui 存在之前完成连接。
func _on_lsp_status_changed() -> void:
	if _status_panel != null:
		_status_panel.refresh_lsp()


# 监听状态信号（新绑定 / 冲突出现或解除 / 配置错误）——
# 重绘状态面板，由它在一次处理中从服务器推导出
# 状态行样式与未监听警告。独立的处理器使 bind()
# 能在 _build_ui 存在之前完成连接。
func _on_port_status_changed() -> void:
	if _status_panel != null:
		_status_panel.refresh()


# 1 秒轮询——一个定时器扇出(fan-out)到运行时标签（试玩测试端口发现 /
# 试玩测试结束）与 .mcp.json 面板的文件有效性刷新
# （文件存在 / 格式错误 → 按钮模式 + 警告）。二者都是可安全轮询的实时事实(FACT)；
# 只读始终保持服务器同步（见 _on_client_connected），绝不在该定时器上处理。
func _on_runtime_timer_timeout() -> void:
	if _status_panel != null:
		_status_panel.refresh_runtime()
	if _mcp_json_panel != null:
		_mcp_json_panel.refresh()


# ---------------------------------------------------------------------------
# 状态刷新——轻量扇出（由编排者(orchestrator)决定何时刷新什么）
# ---------------------------------------------------------------------------

func _refresh_status() -> void:
	if _status_panel != null:
		_status_panel.refresh()
	if _mcp_json_panel != null:
		_mcp_json_panel.sync_read_only_state()
	if _unfocused_control != null:
		_unfocused_control.refresh()


# ---------------------------------------------------------------------------
# 审计日志弹窗
# ---------------------------------------------------------------------------

# 轻量委托——审计对话框现在位于 _audit_section 中。保持公开，
# 使工具菜单(tool_menu.gd)仍能经由停靠面板打开日志。
func show_audit_dialog() -> void:
	if _audit_section != null:
		_audit_section.show_dialog()


# ---------------------------------------------------------------------------
# 令牌重新生成
# ---------------------------------------------------------------------------

func _on_regen_token() -> void:
	if _server != null and _server.has_method("regenerate_token"):
		_server.regenerate_token()
		_toast(EditorLocale.pick("MCP token rotated", "MCP 令牌已轮换"))


# ---------------------------------------------------------------------------
# 配套技能(skill)
# ---------------------------------------------------------------------------

func _open_companion_skills() -> void:
	var skills_dir := "res://addons/godot_mcp_toolkit/CompanionSkills"
	var global_path := ProjectSettings.globalize_path(skills_dir)
	OS.shell_open(global_path)


# ---------------------------------------------------------------------------
# 底栏对话框——注入的对话框呈现器(presenter)的轻量消费者
# ---------------------------------------------------------------------------

func _on_extensions_pressed() -> void:
	if _dialog_presenter != null:
		_dialog_presenter.show_extension_catalog()


func _on_info_pressed() -> void:
	if _dialog_presenter != null:
		_dialog_presenter.show_info(_server)


# ---------------------------------------------------------------------------
# 弹出提示(toast)辅助方法
# ---------------------------------------------------------------------------

func _toast(msg: String, severity: int = _TOAST_INFO, tooltip_text: String = "") -> void:
	if severity >= _TOAST_WARNING:
		push_warning("[MCP] %s" % msg)
	else:
		print("[MCP] %s" % msg)
	# EditorToaster 在 Godot 4.4+ 可用（为兼容性使用动态分发）。
	var toaster = Modules.EditorAccess.get_toaster()
	if toaster != null:
		toaster.push_toast(msg, severity, tooltip_text)
