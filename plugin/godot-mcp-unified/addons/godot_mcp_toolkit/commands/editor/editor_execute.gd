@tool
extends RefCounted
## execute.code:通过引擎的 Expression 类,针对编辑器侧的作用域节点对单个
## GDScript 表达式(而非语句)求值;作用域节点为当前编辑的场景根节点、其下
## 显式指定的 scope_path,或以编辑器基础控件作为
## 回退)。对语句关键字做守卫,并在执行失败时追加一条
## 恢复提示(无法访问的全局、load() 或链式属性访问),由共享的 ExecuteHints 辅助器
## 构建 — 与运行时处理器使用的正是同一个辅助器。
##
## 无状态 — 处理器接收 (parameters) 并返回响应 Dictionary。
## 直接访问 Expression 求值器 / EditorInterface;作用域路径
## 归一化与结果序列化经由 Modules 别名访问。
## 编辑器命令组抽取出的子模块,经由 `preload` 别名访问。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Helpers = Modules.CommandHelpers
const Coerce = Modules.Coerce
const ExecuteHints = Modules.ExecuteHints


# -- 命令 ---------------------------------------------------------------------


static func cmd_execute_code(parameters: Dictionary) -> Dictionary:
	var code := str(parameters.get("code", ""))
	if code.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS", "missing code")

	# 语句关键字守卫(与运行时处理器相同)。
	var trimmed := code.strip_edges()
	for kw in ["var", "return", "func", "if", "for", "while", "class", "const", "match"]:
		if trimmed == kw or trimmed.begins_with(kw + " ") or trimmed.begins_with(kw + "\t") or trimmed.begins_with(kw + "\n"):
			return MCPToolkitError.fail("PARSE_ERROR",
				"execute_code only supports expressions, not statements. '%s' is a statement keyword. " % kw +
				"Use method calls, property access, or arithmetic instead.")

	# 解析作用域节点。
	var scope_node: Node = null
	var scope_path := str(parameters.get("scope_path", ""))
	if scope_path.is_empty():
		var edited := EditorInterface.get_edited_scene_root()
		if edited != null:
			scope_node = edited
		else:
			# 回退到编辑器基础控件,让表达式仍有一个 Node 作用域
			scope_node = EditorInterface.get_base_control()
	else:
		var edited := EditorInterface.get_edited_scene_root()
		if edited == null:
			return MCPToolkitError.fail("NO_SCENE", "No scene open — cannot resolve scope_path")
		scope_path = Helpers.normalize_editor_path(scope_path)
		scope_node = edited.get_node_or_null(NodePath(scope_path))
		if scope_node == null:
			return MCPToolkitError.fail("NOT_FOUND", "scope node not found: " + scope_path)

	var expr := Expression.new()
	var parse_err := expr.parse(code, PackedStringArray())
	if parse_err != OK:
		# 包装 Expression 解析器的原始报错信息,使失败可据以行动:
		# execute_code 解析的是单个 GDScript 表达式(EXPRESSION;不允许语句、
		# 赋值或代码块),这通常就是解析失败的原因。
		return MCPToolkitError.fail("PARSE_ERROR",
			"could not parse the expression: %s. execute_code evaluates a single GDScript expression — no assignments, statements, or multi-line blocks. Use a method call, property access, or arithmetic." % expr.get_error_text())
	var result = expr.execute([], scope_node, false)
	if expr.has_execute_failed():
		var err_text := expr.get_error_text()
		err_text += ExecuteHints.build_hint(err_text, code)
		return MCPToolkitError.fail("EXECUTE_FAILED", err_text)
	return MCPToolkitSuccess.ok({"result": Coerce.serialize_value(result)})
