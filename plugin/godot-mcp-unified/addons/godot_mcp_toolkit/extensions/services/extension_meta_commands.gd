@tool
extends RefCounted
## 提供 extensions.list 只读命令的服务,并拥有上下文协议(MCP)桥接契约所依赖的
## 唯一的每命令线上条目(wire-entry)构建器。
##
## extensions.list 是对注册表扩展方法的一次无状态读取。
## build_command_entry() 是定义每命令线上形状的唯一位置 ——
## 它所锁定的契约见其文档注释。
##
## 无状态:每个函数都把注册表作为参数传入;没有实例状态。


## 构建上下文协议桥接读取的每命令线上条目。这里是 extensions.list、
## extensions.refresh 与 extensions.changed 所消费的发布语言
## (Published-Language)形状的唯一来源 —— 让这三者都在这里构建,
## 面向桥接的契约就不会在它们之间漂移。字段仅在其非空/存在时才包含,
## 与 registry.get_command_metadata() 省略字段的方式保持一致。
static func build_command_entry(registry: MCPToolkitCommandRegistry, method: String) -> Dictionary:
	var meta := registry.get_command_metadata(method)
	var entry: Dictionary = {"method": method}
	if meta.get("description", "") != "":
		entry["description"] = meta["description"]
	if not meta.get("input_schema", {}).is_empty():
		entry["input_schema"] = meta["input_schema"]
	if not meta.get("annotations", {}).is_empty():
		entry["annotations"] = meta["annotations"]
	if not meta.get("group", {}).is_empty():
		entry["group"] = meta["group"]
	if meta.has("timeout_ms"):
		entry["timeout_ms"] = meta["timeout_ms"]
	return entry


## 注册用于桥接发现的 extensions.list 元命令。
static func register_list_command(registry: MCPToolkitCommandRegistry) -> void:
	var handler := func(params: Dictionary) -> Dictionary:
		return cmd_extensions_list(registry, params)
	registry.add("extensions.list", handler, MCPToolkitCommandOptions.new()
		.with_description("List all discovered third-party extensions and their commands")
		.mark_read_only()
		.mark_idempotent()
		.mark_scene_independent())


## 列出每个已发现的扩展命令及其完整的线上元数据。
static func cmd_extensions_list(registry: MCPToolkitCommandRegistry, _params: Dictionary) -> Dictionary:
	var methods := registry.get_extension_methods()
	var result: Array[Dictionary] = []
	for method: String in methods:
		result.append(build_command_entry(registry, method))
	return {"success": true, "commands": result}
