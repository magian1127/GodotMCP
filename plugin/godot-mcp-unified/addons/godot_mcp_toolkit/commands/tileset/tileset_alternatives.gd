@tool
extends RefCounted
## tileset.* 备选瓦片(alternative tile)命令:管理图集瓦片上的备选变体 —
## 添加/移除变体,并应用其翻转(flip)/ 转置(transpose)/ 调制色(modulate)。
##
## 无状态 — 每个处理器接收自己的参数 Dictionary 并返回响应
## Dictionary。持久化与层节点提示来自共享的
## tileset_io.gd 叶子模块(_IO);调制色的颜色投影来自共享的
## Coerce。瓦片集(tileset)命令组抽取出的子模块,
## 经由 `preload` 别名访问。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const _IO := preload("res://addons/godot_mcp_toolkit/commands/tileset/tileset_io.gd")
const Coerce = Modules.Coerce


# -- 命令 ---------------------------------------------------------------------


static func cmd_add_alternative(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path", "atlas_x", "atlas_y"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var source_id := int(parameters.get("source_id", 0))
	var ts_or_err = _IO.load_tileset(file_path)
	if ts_or_err is Dictionary:
		return ts_or_err
	var ts: TileSet = ts_or_err
	if not ts.has_source(source_id):
		return MCPToolkitError.fail("NOT_FOUND",
			"No source with id %d in TileSet" % source_id)
	var source = ts.get_source(source_id)
	if not (source is TileSetAtlasSource):
		return MCPToolkitError.fail("INVALID_CLASS",
			"Source %d is not a TileSetAtlasSource" % source_id)
	var atlas: TileSetAtlasSource = source
	var coord := Vector2i(int(parameters["atlas_x"]), int(parameters["atlas_y"]))
	if not atlas.has_tile(coord):
		return MCPToolkitError.fail("NOT_FOUND",
			"tile (%d,%d) not found in source %d" % [coord.x, coord.y, source_id])
	var r = _apply_alternative(atlas, coord, parameters)
	if r.has("error"):
		return MCPToolkitError.fail("FAILED", r["error"])
	var alt_id: int = r["alt_id"]
	var save_result := await _IO.save_tileset(ts, file_path)
	if save_result.has("error"):
		return save_result
	return MCPToolkitSuccess.ok({
		"path": file_path,
		"tile": {"atlas_x": coord.x, "atlas_y": coord.y},
		"new_alternative_id": alt_id,
		"hint": "Alternative %d inherits base tile properties. Customize with tileset.edit_* tools." % alt_id,
	})


static func cmd_remove_alternative(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path", "atlas_x", "atlas_y", "alternative_id"])
	if err != null:
		return err
	var file_path := str(parameters.get("file_path", ""))
	var source_id := int(parameters.get("source_id", 0))
	var atlas_x := int(parameters.get("atlas_x", 0))
	var atlas_y := int(parameters.get("atlas_y", 0))
	var alt_id := int(parameters.get("alternative_id", 0))
	var ts_or_err = _IO.load_tileset(file_path)
	if ts_or_err is Dictionary:
		return ts_or_err
	var ts: TileSet = ts_or_err
	if not ts.has_source(source_id):
		return MCPToolkitError.fail("NOT_FOUND",
			"No source with id %d in TileSet" % source_id)
	var source = ts.get_source(source_id)
	if not (source is TileSetAtlasSource):
		return MCPToolkitError.fail("INVALID_CLASS",
			"Source %d is not a TileSetAtlasSource" % source_id)
	var atlas: TileSetAtlasSource = source
	var coord := Vector2i(atlas_x, atlas_y)
	if not atlas.has_tile(coord):
		return MCPToolkitError.fail("NOT_FOUND",
			"tile (%d,%d) not found in source %d" % [atlas_x, atlas_y, source_id])
	if not atlas.has_alternative_tile(coord, alt_id):
		return MCPToolkitError.fail("NOT_FOUND",
			"alternative %d not found for tile (%d,%d)" % [alt_id, atlas_x, atlas_y])
	atlas.remove_alternative_tile(coord, alt_id)
	var save_result := await _IO.save_tileset(ts, file_path)
	if save_result.has("error"):
		return save_result
	return MCPToolkitSuccess.ok({
		"path": file_path,
		"removed_alternative_id": alt_id,
		"tile": {"atlas_x": atlas_x, "atlas_y": atlas_y},
		"hint": _IO.layer_node_hint(
			"", " cells using alternative %d revert to the base tile (alternative 0). Check with tilemap.read_cells." % alt_id),
	})


static func _apply_alternative(
	atlas: TileSetAtlasSource, coord: Vector2i, alt: Dictionary
) -> Dictionary:
	var alt_id := atlas.create_alternative_tile(coord)
	var alt_td: TileData = atlas.get_tile_data(coord, alt_id)
	if alt.has("flip_h"):
		alt_td.flip_h = bool(alt["flip_h"])
	if alt.has("flip_v"):
		alt_td.flip_v = bool(alt["flip_v"])
	if alt.has("transpose"):
		alt_td.transpose = bool(alt["transpose"])
	if alt.has("modulate"):
		var m = alt["modulate"]
		if typeof(m) == TYPE_DICTIONARY:
			alt_td.modulate = Coerce.color_from_dict(m)
	return {"alt_id": alt_id}
