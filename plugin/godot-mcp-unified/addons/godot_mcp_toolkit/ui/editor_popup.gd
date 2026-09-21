@tool
extends RefCounted
## 呈现编辑器弹出对话框：居中打开并将其提升到前台。
##
## 非独占（non-exclusive）的编辑器对话框在视口被点击的那一刻就会沉到主视口
## 之后；此时再次触发它的打开按钮只会重新渲染那个已打开却不可见的窗口，
## 让人感觉没有响应。让每个由按钮打开的对话框都经由这一处来呈现，可以保证
## 提升操作的一致性：grab_focus() 既能提升也能聚焦，而且——与在 4.6+ 中
## 已被弃用的 move_to_foreground
## 不同——它在所有受支持的引擎版本上都已绑定
## 且未弃用。


## 居中弹出 [param dialog] 并将其提升到前台。对于复用的对话框，请显式传入
## [param size]：无参的 popup_centered 会沿用窗口当前尺寸（在多次重新打开间
## 只增不减），因此复用的对话框若不传入尺寸就会逐渐漂移变大。
static func present(dialog: Window, size: Vector2i = Vector2i.ZERO) -> void:
	if size == Vector2i.ZERO:
		dialog.popup_centered()
	else:
		dialog.popup_centered(size)
	dialog.grab_focus()
