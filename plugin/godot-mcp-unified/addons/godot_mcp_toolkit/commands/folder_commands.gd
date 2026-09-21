@tool
extends RefCounted
## folder.* 命令处理器 — 在 res:// 下创建与删除目录。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const Helpers = Modules.CommandHelpers


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("folder.create", func(parameters: Dictionary) -> Dictionary:
		return _cmd_folder_create(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("folder.delete", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_folder_delete(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


# -- 命令 ---------------------------------------------------------------------


static func _cmd_folder_create(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["path"])
	if err != null:
		return err
	var path := str(parameters.get("path", ""))
	var guard := FileGuard.resolve_safe(path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var pre_existed := DirAccess.dir_exists_absolute(path)
	var error := DirAccess.make_dir_recursive_absolute(path)
	if error != OK:
		return MCPToolkitError.fail("CREATE_DIR_FAILED",
			"DirAccess.make_dir_recursive_absolute returned %d (path=%s)" % [error, path])
	var status := "returned" if pre_existed else "created"
	return MCPToolkitSuccess.ok({"status": status, "path": path})


static func _cmd_folder_delete(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["path"])
	if err != null:
		return err
	var path := str(parameters.get("path", ""))
	var recursive := bool(parameters.get("recursive", false))
	var guard := FileGuard.resolve_safe(path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))

	if path == "res://" or path == "res:///" or path.get_base_dir() == "":
		return MCPToolkitError.fail("FOLDER_PROTECTED",
			"cannot delete the project root res://; narrow the path")

	var normalized := path
	if normalized.ends_with("/"):
		normalized = normalized.substr(0, normalized.length() - 1)
	if normalized == "res://addons" or normalized == "res://addons/godot_mcp_toolkit":
		return MCPToolkitError.fail("FOLDER_PROTECTED",
			"cannot delete res://addons or the toolkit plugin directory (%s); agent cannot remove its own host" % normalized)
	if not DirAccess.dir_exists_absolute(path):
		return MCPToolkitError.fail("NOT_FOUND", "no folder at %s" % path)
	var normalized_with_slash := normalized + "/"

	# 收集在目标文件夹之内与之外打开的场景标签页。
	var open_scenes := EditorInterface.get_open_scenes()
	var inside_scenes: Array[String] = []
	var outside_scenes: Array[String] = []
	for scene_path in open_scenes:
		var sp := str(scene_path)
		if sp == normalized or sp.begins_with(normalized_with_slash):
			inside_scenes.append(sp)
		else:
			outside_scenes.append(sp)

	var edited := Helpers.get_edited_root()
	var active_path := ""
	if edited != null:
		active_path = str(edited.scene_file_path)
	var active_inside := not active_path.is_empty() and (active_path == normalized or active_path.begins_with(normalized_with_slash))

	# 场景标签页处理策略:
	# - 目标内恰好 1 个场景且在 4.5+ 上:通过辅助函数关闭它(单次
	#   打开+关闭循环是安全的;完全避免幽灵标签页)。
	# - 目标内多个场景:不能在循环中关闭(延迟队列会崩溃)。
	#   把活动场景切换走,返回 stale_tabs 供代理后续通过
	#   scene.close 处理(上下文协议(MCP)往返提供了时序保障)。
	# - 在 4.2–4.4 上:没有关闭 API —— 总是切换走 + stale_tabs。
	var stale_tabs: Array[String] = []
	var warnings: Array[String] = []
	var single_closed := false
	# close_scene 是 4.5+ 的;在 4.2–4.4 上没有关闭场景标签页的 API,因此
	# 陈旧标签页的后续处理提示不得指向(不存在的)scene_close 工具。
	var can_close := EditorInterface.has_method("close_scene")

	if inside_scenes.size() == 1 and can_close:
		# 单个场景 — 通过辅助函数关闭是安全的(无论是否为活动场景)。
		var tab_result := await Helpers.close_scene_tab_safe(inside_scenes[0])
		single_closed = tab_result.get("closed", false)
		if not single_closed:
			# no_api 不应发生(已检查过 has_method),但以防万一。
			stale_tabs = inside_scenes
	elif inside_scenes.size() > 0:
		# 多个场景(或 4.2–4.4):把活动场景切换走,列出幽灵标签页。
		if active_inside:
			if outside_scenes.is_empty():
				return MCPToolkitError.fail("PATH_IN_USE",
					"all open scene tabs are inside %s; open a scene outside the folder first via scene_open, then retry project_delete(kind:'folder')" % path)
			if not await Helpers.open_scene_deferred(outside_scenes[0]):
				return MCPToolkitError.fail("TIMEOUT",
					"could not switch active scene away from %s — filesystem scanning; retry shortly" % path)
		stale_tabs = inside_scenes
		if not can_close:
			warnings.append(
				"phantom tabs: Godot 4.2-4.4 has no API to close scene tabs — %d tab(s) inside %s will remain as phantoms until editor restart" % [inside_scenes.size(), path])

	var script_editor := EditorInterface.get_script_editor()
	if script_editor != null:
		for open_script in script_editor.get_open_scripts():
			if not (open_script is Resource):
				continue
			var resource_path := str((open_script as Resource).resource_path)
			if resource_path.is_empty():
				continue
			if resource_path == normalized or resource_path.begins_with(normalized_with_slash):
				return MCPToolkitError.fail("PATH_IN_USE",
					"folder %s contains open script %s; close the script editor tab manually (no programmatic close API for script tabs), then retry project_delete(kind:'folder')" % [
						path, resource_path])

	var directory := DirAccess.open(path)
	if directory == null:
		return MCPToolkitError.fail("INTERNAL",
			"DirAccess.open(%s) returned null" % path)
	var file_count := directory.get_files().size()
	var subdir_count := directory.get_directories().size()
	if (file_count + subdir_count) > 0 and not recursive:
		return MCPToolkitError.fail("DIR_NOT_EMPTY",
			"folder %s is not empty (contains %d files, %d subdirs); pass recursive:true to delete contents" % [
				path, file_count, subdir_count])

	var files_deleted := 0
	var dirs_deleted := 0
	if recursive and (file_count + subdir_count) > 0:
		var result := _folder_delete_recursive(path)
		files_deleted = int(result.get("files", 0))
		dirs_deleted = int(result.get("dirs", 0))
		if not bool(result.get("success", false)):
			return MCPToolkitError.fail("DELETE_FAILED", str(result.get("error", "unknown")))

	var parent_path := path.get_base_dir()
	var parent_dir := DirAccess.open(parent_path)
	if parent_dir == null:
		return MCPToolkitError.fail("INTERNAL",
			"DirAccess.open(%s) returned null" % parent_path)
	var top_remove := parent_dir.remove(path.get_file())
	if top_remove != OK:
		return MCPToolkitError.fail("DELETE_FAILED",
			"DirAccess.remove returned %d (path=%s)" % [top_remove, path])
	if recursive and (file_count + subdir_count) > 0:
		push_warning("[MCPTools] folder.delete recursive %s (%d files, %d subdirs)" % [
			path, files_deleted, dirs_deleted])
	# 目标文件夹本身刚在上面被移除 —— 把它与递归删除过程中删除的
	# 嵌套子目录一并计数。此前被遗漏,导致不含子目录的文件夹
	# 在已删除的情况下仍报告 directories_deleted:0。
	dirs_deleted += 1
	# 定向去索引:在大多数 Godot 版本中,对目录路径调用 update_file() 是
	# 空操作,因此对文件夹移除回退到 scan()。文件夹删除很罕见,
	# 扫描的代价可以接受。
	var removal := await Helpers.ensure_file_removed(path)
	var result := {
		"path": path,
		"recursive": recursive,
		"files_deleted": files_deleted,
		"directories_deleted": dirs_deleted,
		"deindexed": removal["removed"],
	}
	if single_closed:
		result["tab_closed"] = inside_scenes[0]
	if not stale_tabs.is_empty():
		result["stale_tabs"] = stale_tabs
		if can_close:
			result["hint"] = "Phantom scene tabs remain for %d file(s) inside the deleted folder. Close them one at a time via scene_close. Note: each scene_close may produce a _set_main_scene_state error in the editor console — this is benign Godot engine noise, safe to ignore." % stale_tabs.size()
		else:
			result["hint"] = "Phantom scene tabs remain for %d file(s) inside the deleted folder. Godot 4.2–4.4 has no API to close scene tabs — restart the editor to clear them." % stale_tabs.size()
	if active_inside and not single_closed:
		result["switched_to"] = outside_scenes[0]
	if not warnings.is_empty():
		result["warnings"] = warnings
	return MCPToolkitSuccess.ok(result)


# -- 递归删除辅助函数 ---------------------------------------------------------


static func _folder_delete_recursive(path: String) -> Dictionary:
	var directory := DirAccess.open(path)
	if directory == null:
		return {"files": 0, "dirs": 0, "success": false,
			"error": "DirAccess.open(%s) returned null" % path}
	var files_removed := 0
	var dirs_removed := 0
	for file_name in directory.get_files():
		if file_name.ends_with(".uid"):
			continue
		var full_res_path := path + "/" + file_name
		var uid: int = ResourceLoader.get_resource_uid(full_res_path)
		var remove_error := directory.remove(file_name)
		if remove_error != OK:
			return {"files": files_removed, "dirs": dirs_removed, "success": false,
				"error": "DirAccess.remove %s/%s returned %d" % [path, file_name, remove_error]}
		files_removed += 1
		var uid_companion := file_name + ".uid"
		if directory.file_exists(uid_companion):
			directory.remove(uid_companion)
		if uid != -1 and ResourceUID.has_id(uid):
			ResourceUID.remove_id(uid)
	for file_name in directory.get_files():
		if file_name.ends_with(".uid"):
			directory.remove(file_name)
	for sub_name in directory.get_directories():
		var sub_path := path + "/" + sub_name
		var sub_result := _folder_delete_recursive(sub_path)
		files_removed += int(sub_result.get("files", 0))
		dirs_removed += int(sub_result.get("dirs", 0))
		if not bool(sub_result.get("success", false)):
			return {"files": files_removed, "dirs": dirs_removed, "success": false,
				"error": str(sub_result.get("error", "unknown"))}
		var remove_sub_error := directory.remove(sub_name)
		if remove_sub_error != OK:
			return {"files": files_removed, "dirs": dirs_removed, "success": false,
				"error": "DirAccess.remove (subdir) %s returned %d" % [sub_path, remove_sub_error]}
		dirs_removed += 1
	return {"files": files_removed, "dirs": dirs_removed, "success": true, "error": ""}
