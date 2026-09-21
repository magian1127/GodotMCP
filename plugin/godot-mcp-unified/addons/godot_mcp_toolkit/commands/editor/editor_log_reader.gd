@tool
extends RefCounted
## editor.* 日志/控制台读取器:读取编辑器捕获的输出 — 来自内存中的 LogBuffer
## (source="buffer")或某个 user://logs/*.log 文件
## (source="file") — 按级别/文本/since_id 过滤,经清洗(脱敏)后,
## 整形为条目列表。服务于控制台读取工具。
##
## 无状态 — 每个处理器接收 (server, parameters) 并返回响应
## Dictionary;读取主干辅助函数以参数形式接收其输入。日志
## 子系统(LogBuffer / LogHelpers)、机密信息清洗器、不可信内容
## 包装器以及文本过滤器编译器均通过 Modules 别名使用。editor_commands.gd
## 通过 `preload` 别名消费本模块。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Untrusted = Modules.Untrusted
const Scrubber = Modules.Scrubber
const Helpers = Modules.CommandHelpers
const LogHelpers = Modules.LogHelpers


# -- 命令 ---------------------------------------------------------------------


static func cmd_get_console(server: Node, parameters: Dictionary) -> Dictionary:
	# clear_buffer 在读取前清空过期的日志条目。
	var clear_buffer: bool = parameters.get("clear_buffer", false) == true
	if clear_buffer:
		Modules.LogBuffer.clear()

	var limit: int = int(parameters.get("limit", 200))
	var level_filter: Array = parameters.get("level_filter", [])
	if typeof(level_filter) != TYPE_ARRAY:
		level_filter = []
	var since_id: int = int(parameters.get("since_id", -1))
	var source: String = str(parameters.get("source", "buffer"))

	if limit < 1 or limit > 1000:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"limit must be in [1, 1000] (got %d)" % limit)
	if not (source in ["buffer", "file"]):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"source must be 'buffer' or 'file' (got %s)" % source)
	var valid_levels := ["info", "warning", "error"]
	for level_filter_entry in level_filter:
		if not str(level_filter_entry) in valid_levels:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"level_filter entries must be one of 'info' | 'warning' | 'error' (got %s)" % str(level_filter_entry))

	var tf := _compile_text_filter(parameters)
	if tf[2] != null:
		return tf[2]
	var text_filter: String = tf[0]
	var text_regex: RegEx = tf[1]
	var regex_warning: String = tf[3]

	var result: Dictionary
	if source == "file":
		result = _read_console_log(server, limit, level_filter, since_id, text_filter, text_regex)
	else:
		result = _read_buffer_log(limit, level_filter, since_id, text_filter, text_regex)
	if not regex_warning.is_empty() and result.get("success", false):
		result["warning"] = regex_warning
	# 无头(headless)编辑器不会重新验证脚本,因此返回 count:0 的
	# 错误捕获请求读起来像"没有匹配",而不是"编辑器解析错误不在此处捕获"。
	# 只要请求了错误捕获(level_filter 含 "error" 或设置了 text_filter),
	# 就引导使用 script_check — 无论匹配数量多少。由 is_headless 门控,因此
	# 显示响应逐字节相同;只是附加提示,捕获机制
	# 不受影响。
	if Modules.VersionUtils.is_headless() and result.get("success", false):
		var wants_error_capture := ("error" in level_filter) or (text_filter != "")
		if wants_error_capture:
			result["headless_hint"] = "headless editors don't revalidate scripts, so editor parse errors aren't captured here — use script_check(path) to validate a specific script's parse status."
	return result


# -- 辅助函数 ------------------------------------------------------------------


