@tool
extends RefCounted
## tileset.* 命令注册编排器 — 将所有 tileset.* 工具挂载到
## 注册表上,并把每个处理器委派给对应的命令子模块(结构、
## 备选、瓦片数据)。自身不持有命令逻辑;I/O 主干位于
## 共享的 tileset_io.gd 叶子模块,由各子模块消费。

const _Structure := preload("res://addons/godot_mcp_toolkit/commands/tileset/tileset_structure.gd")
const _Alternatives := preload("res://addons/godot_mcp_toolkit/commands/tileset/tileset_alternatives.gd")
const _TileData := preload("res://addons/godot_mcp_toolkit/commands/tileset/tileset_tile_data.gd")


static func register(registry: MCPToolkitCommandRegistry) -> void:
	# -- tileset 组(结构)--
	registry.add("tileset.create", func(parameters: Dictionary) -> Dictionary:
		return await _Structure.cmd_create(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.add_source", func(parameters: Dictionary) -> Dictionary:
		return await _Structure.cmd_add_source(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.remove_source", func(parameters: Dictionary) -> Dictionary:
		return await _Structure.cmd_remove_source(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.add_alternative", func(parameters: Dictionary) -> Dictionary:
		return await _Alternatives.cmd_add_alternative(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.remove_alternative", func(parameters: Dictionary) -> Dictionary:
		return await _Alternatives.cmd_remove_alternative(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.setup_layers", func(parameters: Dictionary) -> Dictionary:
		return await _Structure.cmd_setup_layers(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	# -- tileset_edit 组(每瓦片属性)--
	# 每个动词都绑定共享的 _TileData.cmd_edit 分发器,但固定自己的
	# 动词(VERB),因此处理器会强制其工具名所承诺的每瓦片键集合
	# (发给 edit_physics 的 terrain 键会被拒绝,而不是被静默应用)。
	registry.add("tileset.edit_physics", func(parameters: Dictionary) -> Dictionary:
		return await _TileData.cmd_edit(parameters, "physics")
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.edit_terrain", func(parameters: Dictionary) -> Dictionary:
		return await _TileData.cmd_edit(parameters, "terrain")
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.edit_navigation", func(parameters: Dictionary) -> Dictionary:
		return await _TileData.cmd_edit(parameters, "navigation")
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.edit_visuals", func(parameters: Dictionary) -> Dictionary:
		return await _TileData.cmd_edit(parameters, "visuals")
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("tileset.edit_custom_data", func(parameters: Dictionary) -> Dictionary:
		return await _TileData.cmd_edit(parameters, "custom_data")
	, MCPToolkitCommandOptions.new().mark_scene_independent())
