extends SceneTree
## 对真实工具坞和向导检查可选配置语义，不启用插件、不连接编辑器。

const ConfigPanel := preload("res://addons/godot_mcp_toolkit/ui/dock/mcp/dock_mcp_json_panel.gd")
const Wizard := preload("res://addons/godot_mcp_toolkit/ui/onboarding_wizard.gd")
const Settings := preload("res://addons/godot_mcp_toolkit/core/settings_registration.gd")

var _failures := 0


class RecordingWriteFlow:
	extends "res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd"
	var writes := 0

	func write(_force_overwrite: bool = false, _on_result: Callable = Callable()) -> void:
		writes += 1


func _init() -> void:
	if not FileAccess.file_exists("res://.mcp-config-test"):
		push_error("此测试只能在隔离运行器创建的目录中执行。")
		quit(2)
		return
	_run.call_deferred()


func _run() -> void:
	var button := Button.new()
	var panel := ConfigPanel.new(button, Callable(), null)
	panel.refresh()
	_check(not panel.visible, "缺少可选配置时仍展示连接故障警告")
	_check("global MCP configuration" in button.tooltip_text, "工具坞未说明客户端配置仍可使用")
	_check(not button.has_theme_color_override("font_color"), "可选配置按钮仍显示警告颜色")
	var status := str(Settings._compute_status_text())
	_check("plugin or global MCP configuration" in status, "设置页仍要求必须创建项目配置")
	_test_wizard("zh_CN", "继续", "可选", "全局 MCP 配置")
	_test_wizard("en", "Continue", "optional", "global MCP configuration")
	_test_continue_does_not_write()
	var file := FileAccess.open("res://.mcp.json", FileAccess.WRITE)
	file.store_string("{ invalid json")
	file.close()
	panel.refresh()
	_check(panel.visible and button.text == "Fix .mcp.json", "有效的 JSON 错误警告被一起隐藏")
	DirAccess.remove_absolute(ProjectSettings.globalize_path("res://.mcp.json"))
	panel.free()
	button.free()
	if _failures == 0:
		print("PASS: MCP 工具坞、设置页及中英文向导提示测试全部通过。")
	quit(0 if _failures == 0 else 1)


func _test_wizard(language: String, continue_text: String, optional: String, global_config: String) -> void:
	var provider := func() -> String: return language
	var wizard := Wizard.new(null, null, null, null, provider)
	var spec: Dictionary = wizard._spec_mcp_json(false)
	_check(continue_text in str(spec.get("ok_label", "")), "向导默认按钮仍隐含创建文件")
	var text := str(spec.get("text", ""))
	_check(optional in text and global_config in text, "向导未解释可选配置和客户端配置")
	_check("required for your" not in text and "必须使用此文件" not in text, "向导仍包含错误的连接前置条件")
	var buttons: Array = spec.get("buttons", [])
	_check(buttons.size() == 1 and buttons[0].get("action", "") == "create_mcp", "缺少独立的创建动作")
	wizard.teardown()


func _test_continue_does_not_write() -> void:
	var write_flow := RecordingWriteFlow.new()
	var wizard := Wizard.new(null, null, write_flow, null)
	wizard.set("_step", 1)
	wizard.set("_mcp_exists", false)
	var dialog := AcceptDialog.new()
	root.add_child(dialog)
	wizard._on_confirmed(dialog)
	_check(write_flow.writes == 0, "向导继续操作仍自动写入项目配置")
	dialog.free()
	wizard.teardown()


func _check(condition: bool, message: String) -> void:
	if not condition:
		_failures += 1
		push_error("FAIL: " + message)
