@tool
extends RefCounted
## 对导出安全地从环境中解析一个监听通道的端口配置:钉住的精确端口、重新
## 安置的扫描区间,或内置的默认区间。由编辑器服务器(transport/mcp_server.gd)
## 与模式 B 运行时自动加载(runtime/mcp_runtime_server.gd)共享。
##
## 仅核心 API(OS.get_environment)— 不引用任何 Editor* 符号,也不 preload
## 任何被编辑器污染的东西,因为运行时自动加载会 preload 本文件,而 GDScript
## 在解析期解析标识符,一个编辑器引用就会让自动加载在导出模板中解析失败
## (godot#91713)。没有 class_name:使用方以
## [code]const PortConfig := preload(...)[/code] 的方式访问它。
##
## 环境查找作为可选 Callable 注入,让纯解析逻辑可以无头单元测试 —
## OS.get_environment 无法在运行中的进程里设置,因此测试传入一个由字典
## 支持的提供者来代替真实环境。

## 每个通道两种互斥的模式。存在钉住 ⇒ [constant
## MODE_PINNED],区间被忽略;没有钉住 ⇒ [constant MODE_SCANNED]。
const MODE_PINNED := "pinned"
const MODE_SCANNED := "scanned"

# 有效的 TCP 端口边界(含端点)。钉住值或区间边界超出此范围属于配置错误,
# 会向用户暴露,绝不静默截断。
const _PORT_FLOOR := 1
const _PORT_CEIL := 65535


## 从环境解析一个通道的监听配置。
##
## [param pin_var] 是精确端口的环境变量(例如 "GODOT_MCP_EDITOR_PORT");
## [param min_var] / [param max_var] 是扫描区间边界变量
## ("..._PORT_MIN" / "..._PORT_MAX")。[param default_min] / [param default_max]
## 是未设置区间环境变量时使用的内置区间。[param getenv] 是可选的
## [code]func(name: String) -> String[/code] 提供者(默认 OS.get_environment),
## 让单元测试可以注入固定环境。
##
## 返回一个 Dictionary:
## [br]- [code]mode[/code]:[constant MODE_PINNED] 或 [constant MODE_SCANNED]。
## [br]- [code]port[/code]:钉住的端口(pinned 模式),否则为 -1。
## [br]- [code]port_min[/code] / [code]port_max[/code]:扫描区间(scanned
##   模式);pinned 模式下两者都等于钉住值。
## [br]- [code]source[/code]:"env-pin"、"env-band" 或 "default" — 用于启动日志。
## [br]- [code]band_ignored[/code]:钉住与区间变量同时设置时为 true
##   (区间被忽略 — 调用方会记录一行说明)。
## [br]- [code]error[/code]:成功时为空;钉住格式错误、值越界或 MIN > MAX 时,
##   为一条可直接记录日志、指名问题变量的消息。非空的 error 对该通道是
##   致命的 — 调用方绝不能回退到静默默认值。
static func resolve(pin_var: String, min_var: String, max_var: String,
		default_min: int, default_max: int, getenv := Callable()) -> Dictionary:
	# OS.get_environment 返回 Variant String;测试接缝可能返回任何东西,
	# 因此在这个动态边界上的每次读取都用 str() 强转。
	var read: Callable = getenv
	if not read.is_valid():
		read = func(name: String) -> String:
			return OS.get_environment(name)

	var pin_raw := str(read.call(pin_var)).strip_edges()
	var min_raw := str(read.call(min_var)).strip_edges()
	var max_raw := str(read.call(max_var)).strip_edges()
	# 空字符串 == 未设置(光秃秃的 "GODOT_MCP_EDITOR_PORT=" 视同不存在)。
	var band_set := not min_raw.is_empty() or not max_raw.is_empty()

	if not pin_raw.is_empty():
		if not pin_raw.is_valid_int():
			return _error(pin_var, "must be an integer port (got \"%s\")" % pin_raw)
		var pin := pin_raw.to_int()
		if pin < _PORT_FLOOR or pin > _PORT_CEIL:
			return _error(pin_var, "%d is outside the valid port range %d-%d" % [
				pin, _PORT_FLOOR, _PORT_CEIL])
		# 钉住模式独占优先 — 区间被忽略,而不是混合。
		return {
			"mode": MODE_PINNED,
			"port": pin,
			"port_min": pin,
			"port_max": pin,
			"source": "env-pin",
			"band_ignored": band_set,
			"error": "",
		}

	if not band_set:
		return {
			"mode": MODE_SCANNED,
			"port": -1,
			"port_min": default_min,
			"port_max": default_max,
			"source": "default",
			"band_ignored": false,
			"error": "",
		}

	# 自定义区间 — 每个边界独立覆盖自己的默认值;缺失的一侧保留默认。
	# 两侧都必须是范围内的整数,且 MIN <= MAX。
	var low := default_min
	var high := default_max
	if not min_raw.is_empty():
		if not min_raw.is_valid_int():
			return _error(min_var, "must be an integer port (got \"%s\")" % min_raw)
		low = min_raw.to_int()
	if not max_raw.is_empty():
		if not max_raw.is_valid_int():
			return _error(max_var, "must be an integer port (got \"%s\")" % max_raw)
		high = max_raw.to_int()
	if low < _PORT_FLOOR or low > _PORT_CEIL:
		return _error(min_var, "%d is outside the valid port range %d-%d" % [
			low, _PORT_FLOOR, _PORT_CEIL])
	if high < _PORT_FLOOR or high > _PORT_CEIL:
		return _error(max_var, "%d is outside the valid port range %d-%d" % [
			high, _PORT_FLOOR, _PORT_CEIL])
	if low > high:
		return _error(min_var, "%d is greater than %s (%d) — the scan band is empty" % [
			low, max_var, high])
	return {
		"mode": MODE_SCANNED,
		"port": -1,
		"port_min": low,
		"port_max": high,
		"source": "env-band",
		"band_ignored": false,
		"error": "",
	}


# 构造一个解析失败的结果并指名问题环境变量,让停靠面板与控制台能确切告诉
# 用户要修什么。模式/端口字段是惰性的(-1)— 调用方先看 `error`,把非空
# 值当作致命错误。
static func _error(var_name: String, detail: String) -> Dictionary:
	return {
		"mode": MODE_SCANNED,
		"port": -1,
		"port_min": -1,
		"port_max": -1,
		"source": "error",
		"band_ignored": false,
		"error": "%s %s" % [var_name, detail],
	}
