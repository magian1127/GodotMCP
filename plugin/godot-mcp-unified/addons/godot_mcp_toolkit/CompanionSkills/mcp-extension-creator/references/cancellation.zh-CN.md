[英文原文](cancellation.md)

# 协作式取消

对于长时间运行的工具（外部 API 调用、繁重处理），可以启用协作式取消，使处理器在调用方取消时提前退出。大多数工具不需要此功能——仅在工具执行应能中途退出的慢速工作时使用。

## 启用方式

在选项上调用 `.mark_cancellable()`。这会改变处理器契约：可取消处理器需要一个**第二参数** `MCPToolkitToolContext`。

```gdscript
var opts := MCPToolkitExtensionOptions.new("Fetch data from an external API") \
    .with_timeout_ms(60000) \
    .mark_cancellable()
registry.add("weather.fetch", _handle_fetch, opts)
```

> 如果设置了 `mark_cancellable()` 却保留单参数处理器，分发器的双参数调用会失败。两者必须匹配。

## 观察取消状态

上下文提供两种获知取消的方法——选择适合当前工作的方式：

- **响应式** — 将 `cancelled` 信号连接到正在执行的操作，使其中止。
- **轮询式** — 在离散步骤之间调用 `ctx.is_cancelled()`。

```gdscript
## Fetches weather data, aborting early if the call is cancelled.
##
## Returns a success envelope with the payload, or an empty dict if cancelled
## mid-flight. [param params] carries the request; [param ctx] is the
## per-invocation cancellation context.
func _handle_fetch(params: Dictionary, ctx: MCPToolkitToolContext) -> Dictionary:
    # Reactive: abort the HTTP request the moment cancellation is requested.
    ctx.cancelled.connect(_http_request.cancel_request)

    var result = await _do_fetch(params.get("query", ""))

    # Polling: check between steps and return early.
    if ctx.is_cancelled():
        return {}

    return MCPToolkitSuccess.ok({"data": result})
```

## 规则

- **不要保存上下文。** 它只属于一次调用——分发器拥有它，在处理器返回后继续持有引用属于错误。
- **取消后立即返回。** 可取消处理器应停止工作并返回；空字典或 `MCPToolkitError.fail("FAILED", "cancelled")` 都可以，重点是停止，而不是继续计算。

## C#

上下文以第二个 `GodotObject` 传入，并按名称驱动（`ctx.Connect("cancelled", …)`、`(bool)ctx.Call("is_cancelled")`）。参见 `references/csharp-extensions.md`。
