@tool
extends RefCounted
## editor.* / execute.* 命令注册编排器 — 将每个 editor.*
## 和 execute.* 工具挂载到注册表上,并把每个处理器(handler)委派给对应的命令
## 子模块(日志读取器、重新扫描、截图、执行)。自身不持有任何子领域逻辑;
## 它唯一保留在本地的是两行的 editor.set_lsp_status 服务器推送处理器。

const _LogReader := preload("res://addons/godot_mcp_toolkit/commands/editor/editor_log_reader.gd")
const _Rescan := preload("res://addons/godot_mcp_toolkit/commands/editor/editor_rescan.gd")
const _Screenshot := preload("res://addons/godot_mcp_toolkit/commands/editor/editor_screenshot.gd")
const _Execute := preload("res://addons/godot_mcp_toolkit/commands/editor/editor_execute.gd")
const _Dialogs := preload("res://addons/godot_mcp_toolkit/commands/editor/editor_dialogs.gd")
const _CsharpBuild := preload("res://addons/godot_mcp_toolkit/commands/editor/editor_csharp_build.gd")


static func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	registry.add("editor.save_scene", func(parameters: Dictionary) -> Dictionary:
		# 通过公共安全类转发:同步保存外层包着 C2 扫描空闲守卫与 C1
		# _in_dispatch 标志(见
		# mcp_toolkit_safe_scene_ops.gd)。绝不要直接调用 EditorInterface.save_scene[_as]
		# — 它可能在分发中途重入 Main::iteration(),导致崩溃或卡死。
		return await MCPToolkitSafeSceneOps.save_scene(str(parameters.get("file_path", "")))
	, MCPToolkitCommandOptions.new())
	registry.add("editor.screenshot", func(parameters: Dictionary) -> Dictionary:
		return await _Screenshot.cmd_screenshot(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only())
	registry.add("editor.refresh", func(parameters: Dictionary) -> Dictionary:
		return await _Rescan.cmd_refresh(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("editor.get_console", func(parameters: Dictionary) -> Dictionary:
		return _LogReader.cmd_get_console(server, parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("editor.wait_for_idle", func(parameters: Dictionary) -> Dictionary:
		return await _Rescan.cmd_wait_for_idle(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("execute.code", func(parameters: Dictionary) -> Dictionary:
		return _Execute.cmd_execute_code(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("editor.set_lsp_status", func(parameters: Dictionary) -> Dictionary:
		return _cmd_set_lsp_status(server, parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	_Dialogs.register(registry, server)
	_CsharpBuild.register(registry, server)


# -- 命令 ---------------------------------------------------------------------


## editor.set_lsp_status — 上下文协议(MCP)服务器把其权威的 GDScript 语言服务器
## 协议(LSP)判定结果推送到这里(编辑器无法读取自身的 LSP 绑定状态)。结果存储在
## 服务器上供停靠面板(dock)显示。内部命令 — 不是上下文协议工具。
static func _cmd_set_lsp_status(server: Node, parameters: Dictionary) -> Dictionary:
	server.set_reported_lsp_status(parameters)
	return MCPToolkitSuccess.ok({"reported": true})
