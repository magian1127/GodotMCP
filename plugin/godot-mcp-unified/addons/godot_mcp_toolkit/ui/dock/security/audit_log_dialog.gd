@tool
extends AcceptDialog
## 编辑器内审计日志查看器——读取审计日志尾部（最后 100 行）并以可滚动方式
## 渲染。“打开文件”会调用系统程序打开磁盘上的完整日志。
##
## 延迟创建并由停靠面板(dock)持有。每次 show_log() 都会重新读取尾部并完整
## 重建内容，因此复用的实例总能显示最新日志。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const EditorPopup := preload("res://addons/godot_mcp_toolkit/ui/editor_popup.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 对话框外壳（标题/按钮/处理器）只在首次显示时安装一次；可滚动内容在每次
# 调用时清空并重建。
var _built: bool = false
var _content_root: VBoxContainer = null
# “打开文件”操作所打开的路径——每次 show_log() 都会刷新，确保按钮始终指向
# 对话框当前显示的日志。
var _current_path: String = ""


## 读取审计日志尾部并渲染，
## 然后居中弹出对话框并滚动到底部。传入停靠面板的
## 审计路径；路径为空时回退(fallback)到规范日志位置。
func show_log(audit_path: String) -> void:
	# 读取日志文件。
	var path := audit_path
	if path.is_empty():
		path = Modules.Audit.get_log_path()
	_current_path = path
	var log_text := ""
	if FileAccess.file_exists(path):
		var file := FileAccess.open(path, FileAccess.READ)
		if file != null:
			var full_text := file.get_as_text()
			file.close()
			var lines := full_text.split("\n")
			if lines.size() > 100:
				var tail := lines.slice(lines.size() - 100)
				log_text = "\n".join(tail)
				log_text += EditorLocale.pick(
					"\n\n... Showing last 100 lines. Open the file to view the full log.",
					"\n\n……当前显示最后 100 行。请打开文件查看完整日志。")
			else:
				log_text = full_text
	if log_text.strip_edges().is_empty():
		log_text = EditorLocale.pick("(audit log is empty)", "（审计日志为空）")

	_ensure_built()

	# 立即清除先前内容——使用 remove_child（而不仅是 queue_free），使重建后的
	# 对话框内容最小尺寸只反映新内容。仅用 queue_free 会让旧子树在当前帧内
	# 仍留在场景树中，导致每次重新打开时 popup_centered 的尺寸偏大
	# （窗口尺寸只增不减）。
	for child in _content_root.get_children():
		_content_root.remove_child(child)
		child.queue_free()

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.custom_minimum_size = Vector2(600, 400)
	_content_root.add_child(scroll)

	var text_label := RichTextLabel.new()
	text_label.bbcode_enabled = false
	text_label.fit_content = true
	text_label.scroll_active = false  # 滚动由父级 ScrollContainer 处理
	text_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	text_label.selection_enabled = true
	text_label.text = log_text
	text_label.add_theme_font_size_override("normal_font_size", 11)
	scroll.add_child(text_label)

	# 以显式尺寸打开并提升到前台（见 EditorPopup），
	# 然后在一次布局之后再滚动到底部。显式尺寸可避免复用的对话框尺寸漂移
	# （无参弹窗会沿用窗口当前尺寸）；提升到前台则保证对话框沉到视口之后时
	# “查看审计日志”按钮仍有响应。
	EditorPopup.present(self, Vector2i(620, 480))
	await get_tree().process_frame
	scroll.scroll_vertical = scroll.get_v_scroll_bar().max_value


# 只安装一次对话框外壳：标题、关闭/打开文件按钮，以及自定义操作处理器
# （它会打开 _current_path 当前指向的文件）。
func _ensure_built() -> void:
	if _built:
		return
	title = EditorLocale.pick("Godot MCP Unified — Audit Log", "Godot MCP Unified — 审计日志")
	ok_button_text = EditorLocale.pick("Close", "关闭")
	exclusive = false
	min_size = Vector2i(620, 480)

	add_button(EditorLocale.pick("Open File", "打开文件"), true, "open_file")
	custom_action.connect(func(action: StringName):
		if action == "open_file":
			var global_path := ProjectSettings.globalize_path(_current_path)
			OS.shell_open(global_path)
	)

	_content_root = VBoxContainer.new()
	_content_root.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_content_root.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add_child(_content_root)

	_built = true
