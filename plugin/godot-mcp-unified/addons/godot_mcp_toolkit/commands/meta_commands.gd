@tool
extends RefCounted
## meta.* 传输层(transport)命令 — 服务器端的限制覆盖。


static func register(registry: MCPToolkitCommandRegistry) -> void:
	registry.add("meta.set_limits", func(parameters: Dictionary) -> Dictionary:
		return _cmd_set_limits(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


## `mcp_toolkit/limits/*` 的唯一许可写入方 — `project.set_setting`
## 刻意拒绝该前缀,把限制修改路由到此处,以便下限钳制
## 始终得到应用(脚本/保存读取上限 >= 64 KB,ws_buffer >= 256 KB)。
static func _cmd_set_limits(parameters: Dictionary) -> Dictionary:
	var script_cap = parameters.get("script_read_cap_kb", null)
	var save_cap = parameters.get("save_read_cap_kb", null)
	var ws_buf = parameters.get("ws_buffer_kb", null)
	if script_cap != null:
		ProjectSettings.set_setting("mcp_toolkit/limits/script_read_cap_kb",
			maxi(int(script_cap), 64))
	if save_cap != null:
		ProjectSettings.set_setting("mcp_toolkit/limits/save_read_cap_kb",
			maxi(int(save_cap), 64))
	if ws_buf != null:
		ProjectSettings.set_setting("mcp_toolkit/limits/ws_buffer_kb",
			maxi(int(ws_buf), 256))
	return MCPToolkitSuccess.ok({
		"script_read_cap_kb": int(ProjectSettings.get_setting(
			"mcp_toolkit/limits/script_read_cap_kb", 256)),
		"save_read_cap_kb": int(ProjectSettings.get_setting(
			"mcp_toolkit/limits/save_read_cap_kb", 256)),
		"ws_buffer_kb": int(ProjectSettings.get_setting(
			"mcp_toolkit/limits/ws_buffer_kb", 1024)),
	})
