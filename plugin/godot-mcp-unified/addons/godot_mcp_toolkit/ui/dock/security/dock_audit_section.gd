@tool
extends VBoxContainer
## 停靠面板(dock)“审计日志”可折叠分区的内容——设置 + 日志查看/清空。
##
## 一个停靠面板子面板。由 dock.gd 构建并持有；加入停靠面板的场景树
## （因此编辑器会随停靠面板一起释放本面板）。只构建一次控件，并拥有审计的
## 全部职责：启用/最大尺寸设置（各自在变更时写入对应的 ProjectSetting）、
## 查看/清空按钮（清空使用共享的自释放确认工厂），以及延迟创建的
## AuditLogDialog。弹出提示(toast)经由注入的 Callable 发出，因此本面板绝不
## 触碰停靠面板私有的 toaster。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const AuditLogDialog := preload("res://addons/godot_mcp_toolkit/ui/dock/security/audit_log_dialog.gd")
const DockConfirm := preload("res://addons/godot_mcp_toolkit/ui/dock/dock_confirm.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 对话框与清空操作使用的审计路径来源；由停靠面板在 bind() 时传入。
var _audit_path: String = ""
# 停靠面板提供的弹出提示(toast)接收端 (msg, severity, tooltip)——通过注入方式
# 提供，使本面板与停靠面板持有的编辑器 toaster 保持解耦。
var _toast: Callable = Callable()

# 延迟创建的日志查看器。以编辑器基础控件为父节点（而不是本面板），使其
# popup_centered() 以编辑器为中心，而不是以停靠面板为中心——因此它不会随
# 本面板的子树一起释放，必须在 _exit_tree 中显式释放（见该处）。
var _audit_dialog: AcceptDialog = null


func _init(audit_path: String, toast: Callable) -> void:
	_audit_path = audit_path
	_toast = toast

	var audit_settings_row := HBoxContainer.new()
	add_child(audit_settings_row)

	var audit_enabled_check := CheckBox.new()
	audit_enabled_check.text = EditorLocale.pick("Enabled", "启用")
	audit_enabled_check.button_pressed = ProjectSettings.get_setting(
		"mcp_toolkit/audit/enabled", true)
	audit_enabled_check.toggled.connect(_on_audit_enabled_toggled)
	audit_settings_row.add_child(audit_enabled_check)

	var audit_size_label := Label.new()
	audit_size_label.text = EditorLocale.pick("  Max KB:", "  最大 KB：")
	audit_size_label.add_theme_font_size_override("font_size", 11)
	audit_settings_row.add_child(audit_size_label)

	var audit_size_spin := SpinBox.new()
	audit_size_spin.min_value = 0
	audit_size_spin.max_value = 10240
	audit_size_spin.step = 128
	audit_size_spin.value = ProjectSettings.get_setting(
		"mcp_toolkit/audit/max_size_kb", 1024)
	audit_size_spin.tooltip_text = EditorLocale.pick("0 = unlimited", "0 = 不限")
	audit_size_spin.value_changed.connect(_on_audit_max_size_changed)
	audit_settings_row.add_child(audit_size_spin)

	var audit_btns := HBoxContainer.new()
	add_child(audit_btns)

	var view_log_btn := Button.new()
	view_log_btn.text = EditorLocale.pick("View Audit Log", "查看审计日志")
	view_log_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	view_log_btn.pressed.connect(show_dialog)
	audit_btns.add_child(view_log_btn)

	var clear_log_btn := Button.new()
	clear_log_btn.text = EditorLocale.pick("Clear Audit Log", "清空审计日志")
	clear_log_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	clear_log_btn.pressed.connect(_on_clear_audit_log)
	audit_btns.add_child(clear_log_btn)


func _exit_tree() -> void:
	# 日志查看器是基础控件的子节点，不在本面板的子树内，因此编辑器不会随面板
	# 一起释放它。必须立即释放（而不是 queue_free），使其引用链在 ObjectDB 的
	# 退出时泄漏检查运行之前就被释放（与停靠面板/服务器在清理时 free() 的
	# 原因相同）。
	if _audit_dialog != null and is_instance_valid(_audit_dialog):
		_audit_dialog.free()
	_audit_dialog = null


# ---------------------------------------------------------------------------
# 审计日志弹窗
# ---------------------------------------------------------------------------

## 打开审计日志查看器，首次使用时延迟创建。设为公开是因为停靠面板的
## show_audit_dialog() 委托方法（由工具菜单调用）会转发到这里。
func show_dialog() -> void:
	if _audit_dialog == null or not is_instance_valid(_audit_dialog):
		_audit_dialog = AuditLogDialog.new()
		EditorInterface.get_base_control().add_child(_audit_dialog)
	_audit_dialog.show_log(_audit_path)


func _on_clear_audit_log() -> void:
	DockConfirm.confirm(
		EditorLocale.pick("Clear Audit Log?", "要清空审计日志吗？"),
		EditorLocale.pick(
			"This will permanently delete all audit log entries.",
			"这会永久删除全部审计日志记录。"),
		EditorLocale.pick("Clear", "清空"),
		func() -> void:
			var path := _audit_path
			if path.is_empty():
				path = Modules.Audit.get_log_path()
			var file := FileAccess.open(path, FileAccess.WRITE)
			if file != null:
				file.store_string("")
				file.close()
			if _toast.is_valid():
				_toast.call(EditorLocale.pick("Audit log cleared", "审计日志已清空"))
	)


# ---------------------------------------------------------------------------
# 设置处理器
# ---------------------------------------------------------------------------

func _on_audit_enabled_toggled(enabled: bool) -> void:
	ProjectSettings.set_setting("mcp_toolkit/audit/enabled", enabled)
	ProjectSettings.save()


func _on_audit_max_size_changed(value: float) -> void:
	ProjectSettings.set_setting("mcp_toolkit/audit/max_size_kb", int(value))
	ProjectSettings.save()
