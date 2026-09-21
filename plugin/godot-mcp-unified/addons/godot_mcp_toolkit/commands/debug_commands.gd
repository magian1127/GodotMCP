@tool
extends RefCounted
## debug.* 命令处理器 — 断点管理 + 调试状态。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard


static func register(registry: MCPToolkitCommandRegistry, debug_bridge: RefCounted) -> void:
	registry.add("debug.state", func(_params: Dictionary) -> Dictionary:
		return _cmd_debug_state(debug_bridge)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("debug.list_breakpoints", func(_params: Dictionary) -> Dictionary:
		return _cmd_debug_list_breakpoints()
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("debug.set_breakpoint", func(params: Dictionary) -> Dictionary:
		return _cmd_debug_set_breakpoint(params)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("debug.continue", func(_params: Dictionary) -> Dictionary:
		return _cmd_debug_continue(debug_bridge)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


# -- 命令 ---------------------------------------------------------------------


static func _cmd_debug_state(debug_bridge: RefCounted) -> Dictionary:
	var state := debug_bridge.get_debug_state() as Dictionary
	return MCPToolkitSuccess.ok(state)


static func _cmd_debug_list_breakpoints() -> Dictionary:
	var script_editor := EditorInterface.get_script_editor()
	if script_editor == null:
		return MCPToolkitSuccess.ok({"breakpoints": [], "count": 0,
			"note": "GDScript breakpoints only"})

	var open_scripts := script_editor.get_open_scripts()
	var breakpoints := []

	# 保存当前脚本,以便在遍历结束后恢复。
	var original_script = script_editor.get_current_script()

	for script in open_scripts:
		if not (script is Script):
			continue
		if not script.resource_path.ends_with(".gd"):
			continue  # 仅限 GDScript — C# 断点由 IDE 管理。

		# 切换到该脚本的标签页以访问其 CodeEdit。
		EditorInterface.edit_script(script, -1, 0, false)

		var editor := script_editor.get_current_editor()
		if editor == null:
			continue
		var code_edit := editor.get_base_editor() as CodeEdit
		if code_edit == null:
			continue

		for line_idx in code_edit.get_line_count():
			if code_edit.is_line_breakpointed(line_idx):
				breakpoints.append({
					"file_path": script.resource_path,
					"line": line_idx + 1,  # 对 API 消费方使用 1 起始计数。
				})

	# 恢复原来的脚本标签页。
	if original_script != null and original_script is Script:
		EditorInterface.edit_script(original_script, -1, 0, false)

	return MCPToolkitSuccess.ok({"breakpoints": breakpoints, "count": breakpoints.size(),
		"note": "GDScript breakpoints only"})


static func _cmd_debug_set_breakpoint(params: Dictionary) -> Dictionary:
	var file_path := str(params.get("file_path", ""))
	var line: int = int(params.get("line", 0))
	var enabled: bool = true
	if params.has("enabled"):
		enabled = bool(params.get("enabled"))

	if file_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "file_path is required")
	if line < 1:
		return MCPToolkitError.fail("INVALID_PARAMS", "line must be >= 1")

	# 拒绝非 GDScript 文件。
	if file_path.ends_with(".cs"):
		return MCPToolkitError.fail("UNSUPPORTED_FILE_TYPE",
			"Breakpoint management supports GDScript (.gd) files only. "
			+ "C# breakpoints should be set in your IDE (VS Code, Rider).")
	if not file_path.ends_with(".gd"):
		return MCPToolkitError.fail("UNSUPPORTED_FILE_TYPE",
			"Breakpoint management supports GDScript (.gd) files only.")

	# I4:FileGuard 路径校验。
	if not file_path.begins_with("res://"):
		return MCPToolkitError.fail("INVALID_PATH", "file_path must start with res://")
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))

	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND",
			"no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)

	# 加载脚本并在编辑器中打开。
	var script: Script = ResourceLoader.load(file_path) as Script
	if script == null:
		return MCPToolkitError.fail("LOAD_FAILED",
			"could not load %s as Script" % file_path)

	# 断点存在于内置脚本编辑器的 CodeEdit 上。若启用了外部
	# 编辑器,edit_script 会改为在外部编辑器中打开文件,内置
	# 标签页永远不会成为当前标签页,下方的同一性绑定就会以晦涩的
	# INTERNAL 失败。提前检测该设置并返回一个独立的引导性错误,让调用方
	# 可以请用户切换回 Godot 的内置编辑器。
	var editor_settings := EditorInterface.get_editor_settings()
	if editor_settings.has_setting("text_editor/external/use_external_editor") \
			and bool(editor_settings.get_setting("text_editor/external/use_external_editor")):
		return MCPToolkitError.fail("EXTERNAL_EDITOR_ACTIVE",
			"breakpoints require Godot's built-in script editor, but this project is set to "
			+ "an external editor",
			"Ask the user to disable Editor Settings → Text Editor → External → 'Use External "
			+ "Editor', then retry. Breakpoints are set on the built-in CodeEdit, which an "
			+ "external editor bypasses.")

	EditorInterface.edit_script(script, line)

	var script_editor := EditorInterface.get_script_editor()
	if script_editor == null:
		return MCPToolkitError.fail("INTERNAL", "ScriptEditor not available")

	# 按同一性(IDENTITY)绑定,而不是按"当前编辑器"绑定。edit_script() 本应把
	# 目标标签页置于前台,但陈旧/幽灵标签页(在 4.2-4.4 上已删除文件的
	# 脚本会保持打开,该版本没有自动关闭机制)可能让另一个标签页处于
	# 当前状态 —— 在那里设置断点会落在错误的文件上,同时却回显所
	# 请求的路径。在改动任何行之前,
	# 先确认当前编辑器确实持有 file_path。
	var current_script := script_editor.get_current_script()
	if current_script == null or current_script.resource_path != file_path:
		return MCPToolkitError.fail("INTERNAL",
			"could not open %s in the script editor to set the breakpoint" % file_path)

	var editor := script_editor.get_current_editor()
	if editor == null:
		return MCPToolkitError.fail("INTERNAL",
			"no current script editor after edit_script")
	var code_edit := editor.get_base_editor() as CodeEdit
	if code_edit == null:
		return MCPToolkitError.fail("INTERNAL", "CodeEdit not available")

	# 根据文件长度校验行号。
	var line_count := code_edit.get_line_count()
	if line > line_count:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"line %d exceeds file length (%d lines)" % [line, line_count])

	# 设置或清除断点(内部使用 0 起始计数),然后验证其是否生效。
	code_edit.set_line_as_breakpoint(line - 1, enabled)
	if code_edit.is_line_breakpointed(line - 1) != enabled:
		return MCPToolkitError.fail("INTERNAL",
			"breakpoint state did not apply on %s:%d" % [file_path, line])

	# 回显经过验证的路径(断点实际落在的那个文件),
	# 绝不回显未经校验的请求路径。
	return MCPToolkitSuccess.ok(
		{"file_path": current_script.resource_path, "line": line, "enabled": enabled})


static func _cmd_debug_continue(debug_bridge: RefCounted) -> Dictionary:
	var result := debug_bridge.try_continue() as Dictionary
	if result.has("error"):
		var code: String = result["error"]
		match code:
			"GAME_NOT_RUNNING":
				return MCPToolkitError.fail("GAME_NOT_RUNNING",
					"no active debug session — start a game with game.start first")
			"NOT_BREAKED":
				return MCPToolkitError.fail("NOT_BREAKED",
					"debug session is active but not paused at a breakpoint")
			_:
				return MCPToolkitError.fail("INTERNAL", str(result))
	return MCPToolkitSuccess.ok(result)
