@tool
extends RefCounted
## 多个命令处理器共用的辅助函数。
## 消除场景节点解析、类层次
## 检查、文件删除、目录创建与日志级别检测的重复。

## 注意:本文件由 core/modules.gd 预加载,因此不能导入 core/modules.gd
## (循环依赖)。请改为对依赖使用直接 preload。
const Coerce := preload("res://addons/godot_mcp_toolkit/contract/coerce.gd")
const FileGuard := preload("res://addons/godot_mcp_toolkit/security/file_guard.gd")
const VersionUtils := preload("res://addons/godot_mcp_toolkit/versioning/mcp_version_utils.gd")
const PropertySetCheck := preload("res://addons/godot_mcp_toolkit/contract/property_set_check.gd")


# -- 属性强制转换 --------------------------------------------------------------


## 校验并强制转换用于设置节点属性的原始 JSON 值。
## 成功时返回 {"ok": true, "value": <已转换>}。
## 失败时返回 {"ok": false, "code": String, "error": String}。
## 当现有属性值是 NodePath 时,自动把 String 转换为 NodePath。
## 拒绝未知的属性名(不在实例的属性列表中)。
static func coerce_for_property(
	node: Object, property_name: String, raw_value: Variant,
) -> Dictionary:
	if not _has_property(node, property_name):
		return {"ok": false, "code": "PROPERTY_NOT_FOUND",
			"error": "property '%s' not found on %s" % [property_name, node.get_class()]}

	var missing := Coerce.check_resource_paths(raw_value)
	if missing != "":
		return {"ok": false, "code": "LOAD_FAILED",
			"error": "resource not found: %s" % missing}

	var coerced = Coerce.coerce_value(raw_value)

	if typeof(coerced) == TYPE_DICTIONARY \
			and (coerced as Dictionary).has("_coerce_error"):
		return {"ok": false, "code": "INVALID_VALUE",
			"error": str(coerced["_coerce_error"])}

	var old_value = node.get(property_name)
	if typeof(old_value) == TYPE_NODE_PATH and typeof(coerced) == TYPE_STRING:
		coerced = NodePath(str(coerced))

	return {"ok": true, "value": coerced}


## 编译正则 text_filter。返回 [RegEx 或 null, error 或 null, warning]。
## 处理来自上下文协议(MCP)传输层的双重转义元字符。
static func compile_text_filter(parameters: Dictionary) -> Array:
	var text_filter: String = str(parameters.get("text_filter", ""))
	var is_regex: bool = bool(parameters.get("is_regex", false))
	if text_filter == "" or not is_regex:
		return [null, null, ""]
	var regex := RegEx.new()
	if regex.compile("(?i)" + text_filter) != OK:
		var err := MCPToolkitError.fail("INVALID_PARAMS",
			"text_filter is not a valid regex (is_regex=true). "
			+ "To search for literal text, omit is_regex or set it to false. "
			+ "For regex, check for unbalanced groups () [] or unescaped metacharacters.")
		return [null, err, ""]
	var warning := _detect_double_escaped_regex(text_filter)
	return [regex, null, warning]


## 检测可能的双重转义正则元字符。
## 同时检查单层(\\d)与双层(\\\\d)的过度转义。
static func _detect_double_escaped_regex(pattern: String) -> String:
	for letter in ["d", "D", "w", "W", "s", "S", "b", "B"]:
		if pattern.find("\\\\\\\\" + letter) >= 0:
			return (
				"Pattern contains '\\\\\\\\%s' (multiple layers of backslash escaping). "
				+ "The regex metacharacter \\%s is over-escaped — use a POSIX "
				+ "character class instead (e.g. [0-9] for \\d, [a-zA-Z0-9_] for \\w)."
			) % [letter, letter, letter]
		if pattern.find("\\\\" + letter) >= 0:
			return (
				"Pattern contains '\\\\%s' (literal backslash + '%s'). "
				+ "If you meant the regex metacharacter \\%s, your backslash "
				+ "is likely double-escaped. Use a POSIX character class instead "
				+ "(e.g. [0-9] for \\d, [a-zA-Z0-9_] for \\w)."
			) % [letter, letter, letter]
	return ""


