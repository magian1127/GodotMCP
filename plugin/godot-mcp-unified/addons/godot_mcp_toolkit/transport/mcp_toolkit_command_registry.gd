@tool
class_name MCPToolkitCommandRegistry
extends RefCounted
## 上下文协议(MCP)命令的中央派发表。
##
## 持有每个已注册命令 — 内置与扩展 — 按方法名索引,并经由 [method call_command]
## 把进来的调用路由到各自的处理器。内置模块与扩展用 [method add] 填充它,传入
## 声明该命令契约的 [MCPToolkitCommandOptions](或 [MCPToolkitExtensionOptions]);
## 它推导出的每命令元数据驱动版本门控、注解、超时钳制、路径守卫,以及派发器
## 经由这里的各个访问器方法回读的只读/串行化路由。它还暴露若干薄外观
## ([method create_options]、[method fail]、[method require]、
## [method create_undo_action]、[method queue_save] 等),让处理器 — 包括无法
## 直接触达 GDScript 静态方法的 C# 处理器 — 通过这一个对象构建选项、错误与
## 撤销动作.[br]
## [br]
## 扩展通过覆写 [method MCPToolkitExtension.register] 注册命令,它会收到活的
## 注册表:
## [codeblock]
## func register(registry, server):
##     registry.add("physics_list_bodies", _on_list_bodies,
##         MCPToolkitExtensionOptions.new("List all physics bodies").mark_read_only())
## [/codeblock]

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Audit = Modules.Audit
const FileGuard = Modules.FileGuard

const _DEFAULT_TIMEOUT_MS := 30000
const _MIN_TIMEOUT_MS := 1000
const _MAX_TIMEOUT_MS := 300000

var _commands: Dictionary = {}
var _extension_methods: Array[String] = []
var _version_blocked: Dictionary = {}  # method -> {min, max, engine}

# 扩展加载冲突防护。当扩展的 register() 运行期间(由加载器以
# begin/end_extension_load 括起),任何对"已被注册名称"的 add() 都会被拒绝 —
# 记录在这里,绝不写入 _commands。这在加载时就封死了意外重用的坑:好心的
# 作者在 register() 内重用了内置名称(或已加载扩展的名称)时,会得到面向
# 编辑器的错误,而不是一次静默覆盖;先加载者胜出,且是原子的。冲突的 add
# 在尝试的那一刻就是空操作:没有瞬时覆盖,外来的处理器 Callable 也不存在
# 变成 GC 悬空的机会。
# 范围:防护只在窗口内生效 — 仅在 register() 期间活动。一个把注册表引用
# 藏起来、在 register() 返回之后(经由信号、计时器或延迟调用)再调用 add()
# 的扩展,仍会按"最后写入者胜"落地。该路径按设计不在防护范围内(已安装的
# 扩展是全信任的进程内代码;防护针对的是意外冲突,而非加载后的故意添加)。
# 加载器在 register() 之后排空 _ext_load_refused,并且只把错误呈现给编辑器 —
# 绝不进入上下文协议响应流。参见 extension_loader.gd 与 docs/extending.md
# (冲突策略:加载窗口内先加载者胜出)。
var _ext_load_guard_active := false
var _ext_load_refused: Array[Dictionary] = []  # [{method, description}]


## 打开扩展加载冲突窗口。打开期间,add() 拒绝任何已存在的名称(记入拒绝
## 列表而不是覆盖)。加载器在调用扩展的 register() 之前立即调用它。
func begin_extension_load() -> void:
	_ext_load_guard_active = true
	_ext_load_refused.clear()


## 关闭扩展加载冲突窗口,并返回期间被拒绝的名称(每项 {method, description}),
## 让加载器抛出面向编辑器的错误,把冲突归因于出问题的扩展。没有冲突时返回
## []。
func end_extension_load() -> Array[Dictionary]:
	_ext_load_guard_active = false
	var refused: Array[Dictionary] = _ext_load_refused.duplicate()
	_ext_load_refused.clear()
	return refused


