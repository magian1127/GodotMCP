@tool
extends RefCounted
## 跟踪活跃的扩展集合,并使其与编辑器文件系统/设置变更保持一致 ——
## 即热重载聚合体(aggregate)。
##
## 在启动发现交接之后,这个持久实例会响应
## EditorFileSystem.filesystem_changed / ProjectSettings.settings_changed:
## 在每次防抖(500ms)扫描中,它会把全局类列表与已知状态做差异比对
## (新增 / 移除 / 修改 / 重试先前失败的),把增量应用到活跃注册表,
## 并向所有已连接的上下文协议(MCP)桥接广播 "extensions.changed" 通知。
## 它还提供 extensions.refresh(调用方强制的重新扫描),
## 在 setup() 中以其自身的 cmd_refresh 注册。
##
## 下面六个状态字典是这个聚合体的内部结构,在一次 _do_rescan 中始终保持
## 彼此一致(被移除的类会以同步步进的方式从所有字典中擦除)。
## 本模块之外的任何代码都不该碰它们。

const _Support := preload("res://addons/godot_mcp_toolkit/extensions/services/extension_support.gd")
const _MetaCommands := preload("res://addons/godot_mcp_toolkit/extensions/services/extension_meta_commands.gd")

# 保留热重载期间加载的 C# 扩展实例的引用,防止 GC 令已注册的 Callable 失效
# (启动流程通过注册表元数据保留它自己的那一份 ——
# 见 extension_discovery.gd)。
var _instances: Array = []
var _registry: MCPToolkitCommandRegistry = null
var _server: Node = null
var _known_extensions: Dictionary = {}      # class_name_str -> script_path
var _class_methods: Dictionary = {}         # class_name_str -> Array[String](方法)
var _class_metadata: Dictionary = {}        # class_name_str -> Dictionary(方法 -> str(meta))
var _failed_classes: Dictionary = {}        # class_name_str -> true(校验失败,扫描时重试)
var _debounce_pending := false

# class_name_str -> 扩展源文件在上一次被加载进活跃注册表那一刻的哈希值。
# 仅在 Godot 4.2 上使用(ONLY),用于检测对现有扩展的会话内编辑 ——
# 4.2 的缓存路径(CACHE_MODE_REUSE)无法实时应用这种编辑,
# 从而让 extensions.refresh 能提示(nudge)调用方重启编辑器。
var _class_source_fingerprint: Dictionary = {}
# class_name_str -> script_path:针对源文件在磁盘上已变化、但在 Godot 4.2 上
# 无法于会话内重新应用(需要重启)的扩展。由 cmd_refresh 排空,
# 并转化为一条响应提示(hint)。
var _pending_restart_modifies: Dictionary = {}


