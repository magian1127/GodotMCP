@tool
extends RefCounted
## 调试日志读取(编辑器侧):缓存式 debugger.get_log 读取器。
##
## 从引擎日志文件中读取最近一次运行会话的输出
## (user://logs/godot.log —— 由游戏/运行进程写入;编辑器进程不写入它,
## 引擎有 !editor 保护)。引擎在每次启动时都会把该文件重新截断
## (RotatedFileLogger 会把上一个日志轮转为带时间戳的备份,
## 并以 FileAccess::WRITE 重新打开基础路径),因此该文件总是恰好
## 是从字节 0 开始的当前会话 —— 本读取器读取整个文件。
## 会剥离 ANSI 转义、按文本过滤并限制行数;当存在 EditorDebuggerPlugin
## 桥接时,把它的 debug_state + error_buffer(必要时回退到
## 日志行错误扫描)合并为一个崩溃上下文响应。
##
## 拥有该子域依赖的两份状态:
##   - 会话已启动标志 + 公开的 mark_session_started() 设置器,运行控制
##     侧在 game.start 时调用它(这样在任何 game.start 之前的读取
##     会返回"尚无会话",而不是上一个编辑器会话的陈旧日志);
##   - 注入的 _debug_bridge(EditorDebuggerPlugin,与
##     debug_commands.gd 在上游共享)+ set_debug_bridge()/clear_debug_bridge()。
## 从试玩测试(playtest)命令组抽取出的子模块,通过 `preload` 别名访问。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Helpers = Modules.CommandHelpers
const LogHelpers = Modules.LogHelpers

# 在本次编辑器会话中,一旦 game.start 启动了运行会话即为 true。引擎在每次
# 启动时都会把 user://logs/godot.log 重新截断(RotatedFileLogger 构造函数 →
# rotate_file → FileAccess::WRITE;源码验证范围 4.2–4.7),因此会话一旦
# 启动,整个文件就是当前会话,读取器从字节 0 开始读取。
# 在第一次 game.start 之前保持 false,这样 debugger.get_log 会报告"尚无
# 会话",而不是上一个编辑器会话的陈旧日志。该文件由游戏/运行进程写入
# (编辑器不写入它 —— 引擎 !editor 保护),因此即使 Logger API 是进程本地的
# (4.5+),读取它也能得到游戏输出。
static var _game_session_started: bool = false

# 调试桥接引用 —— 在 register() 时注入。提供调试器日志负载中的
# error_buffer + debug_state 字段。
static var _debug_bridge: RefCounted = null


# -- 会话启动交接 -------------------------------------------------------------


## 标记运行会话已启动 —— 此后 debugger.get_log 会读取整个
## godot.log。引擎在每次启动时都会把该文件重新截断,因此该文件
## 恰好就是当前会话(从字节 0 开始读取)。由运行控制侧
## (game.start)在运行会话启动的那一刻调用。
static func mark_session_started() -> void:
	_game_session_started = true


# -- 调试桥接注入 -------------------------------------------------------------


static func set_debug_bridge(bridge: RefCounted) -> void:
	_debug_bridge = bridge


static func clear_debug_bridge() -> void:
	_debug_bridge = null


# -- 命令 ---------------------------------------------------------------------


## 编辑器侧的 debugger.get_log — 返回最近一次游戏会话的缓存日志条目。
## 从日志文件(由游戏/运行进程写入 —— 编辑器不写入它)读取,
## 而不是从内存中的 LogBuffer(它是进程本地的,在 4.5+ 上只捕获
## 编辑器输出)读取。
## 当调试桥接可用时,把错误缓冲区和调试状态合并进响应 ——
## 让服务器只需一次调用即可获得完整的崩溃上下文。
static func cmd_debugger_get_log(parameters: Dictionary) -> Dictionary:
	var limit: int = max(1, int(parameters.get("limit", 200)))
	var text_filter: String = str(parameters.get("text_filter", ""))
	var tf := Helpers.compile_text_filter(parameters)
	var text_regex: RegEx = tf[0]
	if tf[1] != null:
		return tf[1]
	var regex_warning: String = tf[2]

	if not _game_session_started:
		var response := MCPToolkitSuccess.ok(_empty_log_page(
			"No game session recorded yet (game_start was never called this editor session)"))
		_merge_debug_bridge_data(response)
		return response

	# 读取当前会话的日志文件(引擎在启动时已将其重新截断)。
	var log_path: String = LogHelpers.resolve_log_path()
	if not FileAccess.file_exists(log_path):
		var response := MCPToolkitSuccess.ok(_empty_log_page(
			"Log file not found. Enable debug/file_logging/enable_file_logging in ProjectSettings and restart."))
		_merge_debug_bridge_data(response)
		return response

	var file := FileAccess.open(log_path, FileAccess.READ)
	if file == null:
		return MCPToolkitError.fail("LOG_BUSY",
			"log file exists but could not be read (err %d)" % FileAccess.get_open_error(),
			MCPToolkitError.log_busy_hint(Modules.LogBuffer.uses_logger_api()))

	var file_len: int = file.get_length()
	if file_len <= 0:
		file.close()
		var response := MCPToolkitSuccess.ok(_empty_log_page(
			"No game output yet (log file is empty)."))
		_merge_debug_bridge_data(response)
		return response

	# 引擎在每次启动时都会把 godot.log 重新截断(见 mark_session_started),
	# 因此整个文件都是本次会话 —— 从字节 0 全部读取。
	var new_bytes := file.get_buffer(file_len)
	file.close()

	var new_text := new_bytes.get_string_from_utf8()
	var all_lines := new_text.split("\n", false)

	# 对所有行一次性剥离 ANSI(供过滤和错误扫描共同使用)。
	var stripped_lines: Array = []
	for line in all_lines:
		var stripped := LogHelpers.strip_ansi(line.strip_edges())
		if not stripped.is_empty():
			stripped_lines.append(stripped)

	# 应用文本过滤。
	var filtered: Array = []
	for stripped in stripped_lines:
		if text_filter != "":
			if text_regex != null:
				if not text_regex.search(stripped):
					continue
			else:
				if stripped.findn(text_filter) < 0:
					continue
		filtered.append(stripped)

	# 应用上限(取最后 N 行)。
	# 在切片把 `filtered` 重新赋值为截断后的尾部之前,
	# 先记录切片前的过滤计数,用作 total_lines。
	var total_lines := filtered.size()
	var truncated := filtered.size() > limit
	if truncated:
		filtered = filtered.slice(filtered.size() - limit)

	# 截断尾部分页(最旧的行先丢弃,无游标)—— 建议提高
	# limit / text_filter,而不是使用游标。
	var hint := ""
	if truncated:
		hint = "more lines remain — raise limit or narrow with text_filter (capped tail: the oldest lines drop first, no cursor)."
	var response := MCPToolkitSuccess.ok(Modules.Pagination.build(
		{"lines": filtered}, "lines", total_lines, filtered.size(), truncated,
		"", 0, hint, {"source": "cache"}))
	# 传入未过滤的行,让错误扫描始终拥有完整上下文。
	_merge_debug_bridge_data(response, stripped_lines)
	if not regex_warning.is_empty():
		response["warning"] = regex_warning
	return response