## 强制转换并设置节点上的属性,处理复合路径(: 和 /)
## 以及专用 setter API(ShaderMaterial 的 shader_parameter/)。
## 成功时返回 {"ok": true},失败时返回
## {"ok": false, "code": ..., "error": ...}。成功结果包含一个 "_undo" 键,
## 内含调用方注册 UndoRedo 所需的信息(类型、生效路径、旧值、旧资源引用)。
##
## 当 make_unique 为 true 且目标子资源是外部的(.tres)时,
## 会在设置之前自动把它复制为内联副本。
## 这复刻了检查器的 "Make Unique" 行为。
##
## 路径类型:
##   无冒号   — 直接在节点上设置(斜杠路径由 Godot 的 _set 处理)。
##   单冒号   — 转换为 / 以进行节点级覆盖(持久化到 .tscn)。
##   多冒号   — 手动导航子资源;内联可以,外部的会被拒绝,
##              除非设置了 make_unique。
static func set_property_compound(
	node: Object, property_name: String, raw_value: Variant,
	make_unique: bool = false,
) -> Dictionary:
	# --- 强制转换(所有路径共用)---
	var missing := Coerce.check_resource_paths(raw_value)
	if missing != "":
		return {"ok": false, "code": "LOAD_FAILED",
			"error": "resource not found: %s" % missing}
	var coerced = Coerce.coerce_value(raw_value)
	if typeof(coerced) == TYPE_DICTIONARY \
			and (coerced as Dictionary).has("_coerce_error"):
		return {"ok": false, "code": "INVALID_VALUE",
			"error": str(coerced["_coerce_error"])}

	# --- 无冒号:直接在节点上设置 ---
	if ":" not in property_name:
		var _undo_old = node.get(property_name)
		node.set(property_name, coerced)
		var result := _check_set_readback(node, property_name, _undo_old, coerced, property_name)
		if result.get("ok", false):
			result["_undo"] = {"type": "property", "path": property_name, "old": _undo_old}
		else:
			# 丢弃:绑定型 setter(position/modulate)可能已把值清零;
			# 恢复先前的值,使失败不具破坏性。
			node.set(property_name, _undo_old)
		return result

	var parts := property_name.split(":")

	# --- 导航子资源链(所有含冒号的路径)---
	# 单冒号与多冒号都会导航到目标子资源。设置 make_unique 时,
	# 从节点往下把链中的每个外部资源都复制一份 ——
	# 这可以防止意外修改任意层级上共享的 .tres 资源。
	# 在 make_unique 之前捕获原始资源,
	# 用于撤销。
	var _undo_old_resource: Variant = null
	if make_unique and parts.size() >= 2:
		_undo_old_resource = node.get(parts[0])

	var target: Object = node
	var made_unique: Array = []  # 记录哪些资源被复制过。
	for i in range(parts.size() - 1):
		var sub = target.get(parts[i])
		if sub == null or not (sub is Object):
			return {"ok": false, "code": "NOT_FOUND",
				"error": "sub-resource '%s' is null on %s" % [parts[i], node.get_class()]}
		if make_unique and sub is Resource \
				and _is_external_resource(sub as Resource):
			var old_path: String = (sub as Resource).resource_path
			sub = (sub as Resource).duplicate()
			target.set(parts[i], sub)
			made_unique.append({
				"property": parts[i],
				"was": old_path,
				"now": "inline",
			})
		target = sub

	var final_prop := parts[-1]

	# --- 单冒号:先尝试斜杠路径覆盖 ---
	if parts.size() == 2:
		var slash_path := parts[0] + "/" + parts[1]
		var _undo_old_slash = node.get(slash_path)

		# 尝试 1:经由斜杠路径做节点级覆盖。
		# 某些节点类型(MeshInstance3D)会处理斜杠路径的 _set(),
		# 并把值作为节点属性持久化到 .tscn。
		node.set(slash_path, coerced)
		var readback = node.get(slash_path)
		if readback != null:
			var result := _check_set_readback_value(_undo_old_slash, readback, coerced, property_name)
			if not made_unique.is_empty():
				result["made_unique"] = made_unique
			if result.get("ok", false):
				var undo := {"type": "property", "path": slash_path, "old": _undo_old_slash}
				if _undo_old_resource != null and not made_unique.is_empty():
					undo["old_resource_prop"] = parts[0]
					undo["old_resource"] = _undo_old_resource
				result["_undo"] = undo
			return result

	# --- 直接修改子资源 ---
	# 适用于斜杠路径无效时的单冒号,以及所有多冒号。
	# 仅对内联子资源持久化(.tscn 的 [sub_resource] 节)。
	# 外部资源:仅内存中 → 警告。
	var _undo_old_sub = _read_sub_property(target, final_prop)
	_write_sub_property(target, final_prop, coerced)
	var readback = _read_sub_property(target, final_prop)
	var result := _check_set_readback_value(_undo_old_sub, readback, coerced, property_name)
	if not made_unique.is_empty():
		result["made_unique"] = made_unique
	if result.get("ok", false):
		# 子资源直接修改 — 撤销时经辅助函数导航该链。
		var undo := {"type": "sub_resource", "path": property_name,
			"old": _undo_old_sub, "new": coerced}
		if _undo_old_resource != null and not made_unique.is_empty():
			undo["old_resource_prop"] = parts[0]
			undo["old_resource"] = _undo_old_resource
		result["_undo"] = undo
	if result.get("ok", false) \
			and target is Resource \
			and _is_external_resource(target as Resource):
		result["warning"] = (
			"Value was set on a shared external sub-resource in memory. "
			+ "This change may not persist after save/reload. "
			+ "Retry with make_unique: true to auto-duplicate all external "
			+ "resources in the chain as inline copies.")
	return result


## 从节点读取复合路径属性,处理冒号链式路径。
## 成功时返回 {"ok": true, "value": <已序列化>},
## 失败时返回 {"ok": false, "code": ..., "error": ...}。
##
## 对单冒号路径,先尝试节点级覆盖(斜杠转换),
## 再回退到子资源读取(无覆盖时返回资源默认值)。
## 这与检查器行为一致:显示生效值。
static func get_property_compound(node: Object, property_name: String) -> Dictionary:
	# 无冒号 — 直接从节点读取(处理仅斜杠的复合路径)。
	if ":" not in property_name:
		return {"ok": true, "value": Coerce.serialize_value(node.get(property_name))}

	var parts := property_name.split(":")

	# 单冒号:先尝试节点级覆盖,再取子资源默认值。
	if parts.size() == 2:
		var slash_path := parts[0] + "/" + parts[1]
		var value = node.get(slash_path)
		if value != null:
			return {"ok": true, "value": Coerce.serialize_value(value)}
		# 为 null 时回退:导航到子资源并从中读取。
		var sub = node.get(parts[0])
		if sub == null or not (sub is Object):
			return {"ok": false, "code": "NOT_FOUND",
				"error": "sub-resource '%s' is null on %s" % [parts[0], node.get_class()]}
		var fallback_value = _read_sub_property(sub, parts[1])
		return {"ok": true, "value": Coerce.serialize_value(fallback_value)}

	# 多冒号:手动导航子资源。
	var target: Object = node
	var final_prop := parts[-1]
	for i in range(parts.size() - 1):
		var sub = target.get(parts[i])
		if sub == null or not (sub is Object):
			return {"ok": false, "code": "NOT_FOUND",
				"error": "sub-resource '%s' is null on %s" % [parts[i], node.get_class()]}
		target = sub
	var value = _read_sub_property(target, final_prop)
	return {"ok": true, "value": Coerce.serialize_value(value)}


