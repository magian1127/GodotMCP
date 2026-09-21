@tool
extends RefCounted
## 追加到 execute_code 失败消息上的恢复提示。
##
## execute_code 通过引擎的 Expression 类求值单个 GDScript 表达式。
## Expression 会把每个裸标识符解析为作用域对象(`self`)的属性查找
## —— 它不理解全局名称 — 因此对引擎单例、项目自动加载
## 或全局 class_name 的引用会以 "Invalid named index '<Name>' for base type Object" 失败,
## 而 load() 根本无法调用。这些辅助函数将那些晦涩的引擎字符串
## 转化为可执行的下一步。
## 纯字符串逻辑;在编辑器与运行时处理器中都很安全,
## 两者共享它,保证提示保持一致。

## 在普通 GDScript 中可访问、但无法通过 Expression 访问的引擎单例。
## 显式列出,以便提示能按名称点名;其他任何
## 无法解析的全局(自动加载、全局类)则由通用的
## 裸标识符分支捕获。
const _ENGINE_SINGLETONS: Array[String] = [
	"EditorInterface",
	"Engine",
	"OS",
	"Input",
	"DisplayServer",
	"ProjectSettings",
	"ResourceLoader",
	"ResourceSaver",
	"RenderingServer",
	"PhysicsServer2D",
	"PhysicsServer3D",
]


## 返回要追加到 [param err_text](来自失败的 [code]Expression.execute()[/code]
## 的 [code]EXECUTE_FAILED[/code] 消息)的提示,给定调用方运行的原始
## [param code]。无法识别已知失败形态时返回空字符串。
##
## 按优先级顺序识别三种形态:不可访问的裸全局
## (位于访问链开头的引擎单例、自动加载或全局类)、
## [code]load()[/code] 调用(Expression 无法执行),以及
## 对返回对象进行的属性访问链。前置全局检查先于
## 通用链式访问检查运行,因为两者都表现为相同的
## "Invalid named index … base type Object" 字符串,而当失败的名称
## 位于表达式开头时,全局解读更为准确。
static func build_hint(err_text: String, code: String) -> String:
	var global_hint := _bare_global_hint(err_text, code)
	if not global_hint.is_empty():
		return global_hint
	var lower_err := err_text.to_lower()
	if "'load'" in lower_err and ("call to" in lower_err or "function" in lower_err):
		return _load_hint(code)
	if "invalid named index" in lower_err and "base type object" in lower_err:
		return (
			"\n\nHint: Expression.execute() cannot chain property access on returned objects. "
			+ "Use runtime_inspect_node or node_call_method for multi-step property access."
		)
	return ""


## 针对 Expression 无法解析的裸全局的提示;若失败不属此形态,
## 则返回 [code]""[/code]。[param err_text] 是失败消息;[param code]
## 是表达式,用于确认失败的名称是前置的全局引用
## (而非通过链式访问到达的属性,那是另一种形态、
## 表述正确的失败)。会点名该标识符,并且当它是已知的引擎单例时予以说明。
static func _bare_global_hint(err_text: String, code: String) -> String:
	var re := RegEx.new()
	re.compile("Invalid named index '([^']+)' for base type Object")
	var m := re.search(err_text)
	if m == null:
		return ""
	var identifier := m.get_string(1)
	if not _is_leading_reference(code, identifier):
		return ""
	var kind := "a global singleton" if identifier in _ENGINE_SINGLETONS else "a global (autoload or global class)"
	return (
		"\n\nHint: '%s' is %s, which is not accessible in execute_code. " % [identifier, kind]
		+ "Expression.execute() resolves a bare identifier as a property of the scope node, "
		+ "not as a global name, so no singleton, autoload, or class_name is reachable this way. "
		+ "Use the dedicated MCP tools instead — e.g. runtime_inspect_node or node_call_method "
		+ "to read a node's live state, project_get_settings for ProjectSettings, or (for a running "
		+ "game) drive the node whose script already references the global."
	)


## [param name] 是否作为前置全局引用出现在 [param code] 中 — 即
## 第一个令牌,位于表达式或子表达式的开头,且前面没有
## [code].[/code](若有点号,则为通过链式访问到达的属性)。
## 对匹配进行锚定,使 [code]OS[/code] 不会匹配 [code]node.get_class_OS[/code]。
static func _is_leading_reference(code: String, name: String) -> bool:
	var re := RegEx.new()
	re.compile("(?:^|[^\\w.])%s\\b" % _escape_regex(name))
	return re.search(code) != null


## 转义 Godot 标识符中永远不会出现、但防御性调用方仍不应
## 未转义注入的 RegEx 元字符。标识符仅包含
## [code][A-Za-z0-9_][/code],因此这只是双保险。
static func _escape_regex(text: String) -> String:
	var out := ""
	for chr in text:
		if chr in "\\^$.|?*+()[]{}":
			out += "\\"
		out += chr
	return out


## 针对 [code]load()[/code] 调用的提示,Expression 在任何上下文中都无法执行它。
## 始终同时提供两种恢复路径 — 给资源赋值与运行脚本
## 逻辑 — 因为两者都可能是调用方的意图;检测到的目标仅决定
## 哪条路径排在前。[param code] 是表达式,用于扫描 load() 的目标。
static func _load_hint(code: String) -> String:
	var re := RegEx.new()
	re.compile("load\\s*\\(\\s*[\"']([^\"']+)[\"']\\s*\\)")
	var m := re.search(code)
	var script_first := m != null and m.get_string(1).ends_with(".gd")
	var assign_remedy := (
		"To assign the resource, use node_set_property with "
		+ "{\"type\": \"Resource\", \"path\": \"res://...\"}."
	)
	var script_remedy := (
		"To run GDScript logic in the editor: "
		+ "(1) create a @tool script with workspace file editing, "
		+ "(2) run editor_sync for that path, "
		+ "(3) create a temporary node with scene_create_node, "
		+ "(4) attach the script with node_set_script, "
		+ "(5) call the method with node_call_method, "
		+ "(6) delete the temp node with node_manage(action:'delete')."
	)
	var lead := script_remedy if script_first else assign_remedy
	var follow := assign_remedy if script_first else script_remedy
	return (
		"\n\nHint: Expression.execute() cannot call load() in any context (editor or runtime). "
		+ lead + " " + follow
	)
