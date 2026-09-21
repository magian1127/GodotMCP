@tool
extends Node
## UndoRedo 的应用/回滚方法,在撤销/重做时按名称调用 — 可能发生在发起命令
## 返回之后很久。
##
## 它们位于一个由服务器持有的常驻 Node 上(经 server.undo_helpers 访问),
## 因为 EditorUndoRedoManager.add_do_method / add_undo_method 接受
## (object, method) 对,并按该对象的编辑器上下文路由撤销 — 这与接受
## Callable 的基础 UndoRedo 类不同。MCPToolkitUndoRedoAction 把每个 Callable
## 拆解为 (object, method) 以喂给编辑器管理器形态,因此被绑定的对象必须是
## 一个真实的、有名字的方法宿主。由于调用是延迟执行的,该宿主必须比命令
## 活得更久 — 这就是需要这个专用 Node 的原因。
##
## 这不是 Callable 出现之前的权宜之计。不要把这些方法内联到命令文件里,
## 也不要把它们绑定在临时/瞬态实例上:宿主被释放就意味着撤销失效。


func _write_file_silent(path: String, content: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_warning("[MCPServer] UndoRedo write of %s failed (err %d)" % [path, FileAccess.get_open_error()])
		return
	file.store_string(content)
	file.close()


func _delete_file_silent(path: String) -> void:
	if not FileAccess.file_exists(path):
		return
	var error := DirAccess.remove_absolute(path)
	if error != OK:
		push_warning("[MCPServer] UndoRedo delete of %s failed (err %d)" % [path, error])


func _set_owner_recursive(node: Node, owner: Node) -> void:
	node.set_owner(owner)
	for child in node.get_children():
		_set_owner_recursive(child, owner)


func _animation_remove_key_at(animation: Animation, track_index: int, time: float) -> void:
	var key_index := animation.track_find_key(track_index, time, Animation.FIND_MODE_EXACT)
	if key_index != -1:
		animation.track_remove_key(track_index, key_index)


func _animation_insert_key_silent(animation: Animation, track_index: int, time: float, value) -> void:
	animation.track_insert_key(track_index, time, value)


func _sm_remove_transition_by_endpoints(sm: AnimationNodeStateMachine, from: String, to: String) -> void:
	for i in range(sm.get_transition_count()):
		if str(sm.get_transition_from(i)) == from and str(sm.get_transition_to(i)) == to:
			sm.remove_transition_by_index(i)
			return


func _tilemap_apply_batch(node: Node, layer: int, cells: Array) -> void:
	var is_layer := node.is_class("TileMapLayer")  # 动态判断 — 避免 < 4.3 上的解析错误
	for cell in cells:
		var coord := Vector2i(int(cell["x"]), int(cell["y"]))
		var source_id := int(cell["source_id"])
		var atlas := Vector2i(int(cell["atlas_x"]), int(cell["atlas_y"]))
		var alternative := int(cell.get("alternative_tile", 0))
		if is_layer:
			node.set_cell(coord, source_id, atlas, alternative)
		else:
			(node as TileMap).set_cell(layer, coord, source_id, atlas, alternative)


## 在节点上设置复合路径属性(冒号链或斜杠路径)。
## 由 UndoRedo 用于复合路径的撤销/重做 — 以与 set_property_compound 相同的
## 方式导航子资源,但不做强制转换或回读。
func compound_set(node: Object, property_name: String, value: Variant) -> void:
	if ":" not in property_name:
		node.set(property_name, value)
		return
	var parts := property_name.split(":")
		# 单冒号:先尝试斜杠路径。
	if parts.size() == 2:
		var slash_path := parts[0] + "/" + parts[1]
		node.set(slash_path, value)
		if node.get(slash_path) != null:
			return
	# 导航到子资源并直接设置。
	var target: Object = node
	for i in range(parts.size() - 1):
		var sub = target.get(parts[i])
		if sub == null or not (sub is Object):
			return
		target = sub
	var final_prop := parts[-1]
	if final_prop.begins_with("shader_parameter/") and target is ShaderMaterial:
		(target as ShaderMaterial).set_shader_parameter(
			final_prop.trim_prefix("shader_parameter/"), value)
	else:
		target.set(final_prop, value)


func _tilemap_restore_batch(node: Node, layer: int, before_state: Array) -> void:
	var is_layer := node.is_class("TileMapLayer")  # 动态判断 — 避免 < 4.3 上的解析错误
	for state in before_state:
		var coord: Vector2i = state["coord"]
		var source_id := int(state["source_id"])
		var atlas: Vector2i = state["atlas"]
		var alternative := int(state["alternative_tile"])
		if is_layer:
			node.set_cell(coord, source_id, atlas, alternative)
		else:
			(node as TileMap).set_cell(layer, coord, source_id, atlas, alternative)