# -- 复合路径辅助函数(私有)--------------------------------------------------


## 从子资源读取属性,必要时使用专用 getter。
static func _read_sub_property(target: Object, prop: String) -> Variant:
	if prop.begins_with("shader_parameter/") and target is ShaderMaterial:
		return (target as ShaderMaterial).get_shader_parameter(
			prop.trim_prefix("shader_parameter/"))
	return target.get(prop)


## 在子资源上写属性,必要时使用专用 setter。
static func _write_sub_property(target: Object, prop: String, value: Variant) -> void:
	if prop.begins_with("shader_parameter/") and target is ShaderMaterial:
		(target as ShaderMaterial).set_shader_parameter(
			prop.trim_prefix("shader_parameter/"), value)
	else:
		target.set(prop, value)


## 检查一个 Resource 是外部存储(独立的 .tres/.res 文件)
## 还是内联(场景或父资源中的内建子资源)。
static func _is_external_resource(res: Resource) -> bool:
	var path := res.resource_path
	if path.is_empty():
		return false
	# 内联子资源的路径形如 "res://scene.tscn::unique_id"。
	if "::" in path:
		return false
	return true


## 通过从目标读回并比较来验证一次 SET 是否成功。
## target + readback_prop 定义从哪里读取设置后的值;before 是
## 设置前的值(属性的类型基准);original_path 用于报错。
static func _check_set_readback(
	target: Object, readback_prop: String, before: Variant,
	coerced: Variant, original_path: String,
) -> Dictionary:
	var after = _read_sub_property(target, readback_prop)
	return _check_set_readback_value(before, after, coerced, original_path)


## 把 [method describe_set_drop] 的三态映射到复合路径调用方期望的
## {ok:…} 结果形状。干净的写入保留历史形态
## {"ok": true, "value": coerced};ADJUSTED 写入附加 "warning"
## (复合路径调用方已会透传 result["warning"]);DROPPED 写入即 SET_FAILED。
static func _check_set_readback_value(
	before: Variant, after: Variant, coerced: Variant, original_path: String,
) -> Dictionary:
	var outcome := describe_set_drop(before, after, coerced, original_path)
	match str(outcome.get("status", "")):
		"dropped":
			return {"ok": false, "code": "SET_FAILED", "error": str(outcome.get("error", ""))}
		"adjusted":
			return {"ok": true, "value": coerced, "warning": str(outcome.get("warning", ""))}
		_:
			return {"ok": true, "value": coerced}


## 属性设置结果分类器 —— 委托给运行时安全的纯叶子
## [code]contract/property_set_check.gd[/code](与运行时 autoload 的
## [code]runtime.set_property[/code] 共享的单一事实来源)。这里保留为
## [code]Helpers.describe_set_drop[/code],让编辑器的节点/场景属性设置
## 路径经由现有的辅助门面访问它,调用点无需改动。
## 返回三态 {"status": "ok" | "adjusted" (+warning) | "dropped" (+error)}。
static func describe_set_drop(
	before: Variant, after: Variant, coerced: Variant, path: String,
) -> Dictionary:
	return PropertySetCheck.describe_set_drop(before, after, coerced, path)


## 检查属性名是否存在于对象实例上。
## 使用 get_property_list(),它涵盖内建、@export 与元数据。
static func _has_property(obj: Object, property_name: String) -> bool:
	for p in obj.get_property_list():
		if p["name"] == property_name:
			return true
	return false


# -- 场景节点解析 --------------------------------------------------------------


static func get_edited_root() -> Node:
	return EditorInterface.get_edited_scene_root()


static func resolve_scene_node(node_path: String) -> Variant:
	var root := get_edited_root()
	if root == null:
		return null
	if node_path.is_empty() or node_path == ".":
		return root
	return root.get_node_or_null(node_path)


