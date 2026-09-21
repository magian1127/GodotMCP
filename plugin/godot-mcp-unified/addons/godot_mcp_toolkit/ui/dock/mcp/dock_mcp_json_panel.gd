@tool
extends PanelContainer
## 停靠面板(dock)的“.mcp.json 健康状态”子面板——共享的缺失/格式错误/只读
## 警告、三态底栏按钮，以及写入/打开/修复流程。
##
## 一个停靠面板子面板。由 dock.gd 构建并持有；这个 PanelContainer 本身就是
## 共享警告面板，并被加入停靠面板的状态卡片（因此编辑器会随停靠面板一起
## 释放它）。它拥有停靠面板的 .mcp.json 交互(UX)集合：警告面板 + 标签
## （在此一次性构建）、三态底栏按钮（由底栏构建并注入，底栏负责放置位置，
## 本面板负责按钮的含义）、与服务器同步的只读缓存，
## 以及周期性的文件有效性轮询。写入委托给注入的共享写入流程
## （由它负责覆盖确认及其“打开 .mcp.json”补救路径）；
## 本面板只保留自己的结果接收端
## （弹出提示(toast) + 只读状态重新同步）。
##
## 两个刷新触发点，刻意区分（保留 68fb6eb 的模型）：
##   * 文件的有效性/是否存在是实时事实(FACT)——由停靠面板 1 秒定时器的
##     扇出(fan-out)刷新（refresh()：写入-打开-修复三种按钮模式 + 缺失/格式错误警告）。
##   * 只读是服务器状态——仅在服务器（重）连接 + 启动时 + 停靠面板写入之后
##     同步（sync_read_only_state()），绝不在定时器中同步：
##     服务器在启动时只读取一次 GODOT_MCP_READ_ONLY，
##     实时轮询会在服务器真正应用之前就声称已进入只读。
## 两条刷新路径都只就地修改(MUTATE) _init() 中构建的警告面板 + 按钮；
## 绝不重建节点。弹出提示经由注入的 Callable 发出，因此本面板绝不触碰
## 停靠面板私有的 toaster。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const MCPJsonSync = Modules.MCPJsonSync
const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 弹出提示(toast)严重级别常量（与 EditorToaster.Severity / 停靠面板的 _TOAST_* 一致）。
const _TOAST_INFO := 0
const _TOAST_WARNING := 1
const _TOAST_ERROR := 2

# 三态底栏按钮——由底栏构建并放置（停靠面板负责放置），
# 注入到此处，使本面板拥有它的标签/提示(tooltip)/颜色/动作（即其含义）。
var _mcp_json_btn: Button = null
# 停靠面板提供的弹出提示(toast)接收端 (msg, severity, tooltip)——通过注入方式
# 提供，使本面板与停靠面板持有的编辑器 toaster 保持解耦。
var _toast: Callable = Callable()

# 共享的“先确认后写入”流程——注入提供（由组装器(composer)持有），使本面板与
# 工具菜单、引导向导使用同一流程，而不是自己拥有它。
var _write_flow: MCPJsonWriteFlow = null

# 本面板内的警告标签（这个 PanelContainer 本身就是警告面板）。
var _warning_label: Label = null

# 缓存的只读状态——在服务器（重）连接 + 启动时从 .mcp.json 同步，
# 不在定时器中同步：服务器在启动时只从 process.env 读取一次
# GODOT_MCP_READ_ONLY，之后不再复查，因此实时轮询会在服务器应用之前就声称
# 只读已生效（只有客户端→服务器重启才能真正生效）。
var _read_only_active: bool = false


