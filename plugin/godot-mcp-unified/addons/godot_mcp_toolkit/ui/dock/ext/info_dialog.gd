@tool
extends AcceptDialog
## 编辑器内的信息/帮助对话框——只读展示连接状态、插件/引擎版本、
## 已注册工具摘要、多实例支持、只读模式、版本兼容性、配套技能(skill)
## 以及参考链接。
##
## 延迟创建并由停靠面板(dock)持有。每次 show_info() 都会重新读取服务器
## 实时状态并完整重建内容，因此复用的实例始终反映
## 当前连接。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const MCPJsonSync = Modules.MCPJsonSync
const DockSectionCard := preload("res://addons/godot_mcp_toolkit/ui/dock/dock_section_card.gd")
const EditorPopup := preload("res://addons/godot_mcp_toolkit/ui/editor_popup.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 对话框外壳（标题/按钮）只在首次显示时安装一次；
# 内容在每次调用时清空并重建。
var _built: bool = false
var _content_root: VBoxContainer = null


## 从传入的服务器重新读取实时状态并渲染信息面板，然后居中弹出对话框。
## 传入停靠面板绑定的上下文协议(MCP)服务器节点。
func show_info(server: Node) -> void:
	_ensure_built()

	# 立即清除先前内容——使用 remove_child（而不仅是 queue_free），
	# 使重建后的对话框内容最小尺寸只反映新内容。
	# 仅用 queue_free 会让旧子树在当前帧内仍留在场景树中，
	# 导致每次重新打开时 popup_centered 的尺寸偏大（窗口尺寸只增不减）。
	for child in _content_root.get_children():
		_content_root.remove_child(child)
		child.queue_free()

	# 固定头部——连接信息 + 插件/引擎版本，
	# 始终显示在可折叠中部之上。
	var connection := DockSectionCard.make_section(_t("Connection", "连接"))
	# 将头部固定在固定高度——make_section 返回 SIZE_EXPAND_FILL，
	# 否则头部会扩张并吞掉可折叠中部。
	connection.size_flags_vertical = 0
	_content_root.add_child(connection)
	var conn: VBoxContainer = connection.get_meta("content")
	if server != null and server.is_listening():
		var port: int = server.get_bound_port()
		var peers: int = server.get_authed_peer_count()
		_add_info_row(conn, _t("Address", "地址"), "127.0.0.1:%d" % port)
		_add_info_row(conn, _t("Peers", "连接数"), _t("%d connected", "已连接 %d 个") % peers)
	else:
		_add_info_row(conn, _t("Address", "地址"), _t("not listening", "未监听"))
	if MCPJsonSync.is_read_only():
		_add_info_row(conn, _t("Mode", "模式"), _t(
			"Read-only (GODOT_MCP_READ_ONLY=1)",
			"只读（GODOT_MCP_READ_ONLY=1）"))
	var plugin_ver := Modules.VersionUtils.read_plugin_version()
	var vi := Engine.get_version_info()
	var godot_ver := "%d.%d.%d" % [vi["major"], vi["minor"], vi["patch"]]
	_add_info_row(conn, _t("Plugin", "插件"), "v%s" % plugin_ver)
	_add_info_row(conn, "Godot", godot_ver)

	# 可折叠中部——每个分区拥有自己的滚动；全部默认折叠
	# （固定头部已显示主要状态），因此没有外层滚动。
	var mid := VBoxContainer.new()
	mid.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_content_root.add_child(mid)

	var tools_content := DockSectionCard.make_collapsible(mid, _t("Registered Tools", "已注册工具"), false)
	if server != null and server.has_method("get_command_methods"):
		var methods: Array = server.get_command_methods()
		methods.sort()
		var groups: Dictionary = {}
		for method in methods:
			var parts := str(method).split(".", true, 1)
			var domain: String = parts[0] if parts.size() > 0 else "other"
			if not groups.has(domain):
				groups[domain] = []
			groups[domain].append(str(method))
		var domain_keys: Array = groups.keys()
		domain_keys.sort()
		_add_info_row(
			tools_content,
			_t("Total", "总计"),
			_t("%d plugin-side commands", "%d 个插件端命令") % methods.size())
		_add_note_label(tools_content, _t(
			"These are the plugin-side commands. The agent-facing tool count is "
				+ "larger and lives in the MCP server (which adds LSP, discover_tools, "
				+ "and extension tools).",
			"这里列出的是插件端命令。智能体实际可用的工具更多，它们位于 MCP "
				+ "服务器中（还包括 LSP、discover_tools 和扩展工具）。"), true)
		for domain in domain_keys:
			var tools: Array = groups[domain]
			_add_note_label(tools_content, "  %s (%d): %s" % [
				str(domain).capitalize(), tools.size(),
				", ".join(PackedStringArray(tools))])
	else:
		_add_info_row(tools_content, _t("Status", "状态"), _t("server not ready", "服务器尚未就绪"))

	var multi_content := DockSectionCard.make_collapsible(
		mid, _t("Multi-Instance Multiplayer", "多实例与多人游戏"), false)
	_add_note_label(multi_content, _t(
		"A:  Two copies via git worktree — FULLY SUPPORTED\n"
			+ "     Each editor gets its own project root, registry entry, and port.\n\n"
			+ "B:  Built-in multi-instance run (F5 + multiple windows) — MOSTLY SUPPORTED\n"
			+ "     Runtime server available; editor MCP commands limited to the host.\n\n"
			+ "C:  Same directory, two editors — NOT SUPPORTED\n"
			+ "     Port collision and registry overwrite; use Pattern A instead.\n\n"
			+ "See addons/godot_mcp_toolkit/docs/multi-instance.md for full details.",
		"A：通过 git worktree 使用两份项目——完全支持\n"
			+ "     每个编辑器都有独立的项目根目录、注册项和端口。\n\n"
			+ "B：使用内置多实例运行（F5 + 多个窗口）——大部分支持\n"
			+ "     运行时服务器可用；编辑器 MCP 命令仅作用于宿主实例。\n\n"
			+ "C：同一目录打开两个编辑器——不支持\n"
			+ "     会发生端口冲突和注册项覆盖；请改用方案 A。\n\n"
			+ "完整说明请参阅 addons/godot_mcp_toolkit/docs/multi-instance.zh-CN.md。"))

	var readonly_content := DockSectionCard.make_collapsible(mid, _t("Read-Only Mode", "只读模式"), false)
	_add_note_label(readonly_content, _t(
		"For supervised environments (classrooms, CI, demos).\n"
			+ "Set GODOT_MCP_READ_ONLY=1 in your .mcp.json env to restrict\n"
			+ "the toolkit to read-only tools only. All mutating tools\n"
			+ "(create, delete, write, execute) are hidden from the AI agent.\n"
			+ "Remove GODOT_MCP_READ_ONLY from .mcp.json and reconnect the\n"
			+ "MCP client to restore full access.\n"
			+ "Read-only is applied when the MCP server launches, so changing\n"
			+ "it requires reconnecting the MCP client — existing connections\n"
			+ "keep their current setting until then.",
		"适用于课堂、CI、演示等受监督环境。\n"
			+ "在 .mcp.json 的 env 中设置 GODOT_MCP_READ_ONLY=1，可将 Toolkit\n"
			+ "限制为只读工具。所有会修改项目的工具（创建、删除、写入、执行）\n"
			+ "都会对 AI 智能体隐藏。\n"
			+ "如需恢复完整权限，请从 .mcp.json 中移除 GODOT_MCP_READ_ONLY，\n"
			+ "然后重新连接 MCP 客户端。只读模式在 MCP 服务器启动时生效，\n"
			+ "因此修改后必须重新连接；现有连接会继续使用原来的设置。"))

	var compat_content := DockSectionCard.make_collapsible(mid, _t("Compatibility", "兼容性"), false)
	_add_note_label(compat_content, _t(
		"Supports Godot 4.2 through 4.7 (untested newer versions run\n"
			+ "with a startup warning). Some tools are version-gated, so\n"
			+ "older versions expose fewer. The shipped guide covers\n"
			+ "per-version behavior, headless mode, C# (.NET) requirements,\n"
			+ "and export stripping.",
		"支持 Godot 4.2 至 4.7（更新但未经测试的版本会在启动时显示警告）。\n"
			+ "部分工具受版本限制，因此旧版 Godot 提供的工具较少。随附指南说明了\n"
			+ "各版本行为、无头模式、C#（.NET）要求以及导出时移除插件的规则。"))
	var open_compat_btn := Button.new()
	open_compat_btn.text = _t("Open Compatibility Guide", "打开兼容性指南")
	var compat_doc := _t(
		"res://addons/godot_mcp_toolkit/docs/compatibility.md",
		"res://addons/godot_mcp_toolkit/docs/compatibility.zh-CN.md")
	open_compat_btn.pressed.connect(func(): OS.shell_open(ProjectSettings.globalize_path(compat_doc)))
	compat_content.add_child(open_compat_btn)

	var skills_content := DockSectionCard.make_collapsible(mid, _t("Companion Skills", "配套技能"), false)
	_add_note_label(skills_content, _t(
		"Companion Skills are SKILL.md 'Agent Skills' — an open standard\n"
			+ "supported by Claude Code, OpenAI Codex, Cursor, and Gemini CLI.\n"
			+ "The plugin bundles skills for common toolkit workflows. Copy a\n"
			+ "skill folder into your client's skills directory:\n"
			+ "    Claude Code: .claude/skills/\n"
			+ "    OpenAI Codex: ~/.agents/skills/\n"
			+ "    Cursor: .cursor/skills/\n"
			+ "    Gemini CLI: .gemini/skills/  (or .agents/skills/)\n"
			+ "Paths follow each client's own docs and can change over time.\n"
			+ "The dock's 'Companion Skills' button opens this same folder.",
		"配套技能是采用 SKILL.md 的“智能体技能”开放标准，Claude Code、\n"
			+ "OpenAI Codex、Cursor 和 Gemini CLI 均支持。插件随附常用 Toolkit\n"
			+ "工作流技能；请将技能文件夹复制到客户端的技能目录：\n"
			+ "    Claude Code：.claude/skills/\n"
			+ "    OpenAI Codex：~/.agents/skills/\n"
			+ "    Cursor：.cursor/skills/\n"
			+ "    Gemini CLI：.gemini/skills/（或 .agents/skills/）\n"
			+ "具体路径以各客户端文档为准，今后可能变化。工具坞中的“配套技能”\n"
			+ "按钮会打开同一文件夹。"))
	var open_skills_btn := Button.new()
	open_skills_btn.text = _t("Open Skills Folder", "打开技能文件夹")
	var skills_dir := "res://addons/godot_mcp_toolkit/CompanionSkills"
	open_skills_btn.pressed.connect(func(): OS.shell_open(ProjectSettings.globalize_path(skills_dir)))
	skills_content.add_child(open_skills_btn)

	# 固定底栏——只包含本地项目资源。没有任何按钮会打开外部 URL。
	var footer := PanelContainer.new()
	footer.add_theme_stylebox_override("panel", DockSectionCard.make_section_style())
	footer.size_flags_vertical = Control.SIZE_SHRINK_END
	_content_root.add_child(footer)
	var links_row := HBoxContainer.new()
	footer.add_child(links_row)
	for pair in [
		[_t("Addon Folder", "插件目录"), "res://addons/godot_mcp_toolkit"],
		[_t("Local README", "本地说明"), _t(
			"res://addons/godot_mcp_toolkit/README.md",
			"res://addons/godot_mcp_toolkit/README.zh-CN.md")],
		[_t("Configuration", "配置说明"), _t(
			"res://addons/godot_mcp_toolkit/docs/advanced_configuration.md",
			"res://addons/godot_mcp_toolkit/docs/advanced_configuration.zh-CN.md")],
		[_t("Extending", "扩展开发"), _t(
			"res://addons/godot_mcp_toolkit/docs/extending.md",
			"res://addons/godot_mcp_toolkit/docs/extending.zh-CN.md")],
		[_t("Licenses", "授权信息"), _t(
			"res://addons/godot_mcp_toolkit/ATTRIBUTIONS.md",
			"res://addons/godot_mcp_toolkit/ATTRIBUTIONS.zh-CN.md")],
	]:
		var btn := Button.new()
		btn.text = pair[0]
		btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var local_path: String = pair[1]
		btn.pressed.connect(func(): OS.shell_open(ProjectSettings.globalize_path(local_path)))
		links_row.add_child(btn)

	# 以显式尺寸打开并提升到前台（见 EditorPopup）。显式尺寸至关重要：
	# 复用的对话框执行无参 popup_centered 会沿用窗口当前尺寸且只增不减，
	# 导致尺寸在多次重新打开间漂移。
	EditorPopup.present(self, Vector2i(520, 460))


# 只安装一次对话框外壳：标题、关闭按钮，
# 以及 show_info() 每次调用都会重新填充的内容根节点。
func _ensure_built() -> void:
	if _built:
		return
	title = _t("Godot MCP Unified — Info / Help", "Godot MCP Unified — 信息 / 帮助")
	ok_button_text = _t("Close", "关闭")
	exclusive = false
	min_size = Vector2i(520, 460)

	_content_root = VBoxContainer.new()
	_content_root.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_content_root.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add_child(_content_root)

	_built = true


# 添加一个小的可换行说明标签。`dim` 会以半透明 alpha 渲染，
# 用于主行之下的次要附注。
func _add_note_label(parent: VBoxContainer, text: String, dim := false) -> void:
	var lbl := Label.new()
	lbl.text = text
	lbl.add_theme_font_size_override("font_size", 11)
	if dim:
		lbl.add_theme_color_override("font_color", EditorLocale.muted_text_color())
	lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	parent.add_child(lbl)


func _add_info_row(parent: VBoxContainer, key: String, value: String) -> void:
	var row := HBoxContainer.new()
	parent.add_child(row)
	var k := Label.new()
	k.text = key + ":"
	k.custom_minimum_size.x = 80
	k.add_theme_font_size_override("font_size", 12)
	row.add_child(k)
	var v := Label.new()
	v.text = value
	v.add_theme_font_size_override("font_size", 12)
	v.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(v)


func _t(english: String, chinese: String) -> String:
	return EditorLocale.pick(english, chinese)
