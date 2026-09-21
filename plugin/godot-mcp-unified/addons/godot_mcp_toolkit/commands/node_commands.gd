@tool
extends RefCounted
## node.* 命令处理器 — 属性获取/设置/列表、方法调用、脚本挂载。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Coerce = Modules.Coerce
const FileGuard = Modules.FileGuard
const Helpers = Modules.CommandHelpers

## node.set_property 拒绝 "groups",因为它的设置是声明式的整体
## 替换(会丢掉列表中没有的任何组),而 node.groups 是
## 增量的。两种语义彼此相反,因此组的操作只经由一个工具。
const _GROUPS_REJECTION_MESSAGE := "'groups' cannot be set via node.set_property: it would replace the whole group list and silently drop any group not included."
const _GROUPS_REJECTION_HINT := "Use node.groups (action 'add'/'remove') to change group membership incrementally, or 'list' to read it."


const COMMON_PROPERTIES_BY_CLASS := {
	"Node": ["name", "process_mode"],
	"Node2D": ["position", "rotation", "scale", "z_index", "visible", "modulate"],
	"Node3D": ["position", "rotation", "scale", "visible"],
	"Control": ["position", "size", "anchor_left", "anchor_right", "anchor_top",
		"anchor_bottom", "visible", "modulate", "size_flags_horizontal", "size_flags_vertical"],
	"Sprite2D": ["texture", "centered", "offset", "flip_h", "flip_v", "hframes", "vframes", "frame"],
	"Sprite3D": ["texture", "centered", "offset", "flip_h", "flip_v"],
	"CollisionShape2D": ["shape", "disabled"],
	"CollisionShape3D": ["shape", "disabled"],
	"RigidBody2D": ["mass", "gravity_scale", "linear_velocity", "angular_velocity"],
	"RigidBody3D": ["mass", "gravity_scale", "linear_velocity", "angular_velocity"],
	"CharacterBody2D": ["velocity", "floor_max_angle", "up_direction"],
	"CharacterBody3D": ["velocity", "floor_max_angle", "up_direction"],
	"Camera2D": ["zoom", "offset", "position_smoothing_enabled"],
	"Camera3D": ["fov", "near", "far", "current"],
	"Area2D": ["monitoring", "monitorable", "gravity"],
	"Area3D": ["monitoring", "monitorable", "gravity"],
	"AnimationPlayer": ["current_animation", "autoplay", "speed_scale"],
	"Timer": ["wait_time", "one_shot", "autostart"],
	"Label": ["text", "horizontal_alignment", "vertical_alignment", "autowrap_mode"],
	"Button": ["text", "disabled", "flat"],
	"TextureRect": ["texture", "stretch_mode"],
	"AudioStreamPlayer": ["stream", "volume_db", "pitch_scale", "autoplay"],
	"AudioStreamPlayer2D": ["stream", "volume_db", "pitch_scale", "max_distance"],
	"AudioStreamPlayer3D": ["stream", "volume_db", "pitch_scale", "max_distance"],
	"MeshInstance3D": ["mesh", "material_override"],
	"Light2D": ["energy", "color", "shadow_enabled"],
	"DirectionalLight3D": ["light_energy", "light_color", "shadow_enabled"],
	"GPUParticles2D": ["process_material", "emitting", "amount", "lifetime"],
	"GPUParticles3D": ["process_material", "emitting", "amount", "lifetime"],
	"LineEdit": ["text", "placeholder_text", "editable", "max_length"],
	"TextEdit": ["text", "editable"],
	"RichTextLabel": ["text", "bbcode_enabled"],
}


