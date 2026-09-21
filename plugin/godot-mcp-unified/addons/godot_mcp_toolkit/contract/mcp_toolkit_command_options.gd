@tool
class_name MCPToolkitCommandOptions
extends RefCounted
## 声明命令注册时所用选项的流畅构建器。
##
## 每个可链式调用的方法都返回 [code]self[/code],因此注册可读作
## 一条链,其终端 [method to_dict] 生成 [method MCPToolkitCommandRegistry.add]
## 消费的元数据字典。每个设置器(setter)记录命令契约的一个方面:
## 其提示([method mark_read_only]、[method mark_destructive]、
## [method mark_idempotent])、其调度行为([method mark_exclusive_execution]、
## [method mark_scene_independent]、[method with_timeout_ms])、其分组
## ([method with_group])、其版本范围([method with_min_godot_version] /
## [method with_max_godot_version]),以及其路径护栏
## ([method guard_project_path] / [method guard_user_path])。[br]
## [br]
## 对于内置工具,只有 [method mark_read_only] 是承重的
## (路由/序列化);面向客户端的 readOnly/destructive/idempotent 提示
## 由服务器权威决定(服务器 [code]src/catalogue.ts[/code])。
## [method mark_destructive] / [method mark_idempotent] 仅适用于扩展(EXTENSION)
## 工具,桥接层通过
## [method MCPToolkitCommandRegistry.get_command_metadata] 读取它们的注解。扩展工具应
## 使用 [MCPToolkitExtensionOptions],这是要求构造时
## 提供描述的子类(描述为空时会报错,但仍会完成构造)。[br]
## [br]
## 示例 — 注册一个只读物理工具:
## [codeblock]
## registry.add("physics_list_bodies", _on_list_bodies,
##     MCPToolkitCommandOptions.new() \
##         .mark_read_only() \
##         .with_timeout_ms(60000) \
##         .with_group("physics_tools", "Physics inspection", ["physics", "force"]))
## [/codeblock]

var _description: String = ""
var _input_schema: Dictionary = {}
var _is_read_only: bool = false
var _is_destructive: bool = false
var _is_idempotent: bool = false
var _is_cancellable: bool = false
var _timeout_ms: int = 0  # 0 = 使用注册表默认值 (30000)
var _group_name: String = ""
var _group_description: String = ""
var _group_keywords: Array = []
var _is_scene_independent: bool = false  # 反向语义:true 表示不要求
var _exclusive_execution: bool = false
var _min_godot_version: String = ""
var _max_godot_version: String = ""
var _success_hint: String = ""
var _path_guards: Dictionary = {}  # param_name -> "project" | "user"


## 设置桥接层为扩展工具宣传的人类可读 [param description]
## (内置描述由服务器权威决定)。
## 返回 [code]self[/code] 以便链式调用。优先使用 [MCPToolkitExtensionOptions],
## 它会在构造函数中接收描述。
func with_description(description: String) -> MCPToolkitCommandOptions:
	_description = description
	return self


## 设置描述工具参数的 JSON-Schema [param schema],供
## 桥接层验证与宣传工具的输入。
## 返回 [code]self[/code] 以便链式调用。
func with_input_schema(schema: Dictionary) -> MCPToolkitCommandOptions:
	_input_schema = schema
	return self


## 将每个命令的看门狗截止时间设为 [param timeout] 毫秒。
## 非正值(默认)表示"使用注册表默认值"(30 秒);
## 注册表将正值下限设为 1 秒、上限设为 300 秒。
## 返回 [code]self[/code] 以便链式调用。
func with_timeout_ms(timeout: int) -> MCPToolkitCommandOptions:
	_timeout_ms = timeout
	return self


## 将命令分配到按需组 [param name],使其仅在请求该组时
## 加载,而非在启动时加载。[param description] 和
## [param keywords] 帮助桥接层呈现与展示该组。传入空
## [param name] 可使命令保持未分组(急切加载)。
## 返回 [code]self[/code] 以便链式调用。
func with_group(name: String, description: String = "",
		keywords: Array = []) -> MCPToolkitCommandOptions:
	_group_name = name
	_group_description = description
	_group_keywords = keywords
	return self


## 将命令标记为只读:它不会修改编辑器或项目状态。
## 这对每个命令都是承重的 — 调度器用它来决定
## 该调用是否必须与变更串行化(只读调用绕过
## 变更队列)— 对于扩展工具,它还成为面向客户端的
## [code]readOnlyHint[/code]。与 [method mark_destructive] 互斥。
## 返回 [code]self[/code] 以便链式调用。
func mark_read_only() -> MCPToolkitCommandOptions:
	_is_read_only = true
	return self


## 将命令标记为破坏性(它可能删除或覆盖数据),为扩展工具
## 暴露面向客户端的 [code]destructiveHint[/code]。仅适用于
## 扩展(EXTENSION)工具 — 内置提示由服务器权威决定。注册表
## 将其视为与 [method mark_read_only] 互斥。
## 返回 [code]self[/code] 以便链式调用。
func mark_destructive() -> MCPToolkitCommandOptions:
	_is_destructive = true
	return self


## 将命令标记为幂等(以相同参数重复调用与调用一次
## 效果相同),为扩展工具暴露面向客户端的
## [code]idempotentHint[/code]。仅适用于扩展(EXTENSION)工具
## — 内置提示由服务器权威决定。
## 返回 [code]self[/code] 以便链式调用。
func mark_idempotent() -> MCPToolkitCommandOptions:
	_is_idempotent = true
	return self


