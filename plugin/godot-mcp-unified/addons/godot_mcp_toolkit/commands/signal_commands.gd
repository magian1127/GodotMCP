@tool
extends RefCounted
## signal.* 命令处理器 — 对已编辑场景节点进行列表、管理(连接/断开)与发射。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Coerce = Modules.Coerce
const Helpers = Modules.CommandHelpers
const SignalPairResolver := preload("res://addons/godot_mcp_toolkit/scene/signal_pair_resolver.gd")

static var _extends_path_re: RegEx = _compile_extends_path_re()

static func _compile_extends_path_re() -> RegEx:
	var re := RegEx.new()
	re.compile('extends\\s+"(res://[^"]+)"')
	return re


static func register(registry: MCPToolkitCommandRegistry, _server: Node) -> void:
	registry.add("signal.list", func(parameters: Dictionary) -> Dictionary:
		return _cmd_signal_list(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only())
	registry.add("signal.manage", func(parameters: Dictionary) -> Dictionary:
		return _cmd_signal_manage(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("signal.emit", func(parameters: Dictionary) -> Dictionary:
		return _cmd_signal_emit(parameters)
	, MCPToolkitCommandOptions.new())


# -- 辅助函数 ------------------------------------------------------------------


static func _get_edited_root() -> Node:
	return Helpers.get_edited_root()


static func _resolve_scene_node(node_path: String) -> Variant:
	return Helpers.resolve_scene_node(node_path)


## 当 get_base_script() 返回 null(编译链断裂)时,通过原始文件读取
## 沿 extends "res://..." 指令向上遍历。若任一祖先源码
## 包含该方法定义则返回 true。
static func _source_walk_has_method(source: String, method_name: String) -> bool:
	var depth := 0
	while depth < 50:
		var m := _extends_path_re.search(source)
		if m == null:
			return false
		var parent_path := m.get_string(1)
		if not FileAccess.file_exists(parent_path):
			return false
		source = FileAccess.get_file_as_string(parent_path)
		if source.find("func %s" % method_name) >= 0:
			return true
		depth += 1
	return false


static func _signal_list_of(
	node: Object, include_connections: bool = false, root: Node = null,
) -> Array:
	var result: Array = []
	for signal_info in node.get_signal_list():
		var signal_name := str(signal_info.get("name", ""))
		var arguments: Array = []
		for argument in signal_info.get("args", []):
			arguments.append({
				"name": str(argument.get("name", "")),
				"type": int(argument.get("type", 0)),
			})
		var entry := {
			"name": signal_name,
			"args": arguments,
		}
		if include_connections:
			var connections: Array = []
			for conn in node.get_signal_connection_list(signal_name):
				var callable: Callable = conn.get("callable", Callable())
				var target_obj = callable.get_object()
				var target_path := ""
				if target_obj is Node and root != null:
					if target_obj == root:
						target_path = "."
					else:
						target_path = str(root.get_path_to(target_obj))
				elif target_obj != null:
					target_path = str(target_obj.get_class())
				else:
					target_path = "<freed>"
				connections.append({
					"target_path": target_path,
					"method_name": callable.get_method(),
					"flags": int(conn.get("flags", 0)),
				})
			entry["connections"] = connections
		result.append(entry)
	return result


## 共享 SignalPairResolver 的编辑器包装。它把编辑器相对路径规范化,
## 把"无已编辑场景"的情形区分为 NO_SCENE(共享解析器只认得 NOT_FOUND),
## 把结构性校验委托给共享解析器,然后在 has_signal/has_method 失败时,
## 叠加运行时路径刻意省略的编辑器专属提示增强
## (实例化场景提示、脚本源码方法遍历)。
## 共享解析器负责骨架;这里负责提示。
static func _resolve_signal_pair(parameters: Variant) -> Dictionary:
	if typeof(parameters) != TYPE_DICTIONARY:
		return {"code": "INVALID_PARAMS", "error": "params must be an object"}
	# 在解析之前,把 /root/… 路径规范化为编辑器相对形式,并严格保持
	# 原有的守卫顺序:先必需参数,再无已编辑场景
	# (NO_SCENE,共享解析器无法把它与 NOT_FOUND 区分开)。
	var source_path := Helpers.normalize_editor_path(
		str(parameters.get("node_path", parameters.get("source_path", ""))))
	var signal_name := str(parameters.get("signal_name", ""))
	var target_path := Helpers.normalize_editor_path(str(parameters.get("target_path", "")))
	var method_name := str(parameters.get("method_name", ""))
	if source_path.is_empty() or signal_name.is_empty() \
			or target_path.is_empty() or method_name.is_empty():
		return {"code": "INVALID_PARAMS",
			"error": "node_path, signal_name, target_path, method_name are all required"}
	var root := _get_edited_root()
	if root == null:
		return {"code": "NO_SCENE", "error": "no edited scene"}
	# 把结构性校验(节点解析 + has_signal/has_method)连同已规范化的
	# 路径委托给共享解析器;它会在 NOT_FOUND 消息中回显这些
	# 规范化路径,与抽取前的输出保持一致。
	var normalized := {
		"source_path": source_path,
		"target_path": target_path,
		"signal_name": signal_name,
		"method_name": method_name,
	}
	var resolved := SignalPairResolver.resolve_pair(normalized, root)
	if not resolved.has("error"):
		return resolved
	# 在两类校验失败(信号/方法缺失)上叠加编辑器专属提示。
	# NOT_FOUND(源/目标缺失)则原样透传。
	if str(resolved.get("code", "")) != "INVALID_PARAMS":
		return resolved
	var source = _resolve_scene_node(source_path)
	if source != null and not source.has_signal(signal_name):
		var instance_hint := ""
		var check: Node = source
		while check != null and check != root:
			if check.scene_file_path != "":
				instance_hint = ". Node is instanced from %s — open that scene to connect script-defined signals" % check.scene_file_path
				break
			check = check.get_parent()
		return {"code": "INVALID_PARAMS",
			"error": "signal %s not on %s%s" % [signal_name, source_path, instance_hint]}
	var target = _resolve_scene_node(target_path)
	if target != null and not target.has_method(method_name):
		var method_hint := ""
		var scr := target.get_script() as Script
		if scr == null:
			method_hint = "; no script is attached — use node_set_script first, or connect via _ready() code"
		elif scr is GDScript:
			var found_in_source := false
			var walk: GDScript = scr
			while walk != null:
				if walk.source_code.find("func %s" % method_name) >= 0:
					found_in_source = true
					break
				var next := walk.get_base_script() as GDScript
				if next == null:
					# 编译链断裂 —— 通过源码文本遍历其余祖先
					found_in_source = _source_walk_has_method(
						walk.source_code, method_name)
					break
				walk = next
			if found_in_source:
				method_hint = "; method found in script source but not visible — the script (or a parent script) likely has compilation errors (check log_read(channel:'editor'))"
			else:
				method_hint = "; script %s is attached but does not define this method — check spelling and inheritance chain" % scr.resource_path
		return {"code": "INVALID_PARAMS",
			"error": "method %s not on %s%s" % [method_name, target_path, method_hint]}
	# INVALID_PARAMS 但没有场景节点可提供提示(如缺少参数)—— 原样透传。
	return resolved


# -- 命令 ---------------------------------------------------------------------


static func _cmd_signal_list(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")
	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var include_connections: bool = bool(parameters.get("include_connections", false))
	var node = _resolve_scene_node(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)
	return MCPToolkitSuccess.ok({"path": node_path, "signals": _signal_list_of(node, include_connections, root)})


static func _cmd_signal_manage(parameters: Dictionary) -> Dictionary:
	var action := str(parameters.get("action", ""))
	if not (action in ["connect", "disconnect"]):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"action must be 'connect' or 'disconnect' (got '%s')" % action)
	var resolved := _resolve_signal_pair(parameters)
	if resolved.has("error"):
		return MCPToolkitError.fail(str(resolved["code"]), str(resolved["error"]))
	var source = resolved["source"]
	var callable: Callable = resolved["callable"]
	var signal_name: String = str(resolved["signal_name"])
	var source_path: String = str(resolved["source_path"])
	var target_path: String = str(resolved["target_path"])
	var method_name: String = str(resolved["method_name"])
	if action == "connect":
		if source.is_connected(signal_name, callable):
			return MCPToolkitSuccess.ok({
				"status": "returned",
				"node_path": source_path,
				"signal": signal_name,
				"target_path": target_path,
				"method": method_name,
			})
		source.connect(signal_name, callable, Object.CONNECT_PERSIST)
		MCPToolkitUndoRedoAction.begin("connect %s.%s -> %s.%s" % [
				source_path, signal_name, target_path, method_name], source) \
			.do_method(source.connect.bind(signal_name, callable, Object.CONNECT_PERSIST)) \
			.undo_method(source.disconnect.bind(signal_name, callable)) \
			.commit_recorded()
		var response := MCPToolkitSuccess.ok({
			"status": "created",
			"node_path": source_path,
			"signal": signal_name,
			"target_path": target_path,
			"method": method_name,
		})
		var target = resolved["target"]
		var same_scene: bool = (source.owner == target.owner) \
			or (source == target.owner) or (target == source.owner)
		if not same_scene:
			response["hint"] = "Cross-scene connections (nodes from different .tscn files) cannot persist — use _ready() code instead."
		else:
			response["hint"] = "Save the scene to persist this connection."
		return response
	else:
		if not source.is_connected(signal_name, callable):
			return MCPToolkitError.fail("NOT_FOUND", "no connection to disconnect")
		source.disconnect(signal_name, callable)
		MCPToolkitUndoRedoAction.begin("disconnect %s.%s -> %s.%s" % [
				source_path, signal_name, target_path, method_name], source) \
			.do_method(source.disconnect.bind(signal_name, callable)) \
			.undo_method(source.connect.bind(signal_name, callable, Object.CONNECT_PERSIST)) \
			.commit_recorded()
		return MCPToolkitSuccess.ok({
			"node_path": source_path,
			"signal": signal_name,
			"target_path": target_path,
			"method": method_name,
		})


static func _cmd_signal_emit(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")
	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var signal_name := str(parameters.get("signal_name", ""))
	if signal_name.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing signal")
	var node = _resolve_scene_node(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)
	if not node.has_signal(signal_name):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"signal %s not on %s" % [signal_name, node_path])
	var raw_args = parameters.get("args", [])
	if typeof(raw_args) != TYPE_ARRAY:
		raw_args = []
	var coerced: Array = [signal_name]
	for argument in raw_args:
		var coerced_arg = Coerce.coerce_value(argument)
		if typeof(coerced_arg) == TYPE_DICTIONARY and (coerced_arg as Dictionary).has("_coerce_error"):
			return MCPToolkitError.fail("INVALID_PARAMS", str(coerced_arg["_coerce_error"]))
		coerced.append(coerced_arg)
	node.callv("emit_signal", coerced)
	return MCPToolkitSuccess.ok()
