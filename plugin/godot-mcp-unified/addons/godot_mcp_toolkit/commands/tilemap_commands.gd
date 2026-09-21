@tool
extends RefCounted
## tilemap.* 命令处理器 — 带撤销重做的批量单元格绘制。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Coerce = Modules.Coerce
const FileGuard = Modules.FileGuard
const Helpers = Modules.CommandHelpers


static func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	registry.add("tilemap.set_cells", func(parameters: Dictionary) -> Dictionary:
		return _cmd_tilemap_set_cells(server, parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("tilemap.read_cells", func(parameters: Dictionary) -> Dictionary:
		return _cmd_tilemap_read_cells(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only())


# -- 辅助函数 ------------------------------------------------------------------


static func _resolve_scene_node(node_path: String) -> Variant:
	return Helpers.resolve_scene_node(node_path)


const _MAX_CELLS := 500


## 针对瓦片地图(tilemap)工具的版本感知提示。
static func _tilemap_version_hint(is_deprecated_tilemap: bool) -> String:
	if is_deprecated_tilemap:
		var ver := Modules.VersionUtils.get_engine_version_pair()
		if Modules.VersionUtils.is_at_least(ver, "4.3"):
			return "Using deprecated TileMap node. Godot 4.3+ provides TileMapLayer — consider upgrading."
		return ""
	return ""


# -- 命令 ---------------------------------------------------------------------


static func _cmd_tilemap_read_cells(parameters: Dictionary) -> Dictionary:
	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	if node_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path")

	var node = _resolve_scene_node(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "no node at %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	var is_layer: bool = node.is_class("TileMapLayer")
	var is_map := node is TileMap
	if not (is_layer or is_map):
		return MCPToolkitError.fail("INVALID_CLASS",
			"node at %s is not a TileMap or TileMapLayer (got %s)" % [
				node_path, node.get_class()])

	var layer := int(parameters.get("layer", 0))
	var region_raw = parameters.get("region", null)
	var source_filter := -1
	if parameters.has("source_id"):
		source_filter = int(parameters["source_id"])

	# 校验已弃用 TileMap 的图层
	if is_map:
		var tile_map := node as TileMap
		var layer_count := tile_map.get_layers_count()
		if layer < 0 or layer >= layer_count:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"layer %d out of range [0, %d) for TileMap %s" % [
					layer, layer_count, node_path])

	# 收集已使用的单元格
	var used_cells: Array
	if is_layer:
		used_cells = node.get_used_cells()
	else:
		used_cells = (node as TileMap).get_used_cells(layer)

	# 根据所有已使用的单元格计算包围矩形
	var bounds_min := Vector2i(0, 0)
	var bounds_max := Vector2i(0, 0)
	if not used_cells.is_empty():
		bounds_min = used_cells[0]
		bounds_max = used_cells[0]
		for cell_coord in used_cells:
			var c: Vector2i = cell_coord
			bounds_min.x = mini(bounds_min.x, c.x)
			bounds_min.y = mini(bounds_min.y, c.y)
			bounds_max.x = maxi(bounds_max.x, c.x)
			bounds_max.y = maxi(bounds_max.y, c.y)
	var bounds := {
		"x": bounds_min.x, "y": bounds_min.y,
		"width": bounds_max.x - bounds_min.x + 1,
		"height": bounds_max.y - bounds_min.y + 1,
	}

	# 按区域过滤
	var region_rect: Rect2i
	var has_region := false
	if region_raw != null and typeof(region_raw) == TYPE_DICTIONARY:
		has_region = true
		region_rect = Rect2i(
			int(region_raw.get("x", 0)), int(region_raw.get("y", 0)),
			int(region_raw.get("width", 1)), int(region_raw.get("height", 1)))

	# 构建单元格数据
	var cells: Array = []
	var cells_total := 0
	var truncated := false
	for cell_coord in used_cells:
		var c: Vector2i = cell_coord
		# 区域过滤
		if has_region and not region_rect.has_point(c):
			continue
		# 读取单元格数据
		var sid: int
		var atlas: Vector2i
		var alt: int
		if is_layer:
			sid = node.get_cell_source_id(c)
			atlas = node.get_cell_atlas_coords(c)
			alt = node.get_cell_alternative_tile(c)
		else:
			var tile_map := node as TileMap
			sid = tile_map.get_cell_source_id(layer, c)
			atlas = tile_map.get_cell_atlas_coords(layer, c)
			alt = tile_map.get_cell_alternative_tile(layer, c)
		# 来源过滤
		if source_filter >= 0 and sid != source_filter:
			continue
		cells_total += 1
		if cells.size() < _MAX_CELLS:
			cells.append({
				"coords": {"x": c.x, "y": c.y},
				"source_id": sid,
				"atlas_coords": {"x": atlas.x, "y": atlas.y},
				"alternative_tile": alt,
			})
		else:
			truncated = true

	# total_cells:完整匹配数(超出上限也会计数);无游标 —— 使用
	# 'region' 查询空间子集。
	var paging_hint := ""
	if truncated:
		paging_hint = (
			"%d of %d cells returned (capped at %d) — " % [cells.size(), cells_total, _MAX_CELLS] +
			"use the 'region' parameter {x, y, width, height} to query spatial subsets (cursor-less).")
	var result := Modules.Pagination.build(
		{"cells": cells}, "cells", cells_total, cells.size(), truncated, "", 0,
		paging_hint, {"bounds": bounds, "node_class": node.get_class()})

	# 版本提示是兼容性说明,不是分页线索 —— 即使在完整读取时也要呈现,
	# 并追加到信封已携带的任何分页提示之后。
	var version_hint := _tilemap_version_hint(is_map)
	if not version_hint.is_empty():
		if result.has("hint"):
			result["hint"] = str(result["hint"]) + " " + version_hint
		else:
			result["hint"] = version_hint

	return MCPToolkitSuccess.ok(result)


## 把区域描述符展开为扁平的单元格数组。
static func _expand_regions_to_cells(regions: Array) -> Array:
	var expanded := []
	for region in regions:
		if typeof(region) != TYPE_DICTIONARY:
			continue
		var rx := int(region.get("x", 0))
		var ry := int(region.get("y", 0))
		var rw := int(region.get("width", 1))
		var rh := int(region.get("height", 1))
		if rw <= 0 or rh <= 0:
			continue
		var sid := int(region.get("source_id", -1))
		var ax := int(region.get("atlas_x", 0))
		var ay := int(region.get("atlas_y", 0))
		var alt := int(region.get("alternative_tile", 0))
		for cy in range(rh):
			for cx in range(rw):
				expanded.append({
					"x": rx + cx, "y": ry + cy,
					"source_id": sid, "atlas_x": ax, "atlas_y": ay,
					"alternative_tile": alt,
				})
	return expanded


static func _cmd_tilemap_set_cells(
	server: Node, parameters: Dictionary,
) -> Dictionary:
	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var layer := int(parameters.get("layer", 0))
	var cells_raw = parameters.get("cells", null)
	var regions_raw = parameters.get("regions", null)
	if node_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path")

	# 把区域展开为单元格。
	if regions_raw != null and typeof(regions_raw) == TYPE_ARRAY:
		var expanded := _expand_regions_to_cells(regions_raw)
		if cells_raw != null and typeof(cells_raw) == TYPE_ARRAY:
			cells_raw = (cells_raw as Array) + expanded
		else:
			cells_raw = expanded

	if typeof(cells_raw) != TYPE_ARRAY:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"cells or regions must be provided. cells: Array of {x,y,source_id,atlas_x,atlas_y,alternative_tile?}. " +
			"regions: Array of {x,y,width,height,source_id,atlas_x,atlas_y,alternative_tile?} for bulk rectangular fills.")
	var cells: Array = cells_raw

	# 逐单元格的参数校验先于一切节点/状态校验——畸形输入必须报告
	# INVALID_PARAMS,而不是被节点缺失(NOT_FOUND)或缺瓦片集
	# (INVALID_STATE)抢先掩盖(冒烟套件断言这一顺序契约)。
	var required_keys := ["x", "y", "source_id", "atlas_x", "atlas_y"]
	for cell_index in range(cells.size()):
		var cell = cells[cell_index]
		if typeof(cell) != TYPE_DICTIONARY:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"cells[%d] must be an object" % cell_index)
		for key in required_keys:
			if not cell.has(key):
				return MCPToolkitError.fail("INVALID_PARAMS",
					"cells[%d] missing required key '%s'" % [cell_index, key])

	var node = _resolve_scene_node(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "no node at %s" % node_path, MCPToolkitError.HINT_NODE_PATH)
	var is_layer: bool = node.is_class("TileMapLayer")  # 动态判断 —— 避免在 < 4.3 上产生解析错误
	var is_map := node is TileMap
	if not (is_layer or is_map):
		return MCPToolkitError.fail("INVALID_CLASS",
			"node at %s is not a TileMap or TileMapLayer (got %s); tilemap.set_cells only accepts tilemap-family nodes" % [
				node_path, node.get_class()])

	# 校验是否已分配瓦片集(tileset)—— 没有瓦片集时放置单元格
	# 会静默地产生不可见的瓦片。
	var has_tileset: bool
	if is_layer:
		has_tileset = node.get("tile_set") != null
	else:
		has_tileset = (node as TileMap).tile_set != null
	if not has_tileset:
		return MCPToolkitError.fail("INVALID_STATE",
			"no tileset assigned to %s — cells would be invisible. " % node_path +
			"Use node_set_property with {\"type\": \"Resource\", \"path\": \"res://path/to/tileset.tres\"} to set tile_set first.")

	if is_map:
		var tile_map := node as TileMap
		var layer_count := tile_map.get_layers_count()
		if layer < 0 or layer >= layer_count:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"layer %d out of range [0, %d) for TileMap %s" % [
					layer, layer_count, node_path])

	var before_state: Array = []
	for cell_index in range(cells.size()):
		var cell: Dictionary = cells[cell_index]
		var coord := Vector2i(int(cell["x"]), int(cell["y"]))
		var previous_source: int
		var previous_atlas: Vector2i
		var previous_alternative: int
		if is_layer:
			previous_source = node.get_cell_source_id(coord)
			previous_atlas = node.get_cell_atlas_coords(coord)
			previous_alternative = node.get_cell_alternative_tile(coord)
		else:
			var tile_map := node as TileMap
			previous_source = tile_map.get_cell_source_id(layer, coord)
			previous_atlas = tile_map.get_cell_atlas_coords(layer, coord)
			previous_alternative = tile_map.get_cell_alternative_tile(layer, coord)
		before_state.append({
			"coord": coord,
			"source_id": previous_source,
			"atlas": previous_atlas,
			"alternative_tile": previous_alternative,
		})

	var cells_written := 0
	var cells_unchanged := 0
	for cell_index in range(cells.size()):
		var cell: Dictionary = cells[cell_index]
		var previous: Dictionary = before_state[cell_index]
		var new_source := int(cell["source_id"])
		var new_atlas := Vector2i(int(cell["atlas_x"]), int(cell["atlas_y"]))
		var new_alternative := int(cell.get("alternative_tile", 0))
		if int(previous["source_id"]) == new_source \
				and (previous["atlas"] as Vector2i) == new_atlas \
				and int(previous["alternative_tile"]) == new_alternative:
			cells_unchanged += 1
		else:
			cells_written += 1

	server.undo_helpers._tilemap_apply_batch(node, layer, cells)
	MCPToolkitUndoRedoAction.begin(
			"tilemap.set_cells %s (%d cells)" % [node_path, cells.size()], node) \
		.do_method(server.undo_helpers._tilemap_apply_batch.bind(node, layer, cells)) \
		.undo_method(server.undo_helpers._tilemap_restore_batch.bind(node, layer, before_state)) \
		.do_reference(node) \
		.commit_recorded()
	var set_result := {
		"node_path": node_path,
		"layer": layer,
		"cells_written": cells_written,
		"cells_unchanged": cells_unchanged,
		"total": cells.size(),
	}
	var version_hint := _tilemap_version_hint(is_map)
	if not version_hint.is_empty():
		set_result["hint"] = version_hint
	return MCPToolkitSuccess.ok(set_result)
