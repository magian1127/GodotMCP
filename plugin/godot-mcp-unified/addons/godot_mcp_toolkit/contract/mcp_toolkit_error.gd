@tool
class_name MCPToolkitError
extends RefCounted
## 共享的上下文协议(MCP)错误契约 — 规范错误码与失败封装。
##
## 每个失败的命令都会返回由 [method fail] 构建的统一字典:
## [code]{"success": false, "error": <message>, "code": <code>}[/code],外加一个
## 可选 [code]"hint"[/code] 提供恢复指导。[constant CODES] 是
## [code]code[/code] 值的权威词汇表;[method require] 是
## 共享的"必需参数存在"检查;[method guard_response_size]
## 防止超大响应超出传输层缓冲区。与
## [MCPToolkitSuccess] 对称,后者构建成功封装。[br]
## [br]
## 示例:
## [codeblock]
## if node == null:
##     return MCPToolkitError.fail("NODE_NOT_FOUND", "no node at " + path)
## [/codeblock]

## 响应可携带的错误 [code]code[/code] 值的权威集合。
## [method fail] 会(在调试构建中)断言其收到的代码出现在此集合中,
## 因此未声明的代码会在开发期间被捕获为漂移。
const CODES: Array[String] = [
	"ALREADY_EXISTS",
	"ALREADY_PLAYING",
	"BUSY",
	"CLASS_MISMATCH",
	"COMPILATION_FAILED",
	"CONNECT_FAILED",
	"CREATE_DIR_FAILED",
	"DELETE_FAILED",
	"DIR_NOT_EMPTY",
	"DISCONNECTED",  # 保留 — 传输层/对端断开;目前不发出。
	"EDITED_SCENE",
	"EDITOR_VIEWPORT_UNAVAILABLE",
	"EMPTY_CONTENT",
	"EXECUTE_FAILED",
	"EXTERNAL_EDITOR_ACTIVE",
	"FAILED",
	"FILE_TOO_LARGE",
	"FILESYSTEM_NOT_READY",
	"FOLDER_PROTECTED",
	"GAME_NOT_RUNNING",
	"HEADLESS_UNSUPPORTED",
	"INTERNAL",
	"INVALID_CLASS",
	"INVALID_METHOD",
	"INVALID_PARAMS",
	"INVALID_PATH",
	"INVALID_STATE",
	"INVALID_VALUE",
	"LOAD_FAILED",
	"LOG_BUSY",
	"LOG_UNAVAILABLE",
	"NO_SCENE",
	"NODE_NOT_FOUND",
	"NOT_A_RESOURCE",
	"NOT_BREAKED",
	"NOT_FOUND",
	"NOT_UNIQUE",
	"PACK_FAILED",
	"PARENT_NOT_FOUND",
	"PARSE_ERROR",
	"PATH_DENIED",
	"PATH_IN_USE",
	"PROPERTY_NOT_FOUND",
	"READ_FAILED",
	"RESPONSE_TOO_LARGE",
	"RUNTIME_WINDOW_MINIMIZED",
	"SAVE_DELETE_FAILED",
	"SAVE_FAILED",
	"SAVE_READ_FAILED",
	"SAVE_WRITE_FAILED",
	"SET_FAILED",
	"TIMEOUT",
	"UNKNOWN_CLASS",
	"UNSUPPORTED",
	"UNSUPPORTED_FILE_TYPE",
	"WRITE_FAILED",
]

## 恢复提示,建议如何找到有效的节点路径,供收到
## 无法解析路径的调用点使用。将其作为 [method fail] 的 [param hint] 传入。
const HINT_NODE_PATH := "Use scene.get_tree to list valid node paths. Root node is always path '.'."

## 恢复提示,建议如何找到有效的文件路径,供收到
## 无法解析路径的调用点使用。将其作为 [method fail] 的 [param hint] 传入。
const HINT_FILE_PATH := "Use asset.list to search for files. Paths must start with res://"

## 恢复提示,建议如何找到有效的类名,供收到
## 未知类名的调用点使用。将其作为 [method fail] 的 [param hint] 传入。
const HINT_CLASS_NAME := "Use classdb.search to find valid class names."

