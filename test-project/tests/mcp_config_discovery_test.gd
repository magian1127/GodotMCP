extends SceneTree
## 仅由隔离运行器执行：复现外层工作区配置与内层 Godot 工程的组合。

const MCPJsonSync := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_sync.gd")

var _failures := 0
var _project := ""
var _parent_config := ""
var _local_config := ""


func _init() -> void:
	_project = ProjectSettings.globalize_path("res://").trim_suffix("/")
	if not FileAccess.file_exists(_project.path_join(".mcp-config-test")):
		push_error("此测试只能在隔离运行器创建的目录中执行。")
		quit(2)
		return
	_parent_config = _project.get_base_dir().path_join(".mcp.json")
	_local_config = _project.path_join(".mcp.json")
	_test_bound_parent()
	_test_parent_is_not_a_write_target()
	_test_local_precedence()
	_test_unrelated_parent()
	_test_windows_path_forms()
	if _failures == 0:
		print("PASS: MCP 配置发现与写入边界回归测试全部通过。")
	quit(0 if _failures == 0 else 1)


func _test_bound_parent() -> void:
	_write(_parent_config, _config(_project))
	_check(MCPJsonSync.has_mcp_json(), "未发现明确绑定当前工程的父目录配置")
	_check(not MCPJsonSync.is_malformed(), "将有效父目录配置误判为 JSON 错误")
	_check(MCPJsonSync.get_all_env_vars().get("GODOT_MCP_PROJECT_PATH", "") == _project,
		"未读取父目录中当前工程的环境变量")
	var result := _try_write()
	_check(bool(result.get("ok", false)), "真实写入流程仍报告：" + str(result.get("message", "")))
	_check(FileAccess.file_exists(_local_config), "明确写入请求未创建工程内配置")
	_remove_local()


func _test_parent_is_not_a_write_target() -> void:
	var before := FileAccess.get_file_as_string(_parent_config)
	_check(not MCPJsonSync.needs_overwrite_confirm(), "把父目录配置误当成覆盖目标")
	var result := _try_write()
	_check(bool(result.get("ok", false)), "不能从已绑定的父目录配置生成工程内配置")
	_check(FileAccess.get_file_as_string(_parent_config) == before, "写入改动了父目录配置")
	_check(MCPJsonSync.needs_overwrite_confirm(), "工程内已有文件时未要求覆盖确认")
	result = _try_write()
	_check(not bool(result.get("ok", true)), "未经确认覆盖了工程内现有文件")
	_remove_local()


func _test_local_precedence() -> void:
	_write(_local_config, "{ invalid json")
	_check(MCPJsonSync.is_malformed(), "父目录有效配置掩盖了工程内 JSON 错误")
	_remove_local()
	_write(_local_config, _config(_project, "local"))
	_check(MCPJsonSync.get_all_env_vars().get("GODOT_MCP_TEST_SOURCE", "") == "local",
		"工程内配置未优先于父目录配置")
	_remove_local()


func _test_unrelated_parent() -> void:
	# http 形态下写入是用户显式动作(daemon 常量条目),不再依赖从绑定配置解析
	# 本地入口;发现边界(不借用未绑定配置)与写入边界(父目录不被改动)仍然成立。
	for binding in [_project + "-sibling", "", "../another-project"]:
		_write(_parent_config, _config(binding))
		_check(not MCPJsonSync.has_mcp_json(), "错误借用了未绑定当前工程的父目录配置")
		var before := FileAccess.get_file_as_string(_parent_config)
		var result := _try_write()
		_check(bool(result.get("ok", false)), "http 形态写入不应依赖父目录绑定")
		_check(FileAccess.file_exists(_local_config), "显式写入未创建工程内配置")
		_check(FileAccess.get_file_as_string(_parent_config) == before, "写入改动了父目录配置")
		_remove_local()
	_write(_parent_config, JSON.stringify({"mcpServers": []}))
	_check(not MCPJsonSync.has_mcp_json(), "错误接受了结构损坏的父目录配置")


func _test_windows_path_forms() -> void:
	if OS.get_name() != "Windows":
		return
	_write(_parent_config, _config(_project.to_upper().replace("/", "\\") + "\\"))
	_check(MCPJsonSync.has_mcp_json(), "Windows 路径大小写、分隔符或末尾斜杠导致漏报")


func _config(binding: String, source: String = "parent") -> String:
	return JSON.stringify({"mcpServers": {"godot": {
		"type": "http", "url": "http://127.0.0.1:6590/", "env": {
			"GODOT_MCP_CONFIG_VERSION": "2",
			"GODOT_MCP_PROJECT_PATH": binding,
			"GODOT_MCP_TEST_SOURCE": source,
		},
	}}})


func _try_write() -> Dictionary:
	var result := {}
	MCPJsonSync.write_from_template(false, func(ok: bool, message: String, severity: int, _tooltip: String):
		result.merge({"ok": ok, "message": message, "severity": severity}))
	return result


func _write(path: String, content: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		_check(false, "无法写入隔离测试文件：" + path)
		return
	file.store_string(content)
	file.close()


func _remove_local() -> void:
	if FileAccess.file_exists(_local_config):
		DirAccess.remove_absolute(_local_config)


func _check(condition: bool, message: String) -> void:
	if not condition:
		_failures += 1
		push_error("FAIL: " + message)