func _init(mcp_json_button: Button, toast: Callable, write_flow: MCPJsonWriteFlow) -> void:
	_mcp_json_btn = mcp_json_button
	_toast = toast
	_write_flow = write_flow

	# 这个 PanelContainer 就是共享警告面板——样式 + 标签只构建一次；
	# 文本/可见性由 refresh()/sync_read_only_state() 按状态就地修改。
	var warn_sb := StyleBoxFlat.new()
	warn_sb.bg_color = EditorLocale.warning_background_color()
	warn_sb.corner_radius_top_left = 4
	warn_sb.corner_radius_top_right = 4
	warn_sb.corner_radius_bottom_left = 4
	warn_sb.corner_radius_bottom_right = 4
	warn_sb.content_margin_left = 8
	warn_sb.content_margin_right = 8
	warn_sb.content_margin_top = 6
	warn_sb.content_margin_bottom = 6
	add_theme_stylebox_override("panel", warn_sb)
	visible = false

	_warning_label = Label.new()
	_warning_label.text = ""
	_warning_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_warning_label.add_theme_color_override("font_color", EditorLocale.warning_color())
	_warning_label.add_theme_font_size_override("font_size", 12)
	add_child(_warning_label)

	# 将注入的底栏按钮绑定到本面板的三态动作。其初始标签/提示/颜色只是占位；
	# 从此以后由 refresh() 全权负责。
	if _mcp_json_btn != null:
		_mcp_json_btn.text = EditorLocale.pick("Open .mcp.json", "打开 .mcp.json")
		_mcp_json_btn.tooltip_text = EditorLocale.pick(
			"Open .mcp.json in the system editor", "在系统编辑器中打开 .mcp.json")
		_mcp_json_btn.pressed.connect(on_button_pressed)


# ---------------------------------------------------------------------------
# 刷新——文件有效性（停靠面板定时器驱动的实时事实(FACT)）+ 只读（服务器同步）
# ---------------------------------------------------------------------------


## 重新计算缺失/格式错误/只读/正常四种状态，并据此同时重绘警告面板与三态
## 按钮——该状态机只存在于这一处（DRY，避免重复）。
## 只就地修改(MUTATE)现有节点；绝不重建。
## 在 1 秒定时器上安全：文件存在性 + 格式错误是实时事实(FACT)；
## 只读使用缓存的 _read_only_active（由服务器同步，不是实时只读轮询）。
func refresh() -> void:
	var warn_text := ""
	var btn_text := EditorLocale.pick("Open .mcp.json", "打开 .mcp.json")
	var btn_tip := EditorLocale.pick(
		"Open .mcp.json in the system editor", "在系统编辑器中打开 .mcp.json")
	var config_path := MCPJsonSync.get_read_mcp_json_path()
	if not config_path.is_empty():
		btn_tip += "\n" + config_path
	var highlight := false
	if not MCPJsonSync.has_mcp_json():  # 实时文件事实(FACT)——可以实时显示
		# 项目文件不是连接前置：Codex 等客户端可由插件或全局配置启动桥接。
		btn_text = EditorLocale.pick("Write .mcp.json", "写入 .mcp.json")
		btn_tip = EditorLocale.pick(
			"Optional project configuration. Clients can also connect through plugin or global MCP configuration.",
			"可选的项目配置。客户端也可以通过插件或全局 MCP 配置连接。")
	elif MCPJsonSync.is_malformed():  # 实时文件事实(FACT)——文件存在但不是有效 JSON
		warn_text = EditorLocale.pick(
			"⚠️ .mcp.json isn't valid JSON. Clients using this file cannot parse it; other client configurations are unaffected. Click \"Fix .mcp.json\" to repair or open the file.",
			"⚠️ .mcp.json 不是有效的 JSON。使用此文件的客户端无法解析它；客户端的其他配置不受影响。点击“修复 .mcp.json”可修复或打开文件。")
		btn_text = EditorLocale.pick("Fix .mcp.json", "修复 .mcp.json")
		btn_tip = EditorLocale.pick(
			"Replace the malformed .mcp.json with a clean template (asks to confirm before overwriting)",
			"用干净模板替换格式错误的 .mcp.json（覆盖前会要求确认）")
		highlight = true
	elif _read_only_active:  # 缓存值，由服务器同步（不是实时只读轮询）
		warn_text = EditorLocale.pick(
			"⚠️ READ-ONLY MODE — mutating tools are hidden. Remove GODOT_MCP_READ_ONLY from .mcp.json and reconnect the MCP client to restore full access.",
			"⚠️ 只读模式 — 会修改项目的工具已隐藏。如需恢复完整权限，请从 .mcp.json 中移除 GODOT_MCP_READ_ONLY，然后重新连接 MCP 客户端。")
		btn_text = EditorLocale.pick("Open .mcp.json ⚠", "打开 .mcp.json ⚠")
		btn_tip = EditorLocale.pick(
			"Open .mcp.json in the system editor (read-only mode active)",
			"在系统编辑器中打开 .mcp.json（只读模式已启用）")
		highlight = true
	# 警告面板（这个 PanelContainer；三种状态共享）。
	visible = not warn_text.is_empty()
	if not warn_text.is_empty() and _warning_label != null:
		_warning_label.text = warn_text
	# 双模式按钮（共享琥珀色高亮）。
	if _mcp_json_btn != null:
		_mcp_json_btn.text = btn_text
		_mcp_json_btn.tooltip_text = btn_tip
		if highlight:
			_mcp_json_btn.add_theme_color_override("font_color", EditorLocale.warning_color())
		else:
			_mcp_json_btn.remove_theme_color_override("font_color")


