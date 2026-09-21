@tool
extends RefCounted
## 捕获全部控制台输出的内存环形缓冲。
##
## 两种捕获策略,在 setup() 时自动选择:
##
## Godot 4.5+   —— Logger 子类(运行时经 GDScript.new() 编译,
##                以避免在旧版本上解析报错)。捕获
##                print()、printerr()、push_warning()、push_error()、
##                引擎错误、着色器错误。零延迟。
##
## Godot 4.2–4.4 —— 日志文件尾随。poll() 自上次读取偏移起
##                  从配置的日志路径读取新字节。
##                  约 500ms 轮询间隔,受 Godot 的
##                  文件刷盘时机影响。
##
## 线程安全:Logger 回调可能在任意线程触发。
## 所有缓冲访问都由 Mutex 保护。

const LogHelpers := preload("res://addons/godot_mcp_toolkit/logging/log_helpers.gd")
# 导出干净(仅核心 API)—— 在运行时自动加载(Autoload)的预加载闭包中安全,
# 该闭包会把本文件拉入。
const VersionUtils := preload("res://addons/godot_mcp_toolkit/versioning/mcp_version_utils.gd")

const _CAPACITY := 500
const _POLL_INTERVAL_MS := 200
const _SCRIPT_ERROR_LATCH_CAPACITY := 16

# -- 共享状态(Mutex 保护) ------------------------------------------

static var _mutex: Mutex = Mutex.new()
static var _entries: Array = []
static var _next_id: int = 0
# 捕获到的脚本错误(Logger error_type 2)的结构化 {id, file, line} 位置,
# 与它们的控制台条目一起锁存。有意作为单独的侧通道:
# 控制台条目保持其 {id, timestamp_unix, level, message} 的
# 线上格式,而 script_check 则恢复它刚触发的某次重载的
# 真实解析行。只有 4.5+ 的 Logger 会填充它 —— 4.2-4.4 的文件尾随
# 没有结构化的行数据。
static var _script_error_locations: Array = []

# -- 策略跟踪 --------------------------------------------------------

static var _use_logger: bool = false
static var _setup_done: bool = false
static var _logger_ref = null  # 防止被 GC —— OS.add_logger() 持有原始 C++ 指针

# -- 文件尾随状态(仅 4.2-4.4) ------------------------------------------

static var _tail_offset: int = 0
static var _tail_path: String = ""
static var _last_poll_ms: int = 0
static var _tail_open_failures: int = 0
## 尾随流中最近检测到的级别,使续行("   at: …")继承前一个
## 错误/警告级别而不是 "info"(位置行携带脚本路径)。
## 在(重新)setup 时重置。参见 LogHelpers.is_continuation_line。
static var _last_tail_level: String = "info"


# =============================================================================
# 公开 API
# =============================================================================


## 当 Logger API(4.5+)启用时返回 true;使用文件尾随(4.2-4.4)时返回 false。
## 调用方以此判断 source="buffer" 是否
## 独立于文件日志设置。
static func uses_logger_api() -> bool:
	return _use_logger


## 从 plugin.gd 的 _enter_tree() 调用一次。
static func setup() -> void:
	if _setup_done:
		return
	_setup_done = true

	if ClassDB.class_exists(&"Logger"):
		_setup_logger()
	else:
		_setup_file_tail()


## 把一条记录推入环形缓冲。线程安全。
## 由 Logger 回调(4.5+)或文件尾随器(4.2-4.4)调用。
static func push(level: String, message: String) -> void:
	_mutex.lock()
	_append_entry_unlocked(level, message)
	_mutex.unlock()


## 推送一条脚本错误(Logger error_type 2):原子地写入控制台条目及其
## 结构化的 {id, file, line} 位置锁存 —— 锁存 id
## 恰好就是该条目的 id,因此 since 游标关联绝不会错配。
## 线程安全。控制台条目的形态与 [method push] 完全一致。
static func push_script_error(level: String, message: String, file: String, line: int) -> void:
	_mutex.lock()
	var entry_id := _append_entry_unlocked(level, message)
	_script_error_locations.append({"id": entry_id, "file": file, "line": line})
	if _script_error_locations.size() > _SCRIPT_ERROR_LATCH_CAPACITY:
		_script_error_locations.pop_front()
	_mutex.unlock()


