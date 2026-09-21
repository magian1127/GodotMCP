@tool
extends RefCounted
## asset.* 命令处理器 — 列表、get_dependencies、导入二进制资产。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const Helpers = Modules.CommandHelpers
const AssetDependents := preload("res://addons/godot_mcp_toolkit/commands/asset_dependents.gd")

const IMPORT_ALLOWED_EXTENSIONS := [
	# 图像
	"png", "jpg", "jpeg", "webp", "svg", "bmp", "tga", "hdr", "exr",
	# 音频
	"wav", "ogg", "mp3",
	# 3D 模型
	"glb", "gltf", "obj", "fbx", "blend", "dae",
	# 字体
	"ttf", "otf", "woff", "woff2",
	# 翻译
	"po", "csv",
	# 视频
	"ogv",
]
const IMPORT_MAX_FILE_BYTES := 50 * 1024 * 1024
const IMPORT_MAX_BASE64_BYTES := 5 * 1024 * 1024


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("asset.list", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_asset_list(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("asset.get_dependencies", func(parameters: Dictionary) -> Dictionary:
		return _cmd_asset_get_dependencies(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("asset.resolve_uid", func(parameters: Dictionary) -> Dictionary:
		return _cmd_asset_resolve_uid(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("asset.uid_of", func(parameters: Dictionary) -> Dictionary:
		return _cmd_asset_uid_of(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("asset.get_dependents", func(parameters: Dictionary) -> Dictionary:
		return _cmd_asset_get_dependents(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("asset.import", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_asset_import(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


# -- 辅助函数 ------------------------------------------------------------------


static func _walk_filesystem_directory(
	directory: EditorFileSystemDirectory,
	name_glob: String,
	class_filter: String,
	extension_filter: Array[String],
	entries: Array,
	max_count: int,
) -> int:
	# 返回该子树中匹配资产的完整计数(用作 total_assets)。
	# 一旦达到 max_count 就停止向 `entries` 收集,但计数
	# 会继续超过上限 —— 与 spatial_map 的"继续统计总数,停止
	# 收集"一致。遍历的对象是内存中的 EditorFileSystem 缓存。
	var total := 0
	for index in range(directory.get_file_count()):
		var file_path := directory.get_file_path(index)
		var file_name := directory.get_file(index)
		var file_type := directory.get_file_type(index)
		if name_glob != "" and not file_name.matchn(name_glob):
			continue
		if extension_filter.size() > 0 \
				and not file_name.get_extension().to_lower() in extension_filter:
			continue
		if class_filter != "":
			if file_type != class_filter \
					and not ClassDB.is_parent_class(file_type, class_filter):
				continue
		var mtime := FileAccess.get_modified_time(file_path)
		# 幽灵条目过滤:删除之后 EditorFileSystem 可能仍保留陈旧条目
		# (mtime 0 = 文件已从磁盘消失,但索引尚未刷新)。
		if mtime == 0 and not FileAccess.file_exists(file_path):
			continue
		total += 1
		if entries.size() < max_count:
			entries.append({
				"path": file_path,
				"class": file_type,
				"size_bytes": null,
				"modified_unix": mtime,
			})
	for subdir_index in range(directory.get_subdir_count()):
		total += _walk_filesystem_directory(
			directory.get_subdir(subdir_index),
			name_glob, class_filter, extension_filter, entries, max_count)
	return total


# -- 命令 ---------------------------------------------------------------------


static func _cmd_asset_list(parameters: Dictionary) -> Dictionary:
	var path_prefix: String = str(parameters.get("path_prefix", "res://"))
	var name_glob: String = str(parameters.get("name_glob", ""))
	var class_filter: String = str(parameters.get("class_filter", ""))
	var extension_filter: Array = parameters.get("extension_filter", [])
	if typeof(extension_filter) != TYPE_ARRAY:
		extension_filter = []
	var requested_limit: int = int(parameters.get("limit", 500))

	var guard := FileGuard.resolve_safe(path_prefix)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	# 校验区分:非正数的 limit 属于调用方错误(拒绝);超过上限的
	# limit 会被钳制并披露,因此请求"全部"的调用方得到的是
	# 截断后的页加上 limit_clamped,而不是硬性失败。
	if requested_limit < 1:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"limit must be >= 1 (got %d)" % requested_limit)
	var max_results: int = mini(requested_limit, 2000)
	var limit_clamped := requested_limit > 2000
	var filesystem := EditorInterface.get_resource_filesystem()
	if filesystem.is_scanning():
		return MCPToolkitError.fail("FILESYSTEM_NOT_READY",
			"Godot's EditorFileSystem is mid-scan; call editor.wait_for_idle to poll until ready, or retry in 500-2000ms")
	if class_filter != "":
		var found_in_classdb := ClassDB.class_exists(class_filter)
		var found_in_global := false
		if not found_in_classdb:
			for global_class_entry in ProjectSettings.get_global_class_list():
				if global_class_entry.get("class", "") == class_filter:
					found_in_global = true
					break
		if not found_in_classdb and not found_in_global:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"unknown class_filter '%s'; checked ClassDB (engine classes) and ProjectSettings.get_global_class_list() (GDScript class_name / C# [GlobalClass])" % class_filter)

	var normalized_extension_filter: Array[String] = []
	for extension in extension_filter:
		var ext := str(extension).to_lower()
		if ext.begins_with("."):
			ext = ext.substr(1)
		normalized_extension_filter.append(ext)

	var root_directory := filesystem.get_filesystem_path(path_prefix)
	if root_directory == null:
		# 刻意使用完整 scan():update_file() 只作用于文件级,不会
		# 索引目录。当目录在磁盘上存在但尚未被索引时会走到这里 ——
		# 罕见,且 scan() 是正确的工具。
		var abs_path := ProjectSettings.globalize_path(path_prefix)
		if DirAccess.dir_exists_absolute(abs_path):
			filesystem.scan()
			# 至多等待 5 秒让扫描完成(与 editor_sync 的刷新阶段一致)
			var scan_start := Time.get_ticks_msec()
			while filesystem.is_scanning() and Time.get_ticks_msec() - scan_start < 5000:
				await Engine.get_main_loop().create_timer(0.1).timeout
			root_directory = filesystem.get_filesystem_path(path_prefix)
		if root_directory == null:
			return MCPToolkitError.fail("NOT_FOUND",
				"no indexed directory at %s (path may exist on disk but not yet scanned — call editor.refresh or wait for is_scanning to clear)" % path_prefix, MCPToolkitError.HINT_FILE_PATH)

	var entries: Array = []
	var total_assets := _walk_filesystem_directory(
		root_directory, name_glob, class_filter,
		normalized_extension_filter, entries, max_results)
	var has_more := total_assets > entries.size()

	# total_assets:完整匹配数(超出上限仍计数)。无游标 —— 深度优先的
	# 文件系统遍历无法线性续接,因此提示建议收窄过滤器/提高 limit。
	var hint := ""
	if has_more:
		hint = "%d of %d assets returned (capped at limit) — narrow with path_prefix/name_glob/class_filter/extension_filter, or raise limit (<= 2000)." % [entries.size(), total_assets]
	var extras: Dictionary = {
		"path_prefix": path_prefix,
		"filters_applied": {
			"name_glob": name_glob,
			"class_filter": class_filter,
			"extension_filter": normalized_extension_filter,
		},
	}
	if limit_clamped:
		extras["limit_clamped"] = true
		var clamp_clause := "requested limit exceeds max 2000; clamped to 2000 — narrow filters to see the rest"
		hint = clamp_clause if hint.is_empty() else "%s %s" % [hint, clamp_clause]
	return MCPToolkitSuccess.ok(Modules.Pagination.build(
		{"entries": entries}, "assets", total_assets, entries.size(), has_more,
		"", 0, hint, extras))


static func _cmd_asset_get_dependencies(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path: String = str(parameters.get("file_path", ""))
	var include_transitive: bool = bool(parameters.get("include_transitive", false))
	var max_results: int = int(parameters.get("limit", 200))

	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	var filesystem := EditorInterface.get_resource_filesystem()
	if filesystem.is_scanning():
		return MCPToolkitError.fail("FILESYSTEM_NOT_READY",
			"Godot's EditorFileSystem is mid-scan; call editor.wait_for_idle to poll until ready, or retry in 500-2000ms")

	var dependencies: Array = []
	var visited: Dictionary = {}
	var queue: Array[String] = [file_path]
	visited[file_path] = true
	var truncated := false
	var total_dependencies := 0
	var depth := 0
	const MAX_TRANSITIVE_DEPTH := 50

	# 为 total_dependencies 统计每个唯一依赖,但一旦达到上限就停止收集行
	# (超出上限仍计数,仍受 MAX_TRANSITIVE_DEPTH 约束)。先收集的
	# limit 行与之前逐字节相同 —— 只有计数会越过上限继续。
	while queue.size() > 0:
		var current := queue.pop_front() as String
		var raw_dependencies := ResourceLoader.get_dependencies(current)
		for raw_dependency in raw_dependencies:
			var raw_string := String(raw_dependency)
			var parts: PackedStringArray = raw_string.split("::")
			var stripped := parts[0]
			var dependency_class := ""
			if stripped.begins_with("uid://"):
				for part_index in range(1, parts.size()):
					if parts[part_index].begins_with("res://"):
						stripped = parts[part_index]
						break
			for part_index in range(parts.size()):
				var segment := parts[part_index]
				if segment != "" and not segment.begins_with("uid://") \
						and not segment.begins_with("res://"):
					dependency_class = segment
					break
			if stripped.is_empty():
				continue
			if visited.has(stripped):
				continue
			visited[stripped] = true
			total_dependencies += 1
			if dependencies.size() < max_results:
				dependencies.append({
					"path": stripped,
					"raw_path": raw_string,
					"class": dependency_class,
				})
			else:
				truncated = true
			if include_transitive:
				if FileAccess.file_exists(stripped):
					queue.append(stripped)
		depth += 1
		if depth > MAX_TRANSITIVE_DEPTH:
			truncated = true
			break

	var warnings: Array[String] = []
	if depth > MAX_TRANSITIVE_DEPTH:
		warnings.append(
			"transitive walk exceeded 50 levels — truncated to prevent unbounded recursion")

	var hint := ""
	if truncated:
		hint = "%d of %d dependencies returned (capped at limit) — raise limit (no cursor), or set include_transitive=false to reduce the set." % [dependencies.size(), total_dependencies]
	return MCPToolkitSuccess.ok(Modules.Pagination.build(
		{"path": file_path, "dependencies": dependencies},
		"dependencies", total_dependencies, dependencies.size(), truncated,
		"", 0, hint, {"include_transitive": include_transitive, "warnings": warnings}))


static func _cmd_asset_resolve_uid(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["uid"])
	if err != null:
		return err
	var uid_text: String = str(parameters.get("uid", "")).strip_edges()

	# 格式预检:text_to_id() 只接受 uid:// + 小写字母数字,畸形输入
	# 直接给确定性错误,不让 ResourceUID 打引擎错误行。
	if not _is_uid_text(uid_text):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"uid must be a well-formed 'uid://<lowercase alphanumeric>' id (got '%s')" % uid_text)
	var id := ResourceUID.text_to_id(uid_text)
	if id == ResourceUID.INVALID_ID:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"uid '%s' does not decode to a valid resource id" % uid_text)
	if ResourceUID.has_id(id):
		var known_path := ResourceUID.get_id_path(id)
		if known_path != "":
			return MCPToolkitSuccess.ok({"uid": uid_text, "path": known_path})
	# 内存缓存未命中时 uid_to_path() 仍可能解析(它会触发引擎的 UID
	# 启动扫描),代价是真正未知的 UID 会打一行引擎错误 —— 与
	# has_id 分支一样返回确定性结果。
	var fallback_path := ResourceUID.uid_to_path(uid_text)
	if fallback_path == "" or fallback_path == uid_text:
		return MCPToolkitError.fail("NOT_FOUND",
			"no resource is registered for uid '%s'" % uid_text,
			"the uid may be stale (file deleted/moved and not rescanned) — editor.refresh then retry, or query the path with asset.list")
	return MCPToolkitSuccess.ok({"uid": uid_text, "path": fallback_path})


static func _cmd_asset_uid_of(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path: String = str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if not FileAccess.file_exists(file_path) and not ResourceLoader.exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND",
			"no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	# 无 UID 的文件(非资源或尚未被导入索引)返回确定性 NOT_FOUND,
	# 语义与 GDEditorBridge /v1/uid 对齐:不静默回退到空串。
	var id := ResourceLoader.get_resource_uid(file_path)
	if id == ResourceUID.INVALID_ID:
		return MCPToolkitError.fail("NOT_FOUND",
			"file %s has no uid (not a resource, or not yet in the uid cache — editor.refresh may index it)" % file_path)
	return MCPToolkitSuccess.ok({"path": file_path, "uid": ResourceUID.id_to_text(id)})


## uid:// 文本格式校验:text_to_id() 只接受小写字母数字字符,
## 预检避免畸形输入触发引擎层错误输出。
static func _is_uid_text(text: String) -> bool:
	if not text.begins_with("uid://") or text == "uid://<invalid>":
		return false
	var body := text.substr(6)
	if body.is_empty():
		return false
	for i in body.length():
		var c := body[i]
		if c >= "0" and c <= "9":
			continue
		if c >= "a" and c <= "z":
			continue
		return false
	return true


## asset.get_dependents — 反向依赖查询("谁引用我")。直接引用走倒排索引一次
## 命中;include_transitive 时反向 BFS(引用者的引用者),防环访问集。
static func _cmd_asset_get_dependents(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path: String = str(parameters.get("file_path", ""))
	var include_transitive: bool = bool(parameters.get("include_transitive", false))
	var max_results: int = int(parameters.get("limit", 200))
	var refresh: bool = bool(parameters.get("refresh", false))
	if max_results < 1:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"limit must be >= 1 (got %d)" % max_results)
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))

	var ensured: Dictionary = AssetDependents.ensure_index(refresh)
	if not ensured.get("ok", false):
		return MCPToolkitError.fail("FILESYSTEM_NOT_READY",
			"reverse-dependency index unavailable (%s); call editor.wait_for_idle to poll until ready, or retry" % str(ensured.get("reason", "unknown")))
	var index: Dictionary = ensured["index"]

	var dependents: Array = []
	var total := 0
	var truncated := false
	if include_transitive:
		# 反向 BFS:从根出发,每一层的引用者成为下一层根;visited 防环并
		# 去重(资源图允许菱形引用,同一引用者可经多条路径到达,只计一次)。
		var visited: Dictionary = {file_path: true}
		var frontier: Array[String] = [file_path]
		var depth := 0
		const MAX_TRANSITIVE_DEPTH := 50
		while frontier.size() > 0:
			var next_frontier: Array[String] = []
			for current in frontier:
				for dependent in (index.get(current, []) as Array):
					if visited.has(dependent):
						continue
					visited[dependent] = true
					next_frontier.append(dependent)
					if dependents.size() < max_results:
						dependents.append(dependent)
					else:
						truncated = true
			frontier = next_frontier
			depth += 1
			if depth > MAX_TRANSITIVE_DEPTH:
				truncated = true
				break
		total = visited.size() - 1
	else:
		for dependent in (index.get(file_path, []) as Array):
			total += 1
			if dependents.size() < max_results:
				dependents.append(dependent)
			else:
				truncated = true

	var hint := ""
	if truncated:
		hint = "%d of %d dependents returned (capped at limit) — raise limit (no cursor), or set include_transitive=false to reduce the set." % [dependents.size(), total]
	return MCPToolkitSuccess.ok(Modules.Pagination.build(
		{"path": file_path, "dependents": dependents}, "dependents", total, dependents.size(), truncated,
		"", 0, hint, {
			"include_transitive": include_transitive,
			"index_built_at": ensured.get("built_at_unix", 0.0),
			"index_age_ms": ensured.get("age_ms", -1),
			"index_cached": ensured.get("cached", false),
		}))


static func _cmd_asset_import(parameters: Dictionary) -> Dictionary:
	var source_path: String = str(parameters.get("source_path", ""))
	var base64_data: String = str(parameters.get("base64_data", ""))
	var dest_path: String = str(parameters.get("dest_path", ""))
	var if_exists: String = str(parameters.get("if_exists", "return"))
	var wait_for_scan_ms: int = int(parameters.get("wait_for_scan_ms", 5000))

	# 扩展名允许列表 —— 丰富的 asset.import 提示保留在这里;共享辅助
	# 函数会为其他调用方做通用复查。
	var extension := dest_path.get_extension().to_lower()
	if extension not in IMPORT_ALLOWED_EXTENSIONS:
		return MCPToolkitError.fail("INVALID_PATH",
			"extension '%s' not in import allowlist: %s; use workspace file editing plus editor_sync for .gd/.cs, resource_write for .tres/.res, or scene_create for .tscn" % [
				extension, ", ".join(PackedStringArray(IMPORT_ALLOWED_EXTENSIONS))])
	var has_source := source_path != ""
	var has_base64 := base64_data != ""
	if has_source and has_base64:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"provide exactly one of source_path or base64_data, not both")
	if not has_source and not has_base64:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"provide source_path (absolute filesystem path) or base64_data (base64-encoded file content)")

	# 源路径模式的防护
	if has_source:
		if source_path.begins_with("res://") or source_path.begins_with("user://"):
			source_path = ProjectSettings.globalize_path(source_path)
		if not FileAccess.file_exists(source_path):
			return MCPToolkitError.fail("NOT_FOUND",
				"source file not found: %s" % source_path, MCPToolkitError.HINT_FILE_PATH)
		var source_file := FileAccess.open(source_path, FileAccess.READ)
		if source_file == null:
			return MCPToolkitError.fail("READ_FAILED",
				"cannot read source file %s (err %d)" % [
					source_path, FileAccess.get_open_error()])
		var source_size := source_file.get_length()
		source_file.close()
		if source_size > IMPORT_MAX_FILE_BYTES:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"source file %d bytes exceeds 50 MB limit" % source_size)

	var decoded_bytes := PackedByteArray()
	if has_base64:
		decoded_bytes = Marshalls.base64_to_raw(base64_data)
		if decoded_bytes.is_empty() and base64_data.length() > 0:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"base64_data is not valid base64")
		if decoded_bytes.size() > IMPORT_MAX_BASE64_BYTES:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"decoded base64 data %d bytes exceeds 5 MB limit" % decoded_bytes.size())

	var bytes_to_write: PackedByteArray
	var source_label: String
	if has_source:
		bytes_to_write = FileAccess.get_file_as_bytes(source_path)
		if FileAccess.get_open_error() != OK:
			return MCPToolkitError.fail("READ_FAILED",
				"cannot read source file %s (err %d)" % [
					source_path, FileAccess.get_open_error()])
		source_label = "filesystem"
	else:
		bytes_to_write = decoded_bytes
		source_label = "base64"

	# 共享的写入 + 导入收尾包裹。该辅助函数校验 if_exists /
	# wait_for_scan_ms,把守路径,处理 if_exists / 父目录,运行下面的
	# write_fn,然后完成导入收尾。只有原始字节写入是本命令特有的。
	var write_fn := func(write_path: String) -> Dictionary:
		var file_handle := FileAccess.open(write_path, FileAccess.WRITE)
		if file_handle == null:
			return MCPToolkitError.fail("WRITE_FAILED",
				"cannot open %s for writing (err %d)" % [
					write_path, FileAccess.get_open_error()])
		file_handle.store_buffer(bytes_to_write)
		file_handle.close()
		return {}

	var result := await Helpers.write_asset_with_settle(
		dest_path, PackedStringArray(IMPORT_ALLOWED_EXTENSIONS),
		if_exists, wait_for_scan_ms, "asset.import", write_fn)
	if not result.get("success", false):
		return result

	# asset.import 专有的负载字段。
	if result.get("status") == "returned":
		result["source"] = null
	else:
		result["source"] = source_label
		result["size_bytes"] = bytes_to_write.size()
	return result
