@tool
class_name MCPToolkitExtensionOptions
extends MCPToolkitCommandOptions
## 用于 EXTENSION 命令的选项构建器,要求提供描述。
##
## [MCPToolkitCommandOptions] 的一个子类,它改在构造函数中接收工具的描述,
## 而不是通过 [method MCPToolkitCommandOptions.with_description] 传入。
## 扩展工具是面向外部用户的 —— LLM 需要描述才能知道这个工具做什么 ——
## 因此注册扩展命令时,请优先使用本类而非基类构建器。
## 从 [MCPToolkitCommandOptions] 继承的每个可链式调用的 setter
## (例如 [method MCPToolkitCommandOptions.mark_read_only])用法完全相同。[br]
## [br]
## 示例:
## [codeblock]
## registry.add("physics_list_bodies", _on_list_bodies,
##     MCPToolkitExtensionOptions.new("List all physics bodies") \
##         .mark_read_only() \
##         .mark_idempotent() \
##         .with_group("physics_tools", "Physics inspection", ["physics", "force"]))
## [/codeblock]


## 用必需的 [param description] 构造选项。这里的描述是"请求提供"
## 而非"强制要求":一个空的(或纯空白字符的)[param description]
## 会推送一条错误,并把描述留空,但对象仍会被构造 ——
## 注册流程照常继续。请传入一句简洁、面向动作的句子,
## 描述这个工具是做什么的。
func _init(description: String) -> void:
	if description.strip_edges() == "":
		push_error("[MCPExtensions] Extension options require a non-empty description")
		return
	_description = description
