@tool
extends RefCounted
## 限制、审计与引导键的 ProjectSettings 注册。
##
## 在插件启动时调用一次,确保 mcp_toolkit/* 键以正确的类型和默认值
## 出现在项目设置检查器中。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const MCPJsonSync = Modules.MCPJsonSync
const NodejsCheck = Modules.NodejsCheck
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

const _BOOTSTRAP_KEY := "mcp_toolkit/internal/bootstrap_complete"

const _STATUS_KEY := "mcp_toolkit/status"
const _READ_ONLY_WARNING_TEXT := (
	"READ-ONLY MODE ACTIVE (GODOT_MCP_READ_ONLY=1) — "
	+ "Only read-only tools are available. Mutating tools are hidden. "
	+ "Remove GODOT_MCP_READ_ONLY from .mcp.json and reconnect "
	+ "the MCP client to restore full access.")
const _MCP_JSON_MISSING_TEXT := (
	"No project .mcp.json detected. Clients can also connect through plugin or global MCP configuration.")
const _NODEJS_NOT_FOUND_TEXT := (
	"NODE.JS NOT FOUND — The MCP server bridge requires Node.js 22+. "
	+ "See the bundled advanced-configuration guide for local setup.")
const _NODEJS_OLD_VERSION_TEXT := (
	"NODE.JS %s FOUND BUT 22+ REQUIRED — "
	+ "See the bundled advanced-configuration guide for local setup.")


static func register_all() -> void:
	_register_limits()
	_register_concurrency()
	_register_audit()
	_register_daemon()
	_register_bootstrap_flag()
	_register_status_field()
	# 清理旧的状态键位置(之前位于 feature_gates/ 下)。
	if ProjectSettings.has_setting("mcp_toolkit/feature_gates/status"):
		ProjectSettings.set_setting("mcp_toolkit/feature_gates/status", null)
	# 清理环境变量覆盖说明键 — 那两个环境变量是 Node 桥时代的遗留,
	# 现行代码从不读取(限制只经 ProjectSettings 本身调整)。
	if ProjectSettings.has_setting("mcp_toolkit/limits/env_override_note"):
		ProjectSettings.set_setting("mcp_toolkit/limits/env_override_note", null)


static func _register_daemon() -> void:
	_register_basic_bool("mcp_toolkit/daemon/autostart", true,
		EditorLocale.pick(
			"Automatically spawn the local godot-mcp-daemon when it is absent, and re-spawn it if it exits (probed every few seconds). Set false (or env GODOT_MCP_DAEMON_AUTOSTART=0) to disable. The executable is resolved from GODOT_MCP_DAEMON_EXE, then addons/godot_mcp_toolkit/bin/<rid>/, then the repository publish directory.",
			"daemon 缺席时自动拉起本地 godot-mcp-daemon,并在其退出后重拉(数秒一次探活)。设为 false(或环境变量 GODOT_MCP_DAEMON_AUTOSTART=0)可禁用。可执行文件按 GODOT_MCP_DAEMON_EXE → addons/godot_mcp_toolkit/bin/<rid>/ → 仓库发布目录的顺序解析。"))


## register_all 的镜像 —— 在卸载时(仅 _disable_plugin)擦除所有
## mcp_toolkit/* ProjectSettings 键。对实时属性列表做前缀扫描,
## 因此覆盖当前及未来所有 mcp_toolkit/* 键(包括遗留的
## feature_gates/ 键),无需维护会过期的硬编码列表,随后持久化
## project.godot。绝不要在 _exit_tree 中调用(它每次重载都会触发)。
static func unregister_all() -> void:
	# 两遍处理:先收集(只读),再置空 —— 绝不在遍历属性
	# 列表的同时修改它。
	var names := _collect_mcp_setting_names()
	for name in names:
		ProjectSettings.set_setting(name, null)
	ProjectSettings.save()


## 只读:收集 mcp_toolkit/ 前缀下的每个 ProjectSettings 键。
## unregister_all 无副作用的核心(也是唯一可单元测试的接缝)——
## 扫描 ProjectSettings.get_property_list() 并返回匹配的名称。
static func _collect_mcp_setting_names() -> PackedStringArray:
	var out := PackedStringArray()
	for entry in ProjectSettings.get_property_list():
		var name := str(entry.get("name", ""))
		if name.begins_with("mcp_toolkit/"):
			out.append(name)
	return out


static func _register_limits() -> void:
	_register_basic_int("mcp_toolkit/limits/script_read_cap_kb", 256,
		EditorLocale.pick(
			"Max script content returned by script.read, in KB. Minimum 64.",
			"script.read 返回的脚本内容上限，单位 KB。最小值为 64。"))
	_register_basic_int("mcp_toolkit/limits/save_read_cap_kb", 256,
		EditorLocale.pick(
			"Max user-file content returned per save.read window, in KB. Minimum 64.",
			"每个 save.read 窗口返回的用户文件内容上限，单位 KB。最小值为 64。"))
	_register_basic_int("mcp_toolkit/limits/ws_buffer_kb", 1024,
		EditorLocale.pick(
			"WebSocket per-peer buffer size, in KB. Minimum 256.",
			"每个 WebSocket 连接的缓冲区大小，单位 KB。最小值为 256。"))