## 将命令标记为可取消,使调度器向处理器传递一个
## [MCPToolkitToolContext],处理器可轮询它以协作式中止。
## 返回 [code]self[/code] 以便链式调用。
func mark_cancellable() -> MCPToolkitCommandOptions:
	_is_cancellable = true
	return self


## 将命令标记为不要求已打开的编辑场景,使调度器
## 在没有活动场景时不会拒绝它。默认情况下,命令会被视为
## 要求活动场景;对场景无关的工具请调用此方法(例如
## 只读取项目文件的工具)。
## 返回 [code]self[/code] 以便链式调用。
func mark_scene_independent() -> MCPToolkitCommandOptions:
	_is_scene_independent = true
	return self


## 将命令标记为独占执行,使调度器将其与所有其他变更
## 串行化,即使它同时也是只读的(用于工作不得与
## 任何其他调度重叠的命令)。
## 返回 [code]self[/code] 以便链式调用。
func mark_exclusive_execution() -> MCPToolkitCommandOptions:
	_exclusive_execution = true
	return self


## 将命令限制为最低引擎版本:当运行的引擎
## 早于 [param version] 时,注册表完全跳过注册,使该命令
## 永不出现。[param version] 为 [code]"major.minor"[/code] 或
## [code]"major.minor.patch"[/code](例如 [code]"4.5"[/code]);无效格式
## 会发出警告,除此之外被忽略。上限参见
## [method with_max_godot_version]。返回 [code]self[/code] 以便链式调用。
func with_min_godot_version(version: String) -> MCPToolkitCommandOptions:
	if not _is_valid_version(version):
		push_warning("[MCPToolkit] Invalid min_godot_version format: '%s' (expected 'major.minor', e.g. '4.5')" % version)
	_min_godot_version = version
	return self


## 将命令限制为最高引擎版本:当运行的引擎
## 晚于 [param version] 时,注册表跳过注册。[param version]
## 使用与 [method with_min_godot_version] 相同的格式;无效格式
## 会发出警告并被忽略。返回 [code]self[/code] 以便链式调用。
func with_max_godot_version(version: String) -> MCPToolkitCommandOptions:
	if not _is_valid_version(version):
		push_warning("[MCPToolkit] Invalid max_godot_version format: '%s' (expected 'major.minor', e.g. '4.6')" % version)
	_max_godot_version = version
	return self


## 设置当处理器未自行设置时自动附加到成功响应上的
## [param hint],为调用方提供后续指导。
## 返回 [code]self[/code] 以便链式调用。
func with_success_hint(hint: String) -> MCPToolkitCommandOptions:
	_success_hint = hint
	return self


## 声明名为 [param param] 的参数携带 [code]res://[/code]
## 项目路径,调度器必须在处理器运行之前验证它
## (对路径穿越/逃逸返回 [code]PATH_DENIED[/code])。请对扩展命令
## 读取或写入的任何由大语言模型(LLM)提供的路径使用此方法;内置工具自行防护,
## 因此这是扩展命令的声明式等效物。调用时缺失或为空的
## 值留给处理器处理(这不是拒绝)。[code]user://[/code]
## 变体参见 [method guard_user_path]。
## 返回 [code]self[/code] 以便链式调用。
func guard_project_path(param: String) -> MCPToolkitCommandOptions:
	_path_guards[param] = "project"
	return self


## 与 [method guard_project_path] 类似,但声明名为
## [param param] 的参数携带 [code]user://[/code] 路径。调度器在处理器运行之前,
## 以路径拒绝错误拒绝路径穿越、非 [code]user://[/code]
## 前缀以及插件自身的内部路径。
## 返回 [code]self[/code] 以便链式调用。
func guard_user_path(param: String) -> MCPToolkitCommandOptions:
	_path_guards[param] = "user"
	return self


static func _is_valid_version(v: String) -> bool:
	var parts := v.split(".")
	if parts.size() < 2:
		return false
	return parts[0].is_valid_int() and parts[1].is_valid_int()


## 构建注册表为该命令存储的选项字典,将在此构建器上
## 设置的每个方面合并为一个元数据映射。没有有意义值的键
## 会被省略;[code]is_active_scene_required[/code] 是
## [method mark_scene_independent] 的反向。[method MCPToolkitCommandRegistry.add]
## 会调用此方法 — 你很少直接调用它。返回元数据字典。
func to_dict() -> Dictionary:
	var d := {
		"description": _description,
		"is_read_only": _is_read_only,
		"is_destructive": _is_destructive,
		"is_idempotent": _is_idempotent,
		"is_cancellable": _is_cancellable,
		"is_active_scene_required": not _is_scene_independent,
	}
	if _exclusive_execution:
		d["exclusive_execution"] = true
	if not _input_schema.is_empty():
		d["input_schema"] = _input_schema
	if _timeout_ms > 0:
		d["timeout_ms"] = _timeout_ms
	if _group_name != "":
		var group := {"name": _group_name}
		if _group_description != "":
			group["description"] = _group_description
		if not _group_keywords.is_empty():
			group["keywords"] = _group_keywords
		d["group"] = group
	if _min_godot_version != "":
		d["min_godot_version"] = _min_godot_version
	if _max_godot_version != "":
		d["max_godot_version"] = _max_godot_version
	if _success_hint != "":
		d["success_hint"] = _success_hint
	if not _path_guards.is_empty():
		d["path_guards"] = _path_guards
	return d