## 注册名为 [param method] 的命令,派发到 [param handler],契约由
## [param options] 声明。处理器是一个接受参数 [Dictionary](对可取消命令还有
## 一个 [MCPToolkitToolContext])并返回响应 [Dictionary] 的 [Callable]。
## 这是内置模块与扩展共用的唯一注册入口.[br]
## [br]
## 它从 [param options] 推导并存储命令元数据,应用若干规则:超出运行引擎的
## [method MCPToolkitCommandOptions.with_min_godot_version] /
## [method MCPToolkitCommandOptions.with_max_godot_version] 范围的命令会被
## 记录为版本阻断而不注册;read-only 与 destructive 同用是矛盾的,因此
## destructive 被强制关闭并警告;超时会被钳制(非正值取 30 秒默认,正值下限
## 1 秒、上限 300 秒)。在带括号的扩展加载期间(见
## [method begin_extension_load]),已存在的 [param method] 会被拒绝而不是
## 覆盖.[br]
## [br]
## [codeblock]
## registry.add("physics_list_bodies", _on_list_bodies,
##     MCPToolkitExtensionOptions.new("List all physics bodies").mark_read_only())
## [/codeblock]
func add(method: String, handler: Callable,
		options: MCPToolkitCommandOptions) -> void:
	# 冲突防护(仅在带括号的扩展加载期间活动):拒绝覆盖已存在的命令。
	# 记录下来供加载器报告;下面的写入不会发生,因此在位处理器的完整性
	# 不受影响。
	if _ext_load_guard_active and _commands.has(method):
		_ext_load_refused.append({
			"method": method,
			"description": options.to_dict().get("description", ""),
		})
		return

	var opts: Dictionary = options.to_dict()

	# 版本门控:阻断与运行引擎不兼容的命令。
	var min_ver: String = opts.get("min_godot_version", "")
	var max_ver: String = opts.get("max_godot_version", "")
	if min_ver != "" or max_ver != "":
		var engine_ver := Modules.VersionUtils.get_engine_version_pair()
		if not Modules.VersionUtils.is_version_in_range(engine_ver, min_ver, max_ver):
			_version_blocked[method] = {"min": min_ver, "max": max_ver, "engine": engine_ver}
			return

	var is_read_only: bool = opts.get("is_read_only", false)
	var is_destructive: bool = opts.get("is_destructive", false)
	var is_idempotent: bool = opts.get("is_idempotent", false)

	# 互斥校验:read-only + destructive 是矛盾。
	if is_read_only and is_destructive:
		push_warning("[MCPExtensions] '%s': is_read_only and is_destructive are mutually exclusive - forcing is_destructive to false" % method)
		is_destructive = false

	# 把友好名称映射到上下文协议注解。
	var annotations := {
		"readOnlyHint": is_read_only,
		"destructiveHint": is_destructive,
		"idempotentHint": is_idempotent,
	}

	# 钳制超时:0/负值 → 默认,然后下限/上限。
	var raw_timeout: int = opts.get("timeout_ms", 0)
	var timeout_ms: int = _DEFAULT_TIMEOUT_MS
	if raw_timeout > 0:
		if raw_timeout > _MAX_TIMEOUT_MS:
			push_warning("[MCPExtensions] '%s': timeout_ms %d exceeds maximum %d - clamped. Consider restructuring the tool to use a start-work-and-poll pattern." % [method, raw_timeout, _MAX_TIMEOUT_MS])
		timeout_ms = clampi(raw_timeout, _MIN_TIMEOUT_MS, _MAX_TIMEOUT_MS)

	var cmd_entry := {
		"handler": handler,
		"description": opts.get("description", ""),
		"input_schema": opts.get("input_schema", {}),
		"annotations": annotations,
		"group": opts.get("group", {}),
		"timeout_ms": timeout_ms,
		"timeout_declared": raw_timeout > 0,  # 作者设置过正的 timeout_ms(区别于取默认)
		"is_cancellable": bool(opts.get("is_cancellable", false)),
		"read_only": is_read_only,
		"active_scene_required": bool(opts.get("is_active_scene_required", true)),
		"exclusive_execution": bool(opts.get("exclusive_execution", false)),
		"success_hint": opts.get("success_hint", ""),
		"path_guards": opts.get("path_guards", {}),
	}
	if min_ver != "":
		cmd_entry["min_godot_version"] = min_ver
	if max_ver != "":
		cmd_entry["max_godot_version"] = max_ver
	_commands[method] = cmd_entry


