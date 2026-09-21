@tool
extends RefCounted
## editor.windows / editor.answer_dialog 命令处理器 — 枚举编辑器主窗口之外的
## 额外窗口(对话框)并按键应答。目标是"模态对话框不再卡死 agent":外改场景
## 文件 + editor.refresh 后 Godot 可能弹出确认框(如 "scene was modified outside
## Godot"),agent 读取按钮清单并选择应答即可,无需人工介入。
##
## 按键分发纪律(参考 GDEditorBridge editor_use.gd 在官方 4.7.1 build 上的实测):
## - BaseButton.press() 在 4.7.1 不暴露给脚本 —— ClassDB.class_has_method(
##   "BaseButton", "press") 为 false,调用会中止所在函数并伪装成功;必须按类型
##   分发:普通按钮 emit pressed;toggle 按钮先 set_pressed()(翻转状态并发
##   toggled)再 emit pressed 保持与真实点击一致;OptionButton/MenuButton 走
##   show_popup()。
## - pressed.emit() 不经过 disabled 检查,按压前必须显式拒绝禁用控件。
## - 信号触发的引擎处理(如场景重载)在信号连接内同步执行,与真实点击一致;
##   应答后只读窗口可见性,不再触碰场景状态,立即返回。

## 测试缝(test seam):非空时替代 EditorInterface.get_base_control(),
## headless 自测由此注入假窗口树。
static var _test_base_control: Control = null


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("editor.windows", func(parameters: Dictionary) -> Dictionary:
		return _cmd_editor_windows(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("editor.answer_dialog", func(parameters: Dictionary) -> Dictionary:
		return _cmd_editor_answer_dialog(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


# -- 辅助函数 ------------------------------------------------------------------


## 编辑器基础控件(测试缝优先;正常路径为编辑器主 UI 根)。
static func _base() -> Control:
	if _test_base_control != null:
		return _test_base_control
	return EditorInterface.get_base_control()


## 主编辑器窗口 = base control 所在窗口(与 SceneTree.root 同一节点)。
static func _main_window() -> Window:
	var control := _base()
	if control == null or not control.is_inside_tree():
		return null
	return control.get_window()


## 从树根深度收集可见的额外窗口(主窗口本身除外)。
static func _visible_extra_windows() -> Array:
	var windows: Array = []
	var control := _base()
	if control == null or not control.is_inside_tree():
		return windows
	var tree := control.get_tree()
	if tree == null or tree.root == null:
		return windows
	_collect_windows(tree.root, _main_window(), windows)
	return windows


static func _collect_windows(node: Node, main: Window, into: Array) -> void:
	if node is Window:
		var win := node as Window
		if win.visible and win != main:
			into.append(win)
	for child in node.get_children():
		_collect_windows(child, main, into)


## 窗口标识:w<instance_id>(与按钮清单/应答参数共用同一 id 空间)。
static func _window_id(win: Window) -> String:
	return "w%d" % win.get_instance_id()


static func _window_kind(win: Window) -> String:
	if win is FileDialog:
		return "file"
	if win is ConfirmationDialog:
		return "confirmation"
	if win is AcceptDialog:
		return "accept"
	return "window"


## 窗口标题:显式 title 优先,AcceptDialog 用对话文本补位,最后落节点名。
static func _window_title(win: Window) -> String:
	if not win.title.is_empty():
		return win.title
	if win is AcceptDialog:
		var text := str((win as AcceptDialog).dialog_text).strip_edges()
		if not text.is_empty():
			return text
	return String(win.name)


## 对话框的按钮清单:ok/cancel/custom 三类角色,custom 带引擎 add_button
## 存进 _action 元数据的动作名(可能为空)。按压前先读 disabled。
static func _dialog_buttons(win: Window) -> Array:
	var buttons: Array = []
	if win is AcceptDialog:
		var dialog := win as AcceptDialog
		var ok_button := dialog.get_ok_button()
		if ok_button != null:
			buttons.append({
				"role": "ok",
				"text": str(ok_button.text),
				"action": "",
				"disabled": ok_button.disabled,
			})
		var cancel_button: Button = dialog.get_cancel_button()
		if cancel_button != null:
			buttons.append({
				"role": "cancel",
				"text": str(cancel_button.text),
				"action": "",
				"disabled": cancel_button.disabled,
			})
		for extra in dialog.get_buttons():
			var button := extra as Button
			if button == null:
				continue
			buttons.append({
				"role": "custom",
				"text": str(button.text),
				"action": str(button.get_meta("_action", "")),
				"disabled": button.disabled,
			})
	return buttons


## 按 id 解析窗口:数字部分必须是仍存活、在树中且可见的 Window,且不得是主窗口。
static func _resolve_window(window_id: String) -> Window:
	var raw := window_id.strip_edges()
	if not raw.begins_with("w") or not raw.substr(1).is_valid_int():
		return null
	var main := _main_window()
	if main != null and _window_id(main) == raw:
		return null
	var obj: Object = instance_from_id(int(raw.substr(1)))
	if obj is Window:
		var win := obj as Window
		if is_instance_valid(win) and win.is_inside_tree() and win.visible:
			return win
	return null


## 按参数定位要按的按钮:role(ok/cancel)→ custom 动作名 → 精确文本,逐级回退。
static func _resolve_button(win: Window, button_selector: String) -> Button:
	if not (win is AcceptDialog):
		return null
	var dialog := win as AcceptDialog
	var lowered := button_selector.to_lower()
	if lowered == "ok":
		return dialog.get_ok_button()
	if lowered == "cancel":
		return dialog.get_cancel_button()
	for extra in dialog.get_buttons():
		var button := extra as Button
		if button == null:
			continue
		if str(button.get_meta("_action", "")) == button_selector:
			return button
	# 文本兜底:ok/cancel/custom 的按钮文本(编辑器本地化后 role 名往往对不上
	# 按钮文字,按 id 列表里看到的 text 精确匹配)。
	var ok_button := dialog.get_ok_button()
	if ok_button != null and str(ok_button.text) == button_selector:
		return ok_button
	var cancel_button: Button = dialog.get_cancel_button()
	if cancel_button != null and str(cancel_button.text) == button_selector:
		return cancel_button
	for extra in dialog.get_buttons():
		var button := extra as Button
		if button != null and str(button.text) == button_selector:
			return button
	return null


## 按类型分发按压(4.7.1 的 press() 不暴露给脚本,详见文件头)。
## 返回空串表示成功,否则为人类可读的失败原因。
static func _press_button(button: BaseButton) -> String:
	if button.disabled:
		return "control is disabled"
	if button is OptionButton:
		(button as OptionButton).show_popup()
		return ""
	if button is MenuButton:
		(button as MenuButton).show_popup()
		return ""
	if button.toggle_mode:
		# 真实点击先发 toggled 再发 pressed;set_pressed() 覆盖前者,
		# 这里补发后者保持忠实。
		var before := button.button_pressed
		button.set_pressed(not before)
		if button.button_pressed == before:
			return "toggle did not change the control state"
		button.pressed.emit()
		return ""
	button.pressed.emit()
	return ""


# -- 命令 ---------------------------------------------------------------------


## 应答方式说明(两种情况共用):对话框应答优先选 reload/overwrite 类动作,
## cancel/dismiss 会保留编辑器内副本,后续编辑器保存会覆盖外部修改。
static func _answering_note() -> String:
	return ("extra windows only (the main editor window is never listed); "
		+ "answer with editor.answer_dialog(window=<id>, button=<role|action|text>). "
		+ "When a refresh hangs on a confirmation dialog, prefer the reload/overwrite "
		+ "action over cancel/dismiss — cancel keeps the in-editor copy and a later "
		+ "editor save would overwrite your external change.")


static func _cmd_editor_windows(_parameters: Dictionary) -> Dictionary:
	var rows: Array = []
	for win in _visible_extra_windows():
		var window := win as Window
		var row := {
			"id": _window_id(window),
			"kind": _window_kind(window),
			"title": _window_title(window),
			"class": window.get_class(),
			"exclusive": window.exclusive,
			"transient": window.transient,
		}
		var buttons := _dialog_buttons(window)
		if not buttons.is_empty():
			row["buttons"] = buttons
		rows.append(row)
	return MCPToolkitSuccess.ok({
		"count": rows.size(),
		"windows": rows,
		"note": _answering_note(),
	})


static func _cmd_editor_answer_dialog(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["window"])
	if err != null:
		return err
	var window_id: String = str(parameters.get("window", ""))
	var button_selector: String = str(parameters.get("button", "ok"))
	if button_selector.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS",
			"button must be 'ok', 'cancel', 'dismiss', a custom action name, or an exact button text")

	var window := _resolve_window(window_id)
	if window == null:
		return MCPToolkitError.fail("NOT_FOUND",
			"no visible extra window with id '%s' (ids come from editor.windows; the main "
			+ "editor window can never be answered) — re-list with editor.windows" % window_id)

	if button_selector.to_lower() == "dismiss":
		# 关闭请求语义:Window close_requested,AcceptDialog 追加 canceled,再隐藏。
		# 注意:对"文件在外部被修改"类确认框,dismiss 等价于取消(保留编辑器内
		# 副本),外部修改会被后续保存覆盖 —— 应答说明里已明确。
		window.close_requested.emit()
		if window is AcceptDialog:
			(window as AcceptDialog).canceled.emit()
		window.hide()
		return MCPToolkitSuccess.ok({
			"window": window_id,
			"action": "dismiss",
			"pressed": false,
			"window_visible_after": window.visible,
		})

	var button := _resolve_button(window, button_selector)
	if button == null:
		return MCPToolkitError.fail("NOT_FOUND",
			"window '%s' has no button matching '%s' (role ok/cancel, custom action name, "
			+ "or exact text) — re-list with editor.windows" % [window_id, button_selector])
	var press_error := _press_button(button)
	if press_error != "":
		return MCPToolkitError.fail("INVALID_PARAMS",
			"cannot press '%s' on window '%s': %s" % [button_selector, window_id, press_error])
	return MCPToolkitSuccess.ok({
		"window": window_id,
		"action": "press",
		"button": button_selector,
		"button_text": str(button.text),
		"pressed": true,
		"window_visible_after": window.visible,
	})
