@tool
class_name MCPToolkitUndoRedoAction
extends RefCounted
## 面向上下文协议(MCP)工具包工具与扩展的链式 UndoRedo 构建器。
##
## 包装 [code]EditorUndoRedoManager[/code],提供自动的无头模式安全空操作、
## [code]"MCP: "[/code] 动作名前缀,以及可链式调用的构建器 API。从
## [method begin] 开始,用 [method do_property] / [method undo_property]
## (以及 [method do_method] / [method undo_method]、[method do_reference] /
## [method undo_reference])成对记录执行侧(do)与撤销侧(undo),然后以
## [method commit_recorded] 或 [method commit] 收尾。当 [method is_active]
## 为 [code]false[/code](无头模式)时,每个构建器方法都是空操作,因此同一段
## 代码在没有编辑器时也能安全运行.[br]
## [br]
## 推荐模式 — 先应用修改,再用 [method commit_recorded] 把它记录为可撤销:
## [codeblock]
## node.set(&"position", new_pos)
## MCPToolkitUndoRedoAction.begin("set position", node) \
##     .do_property(node, &"position", new_pos) \
##     .undo_property(node, &"position", old_pos) \
##     .commit_recorded()
## [/codeblock]
## C# 扩展无法调用这个 GDScript 静态方法 — 请改用
## [method MCPToolkitCommandRegistry.create_undo_action] 而非 [method begin]。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")

## EditorUndoRedoManager — 用于所有操作(create、add、commit)。
## 路由到正确的内部历史由管理器自身处理。
var _mgr = null  # 无类型以兼容无头模式
var _active: bool = false
var _committed: bool = false


## 创建一个撤销动作并返回可供链式调用的构建器。[param description] 是
## 人类可读的动作名(自动加上 [code]"MCP: "[/code] 前缀);[param context_object]
## 是可选的场景所属节点,用于告知 Godot 该动作属于哪个场景的历史
## (多标签编辑时很重要)。返回绑定到 [code]EditorUndoRedoManager[/code] 的
## 构建器;在无头上下文(无编辑器插件)中则返回空操作构建器 —
## 参见 [method is_active]。
static func begin(description: String, context_object: Object = null) -> MCPToolkitUndoRedoAction:
	var action := MCPToolkitUndoRedoAction.new()
	var mgr = Modules.EditorAccess.get_undo_redo()
	if mgr != null:
		mgr.create_action("MCP: " + description, 0, context_object)
		action._mgr = mgr
		action._active = true
	return action


## 当 [code]EditorUndoRedoManager[/code] 可用、且动作已创建但尚未提交时返回
## [code]true[/code]。在无头上下文(每个构建器方法都是空操作)中,用它跳过
## 代价高昂的状态捕获。
func is_active() -> bool:
	return _active


# -- 属性 ---------------------------------------------------------------------

## 记录执行侧(redo)的值:动作应用时把 [param obj] 上的 [param property] 设为
## [param value]。请与携带旧值的 [method undo_property] 成对使用。返回
## [code]self[/code] 以便链式调用。
func do_property(obj: Object, property: StringName, value: Variant) -> MCPToolkitUndoRedoAction:
	if _active:
		_mgr.add_do_property(obj, property, value)
	return self


## 记录撤销侧的值:动作被撤销时把 [param obj] 上的 [param property] 恢复为
## [param value]。请传入修改之前该属性持有的值。返回 [code]self[/code]
## 以便链式调用。
func undo_property(obj: Object, property: StringName, value: Variant) -> MCPToolkitUndoRedoAction:
	if _active:
		_mgr.add_undo_property(obj, property, value)
	return self


# -- 方法(Callable)-----------------------------------------------------------

## 记录 [param callable] 在执行侧(redo)运行。用 [code].bind()[/code] 把参数
## 绑定到该 [Callable] 上。返回 [code]self[/code] 以便链式调用。
## [codeblock]
## action.do_method(node.add_child.bind(child))
## [/codeblock]
func do_method(callable: Callable) -> MCPToolkitUndoRedoAction:
	if _active:
		var args: Array = [callable.get_object(), callable.get_method()]
		args.append_array(callable.get_bound_arguments())
		_mgr.callv(&"add_do_method", args)
	return self


## 记录 [param callable] 在撤销侧运行。用 [code].bind()[/code] 把参数绑定到
## 该 [Callable] 上。返回 [code]self[/code] 以便链式调用。
## [codeblock]
## action.undo_method(parent.remove_child.bind(child))
## [/codeblock]
func undo_method(callable: Callable) -> MCPToolkitUndoRedoAction:
	if _active:
		var args: Array = [callable.get_object(), callable.get_method()]
		args.append_array(callable.get_bound_arguments())
		_mgr.callv(&"add_undo_method", args)
	return self


# -- 引用 -----------------------------------------------------------------------

## 让 [param ref] 在 redo 期间保持存活。用于一个新创建、否则会在被撤销时
## 被释放的对象(例如撤销会把它从树上移除的新节点)。返回 [code]self[/code]
## 以便链式调用。
func do_reference(ref: Object) -> MCPToolkitUndoRedoAction:
	if _active:
		_mgr.add_do_reference(ref)
	return self


## 让 [param ref] 在撤销期间保持存活。用于一个被替换、否则会被释放的旧对象
## (例如执行侧覆盖掉的资源)。返回 [code]self[/code] 以便链式调用。
func undo_reference(ref: Object) -> MCPToolkitUndoRedoAction:
	if _active:
		_mgr.add_undo_reference(ref)
	return self


# -- 提交 -----------------------------------------------------------------------

## 提交动作,让 UndoRedo 立即执行执行侧。适用于应由 UndoRedo 自己驱动修改
## (而非先手动应用)的场合。重复调用它(或 [method commit_recorded])会
## 警告并被忽略。对比 [method commit_recorded] — 那是推荐的默认方式。
func commit() -> void:
	if _committed:
		push_warning("[MCPToolkitUndoRedoAction] Action already committed - ignoring duplicate commit() call")
		return
	_committed = true
	if _active:
		_mgr.commit_action()
		_active = false


## 以"已记录"方式提交动作:修改已被直接应用,因此这里只注册 do/undo 步骤,
## 不重新执行执行侧。这是上下文协议工具的推荐默认方式 — 先应用修改,再调用它,
## 让 Ctrl+Z / Ctrl+Y 生效。重复调用它(或 [method commit])会警告并被忽略。
func commit_recorded() -> void:
	if _committed:
		push_warning("[MCPToolkitUndoRedoAction] Action already committed - ignoring duplicate commit_recorded() call")
		return
	_committed = true
	if _active:
		_mgr.commit_action(false)
		_active = false