## 从 .mcp.json 重新同步缓存的只读标志，然后 refresh()。在启动时、服务器
## （重）连接时以及停靠面板写入之后调用——绝不在定时器中调用：只有
## 上下文协议(MCP)客户端重新启动服务器时，只读才会在服务器端生效
## （服务器在启动时只从 process.env 读取一次 GODOT_MCP_READ_ONLY），因此
## 轮询会在服务器真正应用之前错误地显示只读。
func sync_read_only_state() -> void:
	_read_only_active = MCPJsonSync.has_mcp_json() and MCPJsonSync.is_read_only()
	refresh()


# ---------------------------------------------------------------------------
# 三态按钮动作（写入委托给共享写入流程）
# ---------------------------------------------------------------------------


# 三态底栏按钮（标签由 refresh() 设置）：
#   * 存在且有效       -> “打开”  : 在系统编辑器中打开 .mcp.json。
#   * 缺失             -> “写入” : 共享写入流程——直接写入（没有文件
#                                   可覆盖，因此无需确认）。
#   * 存在但无效       -> “修复”  : 共享写入流程——文件存在，因此
#                                   流程的覆盖确认会在用干净模板替换之前触发；
#                                   格式错误的文件绝不会被悄悄覆盖
#                                   （选择“取消”可保留文件
#                                   供手动修复）。
# 按下时会重新检查状态，因此即使标签暂时过期，
# 动作也始终正确。
func on_button_pressed() -> void:
	if MCPJsonSync.has_mcp_json() and not MCPJsonSync.is_malformed():
		OS.shell_open(MCPJsonSync.get_read_mcp_json_path())
	elif _write_flow != null:
		_write_flow.write(false, _on_mcp_json_write_result)


# 本面板提供给共享写入流程的结果接收端——把
# (ok, message, severity, tooltip) 报告直接映射为一次弹出提示(toast)。
# `severity` 已经符合 _TOAST_* 刻度（0 信息 / 1 警告 / 2 错误）。
# 成功时（例如停靠面板写入缺失文件），立即重新同步只读 + 按钮状态，
# 使双模式按钮立刻从“写入”切换为“打开”，
# 而不是等到下一个定时器节拍。
func _on_mcp_json_write_result(ok: bool, message: String, severity: int, tooltip: String) -> void:
	if _toast.is_valid():
		_toast.call(message, severity, tooltip)
	if ok:
		sync_read_only_state()
