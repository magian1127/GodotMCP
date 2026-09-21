@tool
extends RefCounted
## 项目 MCP 配置的读取与工程根目录 .mcp.json 的显式写入。
##
## 拥有插件对 .mcp.json 的全部访问：既负责读取（路径解析、godot
## 服务器条目的环境变量、只读检测），也负责构建由插件发起的写入。
## 写入生成 daemon HTTP 条目（type=http + 回环 URL，与
## scripts/install-godot-project.ps1 写出的形态一致）：机器级单例 daemon 由
## 编辑器边车或宿主安装器拉起，项目配置不再引用本地 Node server
## （Node 桥已随插件 1.1.0 退役）。
## 该文件的编辑权始终归用户；插件只在用户显式操作时写入
## （停靠面板(dock) / 工具菜单的“写入 .mcp.json”），
## 并保留现有文件中的 GODOT_MCP_* 环境变量键。
## 按设计不含界面(UI)：写入通过注入的 on_result Callable 上报结果，
##
## 使覆盖确认对话框留在共享写入流程中、
## 反馈（弹出提示(toast)）留在各调用方；本仓库只触碰文件本身，
## 绝不触及 EditorInterface / 对话框 / 弹出提示。
## 随附的环境/结构骨架：写入流程读取其 env 块作为基础环境
## （GODOT_MCP_CONFIG_VERSION）；服务器条目为常量 daemon HTTP 形态。

const ConfigDiscovery := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_discovery.gd")

# 由本集成插件拥有的 mcpServers 键。
const _TEMPLATE_PATH := "res://addons/godot_mcp_toolkit/.mcp.json.template"

# daemon 的回环监听面(ADR-0002;端口与 daemon 默认一致,env 可覆盖)。
const _DAEMON_URL := "http://127.0.0.1:6590/"

# 按设计不存在网络/npm 回退(fallback)。
const _SERVER_KEY := "godot"

# 仅编辑器 + 主线程使用，因此共享一个实例是安全的；
# 每次 parse() 都会覆盖先前的数据。
# 环境变量按定义都是字符串。JSON 可能把它们存成整数
static var _json := JSON.new()


static func get_mcp_json_path() -> String:
	return ProjectSettings.globalize_path("res://") + ".mcp.json"


## 查看及读取可以采用明确绑定当前工程的父目录配置；写入路径保持独立。
static func get_read_mcp_json_path() -> String:
	return ConfigDiscovery.find_for_project(ProjectSettings.globalize_path("res://"))


static func has_mcp_json() -> bool:
	return not get_read_mcp_json_path().is_empty()


static func get_all_env_vars() -> Dictionary:
	var raw := _read_server_env()
	# （例如 "GODOT_MCP_READ_ONLY": 1 而不是 "1"）。
	# 把所有值统一转换为 String，使调用方可以安全地用 == "1" 等方式比较。
	# 解析 .mcp.json 且不刷屏控制台。JSON.parse_string() 在文件格式错误时
	var coerced: Dictionary = {}
	for key in raw:
		coerced[key] = str(raw[key])
	return coerced


## 会 ERR_PRINT（“Parse JSON failed. Error at line ...”），
## 而停靠面板约每 1 秒复查一次有效性——因此改用 JSON.new().parse()，
## 它通过返回码静默上报失败。返回解析后的对象；
## 文件缺失 / 不是有效 JSON / 不是 JSON 对象时返回 null。
## 在已解析的 .mcp.json / 模板对象中导航到 Toolkit 服务器条目的 env 字典。
static func _parse_mcp_json():
	return ConfigDiscovery.read_document(get_read_mcp_json_path())


static func _read_server_env() -> Dictionary:
	var parsed = _parse_mcp_json()
	if parsed == null:
		return {}
	return _extract_server_env(parsed)


## 没有匹配的服务器条目或没有 env 字典时返回 {}。
## 实时文件读取与模板基础读取共用此方法，使结构遍历只存在一处。
## 当且仅当 .mcp.json 在服务器条目上设置了 GODOT_MCP_READ_ONLY=1 时为真。
static func _extract_server_env(parsed: Dictionary) -> Dictionary:
	var server_entry := ConfigDiscovery.server_entry(parsed)
	var env = server_entry.get("env", {})
	return env if env is Dictionary else {}


## 共享查询（停靠面板子面板 + 按钮、信息对话框）——只解析一次，只此一个归宿。
## 连续需要两次的调用方应缓存结果
## （停靠面板在每次状态刷新时如此做），而不是重新解析。
## 当且仅当 .mcp.json 存在但不是有效 JSON（或不是 JSON 对象）时为真——
static func is_read_only() -> bool:
	var env := get_all_env_vars()
	return env.get("GODOT_MCP_READ_ONLY", "") == "1"


## 上下文协议(MCP)客户端无法解析它，因此不会启动服务器
## （get_all_env_vars 会静默返回 {}）。
## 这是关于文件当前内容的实时事实(FACT)——与文件存在性一样，
## 可以安全地在停靠面板的 1 秒定时器上检查；只读（服务器状态）则不同。
static func is_malformed() -> bool:
	return has_mcp_json() and _parse_mcp_json() == null