## 把本监视器接到活跃注册表 + 服务器上,并使其就绪:快照当前的扩展类,
## 重建 类->方法 映射,连接 EditorFileSystem 与 ProjectSettings 信号,
## 并把 extensions.refresh 注册到 cmd_refresh 上。
## 调用方必须(MUST)保留本实例(防止 GC 回收;它拥有活跃状态,以及
## 组合器稍后要断开的那些信号连接)。
func setup(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	_registry = registry
	_server = server
	# 快照当前的扩展类。
	_snapshot_current_extensions()
	# 从已注册的扩展方法构建 类->方法 映射。
	_rebuild_class_methods_map()
	# 连接到 EditorFileSystem。
	var efs := EditorInterface.get_resource_filesystem()
	efs.filesystem_changed.connect(on_filesystem_changed)
	# 项目设置变化时也重新扫描(借此捕捉插件的启用/禁用切换)。
	ProjectSettings.settings_changed.connect(on_settings_changed)
	# 注册 extensions.refresh —— 允许 LLM / 无头(headless)模式在
	# 没有编辑器焦点的情况下强制执行文件系统扫描 + 扩展重新发现。
	registry.add("extensions.refresh", func(params: Dictionary) -> Dictionary:
		return await cmd_refresh(params)
	, MCPToolkitCommandOptions.new()
		.with_description("Force a filesystem scan and re-discover extensions (use when files were created externally)")
		.mark_idempotent()
		.mark_scene_independent())
	print("[MCPExtensions] Hot-reload watcher active")


func _snapshot_current_extensions() -> void:
	_known_extensions.clear()
	var classes: Array = ProjectSettings.get_global_class_list()
	for entry in classes:
		if not _Support.is_extension_candidate(entry):
			continue
		if not _Support.is_addon_enabled(entry.get("path", "")):
			continue
		_known_extensions[entry.get("class", "")] = entry.get("path", "")


func _rebuild_class_methods_map() -> void:
	## 从注册表的扩展方法构建 类名 -> 方法 + 元数据 映射。
	## 在监视器启动时调用一次。实时重载期间,新方法会在
	## _load_extension_tracked() 中被增量跟踪。
	_class_methods.clear()
	_class_metadata.clear()
	_class_source_fingerprint.clear()
	var all_ext_methods := _registry.get_extension_methods()
	# 事后无法把方法完美地归因到具体的类,因此我们重新扫描:
	# 对每个已知类,加载其脚本并在一个临时注册表上调用 Register,
	# 以捕获它添加了哪些方法。
	for cn: String in _known_extensions:
		var sp: String = _known_extensions[cn]
		# 为磁盘上的源指纹建立基线,使之后的会话内编辑可被检测到
		# (驱动 Godot 4.2 的重启提示;在 4.3+ 上无副作用)。
		_class_source_fingerprint[cn] = _Support.hash_extension_source(sp)
		var probe := _Support.probe_extension(cn, sp, _server)
		var methods: Array = probe["methods"]
		if not methods.is_empty():
			_class_methods[cn] = methods
			_class_metadata[cn] = probe["metadata"]


## 强制执行一次文件系统扫描并立即重新发现扩展。在 setup() 中被注册为
## extensions.refresh 处理器(handler)—— 它有副作用(驱动一次重新扫描)
## 并返回刷新后的列表,是一个已被认可的 CQS 例外(为调用方省一次往返)。
func cmd_refresh(_params: Dictionary) -> Dictionary:
	## 使用 scan()(而非 scan_sources()),这样才能发现新文件 ——
	## scan_sources() 只会重新检查已知的资源。
	var efs := EditorInterface.get_resource_filesystem()

	if _Support.is_godot_42():
		# Godot 4.2:使用更轻量的 is_scanning() 等待。下方 4.3+ 的信号等待
		# 一方面 (a) 把 await 窗口拉得足够宽,以至于编辑器对 @tool 的重新导入
		# 会与本扩展的(重)加载发生竞态,从而在 4.2 上原生崩溃;
		# 另一方面 (b) 它的有界安定(settle)等待对 4.2 的内存中类列表刷新
		# 并不可靠。经验证,这种 scan + is_scanning() 等待能在 4.2 上立即发现新扩展。
		efs.scan()
		var deadline := Time.get_ticks_msec() + 5000
		while efs.is_scanning() and Time.get_ticks_msec() < deadline:
			await _server.get_tree().create_timer(0.1).timeout
	else:
		# Godot 4.3+:在扫描之前布好全局类列表刷新屏障(global-class-flush barrier)。
		# 仅靠 is_scanning() 并不是安全的屏障:EditorFileSystem 在其工作线程上,
		# 于文件系统遍历的末尾(END)就清除 `scanning` —— 早于主线程刷新
		# 全局类列表(store_global_class_list,位于 _update_script_classes 内)
		# 并发出 script_classes_updated 信号。因此,仅以 is_scanning() 为门槛的
		# 重新扫描会对一份过时的(STALE)ProjectSettings.get_global_class_list()
		# 做差异比对,漏掉新的 class_name —— 表现为刚添加的扩展在刷新时返回 "commands:[]"。
		var classes_flushed := [false]
		var on_flush := func() -> void:
			classes_flushed[0] = true
		var has_flush_signal := efs.has_signal("script_classes_updated")
		if has_flush_signal:
			efs.script_classes_updated.connect(on_flush)
		efs.scan()
		# 阶段 1 —— 通过规范的扫描空闲防护(scan-idle guard)等待文件系统遍历结束。
		await MCPToolkitSafeSceneOps.wait_for_scan_idle()
		# 阶段 2 —— 等待主线程完成类列表刷新,有界。无变更的扫描从不发出
		# script_classes_updated,因此短暂安定(settle)后即继续,而不是挂起;
		# 该标志(扫描前布好)也能捕获阶段 1 期间发生的刷新。
		if has_flush_signal:
			var settle := Time.get_ticks_msec() + 750
			while not classes_flushed[0] and Time.get_ticks_msec() < settle:
				await _server.get_tree().create_timer(0.05).timeout
			if efs.script_classes_updated.is_connected(on_flush):
				efs.script_classes_updated.disconnect(on_flush)

	# 在这份已刷新的类列表上执行重新扫描(绕过防抖)。
	_debounce_pending = false
	_do_rescan()
	# 返回当前扩展列表及其完整元数据(与 extensions.list 和
	# extensions.changed 相同的形状 —— input_schema、annotations 等)。
	var methods := _registry.get_extension_methods()
	var result: Array[Dictionary] = []
	var grouped_keywords: PackedStringArray = []
	for method: String in methods:
		var entry := _MetaCommands.build_command_entry(_registry, method)
		# 为激活提示(hint)收集关键字(在共享线上条目上做的一次仅限 refresh 的
		# 后处理步骤 —— 从构建器产出的条目中回读)。
		for kw in entry.get("group", {}).get("keywords", []):
			if str(kw) not in grouped_keywords:
				grouped_keywords.append(str(kw))
		result.append(entry)
	var response := {"success": true, "refreshed": true, "commands": result}
	if not grouped_keywords.is_empty():
		response["hint"] = (
			"Some extension tools are in on-demand groups and need activation "
			+ "before use. Call discover_tools(request: '%s') to load them."
		) % ", ".join(grouped_keywords)
	# Godot 4.2:把无法实时应用的会话内编辑呈现出来(它们更新后的工具
	# 不在上面的 `commands` 中),让调用方知道需要重启编辑器。
	if not _pending_restart_modifies.is_empty():
		var modified_names: PackedStringArray = []
		for cn: String in _pending_restart_modifies:
			modified_names.append(cn)
		var restart_hint := (
			"Godot 4.2: extension(s) [%s] were modified on disk but in-session changes "
			+ "can't be applied — restart the editor to load the updated version. "
			+ "(New/removed extensions apply live; Godot 4.3+ applies modifications live too.)"
		) % ", ".join(modified_names)
		response["hint"] = (str(response["hint"]) + " " + restart_hint) if response.has("hint") else restart_hint
	return response


func on_filesystem_changed() -> void:
	_schedule_rescan()


func on_settings_changed() -> void:
	# 如果工具包本身正在被禁用,则不要重新扫描 ——
	# 避免拆解(teardown)期间的竞态条件。
	if not EditorInterface.is_plugin_enabled("godot_mcp_toolkit"):
		return
	_schedule_rescan()


func _schedule_rescan() -> void:
	if _debounce_pending:
		return
	_debounce_pending = true
	# 使用 SceneTree 计时器做防抖(500ms)。监视器是一个 RefCounted,
	# 无法直接持有计时器 —— 由服务器节点所在的树来提供。
	_server.get_tree().create_timer(0.5).timeout.connect(_do_rescan)


## 纯集合差异内核:把新扫描到的类集合,对照监视器的已知 + 先前失败状态
## 进行分类;不加载任何脚本、不触碰任何全局状态 —— 每个输入都以参数传入,
## 因此可在无头(headless)模式下做单元测试。返回:
## {"added": Dictionary, "removed": Array[String], "retry": Dictionary}:
##   added   = 在 `current` 中但不在 `known` 中的类           (class_name -> 路径)
##   removed = 在 `known` 中但不在 `current` 中的类           (class_name 列表)
##   retry   = 先前失败、现在仍存在、且并非新增的类。
## `retry` 是对照 `added` 计算出来的,因此下方先构建 `added`。
static func compute_class_diff(current: Dictionary, known: Dictionary, failed: Dictionary) -> Dictionary:
	var added: Dictionary = {}     # class_name -> 路径
	var removed: Array[String] = []
	for cn: String in current:
		if cn not in known:
			added[cn] = current[cn]
	for cn: String in known:
		if cn not in current:
			removed.append(cn)

	# 重试先前失败的类(脚本自上次扫描后已被修复)。
	var retry: Dictionary = {}
	for cn: String in failed:
		if cn in current and cn not in added:
			retry[cn] = current[cn]

	return {"added": added, "removed": removed, "retry": retry}


## 计算阶段:扫描活跃类列表并对照已知状态分类,不修改(mutate)注册表
## 或监视器的跟踪字典(唯一的例外是 _pending_restart_modifies ——
## 4.2 重启提示台账,它是探测的产物,不是注册表状态)。
## 返回一个携带 _apply_delta 所需全部信息的增量字典:
## {current, added, removed, retry, modified}。
func _compute_delta() -> Dictionary:
	var current: Dictionary = {}
	var classes: Array = ProjectSettings.get_global_class_list()
	for entry in classes:
		if not _Support.is_extension_candidate(entry):
			continue
		if not _Support.is_addon_enabled(entry.get("path", "")):
			continue
		current[entry.get("class", "")] = entry.get("path", "")

	# 对照已知状态做纯 新增/移除/重试 差异比对。
	var diff := compute_class_diff(current, _known_extensions, _failed_classes)
	var added_classes: Dictionary = diff["added"]
	var removed_classes: Array[String] = diff["removed"]
	var retry_classes: Dictionary = diff["retry"]

	# 检测现有扩展的内容变化(同一个类内工具被新增/移除/修改)。
	# 重新探测每个已知类,并同时比较方法列表与元数据
	# (annotations、description、schema、timeout)。
	var modified_classes: Dictionary = {}  # class_name -> 路径
	for cn: String in current:
		if cn in added_classes or cn in retry_classes:
			continue
		if not _class_methods.has(cn):
			continue
		var sp: String = current[cn]
		var probe := _Support.probe_extension(cn, sp, _server)
		var fresh_methods: Array = probe["methods"]
		var old_methods: Array = _class_methods.get(cn, [])
		var fresh_meta: Dictionary = probe["metadata"]
		var old_meta: Dictionary = _class_metadata.get(cn, {})
		if not _Support.arrays_equal(fresh_methods, old_methods) or fresh_meta != old_meta:
			modified_classes[cn] = sp

	# 仅限 Godot 4.2:检测对现有扩展的会话内编辑 —— 4.2 的缓存路径
	# (CACHE_MODE_REUSE)无法实时应用它 —— 上面的修改检测探测读到的
	# 是缓存中编辑前的脚本,因此这种变化在那里不可见。改为比较磁盘上的
	# 源指纹,并排队一个一次性的重启提示(nudge)。
	if _Support.is_godot_42():
		for cn: String in current:
			if cn in added_classes or cn in retry_classes or not _class_source_fingerprint.has(cn):
				continue
			if _Support.hash_extension_source(current[cn]) != _class_source_fingerprint[cn]:
				if cn not in _pending_restart_modifies:
					push_warning("[MCPExtensions] '%s' was edited but in-session changes can't be applied on Godot 4.2 - restart the editor to load the updated version (Godot 4.3+ applies edits live)." % cn)
				_pending_restart_modifies[cn] = current[cn]
			else:
				# 指纹再次与已加载版本一致(例如编辑被撤销)。
				_pending_restart_modifies.erase(cn)

	return {
		"current": current,
		"added": added_classes,
		"removed": removed_classes,
		"retry": retry_classes,
		"modified": modified_classes,
	}


## 应用阶段:修改活跃注册表与监视器跟踪字典,使之与计算出的增量一致 ——
## 注销被移除/修改的方法,(重)加载新增/重试/修改的类,
## 采纳新的已知集合,然后广播并记录该变更。
func _apply_delta(delta: Dictionary) -> void:
	var current: Dictionary = delta["current"]
	var added_classes: Dictionary = delta["added"]
	var removed_classes: Array[String] = delta["removed"]
	var retry_classes: Dictionary = delta["retry"]
	var modified_classes: Dictionary = delta["modified"]

	# 在修改状态之前收集被移除的方法名。
	var removed_methods: Array[String] = []
	for cn: String in removed_classes:
		if _class_methods.has(cn):
			removed_methods.append_array(_class_methods[cn])
			_class_methods.erase(cn)
		_class_metadata.erase(cn)
		_failed_classes.erase(cn)
		_class_source_fingerprint.erase(cn)
		_pending_restart_modifies.erase(cn)

	# 处理被修改的扩展:先注销旧方法,再重新加载新版本。
	for cn: String in modified_classes:
		if _class_methods.has(cn):
			for method: String in _class_methods[cn]:
				_registry.remove(method)
				print("[MCPExtensions]   ~ %s (modified, re-registering)" % method)
			removed_methods.append_array(_class_methods[cn])
			_class_methods.erase(cn)
		_class_metadata.erase(cn)

	# 从活跃注册表中注销被移除的方法。
	for method: String in removed_methods:
		if _registry.has_command(method):
			_registry.remove(method)
			print("[MCPExtensions]   - %s (removed)" % method)

	# 注册新扩展。
	for cn: String in added_classes:
		_load_extension_tracked(cn, added_classes[cn])

	# 重试先前失败的类。
	for cn: String in retry_classes:
		_load_extension_tracked(cn, retry_classes[cn])

	# 重新加载被修改的扩展。
	for cn: String in modified_classes:
		_load_extension_tracked(cn, modified_classes[cn])

	# 更新已知状态。
	_known_extensions = current

	# 若有任何变化则广播。
	var total_changes := removed_methods.size()
	for cn: String in added_classes:
		if _class_methods.has(cn):
			total_changes += _class_methods[cn].size()
	for cn: String in retry_classes:
		if _class_methods.has(cn):
			total_changes += _class_methods[cn].size()
	for cn: String in modified_classes:
		if _class_methods.has(cn):
			total_changes += _class_methods[cn].size()

	if total_changes > 0:
		_broadcast_extensions_changed(removed_methods)
		var parts: Array[String] = []
		var add_count := 0
		for cn: String in added_classes:
			if _class_methods.has(cn):
				add_count += 1
		for cn: String in retry_classes:
			if _class_methods.has(cn):
				add_count += 1
		if add_count > 0:
			parts.append("+%d" % add_count)
		if not modified_classes.is_empty():
			parts.append("~%d" % modified_classes.size())
		if not removed_classes.is_empty():
			parts.append("-%d" % removed_classes.size())
		if not parts.is_empty():
			print("[MCPExtensions] Hot-reload: %s class(es) changed" % " ".join(parts))


func _do_rescan() -> void:
	_debounce_pending = false
	var delta := _compute_delta()
	# 没有任何变化 —— 完全跳过应用阶段(不修改注册表,不广播)。
	# 等价于拆分前的提前返回。
	if delta["added"].is_empty() and delta["removed"].is_empty() \
			and delta["retry"].is_empty() and delta["modified"].is_empty():
		return
	_apply_delta(delta)


func _load_extension_tracked(class_name_str: String, script_path: String) -> void:
	## 加载并注册单个扩展,同时跟踪其方法与元数据。
	var before: Array = _registry.get_all_methods()
	var instance := _Support.load_extension(class_name_str, script_path, _registry, _server)
	if instance != null:
		# 保留实例(C# GC 安全)—— load_extension 不再持有它。
		_instances.append(instance)
		var after: Array = _registry.get_all_methods()
		var new_methods: Array[String] = []
		var new_metadata: Dictionary = {}
		for method: String in after:
			if method not in before:
				new_methods.append(method)
				new_metadata[method] = str(_registry.get_command_metadata(method))
		_class_methods[class_name_str] = new_methods
		_class_metadata[class_name_str] = new_metadata
		_failed_classes.erase(class_name_str)
		# 记录刚加载版本的源指纹,并清除任何待处理的重启提示
		# (这个版本现在是生效版本)。
		_class_source_fingerprint[class_name_str] = _Support.hash_extension_source(script_path)
		_pending_restart_modifies.erase(class_name_str)
	else:
		_failed_classes[class_name_str] = true


func _broadcast_extensions_changed(removed_methods: Array[String]) -> void:
	## 构建 extensions.changed 通知载荷(与 extensions.list 响应相同的形状,
	## 外加 removed 数组),并广播给所有对端(peer)。
	var commands: Array[Dictionary] = []
	var methods := _registry.get_extension_methods()
	for method: String in methods:
		commands.append(_MetaCommands.build_command_entry(_registry, method))
	var params := {"commands": commands, "removed": removed_methods}
	_server.broadcast_notification("extensions.changed", params)
