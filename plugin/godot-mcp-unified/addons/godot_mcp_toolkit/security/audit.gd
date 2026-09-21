@tool
extends RefCounted
## 只追加(append-only)的上下文协议(MCP)审计日志,位于
## user://addons/godot_mcp_toolkit/project_instance_<hash>/mcp_audit.log。
##
## 每次工具派发(dispatch)写入一行:
##   <ISO8601Z>\t<method>\t<params_sha256_hex[:12]>
##
## 每次写入都会打开并冲刷(flush)文件,以保证崩溃安全。

const ProjectPaths := preload("res://addons/godot_mcp_toolkit/paths/project_paths.gd")


static func get_log_path() -> String:
	return ProjectPaths.instance_dir() + "mcp_audit.log"


static func log_call(method: String, parameters: Dictionary) -> void:
	if not ProjectSettings.get_setting("mcp_toolkit/audit/enabled", true):
		return
	var log_path := get_log_path()
	var timestamp := Time.get_datetime_string_from_system(true) + "Z"
	var params_hash := JSON.stringify(parameters).sha256_text().substr(0, 12)
	var line := "%s\t%s\t%s\n" % [timestamp, method, params_hash]
	var file: FileAccess = null
	if FileAccess.file_exists(log_path):
		file = FileAccess.open(log_path, FileAccess.READ_WRITE)
		if file != null:
			file.seek_end()
	else:
		ProjectPaths.ensure_dirs()
		file = FileAccess.open(log_path, FileAccess.WRITE)
	if file == null:
		push_warning("[Audit] could not open %s (err %d)" % [
			log_path, FileAccess.get_open_error()])
		return
	file.store_string(line)
	file.flush()
	var size := file.get_length()
	file.close()
	var max_kb: int = ProjectSettings.get_setting("mcp_toolkit/audit/max_size_kb", 1024)
	if max_kb > 0 and size > max_kb * 1024:
		_truncate_log(max_kb * 1024)


static func _truncate_log(max_bytes: int) -> void:
	var log_path := get_log_path()
	var file := FileAccess.open(log_path, FileAccess.READ)
	if file == null:
		return
	var size := file.get_length()
	if size <= max_bytes:
		file.close()
		return
	# 保留上限约 50% 的最近内容,这样就不必在每次写入时都截断。
	var keep := max_bytes / 2
	file.seek(size - keep)
	file.get_line()  # 丢弃不完整的首行
	var remaining := file.get_as_text()
	file.close()
	var out := FileAccess.open(log_path, FileAccess.WRITE)
	if out != null:
		out.store_string(remaining)
		out.close()
