@tool
extends RefCounted
## Godot MCP Unified 界面(UI)共用的编辑器区域设置(locale)与主题辅助工具。
##
## 插件文案跟随 Godot 编辑器(EDITOR)语言，而不是运行中项目的
## TranslationServer 区域设置。中文编辑器区域使用随附的简体中文文案；
## 其他所有区域回退(fallback)到英文。

const EDITOR_LANGUAGE_SETTING := "interface/editor/editor_language"


static func editor_locale() -> String:
	if not Engine.is_editor_hint():
		return ""
	var editor_settings := EditorInterface.get_editor_settings()
	if editor_settings == null or not editor_settings.has_setting(EDITOR_LANGUAGE_SETTING):
		return ""
	return str(editor_settings.get_setting(EDITOR_LANGUAGE_SETTING))


static func is_chinese_editor() -> bool:
	return is_chinese_locale(editor_locale())


static func is_chinese_locale(locale: String) -> bool:
	var normalized := locale.strip_edges().replace("-", "_").to_lower()
	return normalized == "zh" or normalized.begins_with("zh_")


static func pick(english: String, chinese: String) -> String:
	return chinese if is_chinese_editor() else english


## 编辑器主要文字颜色，可指定不透明度。正是对编辑器主题的采样，使次要标签
## 在浅色主题下保持深色、在深色主题下保持浅色。
static func text_color(opacity: float = 1.0) -> Color:
	var color := Color(0.85, 0.85, 0.85)
	if Engine.is_editor_hint():
		var base_control := EditorInterface.get_base_control()
		if base_control != null:
			color = base_control.get_theme_color("font_color", "Label")
	color.a = clampf(opacity, 0.0, 1.0)
	return color


static func muted_text_color() -> Color:
	return text_color(0.72)


static func subtle_text_color() -> Color:
	return text_color(0.84)


static func warning_color() -> Color:
	return (
		Color(1.0, 0.78, 0.24)
		if _is_dark_theme()
		else Color(0.52, 0.30, 0.0)
	)


static func success_color() -> Color:
	return (
		Color(0.48, 1.0, 0.52)
		if _is_dark_theme()
		else Color(0.06, 0.42, 0.12)
	)


static func info_color() -> Color:
	if Engine.is_editor_hint():
		var editor_settings := EditorInterface.get_editor_settings()
		if editor_settings != null and editor_settings.has_setting("interface/theme/accent_color"):
			var accent = editor_settings.get_setting("interface/theme/accent_color")
			if accent is Color:
				return accent
	return Color(0.35, 0.65, 1.0) if _is_dark_theme() else Color(0.05, 0.35, 0.72)


static func warning_background_color() -> Color:
	return _editor_base_color().lerp(warning_color(), 0.18)


static func card_background_color() -> Color:
	var base := _editor_base_color()
	return base.lightened(0.07) if _is_dark_theme() else base.darkened(0.035)


static func card_border_color() -> Color:
	return _editor_base_color().lerp(text_color(), 0.20)


static func _is_dark_theme() -> bool:
	return _editor_base_color().get_luminance() < 0.5


static func _editor_base_color() -> Color:
	if Engine.is_editor_hint():
		var editor_settings := EditorInterface.get_editor_settings()
		if editor_settings != null and editor_settings.has_setting("interface/theme/base_color"):
			var base = editor_settings.get_setting("interface/theme/base_color")
			if base is Color:
				return base
	return Color(0.2, 0.2, 0.2)
