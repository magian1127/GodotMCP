@tool
extends RefCounted
## script.* 命令处理器 — 针对 .gd/.cs/.gdshader/.gdshaderinc 的读取、写入、
## 精准编辑、删除与离线检查。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const Untrusted = Modules.Untrusted
const Helpers = Modules.CommandHelpers

const ALLOWED_EXTENSIONS: Array[String] = ["gd", "cs", "gdshader", "gdshaderinc"]

static var _autoload_hint_re: RegEx = _compile_autoload_hint_re()
static var _preload_hint_re: RegEx = _compile_preload_hint_re()

static func _compile_autoload_hint_re() -> RegEx:
	var re := RegEx.new()
	re.compile('Identifier "(\\w+)" not declared')
	return re

static func _compile_preload_hint_re() -> RegEx:
	var re := RegEx.new()
	re.compile('(?:[Cc]ould not p|[Pp])reload (?:resource )?(?:file|script|scene) "([^"]+)"')
	return re


static func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	registry.add("script.read", func(parameters: Dictionary) -> Dictionary:
		return _cmd_script_read(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("script.write", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_script_write(server, parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("script.edit", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_script_edit(server, parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("script.delete", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_script_delete(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("script.check", func(parameters: Dictionary) -> Dictionary:
		return _cmd_script_check(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())


# -- 命令 ---------------------------------------------------------------------


static func _cmd_script_read(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "file not found: %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	var content := FileAccess.get_file_as_string(file_path)
	var open_error := FileAccess.get_open_error()
	if open_error != OK:
		return MCPToolkitError.fail("READ_FAILED",
			"FileAccess error %d reading %s" % [open_error, file_path])

	# 范围读取:如果提供了 start_line,则返回一个行切片。
	if parameters.has("start_line"):
		var start_line := int(parameters.get("start_line", 0))
		var end_line := int(parameters.get("end_line", start_line))
		if start_line < 1:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"start_line must be >= 1 (got %d)" % start_line)
		if end_line < start_line:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"end_line must be >= start_line (got %d < %d)" % [end_line, start_line])
		var lines := content.split("\n")
		var total_lines := lines.size()
		var clamped_start := mini(start_line, total_lines)
		var clamped_end := mini(end_line, total_lines)
		var slice := lines.slice(clamped_start - 1, clamped_end)
		var result_text := "\n".join(slice)
		var result_bytes := result_text.to_utf8_buffer().size()
		var cap_kb: int = ProjectSettings.get_setting("mcp_toolkit/limits/script_read_cap_kb", 256)
		if result_bytes > cap_kb * 1024:
			return MCPToolkitError.fail("FILE_TOO_LARGE",
				"slice exceeds %d KB response cap; narrow the line range" % cap_kb)
		# 以行为单位的统一分页契约:has_more = 最后返回的行
		# 尚未到达 EOF;若是,构建器会在由这里组装的自然语言循环提示旁边加上
		# next_start_line(1 起始的续读行 = clamped_end + 1)。
		var range_has_more := clamped_end < total_lines
		var range_hint := ""
		if range_has_more:
			range_hint = "more lines remain — re-call script.read with start_line = next_start_line (%d) until has_more is false" % (clamped_end + 1)
		return MCPToolkitSuccess.ok(Modules.Pagination.line_page(
			{
				"content": Untrusted.wrap("script", file_path, result_text),
				"start_line": clamped_start,
				"end_line": clamped_end,
			},
			clamped_end, slice.size(), total_lines, range_hint))

	# 带大小上限的完整读取。
	var content_bytes := content.to_utf8_buffer().size()
	var cap_kb: int = ProjectSettings.get_setting("mcp_toolkit/limits/script_read_cap_kb", 256)
	if content_bytes > cap_kb * 1024:
		var size_err := MCPToolkitError.fail("FILE_TOO_LARGE",
			"file exceeds %d KB response cap" % cap_kb)
		size_err["total_bytes"] = content_bytes
		size_err["hint"] = "read a narrower range via script.read start_line/end_line (ranged reads bypass the cap)"
		return size_err
	# 返回完整文件 ⇒ 永远不会有 has_more。returned == total_lines 与 has_more:false
	# 补全了统一的分页契约,使两种读取形状携带相同的字段。
	var full_lines := content.split("\n")
	return MCPToolkitSuccess.ok(Modules.Pagination.build(
		{"content": Untrusted.wrap("script", file_path, content)},
		"lines", full_lines.size(), full_lines.size(), false, "", 0, ""))



static func _cmd_script_write(server: Node, parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var write_extension := file_path.get_extension().to_lower()
	if not (write_extension in ALLOWED_EXTENSIONS):
		return MCPToolkitError.fail("INVALID_PATH",
			"script.write only writes .gd, .cs, .gdshader, or .gdshaderinc files (got %s); use scene.create for .tscn, resource.write for .tres/.res, or a different tool for other file types" % file_path)
	if not parameters.has("content"):
		return MCPToolkitError.fail("INVALID_PARAMS", "missing content")
	var content := str(parameters.get("content", ""))

	var dir_result := Helpers.ensure_parent_dir(file_path, "script.write")
	if dir_result.has("error"):
		return dir_result
	var dirs_created: bool = dir_result["dirs_created"]

	var existed := FileAccess.file_exists(file_path)
	var prior_content := ""
	if existed:
		prior_content = FileAccess.get_file_as_string(file_path)
		var read_error := FileAccess.get_open_error()
		if read_error != OK:
			return MCPToolkitError.fail("READ_FAILED",
				"could not read prior content of %s (err %d)" % [file_path, read_error])

	var result := await _commit_content(server, file_path, content, prior_content, existed, write_extension)
	if result.has("error"):
		return result
	if dirs_created:
		result["dirs_created"] = true
	return result


static func _cmd_script_edit(server: Node, parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path", "old_string"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var edit_extension := file_path.get_extension().to_lower()
	if not (edit_extension in ALLOWED_EXTENSIONS):
		return MCPToolkitError.fail("INVALID_PATH",
			"script.edit only edits .gd, .cs, .gdshader, or .gdshaderinc files (got %s); use scene.create for .tscn, resource.write for .tres/.res, or a different tool for other file types" % file_path)
	# new_string 必须提供,但 "" 是合法的(空替换会删除该片段)——
	# 因此不能经过 require()(它会拒绝空字符串)。
	# 直接检查是否存在。
	if not parameters.has("new_string"):
		return MCPToolkitError.fail("INVALID_PARAMS", "missing new_string")
	var old_string := str(parameters.get("old_string", ""))
	var new_string := str(parameters.get("new_string", ""))
	# 在触碰文件之前,对空操作/畸形编辑快速失败:一个什么都不会改变的
	# 替换仍会无谓地搅动撤销/索引/诊断。
	if old_string == new_string:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"old_string and new_string are identical — the edit would change nothing")
	var replace_all := bool(parameters.get("replace_all", false))

	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	var prior_content := FileAccess.get_file_as_string(file_path)
	var read_error := FileAccess.get_open_error()
	if read_error != OK:
		return MCPToolkitError.fail("READ_FAILED",
			"FileAccess error %d reading %s" % [read_error, file_path])

	var match_count := prior_content.count(old_string)
	if match_count == 0:
		return MCPToolkitError.fail("NOT_FOUND",
			"old_string not found in %s" % file_path,
			"Re-read the file (script.read) and copy old_string byte-for-byte; whitespace and indentation must match exactly.")
	if match_count > 1 and not replace_all:
		return MCPToolkitError.fail("NOT_UNIQUE",
			"old_string matches %d times in %s" % [match_count, file_path],
			"Add surrounding context so old_string identifies one location, or set replace_all:true to replace all %d." % match_count)

	var new_content: String
	var replacements: int
	if replace_all:
		new_content = prior_content.replace(old_string, new_string)
		replacements = match_count
	else:
		# 恰好一个匹配:在找到的偏移处拼接,而不是用 String.replace(它会
		# 替换所有出现),让其余字节保持原样。
		var at := prior_content.find(old_string)
		new_content = prior_content.substr(0, at) + new_string + prior_content.substr(at + old_string.length())
		replacements = 1

	# 文件已存在(上面检查过),因此撤销会恢复 prior_content。
	var result := await _commit_content(server, file_path, new_content, prior_content, true, edit_extension)
	if result.has("error"):
		return result
	result["replacements"] = replacements
	return result


## 把 [param content] 写入 [param file_path],把它包装进一个编辑器
## UndoRedo 动作,重新索引该文件,并且 —— 对 .gd 文件 —— 附加内联
## 诊断以及陈旧活动实例提示。由 [method _cmd_script_write]
## (整文件)与 [method _cmd_script_edit](精准编辑)共享,
## 使两者走完全相同的写入/撤销/索引/诊断管线。[param prior_content] 是
## 本次写入之前文件的内容([param existed] 为 false 时为空)—— 撤销时
## 恢复它,或在文件原本不存在时删除该文件。[param extension] 是小写的
## 文件扩展名,用于限定仅 .gd 才有的诊断。
## 返回成功信封 [code]{bytes, undoable, indexed, valid?, diagnostics?, hint?}[/code],
## 写入失败时返回 [method MCPToolkitError.fail] 信封。
static func _commit_content(server: Node, file_path: String, content: String,
		prior_content: String, existed: bool, extension: String) -> Dictionary:
	var write_error := _write_file_raw(file_path, content)
	if write_error != OK:
		return MCPToolkitError.fail("WRITE_FAILED",
			"could not open %s for write (err %d)" % [file_path, write_error])

	var _undo := MCPToolkitUndoRedoAction.begin("script_write: %s" % file_path) \
		.do_method(server.undo_helpers._write_file_silent.bind(file_path, content))
	var undoable := _undo.is_active()
	if existed:
		_undo.undo_method(server.undo_helpers._write_file_silent.bind(file_path, prior_content))
	else:
		_undo.undo_method(server.undo_helpers._delete_file_silent.bind(file_path))
	_undo.commit_recorded()

	var index_result := await Helpers.ensure_file_indexed(file_path)

	var bytes_written := content.to_utf8_buffer().size()
	var result := MCPToolkitSuccess.ok({"bytes": bytes_written, "undoable": undoable,
		"indexed": index_result["indexed"]})

	# 内联 GDScript 诊断 — 与 script_check 相同的校验。
	if extension == "gd":
		var validation := _validate_gdscript(content)
		result["valid"] = validation["valid"]
		result["diagnostics"] = validation["diagnostics"]

		# 主动的陈旧活动实例提示。编辑一个在 Godot < 4.4 上编译正常的
		# 已存在 .gd,在重启之前不会到达活动实例
		# (新增成员与修改方法体都是;新建节点也无济于事 ——
		# 经验性刻画,边界为 4.3->4.4)。追加到响应提示之后
		# (校验指引在前,陈旧提醒占据最近的槽位)。
		var version := Modules.VersionUtils.get_engine_version_ints()
		if Modules.StaleInstanceHint.should_warn_on_write(existed, validation["valid"], extension, version.x, version.y):
			result["hint"] = Modules.StaleInstanceHint.write_hint(Modules.VersionUtils.get_engine_version_pair())

	return result


static func _cmd_script_delete(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var extension := file_path.get_extension().to_lower()
	if not (extension in ALLOWED_EXTENSIONS):
		return MCPToolkitError.fail("INVALID_PATH",
			"script.delete only removes .gd, .cs, .gdshader, or .gdshaderinc files (got %s); use scene.delete for .tscn, resource.delete for .tres/.res, or a different tool for other file types" % file_path)
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	var delete_result: Dictionary = await Helpers.delete_res_file_and_deindex(file_path)
	return delete_result


# -- 文件 I/O 辅助函数(由 UndoRedo 经 server 节点引用)-----------------------


static func _cmd_script_check(parameters: Dictionary) -> Dictionary:
	var file_path := str(parameters.get("file_path", ""))
	if file_path == "":
		return MCPToolkitError.fail("INVALID_PARAMS", "file_path is required")
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var extension := file_path.get_extension().to_lower()
	if extension != "gd":
		return MCPToolkitError.fail("INVALID_PARAMS",
			"script.check only supports .gd files (got .%s)" % extension)
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)

	var content := FileAccess.get_file_as_string(file_path)
	var read_error := FileAccess.get_open_error()
	if read_error != OK:
		return MCPToolkitError.fail("READ_FAILED",
			"FileAccess error %d reading %s" % [read_error, file_path])

	var validation := _validate_gdscript(content)
	return MCPToolkitSuccess.ok({
		"file_path": file_path,
		"valid": validation["valid"],
		"diagnostics": validation["diagnostics"],
	})


## 通过 GDScript.new().reload() 校验 GDScript 源码 — 进程内安全的解析。
## 此处不要使用带 CACHE_MODE_IGNORE 的 ResourceLoader.load():
## 在所有 Godot 版本上它都会损坏已加载的脚本(P-056)。
## 由 script_write(内联诊断)与 script_check 共享。
##
## 诊断只携带真实字段:当 4.5+ 的 Logger 捕获从 reload 错误中
## 恢复出错误行时,error 条目会带上 "line"(实际的解析/编译错误行);
## 在 4.2-4.4 上(文件尾捕获 — 没有结构化行号)该键被省略,
## 绝不伪造 0。"col" 永不输出 —— 列属于
## lsp_diagnostics 的领域。
static func _validate_gdscript(source: String) -> Dictionary:
	# 去除 class_name 以防止误报的冲突(P-053)。
	# GDScript.new().reload() 会注册该名称的第二个副本,与已注册的
	# 全局类相撞。把该行置空可保持行号不变,
	# 使任何真实错误仍报告正确的位置。
	var lines := source.split("\n")
	for i in lines.size():
		if lines[i].strip_edges().begins_with("class_name "):
			lines[i] = ""
			break
	# 记录 LogBuffer 位置的快照,以便扫描 reload() 产生的错误。
	# _next_id 是下一条被推送条目将获得的 ID;since_id 使用
	# "id > since_id 的条目",因此要减 1 以包含第一条。
	var pre_id: int = Modules.LogBuffer._next_id - 1
	var script := GDScript.new()
	script.source_code = "\n".join(lines)
	var is_valid := script.reload(false) == OK

	var diagnostics: Array = []
	if not is_valid:
		var error_diagnostic := {
			"severity": "error",
			"message": _compile_error_message(Modules.VersionUtils.get_engine_version_pair()),
		}
		# 真实的错误行,从本次调用刚触发的 reload 错误的 Logger 捕获中
		# 恢复(内存中的脚本以合成的 "gdscript://…" 路径报告,
		# 因此闩锁匹配到的就是我们自己的 —— 从快照到扫描的窗口在主线程上
		# 同步运行)。-1(未恢复到任何内容 —— 4.2-4.4,或 Logger 钩子失败)
		# 时省略该键。
		var real_line: int = Modules.LogBuffer.find_script_error_line_since(pre_id)
		if real_line > 0:
			error_diagnostic["line"] = real_line
		diagnostics.append(error_diagnostic)
		# 扫描 reload 错误中与 autoload 匹配的未解析标识符。
		# 提示条目不带行号 —— 提示针对的是标识符,而不是
		# 源码位置。
		var hints := _check_autoload_hints(pre_id)
		hints.append_array(_check_preload_hints(pre_id))
		for hint in hints:
			diagnostics.append({
				"severity": "hint",
				"message": hint,
			})
	return MCPToolkitSuccess.ok({"valid": is_valid, "diagnostics": diagnostics})


## 版本感知的编译错误诊断消息。log_read(channel:'editor') 只在 Godot 4.5+ 上
## 能呈现编辑器的解析(PARSE)错误(Logger API 挂钩了编辑器的错误流);在
## 4.2-4.4 上,文件日志捕获的是运行中游戏的输出,而非编辑器解析错误,因此
## 在那里把 LLM 引向 log_read 是死路 —— 应改为指向 lsp_diagnostics(4.2+ 可用)。
## 两段文本还都引导"这次修改是否破坏了另一个脚本"的情形:在 4.5+ 上,
## editor_sync→log_read 是更省力的尽力而为的第一遍(它只重载被修改且已打开的
## 脚本,因此一个未打开的受损依赖可能被误读为干净),而
## lsp_diagnostics(scope:'project') 是有保证的全项目扫描;在 4.2-4.4 上它是唯一的
## 全项目编译信号(4.5 之前控制台无法呈现全项目的解析错误)。
## engine_ver 是 Modules.VersionUtils.get_engine_version_pair()(如 "4.5")。
static func _compile_error_message(engine_ver: String) -> String:
	const CONSOLE := "GDScript compile error. Call log_read(channel:'editor') for detailed messages with line numbers. To check whether the change broke another script, editor_sync then log_read(channel:'editor', level_filter:['error']) is a cheaper first pass (best-effort — misses unopened dependents); lsp_diagnostics(scope:'project') is the guaranteed whole-project scan."
	const LSP := "GDScript compile error. On Godot <4.5 editor parse errors are not surfaced by log_read(channel:'editor') — call lsp_diagnostics for line-level detail (or read the script with workspace file tools). To check whether the change broke another script, use lsp_diagnostics(scope:'project') (the only project-wide compile signal on 4.2-4.4)."
	return CONSOLE if Modules.VersionUtils.is_at_least(engine_ver, "4.5") else LSP


## 扫描 reload() 期间发出的 LogBuffer 错误,查找与已注册 autoload
## 匹配的未解析标识符,返回可操作的提示字符串。
static func _check_autoload_hints(pre_id: int) -> Array:
	var buf := Modules.LogBuffer.get_entries(50, ["error"], pre_id)
	var entries: Array = buf.get("entries", [])
	var seen := {}
	var hints: Array = []
	for entry in entries:
		var msg: String = str(entry.get("message", ""))
		var m := _autoload_hint_re.search(msg)
		if m == null:
			continue
		var ident: String = m.get_string(1)
		if seen.has(ident):
			continue
		seen[ident] = true
		if ProjectSettings.has_setting("autoload/" + ident):
			hints.append(
				"Identifier '%s' is a registered autoload. The editor cache may be stale — call autoload_manage with action='register' to refresh it, or reference via get_node('/root/%s')." % [ident, ident])
		else:
			# 对看起来像单例的 PascalCase 名称给出软提示。
			if ident.length() >= 2 and ident[0] == ident[0].to_upper() and ident[0] != ident[0].to_lower():
				hints.append(
					"Identifier '%s' not declared — if this is an autoload singleton, register it first with autoload_manage (action='register', name='%s', script_path='res://...')." % [ident, ident])
	return hints


## 扫描 LogBuffer 错误中引用缺失文件的 preload() 失败。
static func _check_preload_hints(pre_id: int) -> Array:
	var buf := Modules.LogBuffer.get_entries(50, ["error"], pre_id)
	var entries: Array = buf.get("entries", [])
	var seen := {}
	var hints: Array = []
	for entry in entries:
		var msg: String = str(entry.get("message", ""))
		var m := _preload_hint_re.search(msg)
		if m == null:
			continue
		var path: String = m.get_string(1)
		if seen.has(path):
			continue
		seen[path] = true
		if not FileAccess.file_exists(path):
			hints.append(
				"preload('%s') failed because the file doesn't exist yet. Use load() instead — it evaluates at runtime when the file will exist. Or create the file first, then use preload()." % path)
	return hints


static func _write_file_raw(file_path: String, content: String) -> int:
	var file := FileAccess.open(file_path, FileAccess.WRITE)
	if file == null:
		return FileAccess.get_open_error()
	file.store_string(content)
	file.close()
	return OK