## 把运行时风格的 /root/ 路径转换为编辑器相对路径。
## 代理经常在想说 "./Player" 时传入 "/root/Main/Player"。
## 编辑器命令作用于已编辑场景树,其根
## 永远是 "." —— 不存在 /root 节点。/root/ 之后的第一段
## 是运行时场景根名称,会被剥离。
## 当路径带有子节点时("/root/X/Child"),场景名段
## 会被无条件剥离(运行时与编辑器的大小写可能不同)。
## 当路径仅是 "/root/X"(无子节点)时,我们会用当前已编辑
## 场景根名称校验 X(不区分大小写),这样像 "/root/NoSuch"
## 这类明显不存在的路径会原样传递,
## 并由调用方的 get_node_or_null 产生 NOT_FOUND。
static func normalize_editor_path(raw_path: String) -> String:
	if not raw_path.begins_with("/root/") and raw_path != "/root":
		return raw_path

	# 仅 "/root" → "."
	if raw_path == "/root":
		return "."

	# 剥离 "/root/" 前缀 —— 余下部分是 "SceneName" 或 "SceneName/Child/..."
	var after_root := raw_path.substr(6)  # len("/root/") == 6

	var slash_idx := after_root.find("/")
	if slash_idx < 0:
		# "/root/SceneName"(没有更深的子节点)— 对照当前已编辑
		# 场景根校验。若名称不匹配,该路径指向一个不存在的节点;
		# 返回 raw_path,使调用方的
		# get_node_or_null 产生 NOT_FOUND。
		var edited_root := EditorInterface.get_edited_scene_root()
		if edited_root != null and after_root.to_lower() != edited_root.name.to_lower():
			return raw_path
		return "."

	# "/root/SceneName/Child/..." → "./Child/..."
	return "." + after_root.substr(slash_idx)


# -- 类层次检查 ----------------------------------------------------------------


static func class_descends_from(type_name: String, base: String) -> bool:
	if ClassDB.class_exists(type_name):
		return ClassDB.is_parent_class(type_name, base)
	for entry in ProjectSettings.get_global_class_list():
		if str(entry.get("class", "")) == type_name:
			return class_descends_from(str(entry.get("base", "")), base)
	return false


static func class_base_chain(type_name: String) -> String:
	var chain := PackedStringArray()
	var current := type_name
	var depth := 0
	while not current.is_empty() and depth < 16:
		chain.append(current)
		if ClassDB.class_exists(current):
			var parent := ClassDB.get_parent_class(current)
			if parent.is_empty():
				break
			current = parent
		else:
			var found := false
			for entry in ProjectSettings.get_global_class_list():
				if str(entry.get("class", "")) == current:
					current = str(entry.get("base", ""))
					found = true
					break
			if not found:
				break
		depth += 1
	return " -> ".join(chain)


## 把类名解析为它的种类。返回 {"kind": "native"|"global"|"",
## "entry": Dictionary} -- 对全局类,"entry" 是全局类列表行,
## 否则为 {}。调用方自行负责未知类错误 + descends-from 检查。
static func resolve_class_kind(type_name: String) -> Dictionary:
	if ClassDB.class_exists(type_name):
		return {"kind": "native", "entry": {}}
	for entry in ProjectSettings.get_global_class_list():
		if str(entry.get("class", "")) == type_name:
			return {"kind": "global", "entry": entry}
	return {"kind": "", "entry": {}}


# -- 场景标签页管理 ------------------------------------------------------------


## 使用 call_deferred 在编辑器中打开场景,避免延迟队列冲突
## (godotengine/godot#75669)。让出一帧,使编辑器在下一个
## 上下文协议命令执行前完全稳定。
static func open_scene_deferred(file_path: String) -> bool:
	# 绝不在 EditorFileSystem 正在扫描时打开(读取会得到不一致的状态,
	# 且可能崩溃)。扫描空闲等待超时则返回 false,
	# 让调用方中止,而不是与扫描相撞。
	if not await MCPToolkitSafeSceneOps.wait_for_scan_idle():
		return false
	EditorInterface.open_scene_from_path.call_deferred(file_path)
	await (Engine.get_main_loop() as SceneTree).process_frame
	return true


## 尝试关闭 `file_path` 的编辑器标签页。
## 返回 {closed: true, switched: bool} 或 {closed: false, reason: String}。
##
## 成功时,[code]unsaved_changes_discarded: bool[/code] 只在
## Godot 4.7+ 上包含(该版本绑定了脏检查查询);它报告被关闭的
## 标签页是否有未保存的、随关闭一起被丢弃的编辑。该键在 4.5/4.6 上
## 不存在 —— 不存在意味着"无法确定",不同于检测为干净的 [code]false[/code]。
##
## 原因码:
##   "not_open" — 该文件没有打开的编辑器标签页
##   "no_api"   — Godot 4.2–4.4(没有 close_scene 方法)
##
## 在 4.5+ 上,若最后一个标签页被关闭,引擎会自动创建一个空场景,
## 因此不需要 "last_tab" 守卫 —— 调用方永远无需为此担心。
##
## 安全性:至多执行一次 open_scene_from_path + close_scene 循环。
## 绝不循环。打开与关闭都使用 call_deferred,以避免
## 延迟队列冲突(godotengine/godot#75669)。
## 每次延迟调用之后让出一帧,使编辑器在下一个
## 上下文协议命令可以执行之前完全稳定。
##
## 注意:不会恢复之前活动的标签页。关闭非活动标签页后,
## Godot 会自动切换到相邻标签页。通过第三次
## open_scene_from_path 恢复会在引擎中触发一个良性但嘈杂的
## _set_main_scene_state 延迟队列错误 ——
## 不值得刷屏控制台。
static func close_scene_tab_safe(file_path: String) -> Dictionary:
	var open_scenes := EditorInterface.get_open_scenes()
	if not open_scenes.has(file_path):
		return {"closed": false, "reason": "not_open"}

	# 版本门控:close_scene() 需要 4.5+。
	if not EditorInterface.has_method("close_scene"):
		return {"closed": false, "reason": "no_api"}

	var tree := Engine.get_main_loop() as SceneTree

	# 尽力而为的脏状态披露:在切换/关闭改动编辑器状态之前,
	# 捕获该标签页是否有未保存的编辑,以便调用方能警告
	# 关闭会丢弃它们。get_unsaved_scenes() 只在 Godot 4.7 中绑定;
	# 更旧的编辑器不提供脏查询,因此在 4.7 以下它保持缺失,
	# 该字段被直接省略,而不是报告为虚假的"干净"。
	var disclose_unsaved := EditorInterface.has_method("get_unsaved_scenes")
	var unsaved_discarded := false
	if disclose_unsaved:
		# 通过 has_method + call 调用:该方法在 4.7 以下未绑定,
		# 直接的静态引用会在旧编辑器上解析失败。
		var unsaved: PackedStringArray = EditorInterface.call("get_unsaved_scenes")
		unsaved_discarded = file_path in unsaved

	# 若目标不是活动标签页,先激活它。
	# 使用 call_deferred 以避免延迟队列冲突
	# (godotengine/godot#75669 —— 从插件代码直接调用可能崩溃)。
	var current_root := get_edited_root()
	var current_path := current_root.scene_file_path if current_root else ""
	var switched := (current_path != file_path)
	if switched:
		if not await open_scene_deferred(file_path):
			return {"closed": false, "reason": "scan_timeout"}

	# 出于同样的原因,经由 call_deferred 关闭现在活动的标签页。
	EditorInterface.call_deferred("close_scene")
	await tree.process_frame

	var result := {"closed": true, "switched": switched}
	# 只在真正能检测到的版本(4.7+)上呈现该字段;缺失
	# 表示"此处无法确定",不同于检测为干净的标签页。
	if disclose_unsaved:
		result["unsaved_changes_discarded"] = unsaved_discarded
	return result


