extends SceneTree

const Wizard := preload("res://addons/godot_mcp_toolkit/ui/onboarding_wizard.gd")

var _failures := 0
var _locale := {"value": "en"}


func _init() -> void:
	var language_provider := func() -> String:
		return str(_locale["value"])
	var wizard = Wizard.new(null, null, null, null, language_provider)

	var chinese_markers := [
		"设置向导",
		"欢迎使用 Godot MCP Unified",
		"下一步",
		"打开安全文档",
		".mcp.json 是一种可选的项目 MCP 配置",
		"创建 .mcp.json",
		"Godot MCP Unified 工具坞位于底部面板",
		"关闭",
		"res://addons/godot_mcp_toolkit/docs/security-recommendations.zh-CN.md",
	]
	var english_markers := [
		"Setup Wizard",
		"Welcome to the Godot MCP Unified",
		"Next",
		"Open Security Doc",
		".mcp.json is an optional project MCP configuration",
		"Create .mcp.json",
		"The Godot MCP Unified dock is in the bottom panel",
		"Close",
		"res://addons/godot_mcp_toolkit/docs/security-recommendations.md",
	]
	_check_language(wizard, "zh_CN", chinese_markers, ["Welcome to the Godot MCP Unified"])
	_check_language(wizard, "zh-TW", chinese_markers, ["Welcome to the Godot MCP Unified"])
	_check_language(wizard, "zh", chinese_markers, ["Welcome to the Godot MCP Unified"])
	_check_language(wizard, " ZH_hans ", chinese_markers, ["Welcome to the Godot MCP Unified"])
	_check_language(wizard, "en", english_markers, ["欢迎使用 Godot MCP Unified"])
	_check_language(wizard, "ja", english_markers, ["欢迎使用 Godot MCP Unified"])
	_check_language(wizard, "zhfoobar", english_markers, ["欢迎使用 Godot MCP Unified"])
	_check_language(wizard, "", english_markers, ["欢迎使用 Godot MCP Unified"])

	wizard.call("teardown")
	if _failures == 0:
		print("All onboarding locale tests passed.")
	quit(0 if _failures == 0 else 1)


func _check_language(
	wizard: RefCounted,
	locale: String,
	expected_markers: Array,
	forbidden_markers: Array,
) -> void:
	_locale["value"] = locale
	var rendered := _collect_rendered_copy(wizard)
	for marker in expected_markers:
		if str(marker) not in rendered:
			_fail("%s locale did not render %s: %s" % [locale, marker, rendered])
			return
	for marker in forbidden_markers:
		if str(marker) in rendered:
			_fail("%s locale unexpectedly rendered %s: %s" % [locale, marker, rendered])
			return
	var locale_label := locale if not locale.is_empty() else "<empty>"
	print("PASS: %s locale selected the expected onboarding copy" % locale_label)


func _collect_rendered_copy(wizard: RefCounted) -> String:
	var chunks: Array[String] = []
	chunks.append(str(wizard.call("_security_doc_path")))
	var dialog := AcceptDialog.new()
	wizard.call("_show_step", dialog)
	chunks.append(dialog.title)
	dialog.free()
	for spec in [
		wizard.call("_spec_welcome"),
		wizard.call("_spec_mcp_json", false),
		wizard.call("_spec_mcp_json", true),
		wizard.call("_spec_dock_overview"),
	]:
		var page := spec as Dictionary
		chunks.append(str(page.get("text", "")))
		chunks.append(str(page.get("ok_label", "")))
		for button in page.get("buttons", []):
			chunks.append(str((button as Dictionary).get("label", "")))
	return "\n".join(chunks)


func _fail(message: String) -> void:
	_failures += 1
	push_error("FAIL: " + message)
