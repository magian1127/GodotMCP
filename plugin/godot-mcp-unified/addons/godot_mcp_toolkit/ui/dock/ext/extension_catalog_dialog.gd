@tool
extends Window
## 编辑器内的扩展目录对话框——只读取本地 Godot MCP Unified 插件随附的目录。
## 绝不发起网络请求。

const ExtensionCatalog := preload("res://addons/godot_mcp_toolkit/ui/dock/ext/extension_catalog.gd")
const EditorPopup := preload("res://addons/godot_mcp_toolkit/ui/editor_popup.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

var _list_vbox: VBoxContainer = null
var _search_box: LineEdit = null
var _status_label: Label = null
var _update_msg: Label = null
var _entries: Array = []
var _toolkit_version: String = ""
var _godot_version: String = ""


func _ready() -> void:
	title = _t("Godot MCP Unified Extensions", "Godot MCP Unified 扩展")
	exclusive = false
	min_size = Vector2i(580, 460)
	close_requested.connect(hide)

	_toolkit_version = ExtensionCatalog.get_toolkit_version()
	_godot_version = ExtensionCatalog.get_godot_version()

	_build_ui()


func _exit_tree() -> void:
	pass


## 公开入口——由停靠面板(dock)或子菜单调用。
func show_catalog() -> void:
	# 居中打开并提升到前台——否则非独占对话框会沉到视口之后，让“扩展”按钮
	# 在下一次点击时显得毫无反应。
	EditorPopup.present(self, Vector2i(640, 520))
	_load_local_catalog()


# -- 界面(UI)构建 ----------------------------------------------------------


func _build_ui() -> void:
	var margin := MarginContainer.new()
	margin.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	margin.add_theme_constant_override("margin_left", 12)
	margin.add_theme_constant_override("margin_right", 12)
	margin.add_theme_constant_override("margin_top", 8)
	margin.add_theme_constant_override("margin_bottom", 8)
	add_child(margin)

	var outer := VBoxContainer.new()
	outer.add_theme_constant_override("separation", 8)
	margin.add_child(outer)

	# 头部
	var header := Label.new()
	header.text = _t(
		"Browse extensions bundled with this local Godot MCP Unified project.",
		"浏览当前本地 Godot MCP Unified 项目随附的扩展。")
	header.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	outer.add_child(header)

	var assetlib_note := Label.new()
	assetlib_note.text = _t(
		"This view reads only addons/godot_mcp_toolkit/extensions/catalog.json "
			+ "and never contacts a remote catalog.",
		"此界面只读取 addons/godot_mcp_toolkit/extensions/catalog.json，"
			+ "不会连接远程目录。")
	assetlib_note.add_theme_font_size_override("font_size", 11)
	assetlib_note.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	assetlib_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	outer.add_child(assetlib_note)

	# 更新消息（默认隐藏，目录格式更新时显示）
	_update_msg = Label.new()
	_update_msg.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_update_msg.add_theme_color_override("font_color", EditorLocale.warning_color())
	_update_msg.visible = false
	outer.add_child(_update_msg)

	# 搜索框（列表有内容之前隐藏）
	_search_box = LineEdit.new()
	_search_box.placeholder_text = _t("Search extensions...", "搜索扩展……")
	_search_box.clear_button_enabled = true
	_search_box.text_changed.connect(_on_search_changed)
	_search_box.visible = false
	outer.add_child(_search_box)

	# 可滚动的扩展列表
	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	outer.add_child(scroll)

	_list_vbox = VBoxContainer.new()
	_list_vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_list_vbox.add_theme_constant_override("separation", 12)
	scroll.add_child(_list_vbox)

	# 自建扩展分区——扩展 Toolkit 的能力，而不只是浏览目录。
	outer.add_child(HSeparator.new())
	var extend_label := Label.new()
	extend_label.text = _t(
		"Want more than the catalog? You can build your own extensions — "
			+ "addons that add custom MCP tools. Read the Extending guide, or use the "
			+ "'mcp-extension-creator' Companion Skill to scaffold one with your AI "
			+ "assistant.",
		"目录里没有需要的功能？你可以自行创建扩展，为 Toolkit 添加自定义 MCP 工具。"
			+ "请阅读扩展开发指南，或让 AI 助手通过 mcp-extension-creator 配套技能搭建框架。")
	extend_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	extend_label.add_theme_font_size_override("font_size", 11)
	outer.add_child(extend_label)

	var extend_btn := Button.new()
	extend_btn.text = _t("Open Extending Guide", "打开扩展开发指南")
	var guide_path := _t(
		"res://addons/godot_mcp_toolkit/docs/extending.md",
		"res://addons/godot_mcp_toolkit/docs/extending.zh-CN.md")
	extend_btn.pressed.connect(func(): OS.shell_open(ProjectSettings.globalize_path(guide_path)))
	outer.add_child(extend_btn)

	# 底栏——本地目录状态 + 重新读取按钮
	var footer := HBoxContainer.new()
	outer.add_child(footer)

	_status_label = Label.new()
	_status_label.text = ""
	_status_label.add_theme_font_size_override("font_size", 11)
	_status_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	_status_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	footer.add_child(_status_label)

	var refresh_btn := Button.new()
	refresh_btn.text = _t("Reload Local Catalog", "重新读取本地目录")
	refresh_btn.pressed.connect(_load_local_catalog)
	footer.add_child(refresh_btn)


# -- 本地目录 -----------------------------------------------------------


func _load_local_catalog() -> void:
	_status_label.text = _t("Reading local catalog...", "正在读取本地扩展目录……")
	_update_msg.visible = false

	var parsed := ExtensionCatalog.read_catalog()
	if not bool(parsed.get("ok", false)):
		_clear_list()
		var error_code := str(parsed.get("error", ""))
		if error_code == "NEWER_VERSION":
			_update_msg.text = _t(
				"The bundled catalog format is newer than this plugin build.",
				"随插件提供的本地目录格式高于当前插件版本。")
			_update_msg.visible = true
		_show_message(_t(
			"The bundled local extension catalog is missing or invalid.",
			"随插件提供的本地扩展目录缺失或无效。"))
		_set_status(_t("Local catalog error", "本地目录错误"), true)
		return

	_entries = parsed["extensions"]
	_populate_list(_entries)
	_set_status(
		"本地目录已读取 — %d 个扩展" % _entries.size()
		if EditorLocale.is_chinese_editor()
		else "Local catalog loaded \u2014 %d extension%s" % [
			_entries.size(), "" if _entries.size() == 1 else "s"],
		false)


func _set_status(text: String, is_warning: bool) -> void:
	_status_label.text = text
	if is_warning:
		_status_label.add_theme_color_override("font_color", EditorLocale.warning_color())
	else:
		_status_label.add_theme_color_override("font_color", EditorLocale.muted_text_color())


# -- 列表渲染 -----------------------------------------------------------


func _clear_list() -> void:
	for child in _list_vbox.get_children():
		child.queue_free()
	_search_box.visible = false
	_search_box.text = ""


func _show_message(text: String) -> void:
	_clear_list()
	var lbl := Label.new()
	lbl.text = text
	lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	lbl.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	_list_vbox.add_child(lbl)


func _populate_list(extensions: Array) -> void:
	_clear_list()

	if extensions.is_empty():
		_show_message(_t(
			"No optional extensions are bundled. Add local entries to extensions/catalog.json.",
			"当前没有随附的可选扩展；可在 extensions/catalog.json 中添加本地条目。"))
		return

	_search_box.visible = true

	for ext in extensions:
		if not ext is Dictionary:
			continue
		_list_vbox.add_child(_build_entry_card(ext))


func _build_entry_card(ext: Dictionary) -> PanelContainer:
	var card := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = EditorLocale.card_background_color()
	style.border_color = EditorLocale.card_border_color()
	style.set_border_width_all(1)
	style.set_corner_radius_all(4)
	style.content_margin_left = 10.0
	style.content_margin_right = 10.0
	style.content_margin_top = 8.0
	style.content_margin_bottom = 8.0
	card.add_theme_stylebox_override("panel", style)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 4)
	card.add_child(vbox)

	# 第 1 行：名称 + 作者 + 状态徽标(badge)
	var title_row := HBoxContainer.new()
	vbox.add_child(title_row)

	var name_label := Label.new()
	name_label.text = str(ext.get("name", _t("Unknown", "未知")))
	name_label.add_theme_font_size_override("font_size", 14)
	title_row.add_child(name_label)

	var author_label := Label.new()
	author_label.text = _t("  by %s", "  作者：%s") % str(
		ext.get("author", _t("Unknown", "未知")))
	author_label.add_theme_font_size_override("font_size", 12)
	author_label.add_theme_color_override("font_color", EditorLocale.subtle_text_color())
	title_row.add_child(author_label)

	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	title_row.add_child(spacer)

	# 兼容性徽标(badge)——紧邻名称，便于快速扫视
	var compat := ExtensionCatalog.is_compatible(ext, _toolkit_version, _godot_version)
	var compat_label := Label.new()
	if compat:
		compat_label.text = _t("\u2713 Compatible", "\u2713 兼容")
		compat_label.add_theme_color_override("font_color", EditorLocale.success_color())
	else:
		compat_label.text = _t("\u26a0 Check version", "\u26a0 检查版本")
		compat_label.add_theme_color_override("font_color", EditorLocale.warning_color())
	compat_label.add_theme_font_size_override("font_size", 11)
	title_row.add_child(compat_label)

	# 状态徽标（绿=官方，蓝=社区，橙=实验性）
	var status: String = str(ext.get("status", "community"))
	var status_label := Label.new()
	status_label.text = "  %s" % _localized_status(status)
	status_label.add_theme_font_size_override("font_size", 11)
	match status:
		"official":
			status_label.add_theme_color_override("font_color", EditorLocale.success_color())
		"community":
			status_label.add_theme_color_override("font_color", EditorLocale.info_color())
		"experimental":
			status_label.add_theme_color_override("font_color", EditorLocale.warning_color())
		_:
			status_label.add_theme_color_override("font_color", EditorLocale.subtle_text_color())
	title_row.add_child(status_label)

	# 描述
	var desc_label := Label.new()
	desc_label.text = str(ext.get("description", ""))
	desc_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	desc_label.add_theme_font_size_override("font_size", 12)
	vbox.add_child(desc_label)

	# 工具列表（如存在）——可折叠，默认折叠
	var tools = ext.get("tools", [])
	if tools is Array and not tools.is_empty():
		var tools_toggle := Button.new()
		tools_toggle.flat = true
		tools_toggle.text = _t("\u25b6 Tools (%d)", "\u25b6 工具（%d）") % tools.size()
		tools_toggle.add_theme_font_size_override("font_size", 11)
		tools_toggle.alignment = HORIZONTAL_ALIGNMENT_LEFT
		vbox.add_child(tools_toggle)

		var tools_container := VBoxContainer.new()
		tools_container.visible = false
		vbox.add_child(tools_container)

		for tool_entry in tools:
			if not tool_entry is Dictionary:
				continue
			var tool_line := Label.new()
			var tool_name: String = str(tool_entry.get("name", ""))
			var tool_desc: String = str(tool_entry.get("description", ""))
			if tool_desc.is_empty():
				tool_line.text = "    %s" % tool_name
			else:
				tool_line.text = "    %s \u2014 %s" % [tool_name, tool_desc]
			tool_line.add_theme_font_size_override("font_size", 11)
			tool_line.add_theme_color_override("font_color", EditorLocale.subtle_text_color())
			tool_line.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
			tools_container.add_child(tool_line)

		tools_toggle.pressed.connect(func():
			tools_container.visible = not tools_container.visible
			tools_toggle.text = EditorLocale.pick(
				"%s Tools (%d)", "%s 工具（%d）") % [
					"\u25bc" if tools_container.visible else "\u25b6", tools.size()]
		)

	# 安装说明——存在时可展开，缺失时显示回退(fallback)文本
	var raw_install = ext.get("install_instructions", null)
	var has_install: bool = raw_install != null and raw_install is String and not raw_install.strip_edges().is_empty()
	if has_install:
		var install_toggle := Button.new()
		install_toggle.flat = true
		install_toggle.text = _t("\u25b6 Install instructions", "\u25b6 安装说明")
		install_toggle.add_theme_font_size_override("font_size", 11)
		install_toggle.alignment = HORIZONTAL_ALIGNMENT_LEFT
		vbox.add_child(install_toggle)

		var install_label := Label.new()
		install_label.text = str(raw_install)
		install_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		install_label.add_theme_font_size_override("font_size", 11)
		install_label.add_theme_color_override("font_color", EditorLocale.subtle_text_color())
		install_label.visible = false
		vbox.add_child(install_label)

		install_toggle.pressed.connect(func():
			install_label.visible = not install_label.visible
			install_toggle.text = EditorLocale.pick(
				"%s Install instructions", "%s 安装说明") % (
					"\u25bc" if install_label.visible else "\u25b6")
		)
	else:
		var install_note := Label.new()
		install_note.text = _t(
			"See the extension's local README for install instructions.",
			"安装说明请参阅扩展目录中的本地 README。")
		install_note.add_theme_font_size_override("font_size", 11)
		install_note.add_theme_color_override("font_color", EditorLocale.muted_text_color())
		vbox.add_child(install_note)

	# 仅限本地扩展路径；目录条目不能打开 URL 或任意文件。
	var local_path: String = str(ext.get("local_path", ""))
	if not local_path.is_empty():
		var open_btn := Button.new()
		open_btn.text = _t("Open Local Extension", "打开本地扩展")
		open_btn.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
		var path := local_path
		open_btn.pressed.connect(func(): _open_local_path(path))
		vbox.add_child(open_btn)

	# 存储元数据供搜索过滤使用
	card.set_meta("ext_name", str(ext.get("name", "")))
	card.set_meta("ext_description", str(ext.get("description", "")))
	card.set_meta("ext_author", str(ext.get("author", "")))
	card.set_meta("ext_tags", str(ext.get("tags", [])))

	return card


