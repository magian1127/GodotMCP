@tool
extends RefCounted
## 两个编辑器全局对话框的呈现器(presen
## ter)——信息/帮助对话框与扩展目录。
##
## 编辑器全局意味着这些对话框以编辑器基础控件为父节点，而不是以停靠面板
## (dock)为父节点：它们可以从任何入口打开（停靠面板底栏、工具菜单、引导向导），
## 因此它们的创建、复用与销毁清理都归属于这个共享呈现器，而不是停靠面板——
## 停靠面板只是界面(UI)表面，不是服务定位器。每个对话框在首次显示时创建，
## 之后复用（重新打开的对话框会保留其窗口状态）；所有者必须在销毁清理期间
## 调用 [method dispose]。
##
## 仅限编辑器：脚本引用了 EditorInterface，因此本脚本绝不能进入运行时
## 自动加载(Autoload)的 preload 闭包。

const InfoDialog := preload("res://addons/godot_mcp_toolkit/ui/dock/ext/info_dialog.gd")
const ExtensionCatalogDialog := preload("res://addons/godot_mcp_toolkit/ui/dock/ext/extension_catalog_dialog.gd")

# 首次显示时创建，之后各次显示复用；由 dispose() 释放。
var _info_dialog: InfoDialog = null
var _catalog_dialog: ExtensionCatalogDialog = null


## 显示信息/帮助对话框，并在每次调用时从 [param server]（已绑定的
## 上下文协议(MCP)服务器节点）重新读取实时状态。
func show_info(server: Node) -> void:
	if _info_dialog == null or not is_instance_valid(_info_dialog):
		_info_dialog = InfoDialog.new()
		EditorInterface.get_base_control().add_child(_info_dialog)
	_info_dialog.show_info(server)


## 显示扩展目录对话框。
func show_extension_catalog() -> void:
	if _catalog_dialog == null or not is_instance_valid(_catalog_dialog):
		_catalog_dialog = ExtensionCatalogDialog.new()
		EditorInterface.get_base_control().add_child(_catalog_dialog)
	_catalog_dialog.show_catalog()


## 释放两个对话框并清除引用。必须在插件同步清理期间运行，而且必须立即释放
## （绝不使用 queue_free）：这些对话框是基础控件的子节点，没有其他地方会释放
## 它们；进程退出时的延迟释放可能在 ObjectDB 的退出时泄漏检查运行之前
## 尚未真正执行。
func dispose() -> void:
	for dialog in [_info_dialog, _catalog_dialog]:
		if dialog != null and is_instance_valid(dialog):
			dialog.free()
	_info_dialog = null
	_catalog_dialog = null