## 当且仅当项目根目录已存在 .mcp.json（写入会将其覆盖）时为真。
## 共享写入流程用它决定在调用 write_from_template() 之前
## 是否显示覆盖确认对话框。
static func needs_overwrite_confirm() -> bool:
	return FileAccess.file_exists(get_mcp_json_path())


## daemon HTTP 条目为常量内容,写入没有本地文件系统前置条件(Node 时代需要
## 证实随附 server/dist 入口存在,已随 Node 桥退役)。保留函数以维持
## 启用提议(mcp_json_enable_prompt)等调用点。
static func can_write_mcp_json() -> bool:
	return true


## 构建本地 mcpServers 服务器条目（daemon HTTP 形态）。
## 与 scripts/install-godot-project.ps1 写出的条目一致：type=http + 回环 URL；
## 认证 token 由各宿主的安装器注入,项目级条目保持无凭据形态(同规)。
static func build_server_entry() -> Dictionary:
	return {"type": "http", "url": _DAEMON_URL}


## 写入 .mcp.json，并通过 on_result 上报结果，
## 使界面(UI)（弹出提示(toast)）留在调用方一侧。on_result 只会被调用一次，参数为：
##   (ok: bool, message: String, severity: int, tooltip: String)
## — severity 采用编辑器弹出提示刻度（0 信息 / 1 警告 / 2 错误），
## 调用方将其直接转发给自己的弹出提示。模板缺失 -> 错误报告；
## 文件已存在且 force_overwrite == false -> “需要确认”报告
## （防御性——共享写入流程会先通过 needs_overwrite_confirm() 预检）；
## 否则构建 daemon HTTP 条目内容（见 build_server_entry）并报告
## 成功（信息级，以目标路径作为提示(tooltip)）
## 或打开失败（错误级）。不含界面：无对话框、无 EditorInterface、无弹出提示。
static func write_from_template(force_overwrite: bool, on_result: Callable) -> void:
	if not FileAccess.file_exists(_TEMPLATE_PATH):
		on_result.call(false, "Template not found: " + _TEMPLATE_PATH, 2, "")
		return
	var dest := get_mcp_json_path()
	if not force_overwrite and needs_overwrite_confirm():
		# 防御性：停靠面板本应先显示确认对话框。
		# 只报告不写入，确保不会发生未经确认的覆盖。
		on_result.call(false, ".mcp.json already exists — overwrite not confirmed", 1, dest)
		return
	_do_write(dest, _build_content(), on_result)


## 执行把 `content` 实际写入 `dest` 的操作，并通过 on_result 上报。
## 仓库内部私有——公开入口是 write_from_template()。
static func _do_write(dest: String, content: String, on_result: Callable) -> void:
	var file := FileAccess.open(dest, FileAccess.WRITE)
	if file == null:
		on_result.call(
			false, "Failed to write .mcp.json (err %d)" % FileAccess.get_open_error(), 2, ""
		)
		return
	file.store_string(content)
	file.close()
	on_result.call(true, "MCP: .mcp.json written", 0, "Wrote to " + dest)


## 分层合并最终生成的服务器 env，最通用者优先：先是模板的基础 env
## （GODOT_MCP_CONFIG_VERSION），再叠加现有(EXISTING)文件的 env，
## 使用户自己的 GODOT_MCP_* 键（固定端口、只读、令牌/项目路径）
## 在重写后得以保留。纯函数——不修改任何输入；
## 首次创建时 [param existing_env] 为 {}。返回服务器条目合并后的 env。
static func merge_server_env(base_env: Dictionary, existing_env: Dictionary) -> Dictionary:
	var env: Dictionary = base_env.duplicate()
	env.merge(existing_env, true)
	return env


## 构建 .mcp.json 内容——完整的文档字符串。
## 服务器条目委托给 [method _build_entry] 构建并包装
## （见 [method _stringify_entry]）。
static func _build_content() -> String:
	return _stringify_entry(_build_entry())


## 将服务器条目序列化为完整的 .mcp.json 文档字符串
## （制表符缩进，以换行结尾）。
static func _stringify_entry(entry: Dictionary) -> String:
	var document := {"mcpServers": {_SERVER_KEY: entry}}
	return JSON.stringify(document, "\t") + "\n"


## 构建服务器条目（{type, url, env}），并分层合并 env
## 以保留现有文件中的用户键（见 merge_server_env）。
static func _build_entry() -> Dictionary:
	var entry := build_server_entry()
	# 在下一次读取之前，先用 duplicate() 把每次读取到的 env 实体化——
	# 各次读取共用同一个 JSON 解析器，后续解析会使先前返回的切片失效。
	# 模板基础保证 GODOT_MCP_CONFIG_VERSION；保留现有文件的 env，
	# 确保重写绝不会剥除用户的 GODOT_MCP_* 键。
	var base_env := _read_template_env().duplicate()
	var existing_env: Dictionary = {}
	if has_mcp_json():
		existing_env = _read_server_env().duplicate()
	entry["env"] = merge_server_env(base_env, existing_env)
	return entry


## 随附模板的服务器条目 env 字典（即写入的基础 env），
## 模板缺失 / 不是有效 JSON 时返回 {}。
static func _read_template_env() -> Dictionary:
	var text := FileAccess.get_file_as_string(_TEMPLATE_PATH)
	if _json.parse(text) != OK or not _json.data is Dictionary:
		return {}
	return _extract_server_env(_json.data)
