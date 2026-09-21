@tool
extends RefCounted
## 停靠面板(dock)用的自释放确认对话框工厂。
##
## 一个无状态的 `static func` 辅助类（无实例）。停靠面板的两条是/否确认流程
## （清空审计日志、覆盖 .mcp.json）
## 构建的都是同一种 ConfirmationDialog 形态；
## 这一形态——以及自释放保证——就集中在这唯一一处。


# 按设计不涉及弹出提示(toast)/严重级别：工厂只负责构建并显示对话框，以及执行
# 调用方的确认/取消回调。流程所需的任何弹出提示都在它自己的回调内部发出
# （这是调用方的事，不是工厂的）。


## 构建一个 ConfirmationDialog，使其在编辑器基础控件上居中弹出，并在每一次
## 关闭时释放它——点击 OK 运行 `on_confirm`，点击取消/按 Esc/点 ✕ 运行
## `on_cancel`（如提供）——随后将自身 `queue_free()`。调用方无需任何清理：
## 自释放逻辑内建于此，任何调用点都不可能泄漏对话框。
##
## `cancel_text` 为空 → 保留默认的“Cancel”按钮文案。`on_cancel` 为空 →
## 仅关闭并释放（即普通的“你确定吗？”场景）。
##
## 引擎将窗口关闭（✕）和 Esc 都经由与取消按钮相同的 `canceled` 信号路由
## （AcceptDialog::_cancel_pressed），因此连接 `confirmed` + `canceled`
## 即可覆盖全部三条关闭路径。
static func confirm(
		title: String,
		body: String,
		ok_text: String,
		on_confirm: Callable,
		cancel_text: String = "",
		on_cancel: Callable = Callable()) -> void:
	var dialog := ConfirmationDialog.new()
	dialog.exclusive = false
	dialog.title = title
	dialog.dialog_text = body
	dialog.ok_button_text = ok_text
	if not cancel_text.is_empty():
		dialog.cancel_button_text = cancel_text
	dialog.confirmed.connect(func() -> void:
		if on_confirm.is_valid():
			on_confirm.call()
		dialog.queue_free()
	)
	dialog.canceled.connect(func() -> void:
		if on_cancel.is_valid():
			on_cancel.call()
		dialog.queue_free()
	)
	EditorInterface.get_base_control().add_child(dialog)
	dialog.popup_centered()