## 注销名为 [param method] 的命令,同时移除其处理器与扩展标记(若有)。
## 命令未注册时为空操作。
func remove(method: String) -> void:
	_commands.erase(method)
	var idx := _extension_methods.find(method)
	if idx >= 0:
		_extension_methods.remove_at(idx)


## 返回当前已注册全部命令的名称。
func get_all_methods() -> Array:
	return _commands.keys()


## 把名为 [param method] 的命令标记为扩展提供(区别于内置)。幂等。扩展
## 加载器在 [method add] 之后调用它,让桥接能区分扩展命令;参见
## [method get_extension_methods]。
func mark_extension(method: String) -> void:
	if method not in _extension_methods:
		_extension_methods.append(method)


## 返回经 [method mark_extension] 标记为扩展命令的名称副本。
func get_extension_methods() -> Array[String]:
	return _extension_methods.duplicate()


## 返回 `method` 存储的元数据,包括其注解字典。
## 注意:这个字典只经由扩展路径消费 — extension_loader 与扩展服务迭代它,
## 向桥接描述扩展命令。内置的对客户端可见注解
## (readOnlyHint/destructiveHint/idempotentHint)以服务器为准(server src/
## catalogue.ts → 上下文协议 tools/list);服务器从不调用本函数。内置命令上的
## mark_read_only() 对路由/串行化(needs_serialization)是关键,而不是
## 客户端提示。
func get_command_metadata(method: String) -> Dictionary:
	if not _commands.has(method):
		return {}
	var entry: Dictionary = _commands[method]
	var meta := {
		"description": entry.get("description", ""),
		"input_schema": entry.get("input_schema", {}),
		"annotations": entry.get("annotations", {}),
		"group": entry.get("group", {}),
	}
	var timeout_ms: int = entry.get("timeout_ms", _DEFAULT_TIMEOUT_MS)
	if timeout_ms != _DEFAULT_TIMEOUT_MS:
		meta["timeout_ms"] = timeout_ms
	if entry.has("min_godot_version"):
		meta["min_godot_version"] = entry["min_godot_version"]
	if entry.has("max_godot_version"):
		meta["max_godot_version"] = entry["max_godot_version"]
	return meta


## 若名为 [param method] 的命令已注册则返回 [code]true[/code]。
func has_command(method: String) -> bool:
	return _commands.has(method)


## `method` 的看门狗期限依据。若作者"声明"了超时,我们信任它(作者声明的
## 契约 — 内置命令与谨慎的扩展因此获得紧凑、合适的期限);若未声明
## (该命令回退到 30 秒默认,那并非对其时长的刻意陈述),则使用
## _MAX_TIMEOUT_MS,确保未声明但缓慢的方法绝不会被提前强制清除。
func get_watchdog_timeout_ms(method: String) -> int:
	if not _commands.has(method):
		return _MAX_TIMEOUT_MS
	var entry: Dictionary = _commands[method]
	if bool(entry.get("timeout_declared", false)):
		return int(entry.get("timeout_ms", _MAX_TIMEOUT_MS))
	return _MAX_TIMEOUT_MS


## 若名为 [param method] 的命令注册为可取消(见
## [method MCPToolkitCommandOptions.mark_cancellable]),则返回
## [code]true[/code],意味着派发器会传给它一个 [MCPToolkitToolContext]。
## 未知命令返回 [code]false[/code]。
func is_cancellable(method: String) -> bool:
	if not _commands.has(method):
		return false
	return _commands[method].get("is_cancellable", false)


## 若名为 [param method] 的命令被标记为只读(见
## [method MCPToolkitCommandOptions.mark_read_only]),则返回
## [code]true[/code]。未知命令返回 [code]false[/code]。
func is_read_only(method: String) -> bool:
	var cmd = _commands.get(method)
	return cmd != null and cmd.get("read_only", false)


## `method` 的声明式路径守卫({param -> "project"|"user"}),无则为 {}。
## 由 MCPToolkitCommandOptions.guard_project_path/guard_user_path 填充。
func path_guards(method: String) -> Dictionary:
	var cmd = _commands.get(method)
	if cmd == null:
		return {}
	return cmd.get("path_guards", {})