static func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	registry.add("node.get_property", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_get_property(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only())
	registry.add("node.set_property", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_set_property(server, parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("node.get_property_list", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_get_property_list(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only())
	registry.add("node.call_method", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_call_method(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("node.set_script", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_set_script(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("node.manage", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_manage(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("node.groups", func(parameters: Dictionary) -> Dictionary:
		return _cmd_node_groups(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("node.collision_from_sprite", func(parameters: Dictionary) -> Dictionary:
		return _cmd_collision_from_sprite(parameters)
	, MCPToolkitCommandOptions.new())
	registry.add("control.set_layout", func(parameters: Dictionary) -> Dictionary:
		return _cmd_control_set_layout(parameters)
	, MCPToolkitCommandOptions.new())


# -- 辅助函数 ------------------------------------------------------------------


static func _get_edited_root() -> Node:
	return Helpers.get_edited_root()


static func _resolve_scene_node(node_path: String) -> Variant:
	return Helpers.resolve_scene_node(node_path)


## 对 node.set_property 唯一拒绝(让位于 node.groups)的属性名为 true。
## 纯函数(不触碰引擎状态),因此单实例与批量模式共享同一个
## 判定,单元测试也可以在没有编辑器的情况下钉住它。
static func _is_groups_property(property_name: String) -> bool:
	return property_name == "groups"


## 通过把读回值与期望值比较,检测复合路径设置的静默失败。
static func _iscompound_set_failure(expected: Variant, actual: Variant) -> bool:
	if expected == null:
		return false  # 设置 null — 无需验证
	if actual == null:
		return true  # 期望非 null,却得到 null
	if typeof(actual) == TYPE_DICTIONARY and (actual as Dictionary).is_empty():
		if expected is Resource:
			return true  # 期望资源,却得到空字典
		if typeof(expected) == TYPE_DICTIONARY and not (expected as Dictionary).is_empty():
			return true  # 期望有内容的字典,却得到空字典
	return false


## 用于警告消息的值的简短字符串表示。
static func _brief_value(value: Variant) -> String:
	if value == null:
		return "null"
	if typeof(value) == TYPE_DICTIONARY and (value as Dictionary).is_empty():
		return "{} (empty)"
	var s := var_to_str(value)
	if s.length() > 60:
		s = s.left(57) + "..."
	return s


# -- 命令 ---------------------------------------------------------------------


static func _cmd_node_get_property(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var property_name := str(parameters.get("property", ""))

	if node_path.is_empty() or property_name.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path or property")

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	# 复合路径(冒号链式子资源路径,如
	# "material:shader_parameter/value")使用集中的处理器:
	# 先尝试节点级覆盖,再回退到子资源读取。
	if ":" in property_name:
		var result := Helpers.get_property_compound(node, property_name)
		if not result.get("ok", false):
			return MCPToolkitError.fail(
				result.get("code", "NOT_FOUND"),
				str(result.get("error", "")))
		return MCPToolkitSuccess.ok({"value": result["value"]})

	return MCPToolkitSuccess.ok({"value": Coerce.serialize_value(node.get(property_name))})


static func _cmd_node_set_property(server: Node, parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	# 批量模式 — 在单个 UndoRedo 动作中设置多个属性。
	var batch_raw = parameters.get("batch", null)
	if batch_raw != null and typeof(batch_raw) == TYPE_ARRAY and (batch_raw as Array).size() > 0:
		return _batch_set_properties(server, root, batch_raw as Array)

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var property_name := str(parameters.get("property", ""))
	var raw_value = parameters.get("value", null)

	if node_path.is_empty() or property_name.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path or property")

	# "groups" 不是常规属性 —— 它保存在 .tscn 节点头中,
	# 在这里做整体替换式设置会静默剥掉不在给定列表中的任何组。
	# 把所有组的修改引导到 node.groups,它的动词是增量的
	# (两个工具都是急切的,因此它总是可达)。
	# 在节点解析之前检查,使错误与 node_path 是否有效无关地
	# 保持一致(与批量模式中的位置呼应)。
	if _is_groups_property(property_name):
		return MCPToolkitError.fail("INVALID_PARAMS",
			_GROUPS_REJECTION_MESSAGE, _GROUPS_REJECTION_HINT)

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	# 复合/冒号链式路径(如 "libraries/test"、
	# "material:shader_parameter/value"、"theme_override_colors/font_color")
	# 走子资源导航 + UndoRedo 路径;其余都是标量设置。
	if ":" in property_name or "/" in property_name:
		return _set_property_compound_single(
			server, node, node_path, property_name, raw_value, parameters)
	return _set_property_scalar_single(node, node_path, property_name, raw_value)


## 设置一个复合/冒号链式属性并注册其 UndoRedo。editor_helpers.gd 中的
## 集中处理器负责子资源导航、shader_parameter/ 专用 setter、值转换
## 与读回;它返回 _undo 信息,我们在这里经 commit_recorded() 注册。
## node.set_property 复合分支的叶子机制,从处理器中提出,
## 使后者读起来像一个自上而下的编排器。
## 只从 parameters 读取 `make_unique`。
## 直接返回响应字典。
static func _set_property_compound_single(
	server: Node, node: Node, node_path: String, property_name: String,
	raw_value: Variant, parameters: Dictionary,
) -> Dictionary:
	var do_unique := bool(parameters.get("make_unique", false))
	var result := Helpers.set_property_compound(
		node, property_name, raw_value, do_unique)
	if not result.get("ok", false):
		return MCPToolkitError.fail(
			result.get("code", "INVALID_VALUE"),
			str(result.get("error", "")))

	# 为复合路径注册 UndoRedo。所有 do/undo 方法都经由
	# server.undo_helpers.compound_set 路由,以保持一致的
	# EditorUndoRedoManager 上下文(避免历史不匹配错误)。
	var ui: Dictionary = result.get("_undo", {})
	var undo_type: String = str(ui.get("type", ""))
	if undo_type != "":
		var undo_path: String = ui["path"]
		var new_val = ui.get("new", node.get(undo_path))
		var action = MCPToolkitUndoRedoAction.begin("set %s.%s" % [node_path, property_name], node)
		action.do_method(server.undo_helpers.compound_set.bind(
			node, undo_path, new_val))
		action.undo_method(server.undo_helpers.compound_set.bind(
			node, undo_path, ui["old"]))
		# make_unique:撤销时恢复原外部资源。
		if ui.has("old_resource_prop"):
			var res_prop: String = ui["old_resource_prop"]
			var new_res = node.get(res_prop)
			action.do_method(server.undo_helpers.compound_set.bind(
				node, res_prop, new_res))
			action.undo_method(server.undo_helpers.compound_set.bind(
				node, res_prop, ui["old_resource"]))
			if new_res is Resource:
				action.do_reference(new_res)
			if ui["old_resource"] is Resource:
				action.undo_reference(ui["old_resource"])
		action.commit_recorded()

	var response := {}
	if result.has("made_unique"):
		response["made_unique"] = result["made_unique"]
	if result.has("warning"):
		response["warning"] = result["warning"]
	return MCPToolkitSuccess.ok(response)


## 设置一个标量(非复合)属性:转换 → 设置 → 注册 UndoRedo,
## 然后是裸 res:// 读回守卫(把裸 "res://…" 字符串赋给
## Resource 类型属性会静默加载失败 —— 检测到它,并引导调用方
## 使用带标签的 {type:"Resource", path:…} 形式)。node.set_property
## 标量分支的叶子机制,出于单一职责从处理器中提出。
## 不需要 `server` —— 标量撤销使用 do/undo_property。
static func _set_property_scalar_single(
	node: Node, node_path: String, property_name: String, raw_value: Variant,
) -> Dictionary:
	var coerce_result := Helpers.coerce_for_property(node, property_name, raw_value)
	if not coerce_result.get("ok", false):
		return MCPToolkitError.fail(
			coerce_result.get("code", "INVALID_VALUE"),
			str(coerce_result.get("error", "")))
	var coerced = coerce_result["value"]

	var old_value = node.get(property_name)

	# 在写入 editor_description 之前断开 Godot 4.3 的 SceneTreeEditor
	# 工具提示计时器连接,使这次设置不会对一个即将释放的树行布下
	# 计时器(对其他属性/其他版本为空操作)。之后的人工撤销/重做会在
	# 此守卫之外重放该设置 —— 不在范围内,4.4 已修复。见 disarm_tooltip_uaf。
	Helpers.disarm_tooltip_uaf(node, property_name)
	node.set(property_name, coerced)
	# Resource 类型属性上的裸 res:// 字符串:在下面通用的
	# 丢弃守卫之前给出具体的标签形式提示(裸路径会静默加载失败),
	# 否则通用守卫会先以更含糊的消息触发。
	# 特征:原始值是 String + 转换结果非 Resource + 读回值非 String。
	if typeof(raw_value) == TYPE_STRING and str(raw_value).begins_with("res://") \
			and not (coerced is Resource) and not (node.get(property_name) is String):
		return MCPToolkitError.fail("INVALID_VALUE",
			"property '%s' expects a Resource, not a bare string path. " % property_name +
			"Use {\"type\": \"Resource\", \"path\": \"%s\"} as the value." % str(raw_value))
	# 对写入分类。DROPPED(静默的类型错误)在撤销之前失败 —— 什么都没落地,
	# 因此没有需要回滚的状态。ADJUSTED(引擎重塑了值,
	# 如截断 7.9→7 或归一化)确实已提交,因此像干净写入一样记录撤销,
	# 并在成功的同时返回一条点名"已存储 vs 所请求"偏差的警告。
	# OK 是干净的成功。
	var outcome := Helpers.describe_set_drop(old_value, node.get(property_name), coerced, property_name)
	if outcome.get("status", "") == "dropped":
		# 绑定型 setter(position/modulate)可能已把错误类型经变体转换成
		# 零值并存储;恢复先前的值,使 SET_FAILED 真正不具破坏性。
		# 在注册撤销之前执行 —— 没有需要回滚的内容。
		node.set(property_name, old_value)
		return MCPToolkitError.fail("SET_FAILED", str(outcome.get("error", "")))
	var action = MCPToolkitUndoRedoAction.begin("set %s.%s" % [node_path, property_name], node)
	action.do_property(node, property_name, coerced)
	action.undo_property(node, property_name, old_value)
	if coerced is Resource:
		action.do_reference(coerced)
	if old_value is Resource:
		action.undo_reference(old_value)
	action.commit_recorded()
	if outcome.get("status", "") == "adjusted":
		return MCPToolkitSuccess.ok({"warning": str(outcome.get("warning", ""))})
	return MCPToolkitSuccess.ok()


## 在一个 UndoRedo 动作中批量设置多个属性。
## 复合路径(: 或 /)使用 set_property_compound(),并在可能时
## 把它们的撤销信息加到同一个批量动作中(仅属性类型路径)。
static func _batch_set_properties(server: Node, root: Node, entries: Array) -> Dictionary:
	var action = MCPToolkitUndoRedoAction.begin("batch set %d properties" % entries.size(), root)

	var results: Array = []
	for entry in entries:
		if typeof(entry) != TYPE_DICTIONARY:
			results.append({"success": false, "error": "entry must be an object"})
			continue
		var np := str(entry.get("node_path", ""))
		np = Helpers.normalize_editor_path(np)
		var prop := str(entry.get("property", ""))
		var raw_val = entry.get("value", null)

		if np.is_empty() or prop.is_empty():
			results.append({"node_path": np, "property": prop,
				"success": false, "error": "missing node_path or property"})
			continue

		# 逐条目拒绝 "groups"(批量的其余部分仍会应用)。若没有这一步,
		# 它会落到 node.set("groups", …) —— 那不是属性 —— 并无声消失;
		# 组的修改属于 node.groups(增量)。
		if _is_groups_property(prop):
			results.append({"node_path": np, "property": prop, "success": false,
				"error": _GROUPS_REJECTION_MESSAGE, "hint": _GROUPS_REJECTION_HINT})
			continue

		var node := root.get_node_or_null(np)
		if node == null:
			results.append({"node_path": np, "property": prop,
				"success": false, "error": "node not found"})
			continue

		# 复合路径:经由 set_property_compound() 路由,它处理
		# 冒号→斜杠转换、子资源导航与读回。
		# 所有复合撤销都使用 server.undo_helpers.compound_set,
		# 使整个批次保持在一致的 EditorUndoRedoManager 上下文中。
		if ":" in prop or "/" in prop:
			var do_unique := bool(entry.get("make_unique", false))
			var result := Helpers.set_property_compound(
				node, prop, raw_val, do_unique)
			if result.get("ok", false):
				var ui: Dictionary = result.get("_undo", {})
				var undo_type: String = str(ui.get("type", ""))
				if undo_type != "":
					var undo_path: String = ui["path"]
					var new_val = ui.get("new", node.get(undo_path))
					action.do_method(server.undo_helpers.compound_set.bind(
						node, undo_path, new_val))
					action.undo_method(server.undo_helpers.compound_set.bind(
						node, undo_path, ui["old"]))
					if ui.has("old_resource_prop"):
						var res_prop: String = ui["old_resource_prop"]
						var new_res = node.get(res_prop)
						action.do_method(server.undo_helpers.compound_set.bind(
							node, res_prop, new_res))
						action.undo_method(server.undo_helpers.compound_set.bind(
							node, res_prop, ui["old_resource"]))
						if new_res is Resource:
							action.do_reference(new_res)
						if ui["old_resource"] is Resource:
							action.undo_reference(ui["old_resource"])
				var res_entry := {"node_path": np, "property": prop, "success": true}
				if result.has("made_unique"):
					res_entry["made_unique"] = result["made_unique"]
				if result.has("warning"):
					res_entry["warning"] = result["warning"]
				results.append(res_entry)
			else:
				results.append({"node_path": np, "property": prop,
					"success": false, "error": str(result.get("error", ""))})
			continue

		var missing := Coerce.check_resource_paths(raw_val)
		if missing != "":
			results.append({"node_path": np, "property": prop,
				"success": false, "error": "resource not found: %s" % missing})
			continue

		var coerced = Coerce.coerce_value(raw_val)
		if typeof(coerced) == TYPE_DICTIONARY and (coerced as Dictionary).has("_coerce_error"):
			results.append({"node_path": np, "property": prop,
				"success": false, "error": str(coerced["_coerce_error"])})
			continue

		var old_value = node.get(prop)
		if typeof(old_value) == TYPE_NODE_PATH and typeof(coerced) == TYPE_STRING:
			coerced = NodePath(str(coerced))

		# 无条件应用修改,然后记录撤销。当 prop 是 editor_description 时
		# 先解除 Godot 4.3 工具提示计时器 UAF(其他情况为空操作)。
		Helpers.disarm_tooltip_uaf(node, prop)
		node.set(prop, coerced)
		# 分类:DROPPED → 逐条目失败(跳过撤销;批量的其余部分仍会应用,
		# summarize_batch 会汇总它)。ADJUSTED/OK 已提交 → 注册撤销;
		# ADJUSTED 追加一条点名引擎值重塑的逐条目警告。
		var outcome := Helpers.describe_set_drop(old_value, node.get(prop), coerced, prop)
		if outcome.get("status", "") == "dropped":
			# 恢复先前的值(绑定型 setter 可能已把它清零),使该失败条目
			# 不具破坏性;跳过它的撤销,批量其余部分继续应用。
			node.set(prop, old_value)
			results.append({"node_path": np, "property": prop,
				"success": false, "error": str(outcome.get("error", ""))})
			continue
		action.do_method(server.undo_helpers.compound_set.bind(node, prop, coerced))
		action.undo_method(server.undo_helpers.compound_set.bind(node, prop, old_value))
		if coerced is Resource:
			action.do_reference(coerced)
		if old_value is Resource:
			action.undo_reference(old_value)
		var scalar_entry := {"node_path": np, "property": prop, "success": true}
		if outcome.get("status", "") == "adjusted":
			scalar_entry["warning"] = str(outcome.get("warning", ""))
		results.append(scalar_entry)

	action.commit_recorded()

	# 汇总针对不会持久化的外部子资源修改的警告。
	var non_persisting: PackedStringArray = []
	for r in results:
		if r.has("warning"):
			non_persisting.append(str(r.get("property", "")))
	var response := {"results": results}
	if non_persisting.size() > 0:
		response["warning"] = (
			"These compound paths were set on shared external sub-resources "
			+ "and may not persist after save/reload: %s. " % ", ".join(non_persisting)
			+ "Retry those entries with make_unique: true to auto-duplicate "
			+ "them as inline copies that persist.")
	# 把逐条目失败汇总到顶层的失败计数 + 提示(增量式:
	# 全成功批次逐字节一致;既有警告被保留)。
	return MCPToolkitSuccess.ok(Helpers.summarize_batch(response, "results"))


static func _resolve_common_property_names(node: Object) -> Array[String]:
	var result: Array[String] = []
	var current := node.get_class()
	var depth := 0
	while not current.is_empty() and depth < 16:
		if COMMON_PROPERTIES_BY_CLASS.has(current):
			for prop_name in COMMON_PROPERTIES_BY_CLASS[current]:
				if prop_name not in result:
					result.append(prop_name)
		if ClassDB.class_exists(current):
			current = ClassDB.get_parent_class(current)
		else:
			break
		depth += 1
	return result


static func _cmd_node_get_property_list(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")
	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var node = _resolve_scene_node(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)
	var mask := str(parameters.get("mask", "common"))
	if not (mask in ["common", "all", "groups", "script"]):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"mask must be 'common', 'all', 'groups', or 'script' (got '%s')" % mask)
	var visibility_filter := str(parameters.get("visibility", "all"))
	if mask == "script" and not (visibility_filter in ["public", "private", "all"]):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"visibility must be 'public', 'private', or 'all' (got '%s')" % visibility_filter)
	var common_names: Array[String] = []
	if mask == "common":
		common_names = _resolve_common_property_names(node)
	var properties: Array = []
	if mask == "script":
		# 直接使用 Script.get_script_property_list() —— 它对
		# @tool 与非 @tool 脚本都有效,不同于 node.get_property_list():
		# 后者对非 @tool 脚本可能省略 PROPERTY_USAGE_SCRIPT_VARIABLE。
		var script: Script = node.get_script() as Script
		if script != null:
			for property in script.get_script_property_list():
				var usage: int = int(property.get("usage", 0))
				if not (usage & PROPERTY_USAGE_EDITOR):
					continue
				var property_name := str(property.get("name", ""))
				if property_name.is_empty():
					continue
				var vis := "private" if property_name.begins_with("_") else "public"
				if visibility_filter != "all" and vis != visibility_filter:
					continue
				properties.append({
					"name": property_name,
					"type": int(property.get("type", 0)),
					"hint": int(property.get("hint", 0)),
					"hint_string": str(property.get("hint_string", "")),
					"visibility": vis,
				})
	else:
		for property in node.get_property_list():
			var usage: int = int(property.get("usage", 0))
			var property_name := str(property.get("name", ""))
			if property_name.is_empty():
				continue
			if not (usage & PROPERTY_USAGE_EDITOR):
				continue
			if property_name.begins_with("_"):
				continue
			if mask == "common" and property_name not in common_names:
				continue
			if mask == "groups":
				properties.append({
					"name": property_name,
					"usage": usage,
				})
			else:
				properties.append({
					"name": property_name,
					"type": int(property.get("type", 0)),
					"hint": int(property.get("hint", 0)),
					"hint_string": str(property.get("hint_string", "")),
				})
	return MCPToolkitSuccess.ok({
		"path": node_path,
		"class": node.get_class(),
		"mask": mask,
		"properties": properties,
		"count": properties.size(),
	})


## 当目标节点的磁盘上 .gd 定义了 `method_name`(且能编译),而活动实例
## 缺少它时,返回按版本定制的陈旧活动实例恢复提示 ——
## 也就是说,陈旧的是实例,而不是调用错了。在 Godot < 4.4(任何模式)与
## 4.4+ 且无头时触发(无头编辑器从不重新实例化被重载的节点)。其余情况返回 ""
## (没有脚本 / 非 .gd / 方法确实不存在 / 磁盘上的编译不过 / 4.4+
## 且带显示)。纯判定位于 StaleInstanceHint;这里只读取
## 运行版本 + 无头标志 + 磁盘上的源码。仅在
## INVALID_METHOD 错误路径上被调用。
static func _stale_method_hint(node: Object, method_name: String) -> String:
	var scr = node.get_script()
	if scr == null:
		return ""
	var scr_path := str(scr.resource_path)
	if not scr_path.to_lower().ends_with(".gd") or not FileAccess.file_exists(scr_path):
		return ""
	var disk_source := FileAccess.get_file_as_string(scr_path)
	var version := Modules.VersionUtils.get_engine_version_ints()
	var headless := Modules.VersionUtils.is_headless()
	var disk_has := Modules.StaleInstanceHint.source_has_method(disk_source, method_name)
	var disk_ok := Modules.StaleInstanceHint.source_compiles(disk_source)
	if not Modules.StaleInstanceHint.should_hint_on_call(false, disk_has, disk_ok, true, version.x, version.y, headless):
		return ""
	return Modules.StaleInstanceHint.recovery_message(
			Modules.VersionUtils.get_engine_version_pair(), version.y, headless)


## 为 callv() 返回 null 的情形构建 node.call_method 提示,按版本门控。
##
## 非 @tool 的 GDScript 从不在编辑器中运行,因此 callv() 无法分发其
## 方法并返回 null(编辑器会记录 "Method not found")。提示先给可靠的
## 运行时路径,再给编辑器 @tool 修复。该修复以 4.5 为界:
## 在 4.5+ 上,已挂载实例的方法表只能通过完整重开场景来重建
## (scene_close + scene_open)—— editor_sync 不够 —— 而在
## 4.5 以下不存在上下文协议(MCP)可操作的场景重开,因此必须重启编辑器。C#
## 在所有版本上还需要重新构建项目。
## [param ver_pair] 运行中引擎的 "major.minor"(驱动 4.5 门控)。
static func _call_method_null_hint(is_csharp: bool, ver_pair: String) -> String:
	if is_csharp:
		return ("Return value was null. C# methods cannot execute in editor mode without "
			+ "the [Tool] attribute — Godot registers the method signature but does not "
			+ "instantiate the managed .NET object. Properties and signals work normally. Use "
			+ "game.start + execute_code to call C# methods at runtime, or set state via "
			+ "node.set_property (most C# logic runs in _Ready() at startup). To call it in the "
			+ "editor, add [Tool], then rebuild the C# project and relaunch the editor.")
	var editor_tail := ("add @tool, then close and reopen the scene (scene_close + scene_open); "
		+ "editor_sync is not sufficient")
	if not Modules.VersionUtils.is_at_least(ver_pair, "4.5"):
		editor_tail = "add @tool, then relaunch the editor"
	return ("Return value was null. A non-@tool GDScript never runs in the editor, so callv() "
		+ "cannot find the method (the editor logs 'Method not found'). To call it: (1) runtime, "
		+ "reliable — game_start, then execute_code or runtime_inspect_node on the live node; "
		+ "(2) editor — " + editor_tail + ".")


static func _cmd_node_call_method(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no open scene; use scene.open or scene.create first")

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var method_name := str(parameters.get("method_name", ""))
	var args_raw = parameters.get("args", [])
	# args 中的资源引用经由 Coerce.check_resource_paths 校验,
	# 它通过 FileGuard 把关。node_path 是场景树路径,不是文件系统路径。

	if node_path.is_empty() or method_name.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path or method_name")
	if typeof(args_raw) != TYPE_ARRAY:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"args must be an Array (got %s)" % typeof(args_raw))

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND",
			"no node at path %s. This tool is editor-only — for runtime nodes use execute_code or runtime_inspect_node." % node_path)
	if not node.has_method(method_name):
		var no_method_msg := "node %s has no method '%s'; use scene.get_tree or inspect the script class via ClassDB" % [
			node_path, method_name]
		var stale_hint := _stale_method_hint(node, method_name)
		if stale_hint != "":
			return MCPToolkitError.fail("INVALID_METHOD", no_method_msg, stale_hint)
		return MCPToolkitError.fail("INVALID_METHOD", no_method_msg)

	var missing := Coerce.check_resource_paths(args_raw)
	if missing != "":
		return MCPToolkitError.fail("LOAD_FAILED",
			"failed to load resource at %s; verify the path or use resource.write to create it first" % missing)

	var coerced_args = Coerce.coerce_value(args_raw)
	if typeof(coerced_args) != TYPE_ARRAY:
		coerced_args = []
	for arg in coerced_args:
		if typeof(arg) == TYPE_DICTIONARY and (arg as Dictionary).has("_coerce_error"):
			return MCPToolkitError.fail("INVALID_PARAMS", str(arg["_coerce_error"]))
	print("[MCPTools] node.call_method invoked %s.%s(%d args)" % [
		node_path, method_name, (coerced_args as Array).size()])
	var result = node.callv(method_name, coerced_args)

	var response := {
		"path": node_path,
		"method": method_name,
		"result": Coerce.serialize_value(result),
	}
	if result == null:
		var script = node.get_script()
		var is_csharp := script != null and str(script.resource_path).ends_with(".cs")
		response["hint"] = _call_method_null_hint(is_csharp, Modules.VersionUtils.get_engine_version_pair())
	return MCPToolkitSuccess.ok(response)


static func _cmd_node_set_script(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	if node_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path")

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	var script_path := str(parameters.get("script_path", ""))

	if script_path.is_empty():
		var old_script = node.get_script()
		node.set_script(null)
		var clear_action = MCPToolkitUndoRedoAction.begin("clear script on %s" % node_path, node)
		clear_action.do_property(node, &"script", null)
		clear_action.undo_property(node, &"script", old_script)
		if old_script is Resource:
			clear_action.undo_reference(old_script)
		clear_action.commit_recorded()
		return MCPToolkitSuccess.ok({"path": node_path, "script": null, "properties": []})

	var guard := FileGuard.resolve_safe(script_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))

	var loaded = ResourceLoader.load(script_path)
	if loaded == null:
		return MCPToolkitError.fail("LOAD_FAILED",
			"cannot load script at %s; verify the path or create it with workspace file editing, then call editor_sync" % script_path)
	if not (loaded is Script):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"resource at %s is not a Script (got %s)" % [script_path, loaded.get_class()])

	var old_script = node.get_script()
	node.set_script(loaded)
	var set_action = MCPToolkitUndoRedoAction.begin("set script %s on %s" % [script_path, node_path], node)
	set_action.do_property(node, &"script", loaded)
	set_action.undo_property(node, &"script", old_script)
	set_action.do_reference(loaded)
	if old_script is Resource:
		set_action.undo_reference(old_script)
	set_action.commit_recorded()

	var exports: Array = []
	for property in loaded.get_script_property_list():
		var usage: int = int(property.get("usage", 0))
		if not (usage & PROPERTY_USAGE_EDITOR):
			continue
		var property_name := str(property.get("name", ""))
		if property_name.is_empty() or property_name.begins_with("_"):
			continue
		exports.append({
			"name": property_name,
			"type": int(property.get("type", 0)),
			"hint": int(property.get("hint", 0)),
			"hint_string": str(property.get("hint_string", "")),
		})

	return MCPToolkitSuccess.ok({"path": node_path, "script": script_path, "properties": exports})


static func _cmd_node_manage(parameters: Dictionary) -> Dictionary:
	var action := str(parameters.get("action", ""))
	if action.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing action (rename|reparent|reorder|duplicate)")

	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	if node_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path")

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	match action:
		"rename":
			return _manage_rename(root, node, node_path, parameters)
		"reparent":
			return _manage_reparent(root, node, node_path, parameters)
		"reorder":
			return _manage_reorder(root, node, node_path, parameters)
		"duplicate":
			return _manage_duplicate(root, node, node_path, parameters)
		_:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"unknown action '%s'; must be rename|reparent|reorder|duplicate" % action)


static func _manage_rename(
	root: Node, node: Node, node_path: String, parameters: Dictionary,
) -> Dictionary:
	var new_name := str(parameters.get("new_name", ""))
	if new_name.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "rename requires new_name")
	# 没有根节点守卫:重命名场景根是合法的(根名称与场景文件名相互独立),
	# 且 node.set_property 已经能通过 "name" 重命名它 ——
	# 两条路径必须一致。重新挂父/重排/复制保留各自的根守卫:
	# 那些操作对根节点在结构上确实是非法的。

	var old_name := String(node.name)
	node.name = new_name
	MCPToolkitUndoRedoAction.begin("rename %s -> %s" % [old_name, new_name], node) \
		.do_property(node, &"name", new_name) \
		.undo_property(node, &"name", old_name) \
		.commit_recorded()

	var parent := node.get_parent()
	var new_path := str(root.get_path_to(node))
	return MCPToolkitSuccess.ok({"action": "rename", "old_name": old_name,
		"new_name": String(node.name), "new_path": new_path})


static func _manage_reparent(
	root: Node, node: Node, node_path: String, parameters: Dictionary,
) -> Dictionary:
	var new_parent_path := str(parameters.get("new_parent_path", ""))
	new_parent_path = Helpers.normalize_editor_path(new_parent_path)
	if new_parent_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "reparent requires new_parent_path")
	if node == root:
		return MCPToolkitError.fail("INVALID_PATH", "cannot reparent the scene root")

	var new_parent := root.get_node_or_null(new_parent_path)
	if new_parent == null:
		return MCPToolkitError.fail("NOT_FOUND",
			"new parent not found: %s" % new_parent_path, MCPToolkitError.HINT_NODE_PATH)
	# 防止把节点重新挂到它自己下面(会造成环)。
	if new_parent == node or node.is_ancestor_of(new_parent):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"cannot reparent a node under itself or a descendant")

	var keep_global := bool(parameters.get("keep_global_transform", true))
	var old_parent := node.get_parent()
	var old_index := node.get_index()

	node.reparent(new_parent, keep_global)
	node.set_owner(root)
	MCPToolkitUndoRedoAction.begin("reparent %s -> %s" % [node_path, new_parent_path], node) \
		.do_method(node.reparent.bind(new_parent, keep_global)) \
		.do_method(node.set_owner.bind(root)) \
		.undo_method(node.reparent.bind(old_parent, keep_global)) \
		.undo_method(old_parent.move_child.bind(node, old_index)) \
		.undo_method(node.set_owner.bind(root)) \
		.commit_recorded()

	var new_path := str(root.get_path_to(node))
	return MCPToolkitSuccess.ok({"action": "reparent", "new_path": new_path})


static func _manage_reorder(
	root: Node, node: Node, node_path: String, parameters: Dictionary,
) -> Dictionary:
	if not parameters.has("new_index"):
		return MCPToolkitError.fail("INVALID_PARAMS", "reorder requires new_index")
	var new_index := int(parameters.get("new_index", 0))
	if node == root:
		return MCPToolkitError.fail("INVALID_PATH", "cannot reorder the scene root")

	var parent := node.get_parent()
	if parent == null:
		return MCPToolkitError.fail("INTERNAL", "node has no parent")
	var old_index := node.get_index()
	var child_count := parent.get_child_count()
	if new_index < 0 or new_index >= child_count:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"new_index %d out of range [0, %d)" % [new_index, child_count])

	parent.move_child(node, new_index)
	MCPToolkitUndoRedoAction.begin("reorder %s to index %d" % [node_path, new_index], node) \
		.do_method(parent.move_child.bind(node, new_index)) \
		.undo_method(parent.move_child.bind(node, old_index)) \
		.commit_recorded()

	return MCPToolkitSuccess.ok({"action": "reorder", "path": node_path,
		"old_index": old_index, "new_index": node.get_index()})


static func _manage_duplicate(
	root: Node, node: Node, node_path: String, parameters: Dictionary,
) -> Dictionary:
	if node == root:
		return MCPToolkitError.fail("INVALID_PATH", "cannot duplicate the scene root")

	var dup := node.duplicate()
	if dup == null:
		return MCPToolkitError.fail("INTERNAL", "Node.duplicate() returned null for %s" % node_path)

	var new_name := str(parameters.get("new_name", ""))
	if not new_name.is_empty():
		dup.name = new_name

	var parent_path := str(parameters.get("parent_path", ""))
	parent_path = Helpers.normalize_editor_path(parent_path)
	var target_parent: Node
	if parent_path.is_empty():
		target_parent = node.get_parent()
	else:
		target_parent = root.get_node_or_null(parent_path)
		if target_parent == null:
			dup.queue_free()
			return MCPToolkitError.fail("NOT_FOUND",
				"parent not found: %s" % parent_path, MCPToolkitError.HINT_NODE_PATH)

	target_parent.add_child(dup)
	dup.set_owner(root)
	MCPToolkitUndoRedoAction.begin("duplicate %s" % node_path, target_parent) \
		.do_method(target_parent.add_child.bind(dup)) \
		.do_method(dup.set_owner.bind(root)) \
		.do_reference(dup) \
		.undo_method(target_parent.remove_child.bind(dup)) \
		.commit_recorded()

	# 应用可选的属性覆盖(position、scale 等)。
	# 使用 coerce_value_hint,使 {x:200,y:300} 这类无标签字典
	# 能根据属性类型被推断为 Vector2/Vector3/Color。
	var props_raw = parameters.get("properties", null)
	if typeof(props_raw) == TYPE_DICTIONARY:
		for key in (props_raw as Dictionary):
			var prop_name := str(key)
			var existing = dup.get(prop_name)
			var coerced = Coerce.coerce_value_hint(props_raw[key], existing)
			if typeof(coerced) == TYPE_DICTIONARY and (coerced as Dictionary).has("_coerce_error"):
				return MCPToolkitError.fail("INVALID_PARAMS", str(coerced["_coerce_error"]))
			dup.set(prop_name, coerced)

	var dup_path := str(root.get_path_to(dup))
	return MCPToolkitSuccess.ok({"action": "duplicate", "path": dup_path,
		"class": dup.get_class()})


static func _cmd_node_groups(parameters: Dictionary) -> Dictionary:
	var action := str(parameters.get("action", ""))
	if action.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing action (add|remove|list)")

	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	# 批量模式:提供 entries 数组 → 在一个 UndoRedo 动作中处理 N 对 节点+组。
	var entries_raw = parameters.get("entries", null)
	if typeof(entries_raw) == TYPE_ARRAY and (entries_raw as Array).size() > 0:
		if action == "list":
			return MCPToolkitError.fail("INVALID_PARAMS",
				"batch entries not supported with action 'list'; use single mode per node")
		return _batch_node_groups(root, action, entries_raw as Array)

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	if node_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS",
			"missing node_path: required for single-node group operations (add/remove/list); omit it only in batch mode, where each item in entries carries its own node_path.",
			MCPToolkitError.HINT_NODE_PATH)

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	match action:
		"add":
			var group := str(parameters.get("group", ""))
			if group.is_empty():
				return MCPToolkitError.fail("INVALID_PARAMS", "add requires group name")
			var persistent := bool(parameters.get("persistent", true))
			node.add_to_group(group, persistent)
			MCPToolkitUndoRedoAction.begin("add %s to group %s" % [node_path, group], node) \
				.do_method(node.add_to_group.bind(group, persistent)) \
				.undo_method(node.remove_from_group.bind(group)) \
				.commit_recorded()
			return MCPToolkitSuccess.ok({"action": "add", "node": node_path, "group": group})

		"remove":
			var group := str(parameters.get("group", ""))
			if group.is_empty():
				return MCPToolkitError.fail("INVALID_PARAMS", "remove requires group name")
			if not node.is_in_group(group):
				return MCPToolkitError.fail("NOT_FOUND",
					"node %s is not in group '%s'" % [node_path, group])
			node.remove_from_group(group)
			MCPToolkitUndoRedoAction.begin("remove %s from group %s" % [node_path, group], node) \
				.do_method(node.remove_from_group.bind(group)) \
				.undo_method(node.add_to_group.bind(group, true)) \
				.commit_recorded()
			return MCPToolkitSuccess.ok({"action": "remove", "node": node_path, "group": group})

		"list":
			var groups: Array[String] = []
			for g in node.get_groups():
				var gs := str(g)
				if not gs.begins_with("_"):
					groups.append(gs)
			return MCPToolkitSuccess.ok({"action": "list", "node": node_path,
				"groups": groups, "count": groups.size()})

		_:
			return MCPToolkitError.fail("INVALID_PARAMS",
				"unknown action '%s'; must be add|remove|list" % action)


static func _batch_node_groups(root: Node, batch_action: String, entries: Array) -> Dictionary:
	var undo_action = MCPToolkitUndoRedoAction.begin(
		"batch %s groups (%d entries)" % [batch_action, entries.size()], root)

	var results: Array = []
	for entry in entries:
		var e: Dictionary = entry if typeof(entry) == TYPE_DICTIONARY else {}
		var np := str(e.get("node_path", ""))
		np = Helpers.normalize_editor_path(np)
		var group := str(e.get("group", ""))
		if np.is_empty() or group.is_empty():
			results.append({"node_path": np, "group": group, "error": "missing node_path or group"})
			continue
		var node := root.get_node_or_null(np)
		if node == null:
			results.append({"node_path": np, "group": group, "error": "node not found"})
			continue
		match batch_action:
			"add":
				node.add_to_group(group, true)
				undo_action.do_method(node.add_to_group.bind(group, true))
				undo_action.undo_method(node.remove_from_group.bind(group))
				results.append({"node_path": np, "group": group, "status": "added"})
			"remove":
				if not node.is_in_group(group):
					results.append({"node_path": np, "group": group, "error": "not in group"})
					continue
				node.remove_from_group(group)
				undo_action.do_method(node.remove_from_group.bind(group))
				undo_action.undo_method(node.add_to_group.bind(group, true))
				results.append({"node_path": np, "group": group, "status": "removed"})

	undo_action.commit_recorded()

	# 这里的条目携带 {status?, error?},没有 `success` 键 ——
	# summarize_batch 的宽容判定(无 success + error => 失败)会统计它们。
	# 增量式:全成功批次保持相同的 {action, results, count} 形状。
	var response := {"action": batch_action, "results": results, "count": results.size()}
	return MCPToolkitSuccess.ok(Helpers.summarize_batch(response, "results"))


const _LAYOUT_PRESETS := {
	"PRESET_TOP_LEFT": Control.PRESET_TOP_LEFT,
	"PRESET_TOP_RIGHT": Control.PRESET_TOP_RIGHT,
	"PRESET_BOTTOM_LEFT": Control.PRESET_BOTTOM_LEFT,
	"PRESET_BOTTOM_RIGHT": Control.PRESET_BOTTOM_RIGHT,
	"PRESET_CENTER_LEFT": Control.PRESET_CENTER_LEFT,
	"PRESET_CENTER_TOP": Control.PRESET_CENTER_TOP,
	"PRESET_CENTER_RIGHT": Control.PRESET_CENTER_RIGHT,
	"PRESET_CENTER_BOTTOM": Control.PRESET_CENTER_BOTTOM,
	"PRESET_CENTER": Control.PRESET_CENTER,
	"PRESET_LEFT_WIDE": Control.PRESET_LEFT_WIDE,
	"PRESET_TOP_WIDE": Control.PRESET_TOP_WIDE,
	"PRESET_RIGHT_WIDE": Control.PRESET_RIGHT_WIDE,
	"PRESET_BOTTOM_WIDE": Control.PRESET_BOTTOM_WIDE,
	"PRESET_VCENTER_WIDE": Control.PRESET_VCENTER_WIDE,
	"PRESET_HCENTER_WIDE": Control.PRESET_HCENTER_WIDE,
	"PRESET_FULL_RECT": Control.PRESET_FULL_RECT,
}


static func _cmd_control_set_layout(parameters: Dictionary) -> Dictionary:
	var root := _get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)
	var preset_name := str(parameters.get("preset", ""))
	var resize_mode_str := str(parameters.get("resize_mode", "keep_size"))
	var margins_raw = parameters.get("margins", null)

	if node_path.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing node_path")
	if preset_name.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing preset")

	var node := root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % node_path, MCPToolkitError.HINT_NODE_PATH)
	if not (node is Control):
		return MCPToolkitError.fail("INVALID_CLASS",
			"node at %s is %s — control.set_layout requires a Control node" % [
				node_path, node.get_class()])

	var ctrl: Control = node as Control

	if not _LAYOUT_PRESETS.has(preset_name):
		var available := ", ".join(PackedStringArray(_LAYOUT_PRESETS.keys()))
		return MCPToolkitError.fail("INVALID_PARAMS",
			"unknown preset '%s'. Available: %s" % [preset_name, available])

	var preset_enum: int = _LAYOUT_PRESETS[preset_name]
	var mode: int = Control.PRESET_MODE_KEEP_SIZE \
		if resize_mode_str == "keep_size" \
		else Control.PRESET_MODE_MINSIZE

	# 为撤销重做捕获状态
	var old_anchor_left := ctrl.anchor_left
	var old_anchor_top := ctrl.anchor_top
	var old_anchor_right := ctrl.anchor_right
	var old_anchor_bottom := ctrl.anchor_bottom
	var old_offset_left := ctrl.offset_left
	var old_offset_top := ctrl.offset_top
	var old_offset_right := ctrl.offset_right
	var old_offset_bottom := ctrl.offset_bottom

	# 直接应用预设 + 边距,使边距偏移相对于新的锚点位置,
	# 而不是旧的(UndoRedo 会排队 do 方法,因此若排队,
	# 边距值将基于陈旧的偏移计算)。
	ctrl.set_anchors_and_offsets_preset(preset_enum, mode)
	if margins_raw != null and typeof(margins_raw) == TYPE_DICTIONARY:
		if margins_raw.has("left"):
			ctrl.offset_left += float(margins_raw["left"])
		if margins_raw.has("right"):
			ctrl.offset_right += float(margins_raw["right"])
		if margins_raw.has("top"):
			ctrl.offset_top += float(margins_raw["top"])
		if margins_raw.has("bottom"):
			ctrl.offset_bottom += float(margins_raw["bottom"])

	# 使用最终属性值(已应用)记录撤销。
	MCPToolkitUndoRedoAction.begin("control.set_layout %s %s" % [node_path, preset_name], ctrl) \
		.do_property(ctrl, &"anchor_left", ctrl.anchor_left) \
		.do_property(ctrl, &"anchor_top", ctrl.anchor_top) \
		.do_property(ctrl, &"anchor_right", ctrl.anchor_right) \
		.do_property(ctrl, &"anchor_bottom", ctrl.anchor_bottom) \
		.do_property(ctrl, &"offset_left", ctrl.offset_left) \
		.do_property(ctrl, &"offset_top", ctrl.offset_top) \
		.do_property(ctrl, &"offset_right", ctrl.offset_right) \
		.do_property(ctrl, &"offset_bottom", ctrl.offset_bottom) \
		.undo_property(ctrl, &"anchor_left", old_anchor_left) \
		.undo_property(ctrl, &"anchor_top", old_anchor_top) \
		.undo_property(ctrl, &"anchor_right", old_anchor_right) \
		.undo_property(ctrl, &"anchor_bottom", old_anchor_bottom) \
		.undo_property(ctrl, &"offset_left", old_offset_left) \
		.undo_property(ctrl, &"offset_top", old_offset_top) \
		.undo_property(ctrl, &"offset_right", old_offset_right) \
		.undo_property(ctrl, &"offset_bottom", old_offset_bottom) \
		.commit_recorded()

	var response := {
		"path": node_path,
		"preset": preset_name,
		"final_rect": {
			"position": {"x": ctrl.position.x, "y": ctrl.position.y},
			"size": {"x": ctrl.size.x, "y": ctrl.size.y},
		},
	}

	# 若 Control 位于 Container 内则警告
	var parent := ctrl.get_parent()
	if parent != null and parent is Container:
		response["warning"] = (
			"This Control is inside a %s container. " % parent.get_class() +
			"The container will override layout on the next layout pass. " +
			"Consider using size_flags or moving the node outside the container.")

	return MCPToolkitSuccess.ok(response)


static func _cmd_collision_from_sprite(parameters: Dictionary) -> Dictionary:
	var err = MCPToolkitError.require(parameters, ["sprite_path"])
	if err != null:
		return err

	var root := Helpers.get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")

	var sprite_path := str(parameters.get("sprite_path", ""))
	sprite_path = Helpers.normalize_editor_path(sprite_path)
	var node = Helpers.resolve_scene_node(sprite_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "node not found: %s" % sprite_path, MCPToolkitError.HINT_NODE_PATH)

	if not (node is Sprite2D or node is TextureRect):
		return MCPToolkitError.fail("INVALID_CLASS",
			"node at %s is %s — expected Sprite2D or TextureRect" % [sprite_path, node.get_class()])

	var tex = node.get("texture") as Texture2D
	if tex == null:
		return MCPToolkitError.fail("INVALID_PARAMS", "sprite has no texture")

	var img := tex.get_image()
	if img == null:
		return MCPToolkitError.fail("INVALID_PARAMS", "cannot read image data")

	var simplification := float(parameters.get("simplification", 2.0))

	var bitmap := BitMap.new()
	bitmap.create_from_image_alpha(img, 0.1)
	var polygons := bitmap.opaque_to_polygons(
		Rect2(Vector2.ZERO, Vector2(img.get_width(), img.get_height())),
		simplification)

	if polygons.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "no opaque regions found in texture")

	# 解析目标父节点
	var target_parent: Node
	var target_parent_path := str(parameters.get("parent_path", ""))
	if target_parent_path.is_empty():
		target_parent = node.get_parent()
	else:
		target_parent_path = Helpers.normalize_editor_path(target_parent_path)
		target_parent = root.get_node_or_null(target_parent_path)
		if target_parent == null:
			return MCPToolkitError.fail("NOT_FOUND",
				"target parent not found: %s" % target_parent_path, MCPToolkitError.HINT_NODE_PATH)

	var sprite_name := String(node.name)
	var base_name := str(parameters.get("target_name", "%s_collision" % sprite_name))
	var total_points := 0
	var first_path := ""

	var coll_action = MCPToolkitUndoRedoAction.begin("collision from sprite", target_parent)

	for i in range(polygons.size()):
		var coll := CollisionPolygon2D.new()
		if polygons.size() == 1:
			coll.name = base_name
		else:
			coll.name = "%s_%d" % [base_name, i]
		coll.polygon = polygons[i]
		total_points += (polygons[i] as PackedVector2Array).size()

		target_parent.add_child(coll)
		coll.set_owner(root)
		coll_action.do_method(target_parent.add_child.bind(coll))
		coll_action.do_method(coll.set_owner.bind(root))
		coll_action.do_reference(coll)
		coll_action.undo_method(target_parent.remove_child.bind(coll))

		if i == 0:
			first_path = str(root.get_path_to(coll))

	coll_action.commit_recorded()

	return MCPToolkitSuccess.ok({
		"path": first_path,
		"polygon_count": polygons.size(),
		"total_points": total_points,
	})