# -- 文件操作 ------------------------------------------------------------------


## 删除 res:// 文件及其伴生文件(.uid、.import)。
## 清空内存中的 ResourceUID 缓存,以防止陈旧 UID 错误。
## 返回 {success: true, path: String} 或 MCPToolkitError 字典。
static func delete_res_file(file_path: String, companions: Array = [".uid"]) -> Dictionary:
	# 在删除前捕获 UID,以便随后把它从缓存中逐出。
	var uid: int = ResourceLoader.get_resource_uid(file_path)

	var directory := DirAccess.open("res://")
	if directory == null:
		return MCPToolkitError.fail("INTERNAL", "DirAccess.open(res://) returned null")
	var relative_path := file_path.substr("res://".length())
	var remove_error := directory.remove(relative_path)
	if remove_error != OK:
		return MCPToolkitError.fail("DELETE_FAILED",
			"DirAccess.remove returned %d (path=%s)" % [remove_error, file_path])
	for suffix in companions:
		var companion_relative: String = relative_path + str(suffix)
		if directory.file_exists(companion_relative):
			directory.remove(companion_relative)

	# 把 UID 从内存单例中逐出,使引擎不再引用一个已删除的路径。
	# 干净关机时,缓存文件
	# (uid_cache.bin) 会在重写时不含被移除的条目。
	if uid != -1 and ResourceUID.has_id(uid):
		ResourceUID.remove_id(uid)

	return {"success": true, "path": file_path}


## 确保父目录存在,必要时自动创建。
## 返回 {ok: true, dirs_created: bool},失败时返回 MCPToolkitError 字典。
static func ensure_parent_dir(file_path: String, context: String = "") -> Dictionary:
	var parent_dir := file_path.get_base_dir()
	if DirAccess.dir_exists_absolute(parent_dir):
		return {"ok": true, "dirs_created": false}
	var mkdir_err := DirAccess.make_dir_recursive_absolute(parent_dir)
	if mkdir_err != OK:
		return MCPToolkitError.fail("PARENT_NOT_FOUND",
			"parent directory %s does not exist and auto-create failed (err %d); call folder.create manually" % [parent_dir, mkdir_err])
	if not context.is_empty():
		push_warning("[MCPTools] auto-created directory %s for %s" % [parent_dir, context])
	return {"ok": true, "dirs_created": true}


# -- EditorFileSystem 定向更新 ------------------------------------------------


## 定向索引:调用 update_file() 并轮询,直到已索引或超时。
## 若仅靠 update_file() 无法索引该文件则回退到 scan()
## (例如父目录是新的、尚未进入 EditorFileSystem)。
## 返回 {indexed: bool, file_class: String, elapsed_ms: int}。
static func ensure_file_indexed(file_path: String, timeout_ms: int = 3000) -> Dictionary:
	var filesystem := EditorInterface.get_resource_filesystem()
	if filesystem == null:
		return {"indexed": false, "file_class": "", "elapsed_ms": 0}
	filesystem.update_file(file_path)
	var start := Time.get_ticks_msec()
	while filesystem.get_file_type(file_path) == "" and Time.get_ticks_msec() - start < timeout_ms:
		await Engine.get_main_loop().create_timer(0.1).timeout
	if filesystem.get_file_type(file_path) != "":
		var file_class := filesystem.get_file_type(file_path)
		return {"indexed": true, "file_class": file_class, "elapsed_ms": Time.get_ticks_msec() - start}
	# 回退:update_file() 未能索引 —— 父目录可能未知。执行完整扫描。
	filesystem.scan()
	while filesystem.is_scanning() and Time.get_ticks_msec() - start < timeout_ms:
		await Engine.get_main_loop().create_timer(0.1).timeout
	var elapsed := Time.get_ticks_msec() - start
	var file_class := filesystem.get_file_type(file_path)
	var result := {"indexed": file_class != "", "file_class": file_class, "elapsed_ms": elapsed}
	if not result["indexed"]:
		result["hint"] = "indexed is advisory — script_check, resource_load, and scene_open work regardless. Call editor_sync only if asset_query visibility is needed."
	return result