# -- 辅助函数 ------------------------------------------------------------------


## 当没有可用的会话输出时返回的空缓存日志页。
## [param note] 说明适用的是哪种无输出情形。
## 会打上标准的分页不变量(为空、没有更多),
## 使形状与有内容的读取一致。
static func _empty_log_page(note: String) -> Dictionary:
	return Modules.Pagination.build({"lines": []}, "lines", 0, 0, false, "", 0, "",
		{"source": "cache", "note": note})


## debug_state/error_buffer 响应共用的错误条目形状。
## timestamp_ms + entry_type 随来源而变(实时捕获 vs 日志扫描);所有
## 其他字段为透传。使用显式类型,让构建器永远不会从 Variant 做 := 推断 ——
## message/source/function 在调用点已做 str() 类型化。
static func make_error_entry(timestamp_ms: int, message: String, source: String,
		function: String, line: int, entry_type: String) -> Dictionary:
	return {
		"timestamp_ms": timestamp_ms,
		"message": message,
		"source": source,
		"function": function,
		"line": line,
		"type": entry_type,
	}


## 把调试桥接的错误缓冲区 + 状态合并进 debugger.get_log 响应。
## all_lines:未过滤的日志行 —— 错误扫描需要相邻的 "at:" 行,
## 而这些行可能被 text_filter 排除。
static func _merge_debug_bridge_data(response: Dictionary,
		all_lines: Array = []) -> void:
	if _debug_bridge == null:
		return
	response["debug_state"] = _debug_bridge.get_debug_state()
	var buf: Array = _debug_bridge.get_error_buffer()
	# 回退:如果 _capture 未触发(Godot 内置调试器会在插件看到
	# "error" 消息之前先行处理),则扫描日志行中的错误。
	if buf.is_empty() and not all_lines.is_empty():
		buf = _scan_lines_for_errors(all_lines)
	if not buf.is_empty():
		response["error_buffer"] = buf


## 扫描日志行中的错误模式,并构建合成的 error_buffer 条目。
## Godot 错误行遵循如下模式:
##   "USER SCRIPT ERROR: <message>"    (脚本错误)
##   "SCRIPT ERROR: <message>"         (引擎脚本错误)
##   "   at: <function> (<file>:<line>)"  (源码位置,紧跟在错误之后)
## 当 _capture 未触发时,由该回退填充 error_buffer。
static func _scan_lines_for_errors(lines: Array) -> Array:
	var errors: Array = []
	var i := 0
	while i < lines.size():
		var line: String = str(lines[i])
		var msg := ""
		if line.begins_with("USER SCRIPT ERROR:"):
			msg = line.substr(len("USER SCRIPT ERROR:")).strip_edges()
		elif line.begins_with("SCRIPT ERROR:"):
			msg = line.substr(len("SCRIPT ERROR:")).strip_edges()
		elif line.begins_with("ERROR:"):
			msg = line.substr(len("ERROR:")).strip_edges()
		if not msg.is_empty():
			var source := ""
			var func_name := ""
			var source_line := 0
			# 检查下一行是否为 "   at: func (file:line)" 模式。
			if i + 1 < lines.size():
				var next: String = str(lines[i + 1]).strip_edges()
				if next.begins_with("at:"):
					var at_info := next.substr(len("at:")).strip_edges()
					var paren_open := at_info.rfind("(")
					var paren_close := at_info.rfind(")")
					if paren_open >= 0 and paren_close > paren_open:
						func_name = at_info.left(paren_open).strip_edges()
						var loc := at_info.substr(paren_open + 1,
							paren_close - paren_open - 1)
						var colon := loc.rfind(":")
						if colon >= 0:
							source = loc.left(colon)
							source_line = int(loc.substr(colon + 1))
					i += 1  # 跳过 "at:" 行
			errors.append(make_error_entry(
				0, msg, source, func_name, source_line, "log_scan"))
		i += 1
	return errors