## 在 [param since_id] 之后捕获的第一条内存中脚本错误的行号,
## 若没有则返回 -1。
##
## 服务于 script_check 的重载校验:从源码构建的 GDScript(没有
## 磁盘路径)会以合成的 "gdscript://…" 路径上报(在该方案出现之前的
## 引擎上为 "built-in"),因此匹配这种形态会选中调用方
## 自己刚触发的重载错误,而绝不会选中落在同一时间窗内、针对某个
## res:// 脚本的线程化编辑器扫描错误。在 4.2-4.4 上返回 -1
## (文件尾随不锁存任何内容)—— 此时调用方应省略其行字段。
static func find_script_error_line_since(since_id: int) -> int:
	_mutex.lock()
	var found := -1
	for location in _script_error_locations:
		if int(location["id"]) <= since_id:
			continue
		var file := str(location["file"])
		if file == "built-in" or file.begins_with("gdscript://"):
			found = int(location["line"])
			break
	_mutex.unlock()
	return found


# 追加一条记录并返回其 id。调用方必须持有 _mutex。
static func _append_entry_unlocked(level: String, message: String) -> int:
	var entry := {
		"id": _next_id,
		"timestamp_unix": int(Time.get_unix_time_from_system()),
		"level": level,
		"message": LogHelpers.strip_ansi(message),
	}
	_next_id += 1
	if _entries.size() >= _CAPACITY:
		_entries.pop_front()
	_entries.append(entry)
	return int(entry["id"])


## 从缓冲读取记录。先调用 poll() 以确保新鲜度。
##
## 返回过滤后记录的截断尾部,加上两个日志读取器都会盖到线上信封上的
## 分页字段:{entries, returned, next_id, has_more,
## total_lines}。[param since_id] 是续读游标(id <= 它的记录
## 会被丢弃);返回的 next_id 是最后一条记录的 id,供下次调用使用。
static func get_entries(limit: int, level_filter: Array = [], since_id: int = -1, text_filter: String = "", text_regex: RegEx = null) -> Dictionary:
	poll()

	_mutex.lock()
	var filtered: Array = []
	for entry in _entries:
		if since_id >= 0 and int(entry["id"]) <= since_id:
			continue
		if level_filter.size() > 0 and not (str(entry["level"]) in level_filter):
			continue
		if text_filter != "":
			var msg: String = str(entry["message"])
			if text_regex != null:
				if not text_regex.search(msg):
					continue
			else:
				if msg.findn(text_filter) < 0:
					continue
		filtered.append(entry.duplicate())
	_mutex.unlock()

	# 在下面的切片把 `filtered` 重新赋值为截断尾部之前,
	# 先为 total_lines 捕获切片前的过滤计数。
	var total_lines := filtered.size()
	var has_more := filtered.size() > limit
	if has_more:
		filtered = filtered.slice(filtered.size() - limit)

	var next_id: int = -1
	if filtered.size() > 0:
		next_id = int(filtered[-1]["id"])

	# 日志分页信封的共享来源:编辑器控制台读取器与运行时的
	# debugger.get_log 都读取这些键并盖出最终的线上信封,
	# 因此这里的字段名遵循标准分页词汇。
	return {
		"entries": filtered,
		"returned": filtered.size(),
		"next_id": next_id,
		"has_more": has_more,
		"total_lines": total_lines,
	}


## 返回将分配给下一条推送记录的 ID。
## 在 game_start 之前快照此值,以便按游戏会话过滤记录。
static func get_cursor() -> int:
	_mutex.lock()
	var cursor := _next_id
	_mutex.unlock()
	return cursor


## 重置缓冲(包括脚本错误位置锁存 —— 其 id 一并重置)。
static func clear() -> void:
	_mutex.lock()
	_entries.clear()
	_script_error_locations.clear()
	_next_id = 0
	_mutex.unlock()