## 定向去索引:对已删除的路径调用 update_file() 并轮询,
## 直到它从索引中移除。若仅靠 update_file() 无法清除该条目
## (目录级原因或引擎怪癖)则回退到 scan()。
## 返回 {removed: bool, elapsed_ms: int}。
static func ensure_file_removed(file_path: String, timeout_ms: int = 3000) -> Dictionary:
	var filesystem := EditorInterface.get_resource_filesystem()
	if filesystem == null:
		return {"removed": false, "elapsed_ms": 0}
	filesystem.update_file(file_path)
	var start := Time.get_ticks_msec()
	while filesystem.get_file_type(file_path) != "" and Time.get_ticks_msec() - start < timeout_ms:
		await Engine.get_main_loop().create_timer(0.1).timeout
	if filesystem.get_file_type(file_path) == "":
		return {"removed": true, "elapsed_ms": Time.get_ticks_msec() - start}
	# 回退:update_file() 未能移除该条目 —— 完整扫描。
	filesystem.scan()
	while filesystem.is_scanning() and Time.get_ticks_msec() - start < timeout_ms:
		await Engine.get_main_loop().create_timer(0.1).timeout
	var elapsed := Time.get_ticks_msec() - start
	var removed := filesystem.get_file_type(file_path) == ""
	return {"removed": removed, "elapsed_ms": elapsed}


## 删除 res:// 文件(+伴生文件)然后定向去索引。成功时返回合并了
## "deindexed": bool 的 delete_res_file 字典;失败时该字典就是
## 未修改的 delete_res_file 错误(没有 "deindexed" 键)。它把
## 文件/场景/资源/脚本删除共用的 删除 -> ensure_file_removed -> 去索引
## 序列折叠进来,使各调用方不再重复成功守卫 + 键写入。
## delete_res_file 不是协程(不 await);ensure_file_removed 轮询与
## 迟到伴生的宽限等待会让出执行。
static func delete_res_file_and_deindex(
	file_path: String, companions: Array = [".uid"],
) -> Dictionary:
	var delete_result: Dictionary = delete_res_file(file_path, companions)
	if delete_result.get("success", false):
		var removal: Dictionary = await ensure_file_removed(file_path)
		delete_result["deindexed"] = removal["removed"]
		# 导入管线是异步的:asset.import 触发的导入可能恰好在上方
		# 删除/去索引之间落盘 .import 旁车(实测竞态 — 旁车在删除
		# 之后才出现,留下指向不存在文件的幽灵导入)。去索引完成后
		# 补扫一次;对携带 .import 伴生的删除,再等一个短宽限期
		# 扫第二次,吸收仍在飞的导入线程。补扫是尽力而为,不改变
		# 删除本身的成败契约。
		var swept := _sweep_late_companions(file_path, companions)
		if companions.has(".import"):
			await Engine.get_main_loop().create_timer(0.6).timeout
			swept += _sweep_late_companions(file_path, companions)
		if swept > 0:
			delete_result["late_companions_removed"] = swept
	return delete_result


## 删除在主删除之后才落盘的迟到伴生文件(.uid/.import 幽灵)。
## 返回实际删除的伴生文件数;失败只记警告,不影响调用方结果。
static func _sweep_late_companions(file_path: String, companions: Array) -> int:
	var directory := DirAccess.open("res://")
	if directory == null:
		return 0
	var relative_path := file_path.substr("res://".length())
	var removed := 0
	for suffix in companions:
		var companion_relative: String = relative_path + str(suffix)
		if directory.file_exists(companion_relative):
			if directory.remove(companion_relative) == OK:
				removed += 1
			else:
				push_warning("[MCPTools] late companion %s could not be removed" % companion_relative)
	return removed


# -- Godot 4.3 SceneTreeEditor 工具提示计时器 UAF 缓解 ------------------------


## 在 Godot 4.3.x 上为 true —— 它是唯一需要解除工具提示计时器的引擎版本线。
## 4.2 同步渲染节点工具提示(没有会被搁置的延迟计时器);4.4+ 会在树重建间
## 缓存 TreeItem(PR #99700),被绑定的行能在变动后存活。
## 纯函数(engine_ver 注入,如 VersionUtils.get_engine_version_pair()),
## 因此确切的仅 4.3 边界可以做无头单元测试。解除操作零成本,
## 所以门控只看版本 —— 没有操作系统维度。
static func should_disarm_tooltip_uaf(engine_ver: String) -> bool:
	return engine_ver == "4.3"


