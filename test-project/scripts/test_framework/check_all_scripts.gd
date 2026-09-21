# scripts/test_framework/check_all_scripts.gd
# 逐文件 GDScript 校验——加载每个 .gd 文件并报告失败项。
# 运行方式：godot --headless --script scripts/test_framework/check_all_scripts.gd
#
# 以游戏模式运行（不带 --editor），因此继承仅编辑器基类的脚本会被跳过——
# 它们改由 validate_gdscript.sh 的无头编辑器(headless)阶段校验。

extends SceneTree

# 游戏模式下不可用的仅编辑器基类。继承这些基类的脚本会被跳过
# （由无头编辑器阶段校验）。若工具包新增编辑器派生基类，请更新此列表。
const EDITOR_BASE_CLASSES: Array[String] = [
	"EditorPlugin",
	"EditorInspectorPlugin",
	"EditorExportPlugin",
	"EditorProperty",
	"EditorResourcePreviewGenerator",
	"EditorScenePostImport",
	"EditorScript",
	"EditorSyntaxHighlighter",
	"EditorResourcePicker",
	"EditorDebuggerPlugin",
	"EditorNode3DGizmoPlugin",
	"EditorResourceConversionPlugin",
]

const SCAN_ROOT := "res://addons/godot_mcp_toolkit/"

var _pass_count := 0
var _fail_count := 0
var _skip_count := 0


func _init() -> void:
	var files := _glob_gd_files(SCAN_ROOT)
	print("check_all_scripts: scanning %d .gd files under %s" % [files.size(), SCAN_ROOT])
	print("")

	for path in files:
		_check_file(path)

	print("")
	print("check_all_scripts: %d passed, %d failed, %d skipped (editor-only)" % [
		_pass_count, _fail_count, _skip_count,
	])

	if _fail_count > 0:
		print("FAIL: %d script(s) have errors" % _fail_count)
		quit(1)
	else:
		print("PASS: all loadable scripts are valid")
		quit(0)


func _check_file(path: String) -> void:
	var content := FileAccess.get_file_as_string(path)
	if content.is_empty():
		print("  SKIP: %s (empty or unreadable)" % path)
		_skip_count += 1
		return

	if _extends_editor_class(content):
		_skip_count += 1
		return

	var script: Resource = ResourceLoader.load(path)
	if script == null or (script is Script and not script.can_instantiate()):
		print("  FAIL: %s" % path)
		_fail_count += 1
	else:
		_pass_count += 1


func _extends_editor_class(content: String) -> bool:
	for line in content.split("\n", false):
		var stripped := line.strip_edges()

		# 跳过空行、注释、注解(annotations)以及 extends 之前的 class_name
		if stripped.is_empty() or stripped.begins_with("#"):
			continue
		if stripped.begins_with("@") or stripped.begins_with("class_name"):
			continue

		if stripped.begins_with("extends "):
			var base_class := stripped.substr(8).strip_edges()
			# 处理内部类："EditorPlugin.SomeInner" -> "EditorPlugin"
			# 处理路径继承："res://..." -> 不是编辑器类名
			var base_name := base_class.split(".")[0].split("(")[0].strip_edges()
			return base_name in EDITOR_BASE_CLASSES

		# 第一条非 extends 的真实代码行——停止查找
		break

	return false


func _glob_gd_files(root: String) -> Array[String]:
	var results: Array[String] = []
	_glob_recursive(root, results)
	results.sort()
	return results


func _glob_recursive(dir_path: String, results: Array[String]) -> void:
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return

	dir.list_dir_begin()
	var entry := dir.get_next()
	while entry != "":
		var full_path := dir_path.path_join(entry)
		if dir.current_is_dir():
			_glob_recursive(full_path, results)
		elif entry.ends_with(".gd"):
			results.append(full_path)
		entry = dir.get_next()
	dir.list_dir_end()
