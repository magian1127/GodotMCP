@tool
extends RefCounted
## tileset.edit_* 每瓦片数据命令:应用一组按动词划定范围的每瓦片
## TileData 编辑(物理 / 地形 / 导航 / 遮挡+动画 / 自定义
## 数据),拒绝任何属于其他编辑动词的键。
##
## 一个参数化操作 — cmd_edit(parameters, verb) — 配一张按动词索引的
## 策略表。每瓦片编辑策略(哪些键对哪个动词合法,即
## _EDIT_* 表 + _foreign_key_error 校验)与其机制(每个键如何
## 修改 TileData,即 _apply_* 流程 + 几何构建器)不可分割,因此
## 它们一并放在这里。持久化、层节点提示与全瓦片
## 多边形来自共享的 tileset_io.gd 叶子模块(_IO);
## 由 tileset_commands.gd 经 `preload` 别名消费。

const _IO := preload("res://addons/godot_mcp_toolkit/commands/tileset/tileset_io.gd")


# -- 按动词的键强制校验 -----------------------------------------------------------
#
# 每个 tileset.edit_* 工具都恰好负责一个瓦片数据关注点。五个工具
# 共享 cmd_edit,因此若不加校验,本属于某个动词的键(如
# terrain_set)会在另一个动词(如 edit_physics)下被静默应用。这些表
# 让动词拥有裁决权:一个动词只接受自己的每瓦片键加上
# 通用的坐标选择器,任何外来键都会被拒绝,并附带指出
# 真正拥有该键的工具的提示(经 _EDIT_KEY_OWNER 反查)。

## 每个动词定位一个瓦片所需的坐标选择器。
const _EDIT_COORD_KEYS := ["atlas_x", "atlas_y"]

## 各动词允许读取/应用的每瓦片键。这些键集合相互
## 独占 — 一个键恰好属于一个动词 — 这正是单次
## 反查就能在拒绝提示中指明归属工具的原因。
const _EDIT_KEY_SETS := {
	"physics": ["physics_polygon", "physics_layer", "one_way_collision"],
	"terrain": ["terrain_set", "terrain", "terrain_peering"],
	"navigation": ["navigation_polygon", "navigation_layer"],
	"visuals": ["occlusion_polygon", "occlusion_layer", "animation", "probability"],
	"custom_data": ["custom_data"],
}

## 反向映射:每瓦片键 -> 拥有它的工具,用于拒绝提示。
const _EDIT_KEY_OWNER := {
	"physics_polygon": "tileset.edit_physics",
	"physics_layer": "tileset.edit_physics",
	"one_way_collision": "tileset.edit_physics",
	"terrain_set": "tileset.edit_terrain",
	"terrain": "tileset.edit_terrain",
	"terrain_peering": "tileset.edit_terrain",
	"navigation_polygon": "tileset.edit_navigation",
	"navigation_layer": "tileset.edit_navigation",
	"occlusion_polygon": "tileset.edit_visuals",
	"occlusion_layer": "tileset.edit_visuals",
	"animation": "tileset.edit_visuals",
	"probability": "tileset.edit_visuals",
	"custom_data": "tileset.edit_custom_data",
}


## 拒绝第一个不属于 `verb` 的每瓦片键。所有键都合法时返回 "",
## 否则返回一条 INVALID_PARAMS 消息,指明拥有该违规键的
## 工具。纯函数(无引擎状态),便于单元测试。
##
## 两种拒绝都点名本动词接受的键集合:tiles schema 的 items 是自由
## 对象(additionalProperties),正确键名无法从 schema 自省,因此
## 错误消息是调用方学习键名的唯一入口(实测:缺该提示时只能靠猜,
## 例如 physics 分面的键其实是 physics_polygon/physics_layer/
## one_way_collision,猜 polygon/shape/full 都会被拒)。
static func _foreign_key_error(verb: String, tile: Dictionary) -> String:
	var allowed: Array = _EDIT_KEY_SETS.get(verb, [])
	var allowed_list := ", ".join(PackedStringArray(allowed))
	for key in tile:
		var key_str := str(key)
		if key_str in _EDIT_COORD_KEYS or key_str in allowed:
			continue
		if _EDIT_KEY_OWNER.has(key_str):
			return "key '%s' belongs to %s, not tileset.edit_%s (tileset.edit_%s accepts: %s)" % [
				key_str, _EDIT_KEY_OWNER[key_str], verb, verb, allowed_list]
		return "unknown key '%s' for tileset.edit_%s (this verb accepts: %s)" % [
			key_str, verb, allowed_list]
	return ""


