@tool
class_name MCPToolkitSuccess
extends RefCounted
## 成功响应构建器 — 与 [method MCPToolkitError.fail] 对称。
##
## [method ok] 为每个成功响应盖上 [code]"success": true[/code],因此
## 处理器不会意外遗漏调度契约要求的键。
## 请对每个成功返回都使用它,正如每个失败都使用
## [MCPToolkitError] 一样。


## 为 [param data] 盖上 [code]"success": true[/code] 并作为
## 成功响应返回。[param data] 携带处理器的结果载荷(默认是
## 空字典)。返回的字典就是 [param data] 本身,就地修改。
## [codeblock]
## return MCPToolkitSuccess.ok({"value": 42})
## # => {"value": 42, "success": true}
## [/codeblock]
static func ok(data: Dictionary = {}) -> Dictionary:
	data["success"] = true
	return data
