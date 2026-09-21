@tool
extends RefCounted
## file.* 命令处理器 — 针对任意 res:// 路径的通用文件删除。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const Helpers = Modules.CommandHelpers
const AssetDependents := preload("res://addons/godot_mcp_toolkit/commands/asset_dependents.gd")

const _TAB_CLOSE_NOISE_HINT := "Closing a non-active scene tab may produce a _set_main_scene_state error in the editor console. This is benign Godot engine noise — safe to ignore."


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("file.delete", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_file_delete(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


# -- 命令 ---------------------------------------------------------------------


static func _cmd_file_delete(parameters: Dictionary) -> Dictionary:
	var file_path := str(parameters.get("file_path", ""))
	if file_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing file_path")
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if file_path.begins_with("res://addons/godot_mcp_toolkit/"):
		return MCPToolkitError.fail("PATH_DENIED",
			"cannot delete files inside the MCP toolkit plugin directory")
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "file not found: %s" % file_path, MCPToolkitError.HINT_FILE_PATH)

	# 删除前引用安全检查:被其他资源引用时默认拒绝(force=true 覆盖);
	# 索引不可用(如扫描进行中)时如实降级为警告,不静默放行。
	var warnings: Array[String] = []
	if not bool(parameters.get("force", false)):
		var reference_check: Dictionary = AssetDependents.referenced_by(file_path)
		if bool(reference_check.get("blocked", false)):
			var referencers: Array = reference_check.get("referencers", [])
			var preview := ", ".join(PackedStringArray(
				(referencers.slice(0, 10) as Array).map(func(item): return str(item))))
			return MCPToolkitError.fail("REFERENCED",
				"file %s is referenced by %d other resource(s): %s%s" % [
					file_path, int(reference_check.get("total", 0)), preview,
					"…" if referencers.size() > 10 else ""],
				"delete the references first, or re-run with force=true to delete anyway; "
				+ "asset.get_dependents lists the full set (refresh=true rebuilds the index)")
		if not bool(reference_check.get("checked", false)):
			warnings.append(str(reference_check.get("note", "reference check unavailable")))
	else:
		warnings.append("force=true: reference safety check skipped")

	# 对于场景文件(.tscn/.scn),先尝试关闭编辑器标签页。
	var tab_closed := false
	var tab_result := {}
	var ext := file_path.get_extension().to_lower()
	var is_scene := (ext == "tscn" or ext == "scn")

	if is_scene:
		tab_result = await Helpers.close_scene_tab_safe(file_path)
		tab_closed = tab_result.get("closed", false)
		if not tab_closed:
			var reason := str(tab_result.get("reason", ""))
			if reason == "no_api":
				# 4.2–4.4:没有关闭标签页的 API。阻止删除当前活动场景(否则 Ctrl+S
				# 会静默地重建该文件)。非活动标签页继续执行,
				# 并附带一条"幽灵标签页"警告。
				var edited_root := Helpers.get_edited_root()
				if edited_root != null and edited_root.scene_file_path == file_path:
					return MCPToolkitError.fail("EDITED_SCENE",
						"cannot delete the currently-edited scene %s on Godot 4.2-4.4 (no programmatic tab-close API); close the scene tab manually, then retry project_delete(kind:'file')" % file_path)
				warnings.append(
					"phantom tab: scene tab for %s remains open; Godot 4.2-4.4 has no API to close tabs — it will vanish on editor restart or manual close" % file_path)

	var delete_result: Dictionary = await Helpers.delete_res_file_and_deindex(
		file_path, [".uid", ".import"])
	if is_scene:
		delete_result["tab_closed"] = tab_closed
		if tab_closed and tab_result.get("switched", false):
			delete_result["hint"] = _TAB_CLOSE_NOISE_HINT
	if not warnings.is_empty():
		delete_result["warnings"] = warnings
	return delete_result