## 按错误码键控的逐码默认恢复提示。当 [method fail] 收到
## 空提示但该码在此有条目时,该提示会被自动附加 —
## 因此恢复建议从不依赖调用点上下文的错误码会自动携带它。
## 显式传给 [method fail] 的 [param hint] 优先于此表。
##
## [code]LOG_BUSY[/code] / [code]LOG_UNAVAILABLE[/code] 有意缺席:它们的
## 恢复建议受版本门控([code]source="buffer"[/code] 仅在 Godot 4.5+ 上
## 才是真正的回退),因此每个发出点都会显式传入
## [method log_busy_hint] / [method log_unavailable_hint],而不是依赖这里的默认值。
const DEFAULT_HINTS := {
	"TIMEOUT": "The editor may be busy. Try editor_sync before retrying.",
	"UNSUPPORTED": "Check the bundled local compatibility guide for version requirements.",
	"PATH_DENIED": "Paths must use res:// format. Example: res://scenes/main.tscn",
	"PARENT_NOT_FOUND": "Parent directory does not exist. Use folder_create to create it first.",
	"COMPILATION_FAILED": "The game failed to start due to script errors. Fix the errors shown above, then call game_start again. If no errors are shown, call editor_sync to retrigger them, then log_read(channel:'editor') for the full log.",
	"GAME_NOT_RUNNING": "No running game detected. Use game_start first. If the MCP Runtime autoload is missing, re-enable the plugin in Project Settings.",
	"RESPONSE_TOO_LARGE": "The response exceeded the WebSocket transport buffer and could not be delivered. Narrow the query (filter, fewer items, a smaller range) or paginate. If large responses are expected, raise mcp_toolkit/limits/ws_buffer_kb in Project Settings and reconnect.",
}


## 验证 [param required] 中列出的每个键都以非空字符串形式
## 存在于 [param parameters] 中。当所有必需键都满足时返回
## [code]null[/code];对第一个缺失的键则返回 [code]INVALID_PARAMS[/code] 错误字典
## (由 [method fail] 构建,对已知键附带路径/类恢复提示)。
## 请在处理器开头调用它,若结果非 null 则返回该结果。
static func require(parameters: Dictionary, required: Array) -> Variant:
	for key in required:
		var val = parameters.get(key, "")
		if typeof(val) == TYPE_STRING and val.is_empty():
			var hint := ""
			match key:
				"file_path":
					hint = HINT_FILE_PATH
				"node_path":
					hint = HINT_NODE_PATH
				"path":
					hint = "Provide a res:// folder path. Example: res://scenes/"
				"class_name":
					hint = HINT_CLASS_NAME
			return fail("INVALID_PARAMS", "%s is required" % key, hint)
	return null


## 构建失败响应。[param code] 是机器可读的错误码
## (必须是 [constant CODES] 之一);[param message] 是人类可读的
## 说明。返回
## [code]{"success": false, "error": message, "code": code}[/code]。当
## [param hint] 非空时,它作为 [code]"hint"[/code] 附加;否则,如果
## [param code] 在 [constant DEFAULT_HINTS] 中有条目,则附加该默认提示。
## 这是每个处理器报告错误的规范方式。[br]
## [br]
## 示例:
## [codeblock]
## return MCPToolkitError.fail("INVALID_PATH", "path must start with res://")
## [/codeblock]
static func fail(code: String, message: String, hint: String = "") -> Dictionary:
	# 仅调试用的词汇表护栏:发出的代码不在 CODES 中即为漂移。
	# 在发布构建中被剥离,因此绝不会影响线上报文负载。
	assert(code in CODES, "error code '%s' is not declared in MCPToolkitError.CODES" % code)
	var result := {"success": false, "error": message, "code": code}
	if hint != "":
		result["hint"] = hint
	elif DEFAULT_HINTS.has(code):
		result["hint"] = DEFAULT_HINTS[code]
	return result


## 针对 [code]LOG_BUSY[/code] 失败的恢复提示 — 日志文件存在,但读取
## 打开失败。
##
## 将调用方的 [code]LogBuffer.uses_logger_api()[/code] 作为 [param can_use_buffer] 传入
## (在 Godot 4.5+ 上为 [code]true[/code]):只有在那种情况下 [code]source="buffer"[/code]
## 才是真正独立于文件的回退,因此仅在此时引导使用;在 4.2–4.4 上,缓冲区
## 尾随的是同一日志,也会同样失败,所以提示保持停止游戏/重试。
## 在 Windows 上,主因是游戏正在运行:编辑器启用安全保存,因此其
## 读取(READ)打开会请求 [code]_SH_DENYWR[/code](拒绝写入),当试玩进程
## 持有日志写打开时操作系统会拒绝 — 因此游戏退出后文件来源即解除,
## 而 [code]source="buffer"[/code](4.5+)与游戏内的运行时读取器不受影响。
## 没有游戏运行时,较罕见的原因是外部持有方(杀毒软件、文件同步、
## 备份工具)短暂锁定文件。在 POSIX 上不可达(无强制共享锁)。
## 返回要作为 [method fail] 的 [param hint] 传入的字符串。
static func log_busy_hint(can_use_buffer: bool) -> String:
	if can_use_buffer:
		return "Use source=\"buffer\" (in-memory, no file I/O — reads even while a game runs). For the file source: on Windows a running game holds the log open, so game_stop then retry; with no game running, a backup/AV/sync tool may briefly hold it — retry shortly."
	return "On Windows a running game holds the log file open — game_stop then read (source=\"buffer\" tails the same file here, so it won't bypass the lock). With no game running, retry shortly (a backup/AV/sync tool may briefly hold it)."