static func cmd_edit(parameters: Dictionary, verb: String) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["file_path"])
	if err != null:
		return err

	var file_path := str(parameters.get("file_path", ""))
	var source_id := int(parameters.get("source_id", 0))
	var tiles_raw = parameters.get("tiles", null)

	var ts_or_err = _IO.load_tileset(file_path)
	if ts_or_err is Dictionary:
		return ts_or_err
	var ts: TileSet = ts_or_err

	var tile_errors: Array = []
	var tiles_modified := 0

	# -- 每瓦片编辑 --
	if tiles_raw != null and typeof(tiles_raw) == TYPE_ARRAY:
		if not ts.has_source(source_id):
			return MCPToolkitError.fail("NOT_FOUND",
				"No source with id %d in TileSet" % source_id)
		var source = ts.get_source(source_id)
		if not (source is TileSetAtlasSource):
			return MCPToolkitError.fail("INVALID_CLASS",
				"Source %d is not a TileSetAtlasSource" % source_id)
		var atlas: TileSetAtlasSource = source
		var tile_size: Vector2i = ts.tile_size

		for i in range(tiles_raw.size()):
			var tile = tiles_raw[i]
			if typeof(tile) != TYPE_DICTIONARY:
				tile_errors.append("tiles[%d]: not a dictionary" % i)
				continue
			if not tile.has("atlas_x") or not tile.has("atlas_y"):
				tile_errors.append("tiles[%d]: missing atlas_x/atlas_y" % i)
				continue
			# 强制执行动词:属于其他工具的键是调用者的错误,因此
			# 拒绝整个调用(不要静默应用错误的关注点)。
			var foreign := _foreign_key_error(verb, tile)
			if foreign != "":
				return MCPToolkitError.fail("INVALID_PARAMS",
					"tiles[%d]: %s" % [i, foreign])
			var coord := Vector2i(int(tile["atlas_x"]), int(tile["atlas_y"]))
			if not atlas.has_tile(coord):
				tile_errors.append("tiles[%d]: tile (%d,%d) not found in source %d" % [
					i, coord.x, coord.y, source_id])
				continue
			var td: TileData = atlas.get_tile_data(coord, 0)

			# 只有至少应用了一个真正的编辑键时才统计该瓦片 — 仅携带
			# 坐标选择器的瓦片什么都不会应用,因此不得
			# 计入(与拆分前的每瓦片 `modified` 标志一致)。
			var has_real_edit := false
			for k in tile:
				if not str(k) in _EDIT_COORD_KEYS:
					has_real_edit = true
					break
			var feature_errors: Array = _apply_verb(verb, atlas, coord, td, tile, tile_size)
			if feature_errors.is_empty():
				if has_real_edit:
					tiles_modified += 1
			else:
				for fe in feature_errors:
					tile_errors.append("tiles[%d]: %s" % [i, str(fe)])

	# -- 保存 --
	var save_result := await _IO.save_tileset(ts, file_path)
	if save_result.has("error"):
		return save_result

	var edit_result := {
		"path": file_path,
		"tiles_modified": tiles_modified,
		"errors": tile_errors,
		"hint": _IO.layer_node_hint(
			"Edit more tile properties with tileset.edit_*, or paint tiles onto a ",
			" with tilemap.set_cells."),
	}
	# 逐瓦片错误是软失败:整个调用仍算成功(其余瓦片已应用并落盘),但
	# errors 非空必须在顶层显式可见 —— 只看 success 的调用方会误判为全部成功。
	# 与 editor_helpers.summarize_batch 的 failed/hint 约定同形。
	if tile_errors.size() > 0:
		# tile_errors 非空只可能来自上面的数组分支,但总数按实际请求条数取,
		# 非数组形态(理论上不可达)退化为错误条数,避免对 null 取 size()。
		var total_tiles := (tiles_raw as Array).size() if typeof(tiles_raw) == TYPE_ARRAY else tile_errors.size()
		edit_result["failed"] = tile_errors.size()
		edit_result["hint"] = ("%d of %d tiles reported errors — inspect errors[] for per-tile detail. " % [
			tile_errors.size(), total_tiles]) + str(edit_result["hint"])
	return MCPToolkitSuccess.ok(edit_result)


# -- 以意图命名的各动词流程 -------------------------------------------------------
#
# 每个工具一条流程,各自只把属于自己关注点的键应用到单个瓦片上。
# 每条流程返回其累积的软性每瓦片错误([] = 全部应用成功);
# 调用者把这些错误按原样并入每瓦片的 errors[] 数组。


## 把单个瓦片路由到与 `verb` 匹配的流程。键已通过动词校验。
static func _apply_verb(
	verb: String, atlas: TileSetAtlasSource, coord: Vector2i,
	td: TileData, tile: Dictionary, tile_size: Vector2i
) -> Array:
	match verb:
		"physics":
			return _apply_physics(td, tile, tile_size)
		"terrain":
			return _apply_terrain(td, tile)
		"navigation":
			return _apply_navigation(td, tile, tile_size)
		"visuals":
			return _apply_visuals(atlas, coord, td, tile, tile_size)
		"custom_data":
			return _apply_custom_data(td, tile)
	return ["unknown tileset edit verb: %s" % verb]