## 将 text_filter 参数编译为 [text_filter, RegEx-or-null, error-or-null, warning]。
## 正则编译与双重转义检测委派给 Helpers.compile_text_filter。
static func _compile_text_filter(parameters: Dictionary) -> Array:
	var text_filter: String = str(parameters.get("text_filter", ""))
	var is_regex: bool = bool(parameters.get("is_regex", false))
	if text_filter == "" or not is_regex:
		return [text_filter, null, null, ""]
	var tf := Helpers.compile_text_filter(parameters)
	# tf = [RegEx 或 null, 错误或 null, 警告]
	if tf[1] != null:
		return ["", null, tf[1], ""]
	return [text_filter, tf[0], null, tf[2]]


static func _godot_error_name(code: int) -> String:
	match code:
		0: return "OK"
		7: return "ERR_FILE_NOT_FOUND"
		12: return "ERR_CANT_OPEN"
		13: return "ERR_CANT_WRITE"
		31: return "ERR_FILE_CANT_WRITE"
		32: return "ERR_FILE_CANT_READ"
		_: return "Error(%d)" % code


# -- 缓冲区日志读取器 ---------------------------------------------------------


static func _read_buffer_log(limit: int, level_filter: Array, since_id: int, text_filter: String = "", text_regex: RegEx = null) -> Dictionary:
	var buf_result: Dictionary = Modules.LogBuffer.get_entries(limit, level_filter, since_id, text_filter, text_regex)
	var entries: Array = buf_result["entries"]
	for entry in entries:
		var scrubbed := Scrubber.scrub(str(entry["message"]), "console")
		entry["message"] = scrubbed["text"]
	var has_more: bool = buf_result["has_more"]
	# 封顶尾部(capped-tail)行分页:恢复游标是 next_id(经 since_id 回传),
	# 而非 next_offset,因此对共享构建器而言这是无游标的。
	var next_id: int = int(buf_result["next_id"])
	var hint := ""
	if has_more:
		hint = "more lines remain — re-call editor.get_console with since_id = next_id (%d) until has_more is false" % next_id
	var response := Modules.Pagination.build(
		{"entries": Untrusted.wrap("console", "buffer", JSON.stringify(entries))},
		"lines", int(buf_result["total_lines"]), int(buf_result["returned"]), has_more,
		"", 0, hint, {"next_id": next_id, "source": "buffer"})
	# 在 4.2-4.4 上,缓冲区靠尾随日志文件实现 — 若读取为空且文件日志已关闭,
	# 或日志文件无法读取(在 Windows 上被操作系统锁定),则发出警告。4.5 之前控制台
	# 只捕获正在运行的游戏的输出(没有编辑器侧捕获),因此这里的空读取可能
	# 只意味着输出根本不可达 — 引导使用编译状态工具。
	if entries.is_empty() and not Modules.LogBuffer.uses_logger_api():
		if not LogHelpers.is_file_logging_enabled():
			response["warning"] = "On Godot 4.2-4.4 the log buffer captures output by tailing the log file. Enable debug/file_logging/enable_file_logging in ProjectSettings and restart the editor for output capture to work. Editor-only output isn't captured pre-4.5 without a running game — to check compile status use script_check (one file) or lsp_diagnostics(scope:'project') (whole project)."
		elif Modules.LogBuffer._tail_open_failures > 0:
			response["warning"] = "Log file could not be opened for reading (%d failed attempts) — the OS may be locking it. Use source=\"file\" as a fallback." % Modules.LogBuffer._tail_open_failures
	# Autoload(自动加载)提示 — 扫描错误条目,寻找与已注册 Autoload 匹配的未解析标识符。
	if level_filter.is_empty() or ("error" in level_filter):
		var al_hint := _scan_autoload_hints(entries)
		if not al_hint.is_empty():
			response["autoload_hint"] = al_hint
	return MCPToolkitSuccess.ok(response)


## 扫描控制台条目中与已注册 Autoload 匹配的"Identifier X not declared"错误,
## 返回合并后的提示字符串(没有则为空)。
static func _scan_autoload_hints(entries: Array) -> String:
	var re := RegEx.new()
	re.compile('Identifier "(\\w+)" not declared')
	var found: Array = []
	for entry in entries:
		var msg: String = str(entry.get("message", ""))
		var m := re.search(msg)
		if m == null:
			continue
		var ident: String = m.get_string(1)
		if ProjectSettings.has_setting("autoload/" + ident) and not (ident in found):
			found.append(ident)
	if found.is_empty():
		return ""
	return "Some errors reference registered autoloads (%s). The editor cache may be stale — call autoload_manage to re-register them." % ", ".join(found)