## 移除匹配特定级别的记录(例如 "error")。返回移除的数量。
static func clear_level(level: String) -> int:
	_mutex.lock()
	var kept: Array = []
	var removed := 0
	for entry in _entries:
		if str(entry["level"]) == level:
			removed += 1
		else:
			kept.append(entry)
	_entries = kept
	_mutex.unlock()
	return removed


## 在 4.5+ 上为空操作(Logger 处理一切)。
## 在 4.2-4.4 上,尾随日志文件以获取新行。自带节流。
## 可以每帧调用 —— 间隔未到时立即返回。
static func poll() -> void:
	if _use_logger:
		return
	var now := Time.get_ticks_msec()
	if now - _last_poll_ms < _POLL_INTERVAL_MS:
		return
	_last_poll_ms = now
	_tail_log_file()


# =============================================================================
# Logger 策略(Godot 4.5+)
# =============================================================================


## 为结构化的引擎错误组装控制台消息(Logger 的 `_log_error`
## 回调)。仿照引擎 StdLogger 的两行
## "PREFIX: <detail>\n   at: <function> (<file>:<line>)" 形态。
##
## 当 [param append_location] 为 true 时会追加 "   at:" 行,使源路径
## 存放在条目内部。4.6 及更早的编辑器在脚本扫描期间会发出单独的
## 带路径的 "Failed to load script" 错误,因此调用方在那里传 false,
## 路径不会重复;Godot 4.7 从扫描路径中移除了那条单独的消息,
## 路径只留在此回调的 file 参数中,因此调用方传 true 以保住
## 文件名被捕获(文本/控制台过滤器要匹配它)。无文件 / "built-in" 的来源
## 没有有用的路径,因此跳过位置。纯函数 —— 无编辑器符号 —— 因此在
## 无头模式下做单元测试。
static func compose_error_message(prefix: String, code: String, rationale: String,
		function_name: String, file_path: String, line: int, append_location: bool) -> String:
	var detail := code
	if rationale != "":
		detail = code + ": " + rationale
	var full := prefix + ": " + detail
	if append_location and file_path != "" and file_path != "built-in":
		full += "\n   at: " + function_name + " (" + file_path + ":" + str(line) + ")"
	return full


static func _setup_logger() -> void:
	_use_logger = true
	# 运行时编译 Logger 子类,使磁盘上没有任何文件包含
	# "extends Logger" —— 避免在 Godot 4.2-4.4 上解析报错。
	var script := GDScript.new()
	script.source_code = _LOGGER_SOURCE
	var err := script.reload()
	if err != OK:
		push_warning("[LogBuffer] Logger hook compile failed (err %d), falling back to file tail" % err)
		_use_logger = false
		_setup_file_tail()
		return
	_logger_ref = script.new()
	# 传入本脚本的引用,让 logger 能调用 push() / compose_error_message()。
	_logger_ref.set_meta("_log_buffer", load("res://addons/godot_mcp_toolkit/logging/log_buffer.gd"))
	# Godot 4.7 不再在编辑器脚本扫描期间显示那条单独的带路径
	# "Failed to load script" 错误,脚本路径只留在结构化 _log_error
	# 回调的 file 参数中。对 4.7+ 置位标志,使 logger 在脚本错误后附加引擎风格的
	# "   at:" 位置行,文件名得以保留;4.5/4.6 保持之前逐字节的
	# 输出(它们单独的加载错误已携带路径)。
	_logger_ref.set_meta("_append_error_location",
		VersionUtils.is_at_least(VersionUtils.get_engine_version_pair(), "4.7"))
	# 动态调用 —— OS.add_logger() 只存在于 4.5+;静态引用
	# 即使位于受守卫的分支内也会在 4.2-4.4 上解析报错。
	OS.call("add_logger", _logger_ref)


const _LOGGER_SOURCE := '
extends Logger

func _log_message(message: String, error: bool) -> void:
	var buf = get_meta("_log_buffer")
	if buf == null:
		return
	var level: String = "error" if error else "info"
	buf.push(level, message.strip_edges())

