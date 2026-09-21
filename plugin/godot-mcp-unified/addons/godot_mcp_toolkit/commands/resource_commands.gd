@tool
extends RefCounted
## resource.* 命令处理器 — 针对 .tres/.res 文件的加载、写入(创建/更新的 upsert)与删除。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Coerce = Modules.Coerce
const FileGuard = Modules.FileGuard
const Untrusted = Modules.Untrusted
const Helpers = Modules.CommandHelpers
const AssetDependents := preload("res://addons/godot_mcp_toolkit/commands/asset_dependents.gd")

const RESOURCE_SKIP_PROPERTIES: Array[String] = [
	"image", "mesh_arrays", "surface_arrays", "_data",
]


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("resource.load", func(parameters: Dictionary) -> Dictionary:
		return _cmd_resource_load(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	registry.add("resource.write", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_resource_write(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("resource.delete", func(parameters: Dictionary) -> Dictionary:
		return await _cmd_resource_delete(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())


# -- 辅助函数 ------------------------------------------------------------------


static func _property_names_of(object: Object) -> Dictionary:
	var names := {}
	for property in object.get_property_list():
		var property_name := str(property.get("name", ""))
		if not property_name.is_empty():
			names[property_name] = true
	return names


static func _apply_resource_properties(
	resource: Resource, properties: Dictionary, resource_class: String,
) -> Array[String]:
	var warnings: Array[String] = []
	var valid := _property_names_of(resource)
	for key in properties.keys():
		var key_string := str(key)
		var raw_value = properties[key]
		var missing := Coerce.check_resource_paths(raw_value)
		if missing != "":
			warnings.append(
				"property '%s': resource not found at %s; value left unchanged" % [key_string, missing])
			continue
		var coerced = Coerce.coerce_value(raw_value)
		if typeof(coerced) == TYPE_DICTIONARY and (coerced as Dictionary).has("_coerce_error"):
			warnings.append(
				"property '%s': %s; value left unchanged" % [key_string, str(coerced["_coerce_error"])])
			continue
		# 复合路径(如 "sources/0"、"tiles/0:0/0")经由 Object._set() 路由,
		# 许多内置类型(TileSet、AnimationLibrary)用它处理未在
		# get_property_list() 中暴露的子资源槽位。
		if "/" in key_string:
			var before = resource.get(key_string)
			resource.set(key_string, coerced)
			var after = resource.get(key_string)
			if typeof(after) == typeof(before) and after == before:
				if resource_class == "ShaderMaterial" and key_string.begins_with("shader_parameter/"):
					# shader_parameter/<name> 只是检查器(inspector)侧的投影;对它调用
					# Object.set() 不能可靠地路由到 set_shader_parameter,当 uniform 未声明
					# 或未分配着色器时会是空操作。
					warnings.append(
						"property '%s' on ShaderMaterial: set() had no effect. Set shader uniforms via set_shader_parameter(name, value); ensure the ShaderMaterial has a shader assigned and the uniform is declared. The shader_parameter/<name> path is inspector-only." % key_string)
				else:
					warnings.append(
						"property '%s' on %s: set() had no effect (may need a different API)" % [key_string, resource_class])
		elif not valid.has(key_string):
			warnings.append(
				"property '%s' unknown on %s; value ignored" % [key_string, resource_class])
		else:
			resource.set(key_string, coerced)
	return warnings


# -- 命令 ---------------------------------------------------------------------


static func _cmd_resource_load(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if not ResourceLoader.exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "resource not found: %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	var resource := ResourceLoader.load(file_path)
	if resource == null:
		return MCPToolkitError.fail("LOAD_FAILED",
			"ResourceLoader returned null for %s" % file_path)
	var resource_class := resource.get_class()
	var properties := {}
	for property in resource.get_property_list():
		var usage: int = int(property.get("usage", 0))
		if not (usage & PROPERTY_USAGE_EDITOR):
			continue
		var property_name := str(property.get("name", ""))
		if property_name.is_empty() or property_name.begins_with("_"):
			continue
		if property_name in RESOURCE_SKIP_PROPERTIES:
			continue
		properties[property_name] = Coerce.serialize_value(resource.get(property_name))
	var metadata := {}
	if resource is Texture2D:
		metadata["width"] = resource.get_width()
		metadata["height"] = resource.get_height()
	return MCPToolkitSuccess.ok({
		"class": resource_class,
		"path": file_path,
		"properties": Untrusted.wrap(
			"resource", file_path, JSON.stringify(properties)),
		"metadata": metadata,
	})


static func _cmd_resource_write(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var extension := file_path.get_extension().to_lower()
	if not (extension in ["tres", "res"]):
		return MCPToolkitError.fail("INVALID_PATH",
			"resource_write only writes .tres/.res files (got %s); use workspace file editing plus editor_sync for .gd/.cs" % file_path)
	var properties: Dictionary = parameters.get("properties", {}) \
		if typeof(parameters.get("properties", {})) == TYPE_DICTIONARY else {}
	if FileAccess.file_exists(file_path):
		var resource := ResourceLoader.load(file_path)
		if resource == null:
			return MCPToolkitError.fail("NOT_A_RESOURCE",
				"file at %s is not a readable Resource" % file_path)
		var resource_class := resource.get_class()
		var warnings := _apply_resource_properties(resource, properties, resource_class)
		# type 仅在创建时用于选择类;已存在的文件保持自己的类。
		# 会披露传入的 type,避免被解读为静默改型。
		if parameters.has("type"):
			warnings.append("ignored type '%s' — resource already exists; type only applies when creating" % str(parameters.get("type", "")))
		var save_error := ResourceSaver.save(resource, file_path)
		if save_error != OK:
			return MCPToolkitError.fail("SAVE_FAILED",
				"ResourceSaver.save returned %d (path=%s)" % [save_error, file_path])
		# 刷新缓存,以便后续 ResourceRef 加载获得更新后的版本
		ResourceLoader.load(file_path, "", ResourceLoader.CACHE_MODE_REPLACE)
		var update_index := await Helpers.ensure_file_indexed(file_path)
		return MCPToolkitSuccess.ok({
			"path": file_path,
			"resource_class": resource_class,
			"warnings": warnings,
			"indexed": update_index["indexed"],
		})
	var resource_class := str(parameters.get("type", ""))
	if resource_class.is_empty():
		return MCPToolkitError.fail("NOT_FOUND",
			"resource not found at %s; provide 'type' to create it" % file_path, MCPToolkitError.HINT_FILE_PATH)
	var dir_result := Helpers.ensure_parent_dir(file_path, "resource.write")
	if dir_result.has("error"):
		return dir_result
	var dirs_created: bool = dir_result["dirs_created"]
	var rk := Helpers.resolve_class_kind(resource_class)
	var resolved_kind: String = rk["kind"]
	var global_entry: Dictionary = rk["entry"]
	if resolved_kind.is_empty():
		return MCPToolkitError.fail("INVALID_CLASS",
			"unknown class %s; check ClassDB or ProjectSettings global class list" % resource_class, MCPToolkitError.HINT_CLASS_NAME)
	if not Helpers.class_descends_from(resource_class, "Resource"):
		return MCPToolkitError.fail("NOT_A_RESOURCE",
			"%s is not a Resource subclass (base chain: %s)" % [
				resource_class, Helpers.class_base_chain(resource_class)])
	var resource: Resource = null
	if resolved_kind == "native":
		resource = ClassDB.instantiate(resource_class)
	else:
		var script_path := str(global_entry.get("path", ""))
		var script = load(script_path)
		if script == null:
			return MCPToolkitError.fail("INVALID_CLASS",
				"could not load script for %s at %s" % [resource_class, script_path])
		resource = script.new()
	if resource == null:
		return MCPToolkitError.fail("INVALID_CLASS",
			"instantiation returned null for %s" % resource_class)
	var warnings := _apply_resource_properties(resource, properties, resource_class)
	var save_error := ResourceSaver.save(resource, file_path)
	if save_error != OK:
		return MCPToolkitError.fail("SAVE_FAILED",
			"ResourceSaver.save returned %d (path=%s)" % [save_error, file_path])
	# 刷新缓存,以便后续 ResourceRef 加载获得新资源
	ResourceLoader.load(file_path, "", ResourceLoader.CACHE_MODE_REPLACE)
	var create_index := await Helpers.ensure_file_indexed(file_path)
	var create_result := {
		"status": "created",
		"path": file_path,
		"resource_class": resource_class,
		"warnings": warnings,
		"indexed": create_index["indexed"],
	}
	if dirs_created:
		create_result["dirs_created"] = true
	return MCPToolkitSuccess.ok(create_result)


static func _cmd_resource_delete(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	var extension := file_path.get_extension().to_lower()
	if not (extension in ["tres", "res"]):
		return MCPToolkitError.fail("INVALID_PATH",
			"resource deletion only accepts .tres or .res files (got %s); use project_delete with the matching kind for .tscn, .gd, .cs, .gdshader, or .gdshaderinc" % file_path)
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "no file at %s" % file_path, MCPToolkitError.HINT_FILE_PATH)
	# 删除前引用安全检查:被其他资源引用时默认拒绝(force=true 覆盖);
	# 索引不可用时如实降级为警告,不静默放行(与 file.delete 同一纪律)。
	var warnings: Array[String] = []
	if not bool(parameters.get("force", false)):
		var reference_check: Dictionary = AssetDependents.referenced_by(file_path)
		if bool(reference_check.get("blocked", false)):
			var referencers: Array = reference_check.get("referencers", [])
			var preview := ", ".join(PackedStringArray(
				(referencers.slice(0, 10) as Array).map(func(item): return str(item))))
			return MCPToolkitError.fail("REFERENCED",
				"resource %s is referenced by %d other resource(s): %s%s" % [
					file_path, int(reference_check.get("total", 0)), preview,
					"…" if referencers.size() > 10 else ""],
				"delete the references first, or re-run with force=true to delete anyway; "
				+ "asset.get_dependents lists the full set (refresh=true rebuilds the index)")
		if not bool(reference_check.get("checked", false)):
			warnings.append(str(reference_check.get("note", "reference check unavailable")))
	else:
		warnings.append("force=true: reference safety check skipped")
	var delete_result: Dictionary = await Helpers.delete_res_file_and_deindex(file_path)
	if not warnings.is_empty():
		delete_result["warnings"] = warnings
	return delete_result