# -- 控制台日志读取器 ---------------------------------------------------------


static func _detect_log_level(line: String) -> String:
	return LogHelpers.detect_log_level(line)


## 构造 source="file" 的 LOG_UNAVAILABLE 失败,并附带按版本门控的恢复提示
## (仅 Godot 4.5+ 才引导使用缓冲区),无头运行时附加 `headless_hint`。
## 无头的 `--editor` 从不写入 `user://logs/godot.log` — 在所有版本中,文件日志
## 在编辑器模式下都被硬性禁用(引擎的 `!editor` 守卫条件,见 main.cpp)—
## 因此文件缺失是预期行为,而非配置错误;应引导调用者
## 使用 source="buffer"。由 is_headless 门控,因此显示响应逐字节相同。
static func _log_unavailable_for_file(message: String) -> Dictionary:
	var result := MCPToolkitError.fail("LOG_UNAVAILABLE", message,
			MCPToolkitError.log_unavailable_hint(Modules.LogBuffer.uses_logger_api()))
	if Modules.VersionUtils.is_headless():
		result["headless_hint"] = "editor log file is unavailable — headless editors don't write one (file logging is disabled in editor mode) — use source=\"buffer\" (the default; in-memory, works headless on Godot 4.5+)."
	return result


static func _read_console_log(
	server: Node, limit: int, level_filter: Array, since_id: int,
	text_filter: String = "", text_regex: RegEx = null,
) -> Dictionary:
	# 读取 user://logs/ 是对"仅限 res://"规则的一个窄化只读例外。
	# 路径由内部构造(非用户提供),因此不经 FileGuard 门控。
	# 尊重被重定位的 debug/file_logging/log_path:对所配置日志的
	# 所在目录做扫描,寻找其轮转兄弟文件,并优先使用所配置的文件名。使用
	# 原始(未经全局化)的所配置路径,使默认值保持 user://logs,
	# 并让常见情形下响应中的 log_file 元数据逐字节不变。
	var configured_log := LogHelpers.configured_log_path()
	var logs_dir := configured_log.get_base_dir()
	var preferred_name := configured_log.get_file()
	var file_logging_enabled: bool = LogHelpers.is_file_logging_enabled()
	if not DirAccess.dir_exists_absolute(logs_dir):
		if not file_logging_enabled:
			var _hint := "file logging is disabled — enable it in ProjectSettings → Debug → File Logging → Enable File Logging, then restart the editor"
			if Modules.LogBuffer.uses_logger_api():
				_hint += "; alternatively use source=\"buffer\" (default) which captures all output in real-time"
			else:
				_hint += ". On Godot 4.2-4.4 source=\"buffer\" also depends on file logging, so both sources require this setting"
			return _log_unavailable_for_file(_hint)
		return _log_unavailable_for_file(
			"no log directory at %s/ — verify file logging is enabled in ProjectSettings → Debug → File Logging → Enable File Logging" % logs_dir)

	var all_files := DirAccess.get_files_at(logs_dir)
	var log_files: Array[String] = []
	for file_name in all_files:
		if String(file_name).ends_with(".log"):
			log_files.append(String(file_name))
	if log_files.is_empty():
		if not file_logging_enabled:
			var _hint := "file logging is disabled — enable it in ProjectSettings → Debug → File Logging → Enable File Logging, then restart the editor"
			if Modules.LogBuffer.uses_logger_api():
				_hint += "; alternatively use source=\"buffer\" (default) which captures all output in real-time"
			else:
				_hint += ". On Godot 4.2-4.4 source=\"buffer\" also depends on file logging, so both sources require this setting"
			return _log_unavailable_for_file(_hint)
		return _log_unavailable_for_file(
			"no .log files under %s/ — verify file logging is enabled in ProjectSettings → Debug → File Logging → Enable File Logging" % logs_dir)

	var plugin_boot_time: int = server.get_plugin_boot_time()

	var chosen_file := ""
	var chosen_mtime: int = 0
	var warnings: Array[String] = []
	if not file_logging_enabled:
		if Modules.LogBuffer.uses_logger_api():
			warnings.append("file logging is disabled — data may be stale from a previous session; use source=\"buffer\" for real-time output")
		else:
			warnings.append("file logging is disabled — data may be stale from a previous session. On Godot 4.2-4.4 source=\"buffer\" also depends on file logging, so both sources may be stale")

	var preferred_log := logs_dir + "/" + preferred_name
	var preferred_log_mtime: int = 0
	if FileAccess.file_exists(preferred_log):
		preferred_log_mtime = FileAccess.get_modified_time(preferred_log)
	if preferred_log_mtime > 0 and preferred_log_mtime >= plugin_boot_time:
		chosen_file = preferred_log
		chosen_mtime = preferred_log_mtime
	else:
		var best_file := ""
		var best_mtime: int = 0
		for log_file_name in log_files:
			var full_path := logs_dir + "/" + log_file_name
			var mtime := FileAccess.get_modified_time(full_path)
			if mtime >= plugin_boot_time and mtime > best_mtime:
				best_file = full_path
				best_mtime = mtime
		if best_file != "":
			chosen_file = best_file
			chosen_mtime = best_mtime
		else:
			for log_file_name in log_files:
				var full_path := logs_dir + "/" + log_file_name
				var mtime := FileAccess.get_modified_time(full_path)
				if mtime > best_mtime:
					best_file = full_path
					best_mtime = mtime
			if best_file != "":
				chosen_file = best_file
				chosen_mtime = best_mtime
				warnings.append("fallback to stale log — no post-boot log found")

	# Windows 4.4.0 get_modified_time 的自身冲突。引擎的日志器以 GENERIC_WRITE
	# 加"不拒绝任何操作"的共享模式打开活动会话日志(FileAccess WRITE 从不
	# 设置 backup_save -> _SH_DENYNO,任何版本皆然),因此读取方的打开总是
	# 成功,日志从未真正被锁。但在 4.4.0 上,get_modified_time 会用它自己的
	# CreateFileW(GENERIC_READ, FILE_SHARE_READ) 探测,这会拒绝活动写入方
	# -> 共享冲突 -> mtime 读到 0。于是上面 mtime>best_mtime 的循环
	# 永远选不中活动日志,尽管目录枚举(属性级,
	# 对锁免疫)刚刚把它列了出来。改为带着活动日志候选继续往下走(存在时
	# 即所配置的文件名),让打开操作自行裁决。范围限定于 4.4.0
	# (4.4.1 已放宽;4.5 改用带完全共享的 FILE_READ_ATTRIBUTES);
	# 4.2/4.3 使用无句柄的 _wstat,可以正常读取活动文件,因此它们
	# 经由循环正常选择。在 POSIX 上不可达(stat 对不拒绝任何操作的写入方总能成功)。
	if chosen_file == "" and not log_files.is_empty():
		var live_name := preferred_name if log_files.has(preferred_name) else log_files[0]
		chosen_file = logs_dir + "/" + live_name

	if chosen_file == "":
		return _log_unavailable_for_file(
			"no readable log file under %s/ — verify file logging is enabled in ProjectSettings → Debug → File Logging → Enable File Logging; playtest may have rotated the editor's log mid-session" % logs_dir)

	var file_handle := FileAccess.open(chosen_file, FileAccess.READ)
	if file_handle == null:
		var open_err := FileAccess.get_open_error()
		# 存在但不可读 -> LOG_BUSY;真正不存在的文件 -> LOG_UNAVAILABLE。
		# 在 Windows 上,这发生在游戏运行期间:编辑器启用了安全保存
		# (FileAccess::backup_save),因此我们的 READ 打开会请求 _SH_DENYWR(拒绝写入),
		# 而运行中的游戏进程持有日志进行写入时,操作系统会拒绝该请求 —
		# 游戏自己的运行时读取器(共享)与 source=buffer 不受影响,且游戏退出后
		# 即恢复。更罕见的情况:没有游戏运行时的外部占用者
		# (杀毒软件/文件同步/备份)。POSIX 没有强制共享锁,因此该路径在 POSIX 上不可达。
		if FileAccess.file_exists(chosen_file) or log_files.has(chosen_file.get_file()):
			return MCPToolkitError.fail("LOG_BUSY",
				"log file exists but could not be read (%s)" % _godot_error_name(open_err),
				MCPToolkitError.log_busy_hint(Modules.LogBuffer.uses_logger_api()))
		return MCPToolkitError.fail("LOG_UNAVAILABLE",
			"cannot open %s (%s)" % [chosen_file, _godot_error_name(open_err)],
			MCPToolkitError.log_unavailable_hint(Modules.LogBuffer.uses_logger_api()))
	var content := file_handle.get_as_text()
	file_handle.close()

	var lines := content.split("\n")
	var entries: Array = []
	var char_offset: int = 0

	for line_index in range(lines.size()):
		var line: String = LogHelpers.strip_ansi(lines[line_index])
		if line.strip_edges().is_empty():
			char_offset += line.length() + 1
			continue
		var level := _detect_log_level(line)
		if level == "info" and entries.size() > 0 and LogHelpers.is_continuation_line(line):
			var previous: Dictionary = entries[-1]
			if previous["level"] == "error" or previous["level"] == "warning":
				previous["message"] += "\n" + line
				char_offset += line.length() + 1
				continue
		entries.append({
			"id": char_offset,
			"level": level,
			"message": line,
			"timestamp_unix": null,
		})
		char_offset += line.length() + 1

	if level_filter.size() > 0:
		var level_set: Array[String] = []
		for filter_entry in level_filter:
			level_set.append(str(filter_entry))
		var filtered: Array = []
		for entry in entries:
			if entry["level"] in level_set:
				filtered.append(entry)
		entries = filtered

	if since_id >= 0:
		var filtered: Array = []
		for entry in entries:
			if entry["id"] > since_id:
				filtered.append(entry)
		entries = filtered

	if text_filter != "":
		var text_filtered: Array = []
		for entry in entries:
			var msg: String = str(entry["message"])
			if text_regex != null:
				if text_regex.search(msg):
					text_filtered.append(entry)
			else:
				if msg.findn(text_filter) >= 0:
					text_filtered.append(entry)
		entries = text_filtered

	# 在下面把 `entries` 重新赋值为封顶尾部之前,先捕获切片前的条目数
	# 作为 total_lines。
	var total_lines := entries.size()
	var has_more := entries.size() > limit
	if has_more:
		entries = entries.slice(entries.size() - limit)

	var next_id: int = -1
	if entries.size() > 0:
		next_id = entries[-1]["id"]

	for entry in entries:
		var scrubbed := Scrubber.scrub(str(entry["message"]), "console")
		entry["message"] = scrubbed["text"]

	# 封顶尾部行分页(经 next_id → since_id 恢复,对构建器而言无游标)。
	var hint := ""
	if has_more:
		hint = "more lines remain — re-call editor.get_console with since_id = next_id (%d) until has_more is false" % next_id
	var response := Modules.Pagination.build(
		{"entries": Untrusted.wrap("console", str(chosen_file), JSON.stringify(entries))},
		"lines", total_lines, entries.size(), has_more, "", 0, hint,
		{
			"next_id": next_id,
			"log_file": chosen_file,
			"log_mtime": chosen_mtime,
			"warnings": warnings,
		})
	return MCPToolkitSuccess.ok(response)