## 碰撞多边形 + 单向标志(tileset.edit_physics)。
static func _apply_physics(td: TileData, tile: Dictionary, tile_size: Vector2i) -> Array:
	var errors: Array = []
	if tile.has("physics_polygon"):
		var r := _apply_physics_polygon(td, tile, tile_size)
		if not r.is_empty():
			errors.append(r)
	return errors


## 地形集、地形索引与邻接位(peering bits)(tileset.edit_terrain)。
static func _apply_terrain(td: TileData, tile: Dictionary) -> Array:
	var errors: Array = []
	if tile.has("terrain_set"):
		td.terrain_set = int(tile["terrain_set"])
	if tile.has("terrain"):
		td.terrain = int(tile["terrain"])
	if tile.has("terrain_peering") and typeof(tile["terrain_peering"]) == TYPE_DICTIONARY:
		var r := _apply_terrain_peering(td, tile["terrain_peering"])
		if not r.is_empty():
			errors.append(r)
	return errors


## 导航多边形(tileset.edit_navigation)。
static func _apply_navigation(td: TileData, tile: Dictionary, tile_size: Vector2i) -> Array:
	var errors: Array = []
	if tile.has("navigation_polygon"):
		var r := _apply_navigation_polygon(td, tile, tile_size)
		if not r.is_empty():
			errors.append(r)
	return errors


## 遮挡、动画与概率(tileset.edit_visuals)。该工具
## 有意把一个瓦片的三项外观关注点打包在一起。
static func _apply_visuals(
	atlas: TileSetAtlasSource, coord: Vector2i,
	td: TileData, tile: Dictionary, tile_size: Vector2i
) -> Array:
	var errors: Array = []
	if tile.has("occlusion_polygon"):
		var r := _apply_occlusion_polygon(td, tile, tile_size)
		if not r.is_empty():
			errors.append(r)
	if tile.has("animation") and typeof(tile["animation"]) == TYPE_DICTIONARY:
		var r := _apply_animation(atlas, coord, tile["animation"])
		if not r.is_empty():
			errors.append(r)
	if tile.has("probability"):
		td.probability = float(tile["probability"])
	return errors


## 自定义数据层取值(tileset.edit_custom_data)。
static func _apply_custom_data(td: TileData, tile: Dictionary) -> Array:
	if tile.has("custom_data") and typeof(tile["custom_data"]) == TYPE_DICTIONARY:
		var cd: Dictionary = tile["custom_data"]
		for layer_name in cd:
			td.set_custom_data(str(layer_name), cd[layer_name])
	return []


static func _ensure_collision_polygon(td: TileData, physics_layer: int) -> void:
	if td.get_collision_polygons_count(physics_layer) == 0:
		td.add_collision_polygon(physics_layer)


static func _apply_physics_polygon(
	td: TileData, tile: Dictionary, tile_size: Vector2i
) -> String:
	var val = tile["physics_polygon"]
	var physics_layer := int(tile.get("physics_layer", 0))
	if typeof(val) == TYPE_STRING:
		match val:
			"full":
				_ensure_collision_polygon(td, physics_layer)
				td.set_collision_polygon_points(physics_layer, 0,
					_IO.build_full_tile_polygon(tile_size))
			"none":
				while td.get_collision_polygons_count(physics_layer) > 0:
					td.remove_collision_polygon(physics_layer, 0)
			"one_way":
				_ensure_collision_polygon(td, physics_layer)
				td.set_collision_polygon_points(physics_layer, 0,
					_IO.build_full_tile_polygon(tile_size))
				td.set_collision_polygon_one_way(physics_layer, 0, true)
			_:
				return "unknown physics_polygon shorthand: %s" % val
	elif typeof(val) == TYPE_ARRAY:
		_ensure_collision_polygon(td, physics_layer)
		var points := PackedVector2Array()
		for pt in val:
			if typeof(pt) == TYPE_DICTIONARY:
				points.append(Vector2(float(pt.get("x", 0)), float(pt.get("y", 0))))
		td.set_collision_polygon_points(physics_layer, 0, points)
	else:
		return "physics_polygon must be string or Array[{x,y}]"
	if tile.has("one_way_collision"):
		_ensure_collision_polygon(td, physics_layer)
		td.set_collision_polygon_one_way(physics_layer, 0, bool(tile["one_way_collision"]))
	return ""