## 在工具集写入 [param node] 的 [param property] 之前,断开 Godot 4.3 的
## SceneTreeEditor 工具提示计时器连接,使该写入无法布下一个释放后使用(UAF)。
## 除非 [param property] 是 [code]editor_description[/code] 且引擎是
## 4.3.x,否则为空操作(见 [method should_disarm_tooltip_uaf])。
## [br]
## 在 4.3 上,设置 [code]editor_description[/code] 会发出
## [code]editor_description_changed[/code];每个已连接的 SceneTreeEditor 会布下一个
## 绑定到节点当前 [code]TreeItem*[/code] 的 0.5 秒一次性 Timer,而并发的
## 树变动随后会运行 [code]tree->clear()[/code](4.4 之前没有 TreeItem 缓存),
## 释放那一行 —— 于是计时器在被释放的内存上触发 → 编辑器 SIGSEGV。
## 在设置之前移除 SceneTreeEditor 的槽位,就没人能布下计时器。
## 无需重连:编辑器会在其下一次树重建时,以新的行绑定重新添加该连接
## 并同步刷新工具提示(scene_tree_editor.cpp:371-376),因此不会丢失
## 任何工具提示。只移除 SceneTreeEditor 的槽位 —— 连接到同一公开
## 信号的用户脚本保持原样。
## [br]
## 在设置之前立即调用,中间不得有 [code]await[/code];一次让出
## 会给树变动可乘之机,重新添加并重新布下一个槽位。之后的人工撤销/重做会在
## 此守卫之外重放该设置,其暴露方式与任何检查器编辑完全相同 —— 不在
## 本处范围内(4.4 上游已修复)。任何写入 editor_description 的新工具或
## 扩展都必须调用它(见 docs/extending.md)。
static func disarm_tooltip_uaf(node: Object, property: String) -> void:
	if property != "editor_description":
		return
	if not should_disarm_tooltip_uaf(VersionUtils.get_engine_version_pair()):
		return
	for conn in node.get_signal_connection_list(&"editor_description_changed"):
		var callable: Callable = conn["callable"]
		var target: Object = callable.get_object()
		if target != null and target.get_class() == "SceneTreeEditor":
			node.disconnect(&"editor_description_changed", callable)


# -- 文件创建冲突决策 ---------------------------------------------------------


## 解析文件创建者(scene.create 与 asset.import)共用的文件级幂等
## 冲突决策。纯查询:校验
## if_exists,检查目标是否存在,只返回决策 —
## 调用方自持其提前返回的负载、自己的写入与自己的状态
## 字符串(`"replaced" if existed else "created"`)。把*决策*
## (而非负载)集中化,使每个创建者发出的字节 —— 它们理应
## 不同(额外的键、替换副作用、消息措辞)—— 仍归调用方控制,
## 同时消灭重复的 校验->存在性->分支 逻辑。
## (纯查询 —— 只返回数据,不做任何改动。)
##
##   dest_path   res:// 目标(此处不做路径把守 —— 调用方先行把守)
##   if_exists   "return" | "fail" | "replace"
##
## 返回一个决策字典:
##   {valid: false}                                — if_exists 不是合法值;
##                                                   调用方自行发出 INVALID_PARAMS。
##   {valid: true, existed: false, action: "create"}
##                                                 — 无冲突;调用方写入,状态 "created"。
##   {valid: true, existed: true,  action: "return"}
##                                                 — 幂等空操作;调用方自行发出
##                                                   "returned" 成功负载。
##   {valid: true, existed: true,  action: "fail"} — 调用方自行发出 ALREADY_EXISTS 错误。
##   {valid: true, existed: true,  action: "replace"}
##                                                 — 调用方执行自己的替换副作用
##                                                   + 写入,状态 "replaced"。
static func resolve_create_collision(dest_path: String, if_exists: String) -> Dictionary:
	if if_exists not in ["return", "fail", "replace"]:
		return {"valid": false}
	var existed := FileAccess.file_exists(dest_path)
	if not existed:
		return {"valid": true, "existed": false, "action": "create"}
	# 冲突:合法的 if_exists 值正是该情形的动作动词。
	return {"valid": true, "existed": true, "action": if_exists}


# -- 批量部分失败汇总 ---------------------------------------------------------


## 把逐条目的 results[] 汇总为顶层部分失败摘要,使只读响应开头的
## 调用方(或 LLM)也能看到部分条目失败了,
## 而不必逐条检查每个条目。
##
## 修改并返回同一个 `response` 字典:当 >=1 个条目失败时,它添加
## `failed`(整数计数)+ `hint`(String);当全部条目成功时,原样返回
## `response`(不添加任何键)—— 因此全成功的批次与之前逐字节一致。
## 每个既有键(results、count、action、warning 等)都保持调用方
## 设置的原样;这里只会添加这两个汇总键。
##
## 失败判定对形状宽容,使一个辅助函数可以同时服务在用的两种批量
## 约定:一个条目是失败,当且仅当它是 Dictionary 且
## (success == false)或(没有 `success` 键但有 `error` 键)。
## 这覆盖了 {success: bool} 形状(node.set_property 批量)与
## 无 success 字段的 {status?, error?} 形状(node.groups 批量)。
##
## 纯函数(不触碰引擎状态)→ 用手工构造的字典做无头单元测试钉住它。
## (共享的响应整形器,与本文件其他纯响应/决策辅助函数并列。)
static func summarize_batch(response: Dictionary, results_key := "results") -> Dictionary:
	var entries: Array = response.get(results_key, [])
	var total := entries.size()
	var failed := 0
	for entry in entries:
		if typeof(entry) != TYPE_DICTIONARY:
			continue
		var e := entry as Dictionary
		if e.get("success") == false or (not e.has("success") and e.has("error")):
			failed += 1
	if failed > 0:
		response["failed"] = failed
		response["hint"] = "%d of %d entries failed — inspect results[] for per-entry .error." % [failed, total]
	return response


# -- 共享的资产写入 + 导入收尾包裹 -------------------------------------------


