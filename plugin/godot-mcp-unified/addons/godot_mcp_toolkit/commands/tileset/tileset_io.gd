@tool
extends RefCounted
## 共享的 TileSet 支撑叶子模块:加载、校验、保存并重建索引 TileSet
## 资源,外加每个 tileset.* 命令组都依赖的层节点版本提示
## 与全瓦片多边形。
##
## 无状态 — 每个函数以参数形式接收其输入(文件路径、TileSet、瓦片尺寸),
## 并返回其结果(TileSet 或错误的 Variant、空或错误的
## Dictionary、提示 String 或多边形)。没有跨调用状态;整个文件
## 全是静态函数,由瓦片集(tileset)命令子模块通过 `preload` 别名消费。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const Helpers = Modules.CommandHelpers


## 从 file_path 加载并校验 TileSet。返回 TileSet 或错误 Dictionary。
static func load_tileset(file_path: String) -> Variant:
	var guard := FileGuard.resolve_safe(file_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if not FileAccess.file_exists(file_path):
		return MCPToolkitError.fail("NOT_FOUND", "TileSet not found: %s" % file_path)
	var ts = ResourceLoader.load(file_path, "", ResourceLoader.CACHE_MODE_IGNORE)
	if ts == null or not (ts is TileSet):
		return MCPToolkitError.fail("INVALID_CLASS",
			"Resource at %s is not a TileSet" % file_path)
	return ts


## 保存 TileSet、重载缓存并建立索引。成功返回空字典,否则返回错误。
static func save_tileset(ts: TileSet, file_path: String) -> Dictionary:
	var save_err := ResourceSaver.save(ts, file_path)
	if save_err != OK:
		return MCPToolkitError.fail("SAVE_FAILED",
			"ResourceSaver.save returned %d (path=%s)" % [save_err, file_path])
	ResourceLoader.load(file_path, "", ResourceLoader.CACHE_MODE_REPLACE)
	await Helpers.ensure_file_indexed(file_path)
	return {}


## 构建响应尾部的提示,指明 TileSet 所依附的节点 —
## 4.3+ 上为 TileMapLayer,4.2 上为 TileMap — 并包裹在调用者的前缀/后缀中。
static func layer_node_hint(prefix: String, suffix: String) -> String:
	var ver := Modules.VersionUtils.get_engine_version_pair()
	var has_tilemaplayer := Modules.VersionUtils.is_at_least(ver, "4.3")
	var node_name := "TileMapLayer" if has_tilemaplayer else "TileMap"
	return prefix + node_name + suffix


## 覆盖单个瓦片的单位矩形(±w/2, ±h/2)— 默认的碰撞/导航/
## 遮挡形状,以及 "full"/"one_way" 碰撞简写。
static func build_full_tile_polygon(tile_size: Vector2i) -> PackedVector2Array:
	var hw := tile_size.x / 2.0
	var hh := tile_size.y / 2.0
	return PackedVector2Array([
		Vector2(-hw, -hh), Vector2(hw, -hh),
		Vector2(hw, hh), Vector2(-hw, hh)])
