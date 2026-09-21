@tool
extends RefCounted
## navigation.* 命令处理器 — NavigationRegion2D 多边形编辑 + 烘焙。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Helpers = Modules.CommandHelpers


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("navigation.edit_polygon", func(parameters: Dictionary) -> Dictionary:
		return _cmd_edit_polygon(parameters)
	, MCPToolkitCommandOptions.new())


static func _cmd_edit_polygon(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["node_path", "action"])
	if err != null:
		return err

	var node_path := str(parameters["node_path"])
	var action := str(parameters["action"])

	var edited_scene := EditorInterface.get_edited_scene_root()
	if edited_scene == null:
		return MCPToolkitError.fail("NO_SCENE", "No scene is currently open in the editor")

	node_path = Helpers.normalize_editor_path(node_path)
	var node := edited_scene.get_node_or_null(NodePath(node_path))
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "Node not found: " + node_path)

	if not (node is NavigationRegion2D):
		return MCPToolkitError.fail("INVALID_CLASS",
			"Expected NavigationRegion2D, got " + node.get_class())

	var region := node as NavigationRegion2D

	# 确保该区域拥有 NavigationPolygon 资源
	var nav_poly := region.navigation_polygon
	if nav_poly == null:
		nav_poly = NavigationPolygon.new()
		region.navigation_polygon = nav_poly

	match action:
		"set":
			return _action_set(region, parameters, nav_poly)
		"add_outline":
			return _action_add_outline(region, parameters, nav_poly)
		"remove_outline":
			return _action_remove_outline(region, parameters, nav_poly)
		"clear":
			return _action_clear(region, nav_poly)
		"bake":
			return _action_bake(region, nav_poly)
		_:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"Unknown action '%s'; valid actions: set, add_outline, remove_outline, clear, bake" % action)


static func _action_set(region: NavigationRegion2D, parameters: Dictionary, nav_poly: NavigationPolygon) -> Dictionary:
	var outlines = parameters.get("outlines", null)
	if outlines == null or typeof(outlines) != TYPE_ARRAY or outlines.size() == 0:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"'outlines' must be a non-empty Array of outline arrays")

	# 捕获旧状态用于撤销。
	var old_poly := nav_poly.duplicate() as NavigationPolygon
	nav_poly.clear_outlines()
	for outline_data in outlines:
		if typeof(outline_data) != TYPE_ARRAY:
			continue
		var packed := PackedVector2Array()
		for pt in outline_data:
			if typeof(pt) == TYPE_DICTIONARY:
				packed.append(Vector2(float(pt.get("x", 0)), float(pt.get("y", 0))))
		if packed.size() >= 3:
			nav_poly.add_outline(packed)

	_wrap_undo_redo(region, nav_poly, old_poly, "navigation.edit_polygon set")
	return _make_result(nav_poly)


static func _action_add_outline(region: NavigationRegion2D, parameters: Dictionary, nav_poly: NavigationPolygon) -> Dictionary:
	var outline_data = parameters.get("outline", null)
	if outline_data == null or typeof(outline_data) != TYPE_ARRAY:
		return MCPToolkitError.fail("INVALID_PARAMS", "'outline' must be an Array of {x, y} points")

	var packed := PackedVector2Array()
	for pt in outline_data:
		if typeof(pt) == TYPE_DICTIONARY:
			packed.append(Vector2(float(pt.get("x", 0)), float(pt.get("y", 0))))

	if packed.size() < 3:
		return MCPToolkitError.fail("INVALID_PARAMS", "Outline must have at least 3 points")

	var old_poly := nav_poly.duplicate() as NavigationPolygon
	nav_poly.add_outline(packed)

	_wrap_undo_redo(region, nav_poly, old_poly, "navigation.edit_polygon add_outline")
	return _make_result(nav_poly)


static func _action_remove_outline(region: NavigationRegion2D, parameters: Dictionary, nav_poly: NavigationPolygon) -> Dictionary:
	var index = parameters.get("index", null)
	if index == null:
		return MCPToolkitError.fail("INVALID_PARAMS", "'index' is required for remove_outline")

	var idx := int(index)
	if idx < 0 or idx >= nav_poly.get_outline_count():
		return MCPToolkitError.fail("INVALID_PARAMS",
			"Outline index %d out of range (0..%d)" % [idx, nav_poly.get_outline_count() - 1])

	var old_poly := nav_poly.duplicate() as NavigationPolygon
	nav_poly.remove_outline(idx)

	_wrap_undo_redo(region, nav_poly, old_poly, "navigation.edit_polygon remove_outline")
	return _make_result(nav_poly)


static func _action_clear(region: NavigationRegion2D, nav_poly: NavigationPolygon) -> Dictionary:
	var old_poly := nav_poly.duplicate() as NavigationPolygon
	nav_poly.clear_outlines()
	nav_poly.clear_polygons()

	_wrap_undo_redo(region, nav_poly, old_poly, "navigation.edit_polygon clear")
	return MCPToolkitSuccess.ok({"outline_count": 0, "vertex_count": 0})


static func _action_bake(region: NavigationRegion2D, nav_poly: NavigationPolygon) -> Dictionary:
	if nav_poly.get_outline_count() == 0:
		return MCPToolkitError.fail("INVALID_PARAMS", "Cannot bake: no outlines defined")

	# NavigationServer2D 烘焙(parse/bake_from_source_geometry_data)在所有
	# 受支持版本(4.2-4.7)上都存在;has_method() 作为防御性保护。
	if NavigationServer2D.has_method("bake_from_source_geometry_data"):
		var source := NavigationMeshSourceGeometryData2D.new()
		NavigationServer2D.parse_source_geometry_data(nav_poly, source, region)
		NavigationServer2D.bake_from_source_geometry_data(nav_poly, source)
	else:
		# 已弃用的回退 — 在 4.2-4.7 上不可达(bake API 自 4.2 起就存在)
		nav_poly.make_polygons_from_outlines()

	var vertex_count := 0
	for i in nav_poly.get_polygon_count():
		vertex_count += nav_poly.get_polygon(i).size()

	return MCPToolkitSuccess.ok({
		"outline_count": nav_poly.get_outline_count(),
		"polygon_count": nav_poly.get_polygon_count(),
		"vertex_count": vertex_count,
	})


# -- 撤销/重做 ----------------------------------------------------------------


# 把已应用的多边形修改注册到编辑器的 UndoRedo(与 path2d.edit_curve 相同的
# 节点托管资源模式):重做时重新赋值修改后的多边形,撤销时恢复修改前的
# 副本;两个资源都被引用固定,历史记录条目存在期间
# 两者都不会被回收。bake 刻意不做包装 ——
# 烘焙出的多边形只是轮廓的派生结果。
static func _wrap_undo_redo(region: NavigationRegion2D, nav_poly: NavigationPolygon,
		old_poly: NavigationPolygon, action_name: String) -> void:
	MCPToolkitUndoRedoAction.begin(action_name, region) \
		.do_property(region, &"navigation_polygon", nav_poly) \
		.undo_property(region, &"navigation_polygon", old_poly) \
		.do_reference(nav_poly) \
		.undo_reference(old_poly) \
		.commit_recorded()


static func _make_result(nav_poly: NavigationPolygon) -> Dictionary:
	var total_vertices := 0
	for i in nav_poly.get_outline_count():
		total_vertices += nav_poly.get_outline(i).size()
	return MCPToolkitSuccess.ok({
		"outline_count": nav_poly.get_outline_count(),
		"vertex_count": total_vertices,
	})