## 把生成/导入的资产写入 res:// 路径,采用 asset.import 使用的标准契约:
## 路径把守 ->
## 扩展名允许列表 -> if_exists -> 父目录 -> 写入 -> 导入收尾 ->
## 状态/负载。实际写入委托给 `write_fn`,因此每个工具
## 只需提供自己的保存调用(原始字节 / Image.save_png / save_to_wav)。
##
##   dest_path        res:// 目标(此处做路径把守)
##   allowed_exts     本工具可写的小写扩展名(如 ["png"])
##   if_exists        "return"(幂等空操作)| "fail" | "replace"
##   wait_for_scan_ms 导入收尾超时,[0, 30000](0 表示禁用等待)
##   method           处理器名称,用于错误/目录上下文
##   write_fn         Callable(dest_path: String) -> Dictionary
##                    成功时 {},失败时为 MCPToolkitError.fail(...) 字典。
##   known_class      可选。非空时,调用方保证所存
##                    资产的类(例如写了 PNG 的生成器知道它是
##                    Texture2D)。此时直接报告该类,并跳过
##                    阻塞的导入收尾轮询(仍会执行一次
##                    非阻塞的 update_file(),让文件系统停靠面板跟上)。
##                    asset.import 传入 "",因为导入的类型在编辑器执行导入前
##                    是未知的。
##
## 返回核心成功负载
## {status, path, class, warnings, elapsed_ms, success:true}(status 为
## "created" | "replaced" | "returned"),或 MCPToolkitError 字典
## (PATH_DENIED / INVALID_PATH / INVALID_PARAMS / ALREADY_EXISTS / PARENT_NOT_FOUND /
## WRITE_FAILED)。成功时(result.success == true)调用方把工具特有字段
## 合并进返回的字典。
static func write_asset_with_settle(
	dest_path: String,
	allowed_exts: PackedStringArray,
	if_exists: String,
	wait_for_scan_ms: int,
	method: String,
	write_fn: Callable,
	known_class: String = "",
) -> Dictionary:
	var guard := FileGuard.resolve_safe(dest_path)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))

	var extension := dest_path.get_extension().to_lower()
	if not allowed_exts.has(extension):
		return MCPToolkitError.fail("INVALID_PATH",
			"extension '%s' not allowed for %s; expected: %s" % [
				extension, method, ", ".join(allowed_exts)])

	var collision := resolve_create_collision(dest_path, if_exists)
	if not collision["valid"]:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"if_exists must be one of 'return', 'fail', 'replace' (got '%s')" % if_exists)
	if wait_for_scan_ms < 0 or wait_for_scan_ms > 30000:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"wait_for_scan_ms must be in [0, 30000] (got %d); 0 disables wait" % wait_for_scan_ms)

	var file_existed: bool = collision["existed"]
	if file_existed:
		match collision["action"]:
			"return":
				return MCPToolkitSuccess.ok({
					"status": "returned", "path": dest_path,
					"class": known_class if known_class != "" else _file_class_or_null(dest_path), "warnings": [], "elapsed_ms": 0})
			"fail":
				return MCPToolkitError.fail("ALREADY_EXISTS",
					"file already exists at %s; use if_exists:'replace' to overwrite or if_exists:'return' for idempotent no-op" % dest_path)
			"replace":
				pass

	var dir_result := ensure_parent_dir(dest_path, method)
	if dir_result.has("error"):
		return dir_result

	var write_result: Variant = write_fn.call(dest_path)
	if typeof(write_result) == TYPE_DICTIONARY and (write_result as Dictionary).has("error"):
		return write_result

	var warnings: Array[String] = []
	var file_class: Variant = null
	var elapsed_ms := 0

	if known_class != "" and wait_for_scan_ms <= 0:
		# 生成器的默认路径:类由构造即知,因此完全跳过阻塞的
		# 导入收尾轮询。触发一次非阻塞的 update_file(),
		# 让文件系统停靠面板跟上;资产无论如何都可用(resource_load
		# 按需导入)。没有"未能索引"警告 —— 我们从未等待。
		var fs := EditorInterface.get_resource_filesystem()
		if fs != null:
			fs.update_file(dest_path)
		file_class = known_class
	else:
		# asset.import(在编辑器执行导入前类型未知 → 必须经文件系统收尾),
		# 或是明确选择等待的生成器(wait_for_scan_ms > 0)。
		var index_result := await ensure_file_indexed(dest_path, wait_for_scan_ms)
		elapsed_ms = int(index_result["elapsed_ms"])
		if not index_result["indexed"]:
			warnings.append(
				"EditorFileSystem did not index %s within %dms — call editor.wait_for_idle to finish" % [dest_path, wait_for_scan_ms])
		if known_class != "":
			file_class = known_class  # 生成器:构造出的类是权威的
		elif str(index_result["file_class"]) != "":
			file_class = index_result["file_class"]

	var status := "replaced" if file_existed else "created"
	return MCPToolkitSuccess.ok({
		"status": status,
		"path": dest_path,
		"class": file_class,
		"warnings": warnings,
		"elapsed_ms": elapsed_ms,
	})


## 已索引文件的 EditorFileSystem 类名,未知时为 null。
static func _file_class_or_null(file_path: String) -> Variant:
	var filesystem := EditorInterface.get_resource_filesystem()
	if filesystem == null:
		return null
	var file_type := filesystem.get_file_type(file_path)
	return file_type if file_type != "" else null