const _PEERING_MAP := {
	"right": TileSet.CELL_NEIGHBOR_RIGHT_SIDE,
	"bottom_right": TileSet.CELL_NEIGHBOR_BOTTOM_RIGHT_CORNER,
	"bottom": TileSet.CELL_NEIGHBOR_BOTTOM_SIDE,
	"bottom_left": TileSet.CELL_NEIGHBOR_BOTTOM_LEFT_CORNER,
	"left": TileSet.CELL_NEIGHBOR_LEFT_SIDE,
	"top_left": TileSet.CELL_NEIGHBOR_TOP_LEFT_CORNER,
	"top": TileSet.CELL_NEIGHBOR_TOP_SIDE,
	"top_right": TileSet.CELL_NEIGHBOR_TOP_RIGHT_CORNER,
}


static func _apply_terrain_peering(td: TileData, peering: Dictionary) -> String:
	for key in peering:
		if key == "center":
			td.terrain = int(peering[key])
			continue
		if not _PEERING_MAP.has(key):
			return "unknown peering bit: %s" % key
		td.set_terrain_peering_bit(_PEERING_MAP[key], int(peering[key]))
	return ""


static func _build_nav_polygon(verts: PackedVector2Array) -> NavigationPolygon:
	var np := NavigationPolygon.new()
	np.set_vertices(verts)
	var indices := PackedInt32Array()
	for i in range(verts.size()):
		indices.append(i)
	np.add_polygon(indices)
	return np


static func _apply_navigation_polygon(
	td: TileData, tile: Dictionary, tile_size: Vector2i
) -> String:
	var val = tile["navigation_polygon"]
	var nav_layer := int(tile.get("navigation_layer", 0))
	if typeof(val) == TYPE_STRING:
		match val:
			"full":
				var verts := _IO.build_full_tile_polygon(tile_size)
				td.set_navigation_polygon(nav_layer, _build_nav_polygon(verts))
			"none":
				td.set_navigation_polygon(nav_layer, null)
			_:
				return "unknown navigation_polygon shorthand: %s" % val
	elif typeof(val) == TYPE_ARRAY:
		var verts := PackedVector2Array()
		for pt in val:
			if typeof(pt) == TYPE_DICTIONARY:
				verts.append(Vector2(float(pt.get("x", 0)), float(pt.get("y", 0))))
		td.set_navigation_polygon(nav_layer, _build_nav_polygon(verts))
	else:
		return "navigation_polygon must be string or Array[{x,y}]"
	return ""


static func _apply_occlusion_polygon(
	td: TileData, tile: Dictionary, tile_size: Vector2i
) -> String:
	var val = tile["occlusion_polygon"]
	var occ_layer := int(tile.get("occlusion_layer", 0))
	if typeof(val) == TYPE_STRING:
		match val:
			"full":
				var op := OccluderPolygon2D.new()
				op.polygon = _IO.build_full_tile_polygon(tile_size)
				td.set_occluder(occ_layer, op)
			"none":
				td.set_occluder(occ_layer, null)
			_:
				return "unknown occlusion_polygon shorthand: %s" % val
	elif typeof(val) == TYPE_ARRAY:
		var op := OccluderPolygon2D.new()
		var verts := PackedVector2Array()
		for pt in val:
			if typeof(pt) == TYPE_DICTIONARY:
				verts.append(Vector2(float(pt.get("x", 0)), float(pt.get("y", 0))))
		op.polygon = verts
		td.set_occluder(occ_layer, op)
	else:
		return "occlusion_polygon must be string or Array[{x,y}]"
	return ""


static func _apply_animation(
	atlas: TileSetAtlasSource, coord: Vector2i, anim: Dictionary
) -> String:
	var frame_count := int(anim.get("frame_count", anim.get("frames", []).size()))
	if frame_count < 2:
		return "animation needs frame_count >= 2"
	var columns := int(anim.get("columns", frame_count))
	var duration := float(anim.get("frame_duration", 1.0))
	# 移除动画区域将覆盖的瓦片(基础瓦片除外)。
	var rows_needed := ceili(float(frame_count) / float(columns))
	for fy in range(rows_needed):
		for fx in range(columns):
			if fx == 0 and fy == 0:
				continue
			var covered := coord + Vector2i(fx, fy)
			if atlas.has_tile(covered):
				atlas.remove_tile(covered)
	atlas.set_tile_animation_columns(coord, columns)
	atlas.set_tile_animation_frames_count(coord, frame_count)
	for f in range(frame_count):
		atlas.set_tile_animation_frame_duration(coord, f, duration)
	if anim.has("separation"):
		var sep = anim["separation"]
		if typeof(sep) == TYPE_DICTIONARY:
			atlas.set_tile_animation_separation(coord,
				Vector2i(int(sep.get("x", 0)), int(sep.get("y", 0))))
	return ""
