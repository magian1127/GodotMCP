@tool
extends RefCounted
## 共享的扩展支持叶子模块:加载、校验并探测单个上下文协议(MCP)工具包扩展;
## 同时覆盖候选检测与 Godot 4.2 缓存/版本两类问题 ——
## 发现流程与实时监视器都依赖这些能力。
##
## 无状态 —— 每个函数都把输入(注册表、服务器、类名、脚本路径)作为参数
## 传入并返回结果。load_extension() 会返回已加载的实例,
## 因此保留职责(C# GC 安全引用)归调用方(CALLER)所有,
## 而不是由本叶子模块持有任何跨调用状态。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")

const _PREFIX := "MCPToolkit"

# 扩展不能覆盖的内建命名空间。
const RESERVED_PREFIXES: Array[String] = [
	"scene.", "script.", "editor.", "node.", "runtime.", "server.",
	"resource.", "folder.", "file.", "signal.", "playtest.", "project.",
	"input_map.", "animation.", "tilemap.", "asset.", "save.", "meta.",
	"game.", "diff.", "autoload.", "extensions.",
]


## 检查全局类列表条目是否为扩展候选。
## GDScript:通过基类检测(extends MCPToolkitExtension)—— 没有命名限制。
## C#:通过 MCPToolkit 前缀检测(跨语言继承并不可行)。
## 基类检查天然排除了工具包内部类
## (它们的基类是 RefCounted/Node,而非 MCPToolkitExtension)。
static func is_extension_candidate(entry: Dictionary) -> bool:
	var base_class: String = entry.get("base", "")
	if base_class == "MCPToolkitExtension":
		return true
	# C# 无法继承这个 GDScript 基类 —— 改用前缀约定来检测。
	var class_name_str: String = entry.get("class", "")
	var script_path: String = entry.get("path", "")
	if class_name_str.begins_with(_PREFIX) and script_path.ends_with(".cs"):
		return true
	return false


## 仅当脚本位于一个正式的 Godot 插件(addon,含 plugin.cfg)内、且该插件
## 已被禁用时才返回 false。其余一切情况 → true。
static func is_addon_enabled(script_path: String) -> bool:
	if not script_path.begins_with("res://addons/"):
		return true
	var addon_name := script_path.trim_prefix("res://addons/").get_slice("/", 0)
	if addon_name.is_empty():
		return true
	# 没有 plugin.cfg → 不是正式插件,不存在启用/禁用的切换机制。
	if not FileAccess.file_exists("res://addons/%s/plugin.cfg" % addon_name):
		return true
	return EditorInterface.is_plugin_enabled(addon_name)


## 仅在 Godot 4.2.x 上为真。当一个全新或刚修改过的 @tool 扩展在会话内被
## (重)加载,且采用一次全新的 CACHE_MODE_IGNORE 加载,而 EditorFileSystem
## 仍在重新导入它时,4.2 编辑器会原生崩溃:IGNORE 会强制发起第二次
## 未注册的、同步的并行加载(resource_loader.cpp:478-479),它会与编辑器
## 在全局类注册期间自己的重新导入相冲突(引擎 bug:godot#95527 /
## #58883 / #59669)。4.3+ 已妥善处理。匹配采用显式的主次版本号
## (major.minor)相等判断,因此将来的 5.2 不会被误判,
## 而所有 4.2.x 补丁版本都会命中。
static func is_godot_42() -> bool:
	return Modules.VersionUtils.is_engine_version_pair("4.2")


## 加载扩展脚本时的缓存模式。4.2 使用 CACHE_MODE_REUSE,它会返回编辑器
## 已有的(或正在加载中的)资源,而不是催生那种容易引发崩溃的重复同步
## 加载 —— 代价是对现有扩展的会话内编辑(EDIT)在重启之前不会生效
## (缓存读取;以提示/nudge 的形式呈现)。4.3+ 保持 CACHE_MODE_IGNORE:
## 保证读到最新内容(可靠的实时修改检测),
## 且已被证明不会崩溃。
static func _extension_cache_mode() -> int:
	return ResourceLoader.CACHE_MODE_REUSE if is_godot_42() else ResourceLoader.CACHE_MODE_IGNORE


