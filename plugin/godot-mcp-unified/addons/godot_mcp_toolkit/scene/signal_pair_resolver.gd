@tool
extends RefCounted
## 针对一棵场景树校验 {source, signal, target, method} 四元组,树的根由调用方
## 解析后传入,返回可直接连接的键对(source/target/callable/回显的路径与名称)
## 或 {code, error} 字典。这是编辑器信号处理器(模式 A)与运行时自动加载
## (模式 B)共享的结构骨架:两者都针对一个根解析节点路径,然后以相同方式
## 守卫 has_signal / has_method — 唯一的区别是根从何而来(编辑器的
## 正在编辑场景根 vs 运行中的 SceneTree 根),由调用方自行解析并以 Node 传入。
##
## 刻意保持精简:它返回最精简的已校验键对(运行时的历史形态)。编辑器包装它,
## 并在 has_signal/has_method 失败之上叠加自己的友好提示增强(实例化场景提示、
## 脚本源方法遍历)— 该增强仅属于编辑器,留在编辑器文件中(见 007 §D 的
## rule-of-three 告诫);强行把它塞进这个共享形态,要么污染这个干净的子模块,
## 要么用无用的编辑器逻辑撑大运行时路径。
##
## 按约定对导出友好:模式 B 的运行时自动加载会 preload 本文件,而 GDScript
## 在解析期解析标识符(先于任何 is_editor_hint() 守卫),因此只要引用任何
## 编辑器专属类 — 或 preload 一个这样做的脚本 — 都会让自动加载在导出模板中
## 解析失败(godotengine/godot#91713,4.2–4.6 均未修复)。这里的全部标识符
## 集合都是核心类(Node/Object/Callable/Dictionary),且它不 preload 任何东西。
## 请保持这一状态:对 "Editor" 的 grep 必须在非注释行中零命中。


## 针对给定的场景树 [param root] 解析单个节点路径。
##
## 这是两个服务器共用的骨架:根为 null 时返回 null(没有场景/没有运行中的
## 树);空路径或 "." 解析为根本身(因此只想要顶层信号的调用方无需输入完整
## 路径);其余情况经由 get_node_or_null。[param root] 是编辑器的正在编辑
## 场景根,或运行时的运行中 SceneTree 根 — 每个调用方解析自己的根并传入
## Node,绝不传入根解析器 Callable:把裸的静态方法引用当作 Callable 使用,
## 在 Godot 4.2 上会把 self 误绑定为 NIL 并静默中止,因此这个共享静态辅助
## 方法接受已解析的值,而不是一个待调用的 Callable。返回 Variant 是因为
## "节点缺失"是调用方需要分支处理的合法 null 结果。
static func resolve_node(path: String, root: Node) -> Variant:
	if root == null:
		return null
	if path.is_empty() or path == ".":
		return root
	return root.get_node_or_null(path)


## 返回节点的 [{name, args:[{name, type}]}] 信号列表 — 裸的运行时形态
## (不遍历连接;编辑器的列表自行附加连接信息)。
static func list_signals_of(node: Object) -> Array:
	var out: Array = []
	for sig in node.get_signal_list():
		var args: Array = []
		for arg in sig.get("args", []):
			args.append({
				"name": str(arg.get("name", "")),
				"type": int(arg.get("type", 0)),
			})
		out.append({
			"name": str(sig.get("name", "")),
			"args": args,
		})
	return out


## 校验 {source, signal, target, method} 四元组,返回 {code, error} 字典
## (INVALID_PARAMS / NOT_FOUND)或可直接连接的键对:
##   {source, target, source_path, target_path, signal_name, method_name, callable}。
##
## params 中的来源可接受 node_path 或 source_path。预期调用方已经把自己在意的
## 路径规范化(编辑器在调用前规范化 /root/… 路径);本解析器按传入的路径
## 原样处理。[param root] 是两条路径共同解析所依据的场景树根(参见
## [method resolve_node])。has_signal / has_method 守卫返回最精简的消息 —
## 编辑器会在 INVALID_PARAMS 失败之上,自行重新解析违规节点并叠加更丰富的提示。
static func resolve_pair(params: Variant, root: Node) -> Dictionary:
	if typeof(params) != TYPE_DICTIONARY:
		return {"code": "INVALID_PARAMS", "error": "params must be an object"}
	var source_path := str(params.get("node_path", params.get("source_path", "")))
	var signal_name := str(params.get("signal_name", ""))
	var target_path := str(params.get("target_path", ""))
	var method_name := str(params.get("method_name", ""))
	if source_path.is_empty() or signal_name.is_empty() \
			or target_path.is_empty() or method_name.is_empty():
		return {"code": "INVALID_PARAMS",
			"error": "node_path, signal_name, target_path, method_name are all required"}
	var source = resolve_node(source_path, root)
	if source == null:
		return {"code": "NOT_FOUND", "error": "source node not found: %s" % source_path}
	var target = resolve_node(target_path, root)
	if target == null:
		return {"code": "NOT_FOUND", "error": "target node not found: %s" % target_path}
	# 显式 bool — `source`/`target` 是 Variant(resolve_node 的返回值),因此
	# has_signal/has_method 的结果需要带类型的接收变量,而不是 := 推断。
	var has_sig: bool = source.has_signal(signal_name)
	if not has_sig:
		return {"code": "INVALID_PARAMS",
			"error": "signal %s not on %s" % [signal_name, source_path]}
	var has_meth: bool = target.has_method(method_name)
	if not has_meth:
		return {"code": "INVALID_PARAMS",
			"error": "method %s not on %s" % [method_name, target_path]}
	return {
		"source": source,
		"target": target,
		"source_path": source_path,
		"target_path": target_path,
		"signal_name": signal_name,
		"method_name": method_name,
		"callable": Callable(target, method_name),
	}
