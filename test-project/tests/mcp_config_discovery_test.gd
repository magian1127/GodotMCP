extends SceneTree
## 仅由隔离运行器执行：复现外层工作区配置与内层 Godot 工程的组合。

const MCPJsonSync := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_sync.gd")
const PlatformRid := preload("res://addons/godot_mcp_toolkit/paths/platform_rid.gd")

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
	_ensure_shim()
	_test_bound_parent()
	_test_parent_is_not_a_write_target()
	_test_local_precedence()
	_test_unrelated_parent()
	_test_windows_path_forms()
	_test_shim_is_a_write_precondition()
	_test_retired_entry_needs_migration()
	_test_missing_command_needs_migration()
	# 用例之间共享同一个隔离目录,而 UI 用例（另一次 Godot 运行）会断言"无配置"
	# 状态——因此这里必须把写下的父目录配置清干净,否则隔离目录里残留的
	# 绑定配置会把 UI 用例的初始态从"写入"变成"打开"。
	_remove_parent()
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


func _test_shim_is_a_write_precondition() -> void:
	# 写入必须拒绝产出指向不存在入口的条目——那正是要迁移掉的失效形态。
	var shim := MCPJsonSync.shim_path()
	_check(FileAccess.file_exists(shim), "写入前置未指向随附 shim：" + shim)
	var stub := FileAccess.get_file_as_string(shim)
	DirAccess.remove_absolute(shim)
	_check(not MCPJsonSync.can_write_mcp_json(), "shim 缺失时仍声称可写")
	var result := _try_write()
	_check(not bool(result.get("ok", true)), "shim 缺失时仍写出了 .mcp.json")
	_check(not FileAccess.file_exists(_local_config), "shim 缺失时留下了文件")
	_write(shim, stub)
	_check(MCPJsonSync.can_write_mcp_json(), "shim 就位后仍声称不可写")


func _test_retired_entry_needs_migration() -> void:
	# Node 桥(2026-09-14 退役)遗留形态：条目合法可解析，却必然启动失败。
	_write(_parent_config, _retired_config())
	_check(MCPJsonSync.has_mcp_json(), "未发现绑定的父目录配置")
	_check(not MCPJsonSync.is_malformed(), "把合法的退役形态误判为 JSON 错误")
	_check(MCPJsonSync.needs_migration(), "未识别指向已退役 Node 桥入口的条目")
	_check(MCPJsonSync.points_at_retired_entry(), "未把退役入口单独区分出来")


func _test_missing_command_needs_migration() -> void:
	_write(_parent_config, _config(_project, "parent", _project + "-absent/shim.exe"))
	_check(MCPJsonSync.needs_migration(), "未识别 command 指向不存在文件的条目")
	_check(not MCPJsonSync.points_at_retired_entry(), "把普通缺失误判为退役入口")
	# 裸命令由 host 按自身 PATH 解析，插件无权判定存在性——绝不能误报。
	_write(_parent_config, _config(_project, "parent", "godot-mcp-shim"))
	_check(not MCPJsonSync.needs_migration(), "把 host 自行解析的裸命令误判为失效条目")
	# 正常形态(随附 shim 绝对路径)不得被误报。
	_write(_parent_config, _config(_project))
	_check(not MCPJsonSync.needs_migration(), "把随附 shim 条目误判为失效条目")


## 夹具只随附最小 addon；写入前置要求随附 shim 存在，因此按**文档化的随包布局**
## 直接落一个占位文件（插件只做存在性判断；真实 shim 有数十 MB，不值得每次拷贝）。
## 刻意不经 MCPJsonSync.shim_path() 反推路径——文件不存在时它返回空串，
## 那样会陷入"要先有文件才能算出路径"的自举矛盾。
func _ensure_shim() -> void:
	var shim := _bundled_shim_path()
	DirAccess.make_dir_recursive_absolute(shim.get_base_dir())
	_write(shim, "stub")
	# 随包布局是解析链的最后一环；夹具非链接安装，故命中这里。
	_check(MCPJsonSync.shim_path() == shim, "随包 shim 未被落点解析命中：" + MCPJsonSync.shim_path())


## 随包分发形态下的 shim 路径：<工程>/addons/godot_mcp_toolkit/bin/<rid>/godot-mcp-shim[.exe]。
func _bundled_shim_path() -> String:
	var exe := "godot-mcp-shim.exe" if OS.get_name() == "Windows" else "godot-mcp-shim"
	return _project.path_join("addons/godot_mcp_toolkit").path_join("bin").path_join(
		PlatformRid.current()).path_join(exe)


func _config(binding: String, source: String = "parent", command: String = "") -> String:
	return JSON.stringify({"mcpServers": {"godot": {
		"command": MCPJsonSync.shim_path() if command.is_empty() else command,
		"args": [],
		"env": {
			"GODOT_MCP_CONFIG_VERSION": "2",
			"GODOT_MCP_PROJECT_PATH": binding,
			"GODOT_MCP_TEST_SOURCE": source,
		},
	}}})


## 退役前的 Node 桥形态：旧服务器键 + node 入口 + server/dist/index.js。
func _retired_config() -> String:
	return JSON.stringify({"mcpServers": {"godot-mcp-unified": {
		"command": "node",
		"args": [_project.path_join("server/dist/index.js")],
		"env": {
			"GODOT_MCP_CONFIG_VERSION": "1",
			"GODOT_MCP_PROJECT_PATH": _project,
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


func _remove_parent() -> void:
	if FileAccess.file_exists(_parent_config):
		DirAccess.remove_absolute(_parent_config)


func _check(condition: bool, message: String) -> void:
	if not condition:
		_failures += 1
		push_error("FAIL: " + message)