## 扩展源文件的哈希值,不可读时为 0。只是一次纯文本读取 —— 不实例化脚本 ——
## 因此在 4.2 上于重新导入进行期间调用也是安全的。用于检测
## 4.2 缓存路径无法实时应用的会话内编辑。
static func hash_extension_source(script_path: String) -> int:
	if not FileAccess.file_exists(script_path):
		return 0
	var f := FileAccess.open(script_path, FileAccess.READ)
	if f == null:
		return 0
	var text := f.get_as_text()
	f.close()
	return text.hash()


static func arrays_equal(a: Array, b: Array) -> bool:
	if a.size() != b.size():
		return false
	for i in a.size():
		if a[i] != b[i]:
			return false
	return true


## 把扩展加载进一个临时(scratch)注册表,以探明它注册了哪些方法及其元数据。
## 绝不修改活跃注册表。
## 返回 {"methods": Array[String], "metadata": Dictionary}。
static func probe_extension(class_name_str: String, script_path: String, server: Node) -> Dictionary:
	var empty := {"methods": [] as Array[String], "metadata": {}}
	var script: Script = ResourceLoader.load(script_path, "", _extension_cache_mode())
	if script == null:
		return empty
	var instance = script.new()
	if instance == null:
		return empty
	var scratch := MCPToolkitCommandRegistry.new()
	if instance.has_method("Register"):
		instance.Register(scratch, server)
	elif instance.has_method("register"):
		instance.register(scratch, server)
	var methods: Array[String] = []
	var metadata: Dictionary = {}
	for method: String in scratch.get_all_methods():
		methods.append(method)
		metadata[method] = str(scratch.get_command_metadata(method))
	return {"methods": methods, "metadata": metadata}


