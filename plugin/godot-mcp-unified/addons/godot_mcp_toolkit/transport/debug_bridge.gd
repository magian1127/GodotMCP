@tool
extends EditorDebuggerPlugin
## EditorDebuggerPlugin 子类 — 跟踪调试器会话状态,并通过 send_message 提供
## 继续执行。
##
## 由 plugin.gd 经 add_debugger_plugin / remove_debugger_plugin 注册/注销
## (I12 对称性)。会话信号供 debug.state 命令使用;send_message("continue")
## 供 debug.continue 使用。

## 共享的错误条目形态(timestamp 与 type 因来源而异;其余原样透传)。
const PlaytestLogReader := preload("res://addons/godot_mcp_toolkit/commands/playtest_log_reader.gd")

const _ERROR_BUFFER_MAX := 50

var _session: EditorDebuggerSession = null
var _session_id: int = -1
var _active: bool = false
var _breaked: bool = false
var _can_debug: bool = false
var _error_buffer: Array = []


func _setup_session(session_id: int) -> void:
	var session := get_session(session_id)
	if session == null:
		return
	# 断开上一个会话的信号(游戏重启场景)。
	_disconnect_session()
	_session = session
	_session_id = session_id
	session.started.connect(_on_started)
	session.stopped.connect(_on_stopped)
	session.breaked.connect(_on_breaked)
	session.continued.connect(_on_continued)


func _disconnect_session() -> void:
	if _session == null:
		return
	if _session.started.is_connected(_on_started):
		_session.started.disconnect(_on_started)
	if _session.stopped.is_connected(_on_stopped):
		_session.stopped.disconnect(_on_stopped)
	if _session.breaked.is_connected(_on_breaked):
		_session.breaked.disconnect(_on_breaked)
	if _session.continued.is_connected(_on_continued):
		_session.continued.disconnect(_on_continued)


func _has_capture(capture: String) -> bool:
	return capture == "error"


func _capture(message: String, data: Array, session_id: int) -> bool:
	# 仅处理错误消息(不含 error:clear_execution 等)。
	# message 可能是 "error:error"(完整)或 "error"(前缀被剥离)。
	if not (message == "error" or message == "error:error"):
		return false
	_parse_and_buffer_error(data)
	return false  # 不抑制内建处理


func _on_started() -> void:
	_active = true
	_breaked = false
	_can_debug = false
	_error_buffer.clear()


func _on_stopped() -> void:
	_active = false
	_breaked = false
	_can_debug = false


func _on_breaked(can_debug: bool) -> void:
	_breaked = true
	_can_debug = can_debug
	# 回退:若本次中断未触发 _capture(内建调试器可能在插件之前拦截错误
	# 消息),记录一条通用条目。
	if can_debug and _error_buffer.is_empty():
		_buffer_error(PlaytestLogReader.make_error_entry(
				Time.get_ticks_msec(),
				"Game paused at error (details in editor Errors tab or log)",
				"", "", 0, "break"))


func _on_continued() -> void:
	_breaked = false
	_can_debug = false


## 返回当前调试器状态,供 debug.state 使用。
func get_debug_state() -> Dictionary:
	return {
		"active": _active,
		"breaked": _breaked,
		"can_debug": _can_debug,
	}


## 尝试继续执行。返回 {success:true} 或
## {error: "GAME_NOT_RUNNING"|"NOT_BREAKED"}。
func try_continue() -> Dictionary:
	if _session == null or not _active:
		return {"error": "GAME_NOT_RUNNING"}
	if not _breaked:
		return {"error": "NOT_BREAKED"}
	_session.send_message("continue", [])
	return {"success": true}


## 返回当前/最近一次游戏会话的错误缓冲(副本)。
func get_error_buffer() -> Array:
	return _error_buffer.duplicate()


## 清理 — 在 remove_debugger_plugin 之前由 plugin._exit_tree 调用。
func cleanup() -> void:
	_disconnect_session()
	_session = null
	_session_id = -1
	_active = false
	_breaked = false
	_can_debug = false
	_error_buffer.clear()


## 从调试器协议解析错误数据并追加到环形缓冲。
##
## 防御性/面向未来 — 目前在现行路径上不可达:引擎把裸的 "error" 消息路由到
## 已注册的 _msg_error 处理器(script_editor_debugger.cpp),因此它永远到不了
## plugins_capture / _capture。今天真正填充缓冲的是 _on_breaked 与日志扫描
## 回退。保留它(并保持正确解码),以防未来的引擎版本把 "error" 呈现给
## 捕获插件。
##
## 数据布局 — DebuggerMarshalls::OutputError::serialize(),字段在前 /
## 调用栈在最后,在 Godot 4.0–4.7 之间核实未变:
##   [0]=hr  [1]=min  [2]=sec  [3]=msec  [4]=source_file  [5]=source_func
##   [6]=source_line  [7]=error  [8]=error_descr  [9]=warning
##   [10]=callstack.size()*3   [11+]=(file, func, line) 三元组
func _parse_and_buffer_error(data: Array) -> void:
	if data.size() < 10:
		return  # 畸形 — 固定头部字段不足

	var source_file := str(data[4])
	var source_func := str(data[5])
	var source_line: int = int(data[6])
	var error := str(data[7])
	var error_descr := str(data[8])
	var is_warning: bool = bool(data[9])

	if is_warning:
		return  # 只捕获错误,不捕获警告

	# 优先使用人类可读的描述;退回裸错误文本。
	var msg := error_descr if not error_descr.is_empty() else error

	_buffer_error(PlaytestLogReader.make_error_entry(
			Time.get_ticks_msec(), msg, source_file, source_func,
			source_line, "error"))


func _buffer_error(entry: Dictionary) -> void:
	_error_buffer.append(entry)
	if _error_buffer.size() > _ERROR_BUFFER_MAX:
		_error_buffer.pop_front()
