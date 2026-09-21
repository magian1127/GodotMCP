@tool
class_name MCPToolkitExtension
extends RefCounted
## 第三方上下文协议(MCP)工具包扩展的基类。
##
## 要用 GDScript 创建一个扩展,请声明一个直接继承本类的 [code]class_name[/code],
## 并重写 [method register]。发现机制基于基类 ——
## 项目全局类列表中的任何直接子类都会被找到并加载,
## 没有命名限制,也不要求位于任何特定目录。
## [code]MCPToolkit[/code] 名称前缀对 GDScript 而言是一种[b]推荐[/b]的约定
## (保持命名空间整洁,使你的类永不与用户代码冲突);
## 它[b]仅对 C# 是必需的[/b]:此时扩展是一个
## [code][GlobalClass][/code],其名字以 [code]MCPToolkit[/code] 开头
## (C# 无法继承这个 GDScript 基类)。只有直接子类会被发现 ——
## 多级继承被有意地不支持。[br]
## [br]
## 完整文档请参阅
## [code]addons/godot_mcp_toolkit/docs/extending.md[/code]。


## 重写点:注册本扩展的命令。加载时调用一次,传入活跃的 [param registry]
## (在其上调用 [method MCPToolkitCommandRegistry.add] 来逐条添加命令)
## 和 [param server] —— 即上下文协议服务器节点;可取消的处理器在调用期间可以使用它,
## 但不得在调用结束后继续保存。基类实现只会警告自己没有被重写,
## 因此一个没有添加任何命令的子类就是一个 bug。
func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	push_warning("MCPToolkitExtension: register() not overridden in %s"
		% get_script().resource_path)
