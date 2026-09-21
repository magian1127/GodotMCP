@tool
extends RefCounted
## 编辑器服务的访问层,以存储的 EditorPlugin 引用为锚点。
##
## 封装插件所需的各个编辑器单例(撤销/重做、通知器(toaster)、主题),让调用方
## 通过一处来访问它们。设计上仅限编辑器使用 —— 只会由编辑器代码通过枢纽常量
## Modules.EditorAccess 使用。

## 存储 EditorPlugin 实例,以便在所有 Godot 4.x 版本上都能通过 plugin.get_undo_redo()
## 访问 EditorUndoRedoManager —— 而不仅限于添加了
## EditorInterface.get_editor_undo_redo() 的 4.4+。
static var _plugin: EditorPlugin


## 注入 EditorPlugin 实例以启用编辑器服务访问(与 [method clear_plugin] 配对)。
static func set_plugin(plugin: EditorPlugin) -> void:
	_plugin = plugin


## 在拆除时清除存储的插件引用(与 [method set_plugin] 配对)。
static func clear_plugin() -> void:
	_plugin = null


## 插件引用一经注入即为 true(编辑器上下文,插件已加载)。
static func has_plugin() -> bool:
	return _plugin != null


## 通过存储的插件引用获取 EditorUndoRedoManager。
## 在编辑器上下文中,所有 Godot 4.x 版本都会返回该单例。
## 仅在无头(headless)模式下返回 null(未加载插件)。
static func get_undo_redo():
	if _plugin != null:
		return _plugin.get_undo_redo()
	return null


## 通过动态派发安全地获取 EditorToaster。
## 编辑器通知器(toaster)仅在 Godot 4.4+ 上可用,因此对调用加以守卫,
## 在不存在该方法的更早版本上返回 null。
static func get_toaster():
	if EditorInterface.has_method("get_editor_toaster"):
		return EditorInterface.call("get_editor_toaster")
	return null


## 获取编辑器主题(Theme)。
## EditorInterface.get_editor_theme() 在每个受支持版本(4.2+)上都有绑定,
## 因此无需守卫。
static func get_editor_theme() -> Theme:
	return EditorInterface.get_editor_theme()