# -- 搜索过滤 -----------------------------------------------------------


func _on_search_changed(query: String) -> void:
	var q := query.strip_edges().to_lower()
	for child in _list_vbox.get_children():
		if not child is PanelContainer:
			continue
		if q.is_empty():
			child.visible = true
			continue
		var name_str: String = child.get_meta("ext_name", "").to_lower()
		var desc_str: String = child.get_meta("ext_description", "").to_lower()
		var author_str: String = child.get_meta("ext_author", "").to_lower()
		var tags_str: String = child.get_meta("ext_tags", "").to_lower()
		child.visible = (
			q in name_str or q in desc_str or q in author_str or q in tags_str)


# -- 打开本地路径 -----------------------------------------------------------


func _open_local_path(local_path: String) -> void:
	if not ExtensionCatalog.is_allowed_local_path(local_path):
		var msg := _t(
			"Refusing to open a path outside the bundled extensions folder.",
			"已拒绝打开随附扩展目录之外的路径。")
		push_warning("[MCPLocalCatalog] %s" % msg)
		_set_status(msg, true)
		return
	OS.shell_open(ProjectSettings.globalize_path(local_path))


func _localized_status(status: String) -> String:
	if not EditorLocale.is_chinese_editor():
		return status.capitalize()
	match status:
		"official":
			return "官方"
		"community":
			return "社区"
		"experimental":
			return "实验性"
		_:
			return status


func _t(english: String, chinese: String) -> String:
	return EditorLocale.pick(english, chinese)