## 加载、校验并把单个扩展注册进活跃注册表。成功时返回被保留的实例,
## 任何失败(脚本无法加载、契约校验失败、或注册了零条新命令)时返回 null。
## 调用方(CALLER)必须保留返回的实例 ——
## 它是保住扩展已注册 Callable 存活的
## C# GC 安全引用。
static func load_extension(class_name_str: String, script_path: String, registry: MCPToolkitCommandRegistry, server: Node) -> Object:
	var script: Script = ResourceLoader.load(script_path, "", _extension_cache_mode())
	if script == null:
		push_warning("[MCPExtensions] '%s': failed to load script at %s" % [class_name_str, script_path])
		return null

	var is_csharp := script_path.ends_with(".cs")
	var instance = script.new()
	if instance == null:
		push_warning("[MCPExtensions] '%s': script.new() returned null" % class_name_str)
		return null

	# 校验扩展契约。
	if is_csharp:
		# C# 无法继承 GDScript 类 —— 改用鸭子类型(duck typing)检测。
		if not instance.has_method("Register") and not instance.has_method("register"):
			push_warning("[MCPExtensions] '%s': C# class missing Register() method - skipped" % class_name_str)
			return null
	else:
		# GDScript 必须继承 MCPToolkitExtension。
		if not (instance is MCPToolkitExtension):
			push_warning("[MCPExtensions] '%s': GDScript class does not extend MCPToolkitExtension - skipped" % class_name_str)
			return null

	# 在注册之前记录现有方法,以便检测新增的方法。
	var before: Array = registry.get_all_methods()

	# 在 register() 前后打开加载期冲突防护(collision-guard)窗口。窗口生效期间,
	# 任何对已注册名称(内建命令或先前扩展)的 add() 都会在注册表中被拒绝,
	# 而不是被静默覆盖 —— 堵上 registry.add() 否则会允许的"意外重用"这个坑
	# (在窗口之外它是最后写入者获胜)。窗口内先加载者获胜;
	# 落败者会在下方被报告。注意:该防护只限窗口范围 —— 它并不能(NOT)
	# 阻止一个完全受信的扩展把 add() 推迟到 register() 之后才执行
	# (已安装的扩展是完全受信的进程内代码;
	# 那超出了本防护的范围)。
	registry.begin_extension_load()

	# 调用 register —— 同时兼容 GDScript(snake_case)与 C#(PascalCase)。
	if instance.has_method("Register"):
		instance.Register(registry, server)
	elif instance.has_method("register"):
		instance.register(registry, server)

	# 排空并报告防护在 register() 期间拒绝的所有冲突。这些冲突只上报给编辑器
	# (push_error + 可用时的停靠面板 toast)—— 绝不进入上下文协议响应流,
	# 因此 LLM 不会看到额外的噪音。
	var refused: Array[Dictionary] = registry.end_extension_load()
	for entry: Dictionary in refused:
		_report_collision(class_name_str, str(entry.get("method", "")))

	# 校验新注册的方法。
	var after: Array = registry.get_all_methods()
	var new_count := 0
	for method: String in after:
		if method in before:
			continue
		var rejected := false
		for prefix: String in RESERVED_PREFIXES:
			if method.begins_with(prefix):
				registry.remove(method)
				push_warning("[MCPExtensions] '%s': '%s' uses reserved namespace '%s*' - rejected" % [class_name_str, method, prefix])
				rejected = true
				break
		if not rejected:
			registry.mark_extension(method)
			var meta := registry.get_command_metadata(method)
			var group_name: String = meta.get("group", {}).get("name", "")
			if group_name:
				print("[MCPExtensions]   + %s (group: %s)" % [method, group_name])
			else:
				print("[MCPExtensions]   + %s" % method)
			new_count += 1

	if new_count == 0:
		# 当所有 add() 都发生冲突时,上方的拒绝记录就是原因 —— 要明说,
		# 而不是只给一句干巴巴的"零条新命令"。这里不保留实例是正确的:
		# 它没有注册任何活跃命令,因此没有绑定到它的活跃 Callable
		# (没有 GC 断链风险),而它试图覆盖的每一条既有命令
		# 都完好无损。
		if refused.is_empty():
			push_warning("[MCPExtensions] '%s': registered zero new commands" % class_name_str)
		else:
			push_warning("[MCPExtensions] '%s': registered zero new commands - all %d add(s) collided with already-registered commands" % [class_name_str, refused.size()])
		return null

	# 返回实例,让调用方得以保留它(对 C# 至关重要 ——
	# 防止 GC 令 Callable 失效)。
	return instance


## 只向编辑器(EDITOR)呈现被拒绝的扩展命令冲突 —— 绝不进入上下文协议响应流
## (不给面向代理(agent)的噪音)。push_error 总会触发(输出面板 +
## 错误选项卡);当 EditorToaster 存在时(Godot 4.4+;更低版本走下方
## 的空安全降级)还会追加一条停靠面板 toast。会点名违规的扩展 +
## 它试图抢占的命令。冲突的 add() 已在注册表中被拒绝,
## 因此这是一次报告,而非状态变更。
static func _report_collision(class_name_str: String, method: String) -> void:
	var msg := (
		"Godot MCP Unified: extension '%s' tried to register '%s', " % [class_name_str, method]
		+ "but that command is already registered - keeping the existing one. "
		+ "Rename the extension's command to a unique <namespace>.<action>.")
	push_error("[MCPExtensions] " + msg)
	# EditorToaster 是 4.4+ 才有的 —— 在更低版本上 get_toaster() 返回 null,
	# 因此降级为仅使用 push_error。严重级别 2 == EditorToaster.SEVERITY_ERROR。
	var toaster = Modules.EditorAccess.get_toaster()
	if toaster != null:
		toaster.push_toast(msg, 2)