static func _register_concurrency() -> void:
	_register_basic_int("mcp_toolkit/concurrency/scan_idle_timeout_ms", 5000,
		EditorLocale.pick(
			"How long a scene save/open waits for the EditorFileSystem scan to finish before aborting, in ms. 0 = fail-fast; recommended 1000-30000. Higher lets slow-import projects finish scanning at the cost of longer save stalls. Not clamped.",
			"保存或打开场景时等待 EditorFileSystem 扫描完成的最长时间，单位毫秒。0 = 立即失败；建议 1000–30000。更高的值可让导入较慢的项目完成扫描，但保存会停顿更久。不做范围限制。"))
	_register_basic_int("mcp_toolkit/concurrency/mutation_watchdog_grace_ms", 60000,
		EditorLocale.pick(
			"Grace added to a mutation's deadline before the dispatch watchdog force-clears a wedged lock, in ms. Added to the command's declared timeout (or the 300s ceiling for undeclared commands). The watchdog is a safety net that should normally never fire. Not clamped.",
			"调度看门狗强制清除卡死锁之前，添加到修改命令截止时间的宽限期，单位毫秒。该值会加到命令声明的超时上；未声明超时的命令按 300 秒上限计算。看门狗仅用于兜底，正常情况下不应触发。不做范围限制。"))


static func _register_audit() -> void:
	_register_basic_bool("mcp_toolkit/audit/enabled", true,
		EditorLocale.pick(
			"Enable MCP audit log at user://addons/godot_mcp_toolkit/project_instance_<hash>/mcp_audit.log.",
			"启用 MCP 审计日志：user://addons/godot_mcp_toolkit/project_instance_<hash>/mcp_audit.log。"))
	_register_basic_int("mcp_toolkit/audit/max_size_kb", 1024,
		EditorLocale.pick(
			"Max audit log size in KB. 0 = unlimited. Log truncates to 50% when exceeded.",
			"审计日志最大大小，单位 KB。0 = 不限。超过上限时会截断到 50%。"))


static func _register_bootstrap_flag() -> void:
	if not ProjectSettings.has_setting(_BOOTSTRAP_KEY):
		ProjectSettings.set_setting(_BOOTSTRAP_KEY, false)
	ProjectSettings.set_initial_value(_BOOTSTRAP_KEY, false)


static func _register_status_field() -> void:
	var text := _compute_status_text()
	if not ProjectSettings.has_setting(_STATUS_KEY):
		ProjectSettings.set_setting(_STATUS_KEY, text)
	else:
		ProjectSettings.set_setting(_STATUS_KEY, text)
	ProjectSettings.set_initial_value(_STATUS_KEY, "")
	ProjectSettings.set_as_basic(_STATUS_KEY, true)
	ProjectSettings.set_order(_STATUS_KEY, 1000)
	ProjectSettings.add_property_info({
		"name": _STATUS_KEY, "type": TYPE_STRING,
		"hint": PROPERTY_HINT_MULTILINE_TEXT,
		"hint_string": EditorLocale.pick(
			"Read-only status display — value is managed by the plugin.",
			"只读状态显示；该值由插件管理。"),
	})


static func _compute_status_text() -> String:
	var parts := PackedStringArray()
	# 只读模式检查。
	var env := MCPJsonSync.get_all_env_vars()
	if env.get("GODOT_MCP_READ_ONLY", "") == "1":
		parts.append(EditorLocale.pick(
			_READ_ONLY_WARNING_TEXT,
			"只读模式已启用（GODOT_MCP_READ_ONLY=1）— 目前只有只读工具可用，"
				+ "会修改项目的工具已隐藏。如需恢复完整权限，请从 .mcp.json 中移除 "
				+ "GODOT_MCP_READ_ONLY，然后重新连接 MCP 客户端。"))
	# .mcp.json 是否存在。
	if not MCPJsonSync.has_mcp_json():
		parts.append(EditorLocale.pick(
			_MCP_JSON_MISSING_TEXT,
			"未检测到项目 .mcp.json。客户端也可以通过插件或全局 MCP 配置连接。"))
	# Node.js 可用性。
	var node_check := NodejsCheck.check()
	if not node_check["found"]:
		parts.append(EditorLocale.pick(
			_NODEJS_NOT_FOUND_TEXT,
			"未找到 NODE.JS — 本地 MCP 服务器桥接需要 Node.js 22 或更高版本。"
				+ "请查看随插件提供的高级配置文档。"))
	elif not node_check["meets_minimum"]:
		parts.append(EditorLocale.pick(
			_NODEJS_OLD_VERSION_TEXT,
			"检测到 NODE.JS %s，但需要 22 或更高版本 — 请查看随插件提供的本地安装说明。"
			) % str(node_check["version"]))
	return "\n\n".join(parts)


static func _register_basic_bool(key: String, default_value: bool, hint: String) -> void:
	if not ProjectSettings.has_setting(key):
		ProjectSettings.set_setting(key, default_value)
	ProjectSettings.set_initial_value(key, default_value)
	ProjectSettings.set_as_basic(key, true)
	ProjectSettings.add_property_info({
		"name": key, "type": TYPE_BOOL,
		"hint": PROPERTY_HINT_NONE, "hint_string": hint,
	})


static func _register_basic_int(key: String, default_value: int, hint: String) -> void:
	if not ProjectSettings.has_setting(key):
		ProjectSettings.set_setting(key, default_value)
	ProjectSettings.set_initial_value(key, default_value)
	ProjectSettings.set_as_basic(key, true)
	ProjectSettings.add_property_info({
		"name": key, "type": TYPE_INT,
		"hint": PROPERTY_HINT_NONE, "hint_string": hint,
	})
