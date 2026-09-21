@tool
extends RefCounted
## 跨越 4.2–4.7 版本分界、在编辑器中承载工具集停靠面板(dock)的版本适配器。
##
## Godot 4.6 重构了编辑器停靠面板(dock):底部面板的标签栏移入了一个
## 可折叠的分栏,因此旧的 [code]add_control_to_bottom_panel[/code] 路径
## 在 4.7 上会渲染出一个不可见的面板。新的 [code]EditorDock[/code] +
## [code]EditorPlugin.add_dock[/code] API(4.6 新增)取代了它。本适配器
## 把这一接缝隔离在一个能力探测开关之后 ——
## [code]ClassDB.class_exists("EditorDock")[/code],在 4.6+ 上为 true ——
## 这样组装器与向导保持版本无关:4.6+ 走 [code]add_dock[/code],
## 4.2–4.5 走已弃用的底部面板调用。
##
## 从不把 [code]EditorDock[/code] 包装器作为类型名书写(该类在
## 4.2–4.5 上不存在,静态引用在那里会导致解析错误);它通过
## [code]ClassDB.instantiate[/code] 创建,以无类型方式存储,
## 并由动态 [code]call(...)[/code] 派发驱动。仅限编辑器:引用了
## [code]EditorPlugin[/code],因此只被编辑器代码预加载,绝不会到达运行时自动加载(Autoload)。

# EditorDock.DOCK_SLOT_BOTTOM 的整数值。写成符号形式的
# EditorDock.DOCK_SLOT_BOTTOM 在 4.2–4.5 上(类不存在)即使位于死分支也会解析报错,
# 因此硬编码该序号 —— 数值已在 4.6 与 4.7 的引擎源码
# (editor/docks/dock_constants.h)中核实一致。
const _DOCK_SLOT_BOTTOM := 8

# EditorDock.DOCK_LAYOUT_HORIZONTAL 的整数值(同样的解析安全
# 原因),与引擎自带底部面板停靠面板(dock)所用的水平布局一致。
const _DOCK_LAYOUT_HORIZONTAL := 2


## 把 [param control] 作为标题为 [param title] 的工具集停靠面板(dock)承载;
## 返回另外两个函数所需的宿主。
##
## 在 4.6+ 上,把 [param control] 包装进一个固定在底部槽位的 [code]EditorDock[/code]
## —— 与引擎自带的底部面板停靠面板(dock)一致(非全局、瞬态、
## 水平布局)—— 并通过 [code]add_dock[/code] 注册;返回的
## [code]EditorDock[/code] 会被回传给 [method remove] 和 [method reveal]。
## 在 4.2–4.5 上,通过已弃用的 [code]add_control_to_bottom_panel[/code]
## 添加 [param control] 并返回 [code]null[/code]:没有包装器,
## [param control] 本身就是拆除/显示的句柄。
static func add(plugin: EditorPlugin, control: Control, title: String) -> Object:
	if _has_editor_dock():
		var dock: Object = ClassDB.instantiate("EditorDock")
		dock.call("add_child", control)
		dock.call("set_title", title)
		# 复现引擎自带的底部面板停靠面板(dock)设置(editor_bottom_panel.cpp
		# 的 add_item):非全局 + 瞬态,使该停靠面板(dock)既不会保存进编辑器
		# 布局,也不会列在停靠面板菜单中;固定到底部槽位并使用
		# 水平布局。底部槽位是关键 —— 没有它,add_dock
		# 会以隐藏状态添加停靠面板(dock),而 reveal() 会打开一个浮动窗口而不是面板。
		dock.call("set_global", false)
		dock.call("set_transient", true)
		dock.call("set_default_slot", _DOCK_SLOT_BOTTOM)
		dock.call("set_available_layouts", _DOCK_LAYOUT_HORIZONTAL)
		plugin.call("add_dock", dock)
		return dock
	plugin.add_control_to_bottom_panel(control, title)
	return null


## 移除由 [method add] 创建的停靠面板(dock)。[param host] 是该调用的返回值
## (4.6+ 上为 [code]EditorDock[/code] 包装器,4.2–4.5 上为 [code]null[/code])。
##
## 仅解除父子关系 —— [param control] 与包装器都不会被释放
## (拆除由调用方负责;参见组装器的 dispose())。
static func remove(plugin: EditorPlugin, control: Control, host: Object) -> void:
	if _has_editor_dock():
		plugin.call("remove_dock", host)
		return
	plugin.remove_control_from_bottom_panel(control)


## 显示停靠面板(dock):选中其标签页并展开底部面板。[param host] 是
## [method add] 的返回值。
##
## 在 4.6+ 上,[code]EditorDock.make_visible[/code] 既选中标签页又展开
## 已折叠的分栏;而原始的 [code]make_bottom_panel_item_visible[/code] 只设置一个
## 可见性标志,面板仍保持折叠。在 4.2–4.5 上,旧调用才是
## 正确的显示方式。
static func reveal(plugin: EditorPlugin, control: Control, host: Object) -> void:
	if _has_editor_dock():
		host.call("make_visible")
		return
	plugin.make_bottom_panel_item_visible(control)


# 在存在 EditorDock + add_dock API 的 Godot 4.6+ 上为 true;在 4.2–4.5 上为 false。
# 这是三个公开函数共同分支依据的唯一能力探测。
static func _has_editor_dock() -> bool:
	return ClassDB.class_exists("EditorDock")
