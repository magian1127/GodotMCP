@tool
extends HBoxContainer
## 停靠面板(dock)的“失去焦点时保持响应”子面板——可选开启的编辑器限速开关。
##
## 一个停靠面板子面板。由 dock.gd 构建并持有；加入停靠面板的场景树
## （因此编辑器会随停靠面板一起释放它）。只构建一次复选框 + 三态指示器，
## 并拥有该可选项 EditorSetting 的读写。切换时通过绑定的服务器立即应用设置
## （具备冲突感知/崩溃安全的恢复逻辑位于服务器端——
## notify_unfocused_responsive_setting_changed()）；
## refresh() 依据服务器实时状态重绘指示器而不重建。
## 在树中期间会订阅 EditorSettings.settings_changed，
## 因此在编辑器设置对话框中做出的切换会立即重绘复选框 + 标签，
## 而不必等待下一次连接/断开。

const _SETTING_KEY := "mcp_toolkit/performance/keep_editor_responsive_unfocused"
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 读取服务器用于实时指示器（fps + 已连接）以及在切换时应用设置；
# 持有它是为了让 refresh() 无需停靠面板再次传入即可重绘。
var _server: Node = null

var _check: CheckBox = null
var _state_label: Label = null


func _init(server: Node) -> void:
	_server = server

	_check = CheckBox.new()
	_check.text = EditorLocale.pick("Responsive when unfocused", "失去焦点时保持响应")
	var resp_enabled := true
	var resp_es := EditorInterface.get_editor_settings()
	if resp_es != null and resp_es.has_setting(_SETTING_KEY):
		resp_enabled = bool(resp_es.get_setting(_SETTING_KEY))
	_check.set_pressed_no_signal(resp_enabled)
	_check.toggled.connect(_on_toggled)
	add_child(_check)

	_state_label = Label.new()
	_state_label.add_theme_font_size_override("font_size", 11)
	_state_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_state_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	add_child(_state_label)


func _enter_tree() -> void:
	# 与编辑器设置实时同步：外部切换（编辑器设置对话框）不会经过本控件，
	# 因此订阅并重绘。该信号不带键值——不存在按键过滤——
	# 而 refresh() 既廉价又幂等，所以在每次设置变更时重绘没有问题。
	# 不会形成反馈循环：_on_toggled 写入设置后，
	# 随之而来的信号会再次调用 refresh()，
	# 它重新读取同一个值并绘制出相同的结果。
	var es := EditorInterface.get_editor_settings()
	if es != null and not es.settings_changed.is_connected(refresh):
		es.settings_changed.connect(refresh)
	# 首次绘制——_init 构建的标签没有文本，若不这样做，
	# 指示器在第一次连接/断开之前会一直空白（或停留在过期状态）。
	refresh()


func _exit_tree() -> void:
	# EditorSettings 单例的寿命超过本控件——若不断开连接，
	# 停靠面板释放后回调会变成僵尸回调。
	var es := EditorInterface.get_editor_settings()
	if es != null and es.settings_changed.is_connected(refresh):
		es.settings_changed.disconnect(refresh)


func _on_toggled(enabled: bool) -> void:
	var es := EditorInterface.get_editor_settings()
	if es != null:
		es.set_setting(_SETTING_KEY, enabled)
	# 立即应用：已连接时开启 → 立刻提速；活跃时关闭 → 立即执行具备冲突感知的
	# 恢复（而不是等待下一次连接/断开）。
	if _server != null:
		_server.notify_unfocused_responsive_setting_changed()
	refresh()


## 始终如实的三态指示器：关闭 / 开启（空闲）/ 开启 · 活跃 · {fps} fps。
## 只重绘现有标签并保持复选框同步；只做就地修改——绝不重建，
## 因此在每次刷新触发和连接状态变化时都是安全的。
func refresh() -> void:
	if _state_label == null or _check == null:
		return
	var enabled := true
	var es := EditorInterface.get_editor_settings()
	if es != null and es.has_setting(_SETTING_KEY):
		enabled = bool(es.get_setting(_SETTING_KEY))
	# 若该设置在编辑器设置中被更改，则保持复选框同步。
	if _check.button_pressed != enabled:
		_check.set_pressed_no_signal(enabled)
	var fps: int = _server.get_unfocused_responsive_fps() if _server != null else 60
	var connected: bool = _server != null and _server.get_authed_peer_count() > 0
	_check.tooltip_text = EditorLocale.pick(
		"Editor stays ~%d fps while unfocused so MCP commands stay responsive "
			+ "while a client is connected — raises background CPU. Off uses Godot's "
			+ "default low-power unfocused throttle. Configure the rate in "
			+ "Editor Settings → Mcp Toolkit → Performance.",
		"连接客户端后，编辑器失去焦点时仍保持约 %d fps，使 MCP 命令能够及时响应；"
			+ "这会增加后台 CPU 占用。关闭后使用 Godot 默认的低功耗限速。可在“编辑器设置"
			+ " → Mcp Toolkit → Performance”中调整帧率。") % fps
	if not enabled:
		_state_label.text = EditorLocale.pick("Off", "关闭")
		_state_label.remove_theme_color_override("font_color")
	elif not connected:
		_state_label.text = EditorLocale.pick("On (idle)", "开启（空闲）")
		_state_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	else:
		_state_label.text = EditorLocale.pick(
			"On · active · %d fps", "开启 · 活跃 · %d fps") % fps
		_state_label.add_theme_color_override("font_color", EditorLocale.success_color())
