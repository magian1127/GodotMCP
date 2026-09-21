@tool
extends VBoxContainer
## 停靠面板(dock)的“安全与响应上限”子面板——令牌重新生成 + 响应上限。
##
## 一个停靠面板子面板。由 dock.gd 构建并持有；加入停靠面板的场景树
## （因此编辑器会随停靠面板一起释放它）。只构建一次自己的控件，并拥有各上限
## SpinBox 的交互逻辑：每个上限在变更时写入对应的 ProjectSetting。令牌重新
## 生成会触及服务器令牌（由停靠面板持有），因此它以信号形式暴露、交由停靠
## 面板处理，而不是在此处直接接线。

# 用户点击“重新生成令牌”时发出。停靠面板持有服务器 + 弹出提示(toast)，
# 因此由它执行实际的令牌轮换；本面板只发出请求。
signal regenerate_token_requested

const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

var _script_cap_spinbox: SpinBox = null
var _save_cap_spinbox: SpinBox = null
var _ws_buffer_spinbox: SpinBox = null


func _init() -> void:
	var regen_btn := Button.new()
	regen_btn.text = EditorLocale.pick("Regenerate Token", "重新生成令牌")
	regen_btn.pressed.connect(_on_regen_pressed)
	add_child(regen_btn)

	var limits_row := HBoxContainer.new()
	add_child(limits_row)

	var cap_label := Label.new()
	cap_label.text = EditorLocale.pick("Script cap:", "脚本读取上限：")
	limits_row.add_child(cap_label)
	_script_cap_spinbox = SpinBox.new()
	_script_cap_spinbox.min_value = 64
	_script_cap_spinbox.max_value = 4096
	_script_cap_spinbox.step = 64
	_script_cap_spinbox.suffix = "KB"
	_script_cap_spinbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_script_cap_spinbox.value = ProjectSettings.get_setting(
		"mcp_toolkit/limits/script_read_cap_kb", 256)
	_script_cap_spinbox.value_changed.connect(_on_script_cap_changed)
	limits_row.add_child(_script_cap_spinbox)

	var save_cap_label := Label.new()
	save_cap_label.text = EditorLocale.pick("Save cap:", "用户文件读取上限：")
	limits_row.add_child(save_cap_label)
	_save_cap_spinbox = SpinBox.new()
	_save_cap_spinbox.min_value = 64
	_save_cap_spinbox.max_value = 4096
	_save_cap_spinbox.step = 64
	_save_cap_spinbox.suffix = "KB"
	_save_cap_spinbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_save_cap_spinbox.value = ProjectSettings.get_setting(
		"mcp_toolkit/limits/save_read_cap_kb", 256)
	_save_cap_spinbox.value_changed.connect(_on_save_cap_changed)
	limits_row.add_child(_save_cap_spinbox)

	var ws_label := Label.new()
	ws_label.text = EditorLocale.pick("WS buffer:", "WS 缓冲区：")
	limits_row.add_child(ws_label)
	_ws_buffer_spinbox = SpinBox.new()
	_ws_buffer_spinbox.min_value = 256
	_ws_buffer_spinbox.max_value = 8192
	_ws_buffer_spinbox.step = 256
	_ws_buffer_spinbox.suffix = "KB"
	_ws_buffer_spinbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_ws_buffer_spinbox.value = ProjectSettings.get_setting(
		"mcp_toolkit/limits/ws_buffer_kb", 1024)
	_ws_buffer_spinbox.value_changed.connect(_on_ws_buffer_changed)
	limits_row.add_child(_ws_buffer_spinbox)

	var limits_note := Label.new()
	limits_note.text = EditorLocale.pick(
		"These may be overridden by env vars in .mcp.json on connect.",
		"连接时，.mcp.json 中的环境变量可能会覆盖这些值。")
	limits_note.add_theme_font_size_override("font_size", 11)
	limits_note.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	limits_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(limits_note)


func _on_regen_pressed() -> void:
	regenerate_token_requested.emit()


func _on_script_cap_changed(value: float) -> void:
	var clamped := maxi(64, int(value))
	ProjectSettings.set_setting("mcp_toolkit/limits/script_read_cap_kb", clamped)
	ProjectSettings.save()


func _on_save_cap_changed(value: float) -> void:
	var clamped := maxi(64, int(value))
	ProjectSettings.set_setting("mcp_toolkit/limits/save_read_cap_kb", clamped)
	ProjectSettings.save()


func _on_ws_buffer_changed(value: float) -> void:
	var clamped := maxi(256, int(value))
	ProjectSettings.set_setting("mcp_toolkit/limits/ws_buffer_kb", clamped)
	ProjectSettings.save()
