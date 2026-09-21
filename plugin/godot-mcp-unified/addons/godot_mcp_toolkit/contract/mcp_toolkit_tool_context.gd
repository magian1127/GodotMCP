@tool
class_name MCPToolkitToolContext
extends RefCounted
## 可取消扩展工具处理器的单次调用上下文。
##
## 当命令使用以下方式注册时:
## [method MCPToolkitCommandOptions.mark_cancellable],调度器会将
## 此类的一个实例作为处理器的第二个参数传入。
## 处理器以两种方式协作式观察取消:反应式,即连接到
## [signal cancelled] 以中止长时运行的操作;或通过轮询
## [method is_cancelled] 在离散步骤之间检查。调度器拥有此对象 —
## 请勿在处理器返回后继续持有它,因为它仅限定于单次工具
## 调用。

## 当调用被取消时发出一次,供想要立即反应
## 而非轮询 [method is_cancelled] 的处理器使用。
signal cancelled

var _cancelled := false


## 请求取消:翻转取消状态并发出 [signal cancelled]
## (幂等 — 第二次调用会被忽略)。[b]由调度系统调用[/b],
## 而不是由扩展处理器调用;处理器应通过
## [method is_cancelled] 或 [signal cancelled] 观察取消,而不是调用此方法。
func cancel() -> void:
	if _cancelled:
		return
	_cancelled = true
	cancelled.emit()


## 当调用已被取消时返回 [code]true[/code]。在长时操作的步骤之间
## 轮询它,并在其为真时提前返回。
## [codeblock]
## func _on_long_tool(parameters, ctx):
##     for item in big_list:
##         if ctx.is_cancelled():
##             return MCPToolkitError.fail("FAILED", "cancelled")
##         _process(item)
##     return MCPToolkitSuccess.ok()
## [/codeblock]
func is_cancelled() -> bool:
	return _cancelled