func _log_error(function_name: String, file_path: String, line: int,
		code: String, rationale: String, _editor_notify: bool,
		error_type: int, _script_backtraces) -> void:
	var buf = get_meta("_log_buffer")
	if buf == null:
		return
	var level: String = "warning" if error_type == 1 else "error"
	var prefix: String = "WARNING" if error_type == 1 else "ERROR"
	# error_type 2 == Logger.ERR_SCRIPT：只有脚本错误才带有值得保留的 .gd 源文件
	# 路径。4.7+ 的开关（在 setup 时依据引擎版本设置）用于启用位置行；
	# 见 compose_error_message。
	var append_location: bool = bool(get_meta("_append_error_location", false)) and error_type == 2
	var composed: String = buf.compose_error_message(prefix, code, rationale, function_name, file_path, line, append_location)
	if error_type == 2:
		# 脚本错误还会额外锁存结构化的 {file, line}：合成消息会丢弃内存脚本的
		# 这两项，而 script_check 需要它刚执行的那次重载的真实解析行号。
		buf.push_script_error(level, composed, file_path, line)
	else:
		buf.push(level, composed)
'


# =============================================================================
# 文件尾随策略(Godot 4.2-4.4)
# =============================================================================


## 在 config/name 变更导致 user:// 偏移后,重新解析日志尾随路径。
## 使用 Logger API(4.5+)时为空操作。
static func reset_tail_path() -> void:
	if _use_logger:
		return
	_tail_path = LogHelpers.resolve_log_path()
	# 不要重置偏移 —— 如果引擎一直在写同一个文件
	# (且绝对路径没有变化),我们保持自己的位置。
	# 如果确实是新文件,_tail_log_file() 会自动重置来处理
	# file_len < _tail_offset 的情况。


static func _setup_file_tail() -> void:
	# 解析为绝对操作系统路径,这样 user:// 解析结果的偶然偏移
	# (例如由 config/name 变更引起)就不会悄悄破坏尾随。
	# UserPathMonitor 在重命名时调用 reset_tail_path() 来重新解析。
	_tail_path = LogHelpers.resolve_log_path()
	# 从头开始,让缓冲捕获完整的当前会话日志。
	# 在 Windows 上,Godot 以缓冲写入方式保持日志文件打开 ——
	# 插件加载后写入的新内容在刷盘前可能对我们的读取句柄不可见。
	# 从 0 开始能确保启动消息被捕获。
	_tail_offset = 0
	_last_tail_level = "info"


static func _tail_log_file() -> void:
	if _tail_path.is_empty():
		return
	if not FileAccess.file_exists(_tail_path):
		return
	var f := FileAccess.open(_tail_path, FileAccess.READ)
	if f == null:
		_tail_open_failures += 1
		return
	var file_len: int = f.get_length()
	if file_len <= _tail_offset:
		if file_len < _tail_offset:
			# 文件被截断/轮转 —— 重置。
			_tail_offset = 0
		f.close()
		return
	f.seek(_tail_offset)
	var remaining: int = file_len - _tail_offset
	var new_bytes := f.get_buffer(remaining)
	_tail_offset = file_len
	f.close()

	var new_text := new_bytes.get_string_from_utf8()
	if new_text.is_empty():
		return
	var lines := new_text.split("\n")
	for line in lines:
		var stripped := line.strip_edges()
		if stripped.is_empty():
			continue
		# 续行("   at: …")继承前一个错误/警告级别,使多行错误保持
		# 错误级别(其位置行携带脚本路径)—— 与 source=file 读取器的
		# 合并行为以及 4.5+ Logger 的“每个错误一条记录”保持一致。
		# 参见 LogHelpers.is_continuation_line。
		var level: String
		if LogHelpers.is_continuation_line(line) \
				and (_last_tail_level == "error" or _last_tail_level == "warning"):
			level = _last_tail_level
		else:
			level = _detect_log_level(stripped)
		push(level, stripped)
		_last_tail_level = level


static func _detect_log_level(line: String) -> String:
	return LogHelpers.detect_log_level(line)