## 若名为 [param method] 的命令要求打开一个正在编辑的场景,即它未被标记为
## 场景无关(见 [method MCPToolkitCommandOptions.mark_scene_independent]),
## 则返回 [code]true[/code];已注册命令默认为 [code]true[/code]。未知命令
## 返回 [code]false[/code]。
func is_active_scene_required(method: String) -> bool:
	var cmd = _commands.get(method)
	return cmd != null and cmd.get("active_scene_required", true)


## 若名为 [param method] 的命令被标记为独占执行(见
## [method MCPToolkitCommandOptions.mark_exclusive_execution]),则返回
## [code]true[/code]。未知命令返回 [code]false[/code]。
func is_exclusive_execution(method: String) -> bool:
	var cmd = _commands.get(method)
	return cmd != null and cmd.get("exclusive_execution", false)


## 若对名为 [param method] 的命令的调用必须相对变更做串行化 — 即它是独占的,
## 或它不是只读 — 则返回 [code]true[/code]。只读且非独占的命令返回
## [code]false[/code](它们可以并发运行)。未知命令返回 [code]true[/code]
## (安全的默认值)。
func needs_serialization(method: String) -> bool:
	if not _commands.has(method):
		return true  # 未知命令的安全默认。
	if is_exclusive_execution(method):
		return true
	return not is_read_only(method)


## 返回一个全新的 [MCPToolkitCommandOptions] 构建器。便捷外观,让处理器
## (尤其是无法直接触达 GDScript [code]new()[/code] 的 C# 处理器)能经由
## 注册表构建选项。返回新的构建器。
func create_options() -> MCPToolkitCommandOptions:
	return MCPToolkitCommandOptions.new()


## 返回一个以 [param description] 为种子的全新 [MCPToolkitExtensionOptions]
## 构建器。等价于 [code]MCPToolkitExtensionOptions.new(description)[/code] 的
## 外观,供 C# 处理器经由注册表访问。返回新的构建器。
func create_extension_options(description: String) -> MCPToolkitExtensionOptions:
	return MCPToolkitExtensionOptions.new(description)


## 开始一个名为 [param description] 的撤销动作(可选地限定到
## [param context_object]),并返回其 [MCPToolkitUndoRedoAction] 构建器。
## 这是 [method MCPToolkitUndoRedoAction.begin] 的外观,让无法调用那个
## GDScript 静态方法的 C# 处理器经由注册表构建撤销动作。
func create_undo_action(description: String, context_object: Object = null) -> MCPToolkitUndoRedoAction:
	return MCPToolkitUndoRedoAction.begin(description, context_object)


## 面向扩展处理器的编辑器安全场景保存 — 包装 MCPToolkitSafeSceneOps
## (正如 create_undo_action 包装 MCPToolkitUndoRedoAction),让 C# 处理器
## (无法 await 或调用 GDScript 静态方法)经由这个唯一的注册表外观触达它。
## 用法:`id = registry.Call("queue_save", path)`,然后轮询
## `registry.Call("check_save", id [, clear])` 直到 `done` 为 true。保存在
## 处理器返回之后运行(适用 C2 扫描空闲 + C1 重入守卫)。
func queue_save(path := "") -> String:
	return MCPToolkitSafeSceneOps.queue_save(path)


## 按 [param save_id] 轮询排队中的保存;当 [param clear] 为 true 且保存完成时,
## 可选地清除记录。这是 [method MCPToolkitSafeSceneOps.check_save] 的外观
## (与 [method queue_save] 配对),面向同步/C# 处理器。返回与该方法相同的
## 状态字典。
func check_save(save_id: String, clear := false) -> Dictionary:
	return MCPToolkitSafeSceneOps.check_save(save_id, clear)


## 移除全部已注册命令,并清空扩展标记与版本阻断状态。在插件拆卸时使用,
## 以便在编辑器退出时的泄漏检查之前断开处理器 [Callable] 引用。
func clear() -> void:
	_commands.clear()
	_extension_methods.clear()
	_version_blocked.clear()


## 以 [param code]、[param message] 与可选的 [param hint] 构建失败响应。
## 这是 [method MCPToolkitError.fail] 的外观,让处理器经由注册表返回错误。
## 返回失败字典。
func fail(code: String, message: String, hint: String = "") -> Dictionary:
	return MCPToolkitError.fail(code, message, hint)


