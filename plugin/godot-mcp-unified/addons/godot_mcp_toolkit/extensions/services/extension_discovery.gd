@tool
extends RefCounted
## 一次性的启动发现:把通过对全局类列表做反射找到的每个已启用扩展候选
## 加载进活跃的命令注册表,且仅执行一次。
##
## 无状态。discover_and_register() 枚举 ProjectSettings.get_global_class_list(),
## 经共享支持叶子模块过滤出已启用的候选,逐个加载,并把保留下来的实例
## 作为元数据(meta)存储在注册表上,使其存活期超过本次调用(即保住
## 已注册 Callable 存活的 C# GC 安全引用)。监视器(watcher,独立模块)会
## 在本轮之后接管实时热重载;本模块此后不再运行。

const _Support := preload("res://addons/godot_mcp_toolkit/extensions/services/extension_support.gd")


## 发现并把每个已启用的扩展候选注册进活跃注册表。返回已加载的扩展数量。
## 保留下来的 C# 实例会以 "_extension_instances" 为键存储在注册表上,
## 使其存活期超过本次调用。
static func discover_and_register(registry: MCPToolkitCommandRegistry, server: Node) -> int:
	var classes: Array = ProjectSettings.get_global_class_list()
	var instances: Array = []
	for entry in classes:
		if not _Support.is_extension_candidate(entry):
			continue
		var script_path: String = entry.get("path", "")
		if not _Support.is_addon_enabled(script_path):
			continue
		var class_name_str: String = entry.get("class", "")
		# 保留返回的实例(C# GC 安全)—— load_extension 并不持有它;
		# 保留职责归调用方所有。
		var instance := _Support.load_extension(class_name_str, script_path, registry, server)
		if instance != null:
			instances.append(instance)
	# 把实例的所有权移交注册表,使其存活期超过本次调用。
	if not instances.is_empty():
		registry.set_meta("_extension_instances", instances)
	return instances.size()
