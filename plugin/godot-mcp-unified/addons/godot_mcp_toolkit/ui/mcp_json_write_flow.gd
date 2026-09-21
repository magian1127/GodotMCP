@tool
extends RefCounted
## 项目根目录 .mcp.json 的共享“先确认后写入”流程。
##
## “写入 .mcp.json”这一用户操作唯一所在之处：检查写入是否会覆盖现有文件，
## 如果会则显示覆盖确认，将文件 I/O 委托给
## [code]MCPJsonSync.write_from_template[/code]
## （其保持纯 I/O），并通过调用方的结果回调上报结果。
## 由组装器(composer)构建，并注入到每个提供写入入口的界面中，
## 因此没有任何界面拥有该流程，也没有界面会绕道停靠面板(dock)去获取它
## ——停靠面板只是界面(UI)表面，不是服务定位器。
##
## 仅限编辑器：确认对话框以编辑器基础控件为父节点，
## 因此本脚本绝不能进入运行时自动加载(Autoload)的 preload 闭包。

const MCPJsonSync := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_sync.gd")
const DockConfirm := preload("res://addons/godot_mcp_toolkit/ui/dock/dock_confirm.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")


## 依据随附模板写入 .mcp.json；当文件已存在时先确认
## （[param force_overwrite] 为真时跳过确认）。
##
## 拒绝确认时不会写入，而是改为在系统编辑器中打开现有文件
## ——即“先别覆盖，让我自己看看”的补救路径——并且不产生任何上报。
## 写入尝试之后，[param on_result] 会收到一份 (ok, message, severity, tooltip)
## 报告；severity 采用编辑器弹出提示(toast)的级别刻度
## （0 信息 / 1 警告 / 2 错误）。传入空 Callable 即为即发即忘(fire-and-forget)。
func write(force_overwrite: bool = false, on_result: Callable = Callable()) -> void:
	# MCPJsonSync 会无条件调用其 on_result，因此对即发即忘（空回调）的
	# 调用方接收端要包一层有效性检查，而不是直接透传。
	var report := func(ok: bool, message: String, severity: int, tooltip: String) -> void:
		if on_result.is_valid():
			on_result.call(
				ok, _localized_result_message(message), severity,
				_localized_result_tooltip(tooltip))

	if not force_overwrite and MCPJsonSync.needs_overwrite_confirm():
		var dest := MCPJsonSync.get_mcp_json_path()
		# “取消”按钮被改用作“打开 .mcp.json”：拒绝覆盖时会打开该文件，
		# 让用户可以自行编辑（修复损坏的文件，或查看正常的文件），而不是把它弄丢。
		# Esc/✕ 也经由同一路径——即有意设计的“先别覆盖，让我自己看看”补救路径。
		# 这些 lambda 只捕获局部变量（report、dest）——在 Godot 4.2 上，
		# lambda 内对成员的裸引用不会标记为使用 self，
		# 会在 Nil 的 self 上查找。
		DockConfirm.confirm(
			EditorLocale.pick(".mcp.json already exists", ".mcp.json 已存在"),
			EditorLocale.pick(
				"Overwrite .mcp.json with a clean template?\n\n" + dest
					+ "\n\nThis replaces your current content — choose \"Open .mcp.json\" instead to edit the file yourself.",
				"要使用干净模板覆盖 .mcp.json 吗？\n\n" + dest
					+ "\n\n这会替换当前内容；如果希望自行编辑，请改选“打开 .mcp.json”。"),
			EditorLocale.pick("Overwrite", "覆盖"),
			func() -> void: MCPJsonSync.write_from_template(true, report),
			EditorLocale.pick("Open .mcp.json", "打开 .mcp.json"),
			func() -> void: OS.shell_open(dest),
		)
		return

	MCPJsonSync.write_from_template(force_overwrite, report)


func _localized_result_message(message: String) -> String:
	if not EditorLocale.is_chinese_editor():
		return message
	if message.begins_with("Template not found: "):
		return "未找到模板：" + message.trim_prefix("Template not found: ")
	if message == ".mcp.json already exists — overwrite not confirmed":
		return ".mcp.json 已存在 — 尚未确认覆盖"
	if message.begins_with("Failed to write .mcp.json"):
		return message.replace("Failed to write .mcp.json", "写入 .mcp.json 失败")
	if message == "MCP: .mcp.json written":
		return "MCP：已写入 .mcp.json"
	return message


func _localized_result_tooltip(tooltip: String) -> String:
	if EditorLocale.is_chinese_editor() and tooltip.begins_with("Wrote to "):
		return "已写入：" + tooltip.trim_prefix("Wrote to ")
	return tooltip