## 针对 [code]LOG_UNAVAILABLE[/code] 失败的恢复提示 — 日志文件无法
## 找到或读取。
##
## 将调用方的 [code]LogBuffer.uses_logger_api()[/code] 作为 [param can_use_buffer] 传入
## (在 Godot 4.5+ 上为 [code]true[/code]):只有在那种情况下 [code]source="buffer"[/code]
## 才能独立于文件日志工作,因此仅在此时提供;在 4.2–4.4 上,缓冲区读取
## 的是同一个文件,依赖同样的设置。返回要作为
## [method fail] 的 [param hint] 传入的字符串。
static func log_unavailable_hint(can_use_buffer: bool) -> String:
	var hint := "log file could not be read — enable file logging in ProjectSettings → Debug → File Logging → Enable File Logging (debug/file_logging/enable_file_logging), then restart the editor"
	if can_use_buffer:
		return hint + ". Or use source=\"buffer\" (in-memory, real-time — no file needed)."
	return hint + "."


## 针对超大截图载荷定制的 RESPONSE_TOO_LARGE 提示。捕获结果
## 溢出缓冲区时有一个通用提示不具备的模式专用逃生通道:
## 以 [code]image_response_mode:"disk"[/code] 重新请求,将 PNG 持久化到磁盘
## 并只接收其路径,或请求更小的尺寸 — 因此当超预算的结果
## 携带 [code]image_base64[/code] 时,护栏会替换为此提示。
const _IMAGE_RESPONSE_TOO_LARGE_HINT := "The captured image exceeded the WebSocket transport buffer. Retry with image_response_mode:\"disk\" to save the PNG to disk and receive only its file path, or request a smaller size. Raising mcp_toolkit/limits/ws_buffer_kb in Project Settings also works but requires a reconnect."


## 对响应进行护栏检查时在 max_bytes 之下预留的余量,单位为字节。
##
## 当 `已排队字节数 + 本载荷 > outbound_buffer_size`
## (godotengine/godot wsl_peer.cpp, ERR_OUT_OF_MEMORY;稳定于 4.2–4.5)时,
## 原生 WS 发送会整体拒绝一帧 — 绝不分块。被检查的大小是
## 原始 UTF-8 载荷,但比较还包含先前发送的、已排队但尚未
## 冲刷给慢对端的内容,以及 wslay 按消息的成帧簿记。
## 此余量吸收这些不可见的在途积压,使被我们判定为"安全"的
## 响应仍能通过缓冲区。公开供构建侧(如 ScreenshotResponse)在
## 发送之前用同一口径预判载荷是否放得下。
const SIZE_GUARD_MARGIN := 4096


## 返回 [param dict] 转为 JSON 字符串后的 UTF-8 字节长度 —
## WebSocket 发送路径度量的单位(不是字符数)。纯函数;在编辑器
## 与运行时中都可安全调用。
static func response_byte_size(dict: Dictionary) -> int:
	return JSON.stringify(dict).to_utf8_buffer().size()


## 对已完整构建的 JSON-RPC 响应按对端的发送缓冲区进行大小护栏检查。
##
## 当响应放得下时原样返回 [param response];否则 — 即其 UTF-8 字节
## 长度将超过 [param max_bytes] 减去成帧余量时 — 返回一个尺寸安全的
## 替代响应,保留 [code]jsonrpc[/code] + [code]id[/code],并将
## [code]result[/code] 替换为紧凑的 [code]RESPONSE_TOO_LARGE[/code] 失败
## (携带恢复提示)。该替代响应在结构上就很小,因此
## 调用方可直接发送而无需再次检查。[br]
## [br]
## [param max_bytes] 是对端的 outbound_buffer_size(在连接接受(accept)时捕获),
## 因此该护栏对编辑器服务器与运行时服务器工作方式相同 —
## 两者都经共享的 ws_transport accept_pending() 从
## [code]mcp_toolkit/limits/ws_buffer_kb[/code] 设置对端缓冲,此处不会重新读取
## ProjectSetting。
static func guard_response_size(response: Dictionary, max_bytes: int) -> Dictionary:
	if max_bytes <= 0:
		return response  # 未配置缓冲区上限 — 没有需要防护的内容。
	if response_byte_size(response) <= max_bytes - SIZE_GUARD_MARGIN:
		return response
	var safe := {}
	if response.has("jsonrpc"):
		safe["jsonrpc"] = response["jsonrpc"]
	safe["id"] = response.get("id", null)
	# 超大的截图载荷会使用模式专用的逃生通道(持久化到
	# 磁盘 / 更小的尺寸);其他任何超预算结果保留通用的
	# 缩小范围/分页建议。
	var hint := DEFAULT_HINTS["RESPONSE_TOO_LARGE"]
	var result_payload = response.get("result")
	if typeof(result_payload) == TYPE_DICTIONARY and (result_payload as Dictionary).has("image_base64"):
		hint = _IMAGE_RESPONSE_TOO_LARGE_HINT
	safe["result"] = fail("RESPONSE_TOO_LARGE",
		"response too large for the transport buffer (limit ~%d KB)" % int(max_bytes / 1024),
		hint)
	return safe
