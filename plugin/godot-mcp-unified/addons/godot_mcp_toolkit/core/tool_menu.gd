@tool
extends RefCounted
## 持有“工具 > Godot MCP Unified”子菜单 + 命令面板界面,
## 并路由其动作。
##
## 从同一张 _ACTIONS 表构建数据驱动的子菜单与对应的命令面板命令,
## 然后把每个被选中的动作路由给它的协作者:服务器
## (重新生成令牌)、停靠面板(dock)(审计日志 —— 停靠面板(dock)的一个分区)、共享写入
## 流程(.mcp.json)、对话框呈现器(扩展目录),或编辑器
## 设置(打开项目设置)。由编排器(plugin.gd)构建,
## 并传入其动作所需的协作者;在 _enter_tree 期间 install(),
## 在 _exit_tree 期间 uninstall()。
##
## 仅限编辑器:引用了 EditorInterface(命令面板)与 EditorPlugin 宿主,
## 因此由仅限编辑器的 plugin.gd 构建,运行时自动加载(Autoload)
## 永远不会触及它。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const SettingsNavigator := preload("res://addons/godot_mcp_toolkit/ui/settings_navigator.gd")
const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
const ToolkitDialogPresenter := preload("res://addons/godot_mcp_toolkit/ui/toolkit_dialog_presenter.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 写入失败时的 toast 通知严重级别(与 EditorToaster.Severity /
# 写入流程的结果刻度一致:0 信息 / 1 警告 / 2 错误)。
const _TOAST_WARNING := 1

# 数据驱动的菜单 / 命令面板注册。
# "label" 是命令面板中的名称(加前缀以便检索);
# "menu_label" 是“工具 > Godot MCP Unified”子菜单内显示的短名称。
const _ACTIONS := [
	{"label": "Godot MCP Unified: Regenerate Token", "label_zh": "Godot MCP Unified：重新生成令牌", "menu_label": "Regenerate Token", "menu_label_zh": "重新生成令牌", "key": "mcp/regenerate_token", "method": "_on_regen_token"},
	{"label": "Godot MCP Unified: Show Audit Log", "label_zh": "Godot MCP Unified：显示审计日志", "menu_label": "Show Audit Log", "menu_label_zh": "显示审计日志", "key": "mcp/show_audit_log", "method": "_on_show_audit"},
	{"label": "Godot MCP Unified: Open Project Settings", "label_zh": "Godot MCP Unified：打开项目设置", "menu_label": "Open Project Settings", "menu_label_zh": "打开项目设置", "key": "mcp/open_settings", "method": "_on_open_settings"},
	{"label": "Godot MCP Unified: Write .mcp.json", "label_zh": "Godot MCP Unified：写入 .mcp.json", "menu_label": "Write .mcp.json", "menu_label_zh": "写入 .mcp.json", "key": "mcp/write_mcp_json", "method": "_on_write_mcp_json"},
	{"label": "Godot MCP Unified: Extension Catalog", "label_zh": "Godot MCP Unified：扩展目录", "menu_label": "Extension Catalog...", "menu_label_zh": "扩展目录……", "key": "mcp/extension_catalog", "method": "_on_extension_catalog"},
]

var _plugin: EditorPlugin = null
var _server: Node = null
# 仅为审计日志动作保留 —— 审计查看器是停靠面板(dock)的一个分区,因此
# 停靠面板(dock)仍是其所有者;其余动作都路由给注入的协作者。
var _dock: Control = null
var _write_flow: MCPJsonWriteFlow = null
var _dialog_presenter: ToolkitDialogPresenter = null
var _tool_submenu: PopupMenu = null


func _init(
		plugin: EditorPlugin, server: Node, dock: Control,
		write_flow: MCPJsonWriteFlow, dialog_presenter: ToolkitDialogPresenter) -> void:
	_plugin = plugin
	_server = server
	_dock = dock
	_write_flow = write_flow
	_dialog_presenter = dialog_presenter


# -- 菜单注册 ---------------------------------------------------------


func install() -> void:
	# “工具 > Godot MCP Unified”子菜单。
	_tool_submenu = PopupMenu.new()
	_tool_submenu.name = "MCPToolkitMenu"
	for i in _ACTIONS.size():
		_tool_submenu.add_item(EditorLocale.pick(
			_ACTIONS[i]["menu_label"], _ACTIONS[i]["menu_label_zh"]), i)
	_tool_submenu.id_pressed.connect(_on_submenu_id_pressed)
	_plugin.add_tool_submenu_item("Godot MCP Unified", _tool_submenu)
	# -- 命令面板(4.0+;为安全起见仍加守卫) --
	if EditorInterface.has_method("get_command_palette"):
		var palette = EditorInterface.call("get_command_palette")
		if palette != null:
			for action in _ACTIONS:
				palette.add_command(
					EditorLocale.pick(action["label"], action["label_zh"]), action["key"],
					Callable(self, action["method"]))


func uninstall() -> void:
	# 命令面板。
	if EditorInterface.has_method("get_command_palette"):
		var palette = EditorInterface.call("get_command_palette")
		if palette != null:
			for action in _ACTIONS:
				palette.remove_command(action["key"])
	# 子菜单 —— 立即 free(),而非 queue_free():uninstall 运行在插件的
	# _exit_tree 拆除路径上,延迟删除会让子菜单的
	# id_pressed 连接(以及本实例的预加载链)存留到 ObjectDB
	# 退出时的泄漏检查之后 → 误报“退出时资源仍被占用”。子菜单
	# 已在上面从菜单中移除,因此没有东西在遍历它。
	_plugin.remove_tool_menu_item("Godot MCP Unified")
	if _tool_submenu != null and is_instance_valid(_tool_submenu):
		_tool_submenu.free()
	_tool_submenu = null


# -- 子菜单路由器 ------------------------------------------------------------


func _on_submenu_id_pressed(id: int) -> void:
	if id >= 0 and id < _ACTIONS.size():
		Callable(self, _ACTIONS[id]["method"]).call()


# -- 菜单处理器 -------------------------------------------------------------


func _on_regen_token() -> void:
	if _server != null:
		_server.regenerate_token()
		print("[MCP] Token rotated")
		var toaster = Modules.EditorAccess.get_toaster()
		if toaster != null:
			toaster.push_toast(EditorLocale.pick("MCP token rotated", "MCP 令牌已轮换"), 0)


func _on_show_audit() -> void:
	if _dock != null:
		_dock.show_audit_dialog()
	else:
		var global_path := ProjectSettings.globalize_path(Modules.Audit.get_log_path())
		OS.shell_open(global_path)


func _on_open_settings() -> void:
	SettingsNavigator.open_mcp_settings()


func _on_write_mcp_json() -> void:
	if _write_flow != null:
		_write_flow.write(false, _on_write_mcp_json_result)


# 菜单自己的共享写入流程反馈出口 —— 通过“工具”菜单的写入
# 不涉及停靠面板(dock),因此由菜单自行 toast 通知
# (并输出控制台日志)结果。
func _on_write_mcp_json_result(_ok: bool, message: String, severity: int, tooltip: String) -> void:
	if severity >= _TOAST_WARNING:
		push_warning("[MCP] %s" % message)
	else:
		print("[MCP] %s" % message)
	var toaster = Modules.EditorAccess.get_toaster()
	if toaster != null:
		toaster.push_toast(message, severity, tooltip)


func _on_extension_catalog() -> void:
	if _dialog_presenter != null:
		_dialog_presenter.show_extension_catalog()
