@tool
extends RefCounted

const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")
const Dock := preload("res://addons/godot_mcp_toolkit/ui/dock/dock.gd")
const InfoDialog := preload("res://addons/godot_mcp_toolkit/ui/dock/ext/info_dialog.gd")

var _failures := 0


class FakeServer extends Node:
	signal client_connected(peer_count: int)
	signal client_disconnected(peer_count: int)
	signal command_received(method: String)
	signal lsp_status_changed
	signal port_status_changed

	func is_listening() -> bool:
		return true

	func get_bound_port() -> int:
		return 6550

	func get_port_source() -> String:
		return "default"

	func get_port_warning() -> Dictionary:
		return {"active": false, "message": "", "label": ""}

	func get_authed_peer_count() -> int:
		return 0

	func get_reported_lsp_status() -> Dictionary:
		return {}

	func get_unfocused_responsive_fps() -> int:
		return 60

	func notify_unfocused_responsive_setting_changed() -> void:
		pass

	func regenerate_token() -> void:
		pass

	func get_command_methods() -> Array:
		return ["node.get", "scene.open"]


func run() -> void:
	var server := FakeServer.new()
	var base := EditorInterface.get_base_control()
	base.add_child(server)

	var dock := Dock.new()
	dock.bind(server, "", null, null)
	base.add_child(dock)

	var info := InfoDialog.new()
	base.add_child(info)
	info.show_info(server)

	var rendered := _collect_text(dock) + "\n" + _collect_text(info)
	if EditorLocale.is_chinese_editor():
		_check_markers(rendered, [
			"服务器状态",
			"正在监听 127.0.0.1:6550 (默认)",
			"0 个连接",
			"运行时：未运行",
			"失去焦点时保持响应",
			"审计日志",
			"安全与响应上限",
			"配套技能",
			"信息 / 帮助",
			"Godot MCP Unified — 信息 / 帮助",
			"已注册工具",
			"2 个插件端命令",
		], [
			"Server Status",
			"Listening on 127.0.0.1:6550 (default)",
			"0 peers",
			"Runtime: not running",
			"Responsive when unfocused",
			"Audit Log",
			"Security & Response Limits",
			"Companion Skills",
			"Info / Help",
			"Registered Tools",
		])
	else:
		_check_markers(rendered, [
			"Server Status",
			"Listening on 127.0.0.1:6550 (default)",
			"0 peers",
			"Runtime: not running",
			"Responsive when unfocused",
			"Audit Log",
			"Security & Response Limits",
			"Companion Skills",
			"Info / Help",
			"Registered Tools",
		], [
			"服务器状态",
			"正在监听",
			"运行时：",
			"失去焦点时保持响应",
			"审计日志",
			"安全与响应上限",
			"已注册工具",
		])

	_check_theme_contrast(base)

	info.free()
	dock.free()
	server.free()
	if _failures == 0:
		print("All editor UI locale and theme tests passed for locale: %s" % EditorLocale.editor_locale())
	base.get_tree().quit(0 if _failures == 0 else 1)


func _collect_text(node: Node) -> String:
	var chunks: Array[String] = []
	if node is Window:
		chunks.append((node as Window).title)
	if node is Label:
		chunks.append((node as Label).text)
	elif node is BaseButton:
		chunks.append((node as BaseButton).text)
	if node is Control:
		chunks.append((node as Control).tooltip_text)
	for child in node.get_children():
		chunks.append(_collect_text(child))
	return "\n".join(chunks)


func _check_markers(rendered: String, expected: Array, forbidden: Array) -> void:
	for marker in expected:
		if str(marker) not in rendered:
			_fail("Missing expected UI copy: %s" % marker)
	for marker in forbidden:
		if str(marker) in rendered:
			_fail("Found wrong-locale UI copy: %s" % marker)


func _check_theme_contrast(base: Control) -> void:
	var theme_text := base.get_theme_color("font_color", "Label")
	var muted := EditorLocale.muted_text_color()
	if Vector3(theme_text.r, theme_text.g, theme_text.b).distance_to(
			Vector3(muted.r, muted.g, muted.b)) > 0.01:
		_fail("Muted text does not derive from the editor theme font color")
	if muted.a < 0.7:
		_fail("Muted text opacity is too low for small editor labels: %.3f" % muted.a)

	var editor_settings := EditorInterface.get_editor_settings()
	var background = editor_settings.get_setting("interface/theme/base_color")
	if not background is Color:
		return
	var effective_muted := Color(
		lerpf(background.r, muted.r, muted.a),
		lerpf(background.g, muted.g, muted.a),
		lerpf(background.b, muted.b, muted.a))
	if _contrast_ratio(effective_muted, background) < 3.0:
		_fail("Muted text contrast is below 3:1 against the editor base color")
	for semantic_color in [EditorLocale.warning_color(), EditorLocale.success_color()]:
		if _contrast_ratio(semantic_color, background) < 3.0:
			_fail("Semantic status color contrast is below 3:1 against the editor base color")


func _contrast_ratio(a: Color, b: Color) -> float:
	var lighter := maxf(_relative_luminance(a), _relative_luminance(b))
	var darker := minf(_relative_luminance(a), _relative_luminance(b))
	return (lighter + 0.05) / (darker + 0.05)


func _relative_luminance(color: Color) -> float:
	return (
		0.2126 * _linear_channel(color.r)
		+ 0.7152 * _linear_channel(color.g)
		+ 0.0722 * _linear_channel(color.b)
	)


func _linear_channel(channel: float) -> float:
	return channel / 12.92 if channel <= 0.04045 else pow((channel + 0.055) / 1.055, 2.4)


func _fail(message: String) -> void:
	_failures += 1
	push_error("FAIL: " + message)
