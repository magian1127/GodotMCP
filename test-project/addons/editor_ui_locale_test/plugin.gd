@tool
extends EditorPlugin

const EditorUiLocaleTest := preload("res://tests/editor_ui_locale_test.gd")
const _RUN_ENV := "GODOT_MCP_RUN_EDITOR_UI_TEST"


func _enter_tree() -> void:
	if OS.get_environment(_RUN_ENV) == "1":
		call_deferred("_run_test")


func _run_test() -> void:
	EditorUiLocaleTest.new().run()