## 校验 [param required] 中的每个键都存在于 [param parameters] 且非空。
## 这是 [method MCPToolkitError.require] 的外观:满足时返回
## [code]null[/code],否则为第一个缺失键返回 [code]INVALID_PARAMS[/code]
## 错误字典。
func require(parameters: Dictionary, required: Array) -> Variant:
	return MCPToolkitError.require(parameters, required)


## 派发一个已注册命令(这是协程 — 请用 [code]await[/code] 调用)。查找
## [param method],针对 [param parameters] 运行其声明的路径守卫,调用处理器
## (命令可取消时传入 [param ctx]),并在返回前强制执行响应契约.[br]
## [br]
## 返回处理器的响应 [Dictionary];以下情况返回错误字典:命令未知
## ([code]NOT_FOUND[/code],或对运行引擎被版本阻断时的
## [code]UNSUPPORTED[/code]);受守卫的路径被拒绝([code]PATH_DENIED[/code]);
## 或处理器违反契约,返回了非字典或省略了 [code]success[/code] 键
## ([code]INTERNAL[/code])。响应成功且没有提示时,会附上该命令注册的成功
## 提示(若有)。[param ctx] 是可取消命令的每次调用 [MCPToolkitToolContext],
## 否则为 [code]null[/code]。
func call_command(method: String, parameters: Dictionary,
		ctx: MCPToolkitToolContext = null) -> Dictionary:
	if not _commands.has(method):
		if _version_blocked.has(method):
			var info: Dictionary = _version_blocked[method]
			var detail := "requires"
			if info["min"] != "":
				detail += " >= %s" % info["min"]
			if info["max"] != "":
				detail += " <= %s" % info["max"]
			return MCPToolkitError.fail("UNSUPPORTED",
				"%s %s (connected: %s)" % [method, detail, info["engine"]])
		return MCPToolkitError.fail("NOT_FOUND", "unknown method: " + method)
	Audit.log_call(method, parameters)

	# 声明式路径守卫(扩展命令)— 在处理器运行"之前"校验已声明的路径参数。
	# 内置命令没有声明(它们在处理器内部经 FileGuard 自我防护),因此这个
	# 循环对它们是空操作。缺失/空值交由处理器处理(未提供的可选路径不算
	# 拒绝)。仅工具包侧;服务器看不到这些。
	var guards: Dictionary = _commands[method].get("path_guards", {})
	for param in guards:
		var raw = parameters.get(param, "")
		if not (raw is String) or (raw as String).strip_edges().is_empty():
			continue
		if str(guards[param]) == "user":
			var ug: Dictionary = FileGuard.resolve_safe_user(raw)
			if not ug.get("ok", false):
				return MCPToolkitError.fail(
					str(ug.get("error_code", "PATH_DENIED")), str(ug.get("error_message", "path denied")))
		else:
			var pg: Dictionary = FileGuard.resolve_safe(raw)
			if pg.get("error") != null:
				return MCPToolkitError.fail("PATH_DENIED", str(pg.get("reason", "path denied")))

	var result
	if ctx != null:
		result = await _commands[method]["handler"].call(parameters, ctx)
	else:
		result = await _commands[method]["handler"].call(parameters)

	# ── 响应契约强制 ──
	if not result is Dictionary:
		push_error("[MCPToolkit] Handler for '%s' returned non-Dictionary (%s)" % [method, type_string(typeof(result))])
		return MCPToolkitError.fail("INTERNAL", "Handler for %s returned non-Dictionary" % method)

	if not result.has("success"):
		push_error("[MCPToolkit] Handler for '%s' returned Dictionary without 'success' key" % method)
		return MCPToolkitError.fail("INTERNAL", "Handler for %s returned Dictionary without 'success' key" % method)

	# ── 处理器未设置时自动注入注册的成功提示 ──
	# 注意:提示注入在这里(工具包侧)服务扩展工具。
	# 内置工具的提示由服务器侧在 callAndWrap() 中注入。
	# 两者都以 result["hint"] 作为契约面 — 不会重复注入。
	var sh: String = _commands[method].get("success_hint", "")
	if sh != "" and result.get("success", false) and not result.has("hint"):
		result["hint"] = sh

	return result
