@tool
extends RefCounted
## 配置发现只读执行。父目录配置必须明确绑定当前工程，绝不借用邻近工程。
## 同处提供条目形态判定(Node 桥退役前的 stdio 旧形态 / 指向已退役入口),
## 使停靠面板的"迁移"提示与写入路径共享同一判据。

const _SERVER_KEY := "godot"
## 2026-10 键简化前的旧键:读取时兼容,让既有工程的 .mcp.json 继续被发现;
## 写入始终使用 _SERVER_KEY,重写即完成迁移。
const _LEGACY_SERVER_KEYS := ["godot-mcp-unified"]


static func find_for_project(project_path: String) -> String:
	var project := project_path.replace("\\", "/").simplify_path().trim_suffix("/")
	var local_path := project.path_join(".mcp.json")
	# 工程内的损坏文件也必须保留为检测对象，不能被父目录配置掩盖。
	if FileAccess.file_exists(local_path):
		return local_path
	var directory := project.get_base_dir()
	while not directory.is_empty() and directory != project:
		var candidate := directory.path_join(".mcp.json")
		var entry := server_entry(read_document(candidate))
		var env = entry.get("env", {})
		if env is Dictionary:
			var binding := str(env.get("GODOT_MCP_PROJECT_PATH", ""))
			if _same_project(binding, project):
				return candidate
		var parent := directory.get_base_dir()
		if parent == directory:
			break
		directory = parent
	return ""


static func read_document(path: String):
	if path.is_empty() or not FileAccess.file_exists(path):
		return null
	var parser := JSON.new()
	if parser.parse(FileAccess.get_file_as_string(path)) != OK or not parser.data is Dictionary:
		return null
	return parser.data


static func server_entry(document: Variant) -> Dictionary:
	if not document is Dictionary:
		return {}
	var servers = document.get("mcpServers", {})
	if not servers is Dictionary:
		return {}
	var entry = servers.get(_SERVER_KEY, {})
	if entry is Dictionary and not entry.is_empty():
		return entry
	# 旧键兼容:文档仍用 godot-mcp-unified 时按旧键读取(写入会用新键重写)。
	for legacy_key in _LEGACY_SERVER_KEYS:
		entry = servers.get(legacy_key, {})
		if entry is Dictionary and not entry.is_empty():
			return entry
	return {}


## Node 桥(2026-09-14 退役)的 server 入口相对后缀:退役前写出的条目把
## command/args 指向它,而该文件已随桥一并删除——命中即客户端必然启动失败。
const _RETIRED_ENTRY_SUFFIX := "server/dist/index.js"


## 当且仅当该条目指向已退役的 Node 桥入口(server/dist/index.js)时为真。
## 用于把迁移提示说准:命中的是"入口是已退役的 Node 桥",而不是泛泛的"文件不在"。
static func points_at_retired_entry(entry: Dictionary) -> bool:
	var candidates: Array = [str(entry.get("command", ""))]
	var args = entry.get("args", [])
	if args is Array:
		for arg in args:
			candidates.append(str(arg))
	for candidate in candidates:
		if candidate.replace("\\", "/").contains(_RETIRED_ENTRY_SUFFIX):
			return true
	return false


## 当且仅当条目的 command 是绝对路径、且该文件已不存在时为真——
## 此时 host spawn 必然失败。
## 只看绝对路径:相对或裸命令(如 "node"、"npx")由 host 按自身 PATH 解析,
## 插件无权判定其存在性,误报比漏报更糟。典型来源是条目在另一台机器或
## 另一套目录布局下写出,或 addon 的 bin/<rid>/ 产物从未就位。
static func command_is_missing(entry: Dictionary) -> bool:
	var command := str(entry.get("command", "")).strip_edges()
	if command.is_empty() or not command.is_absolute_path():
		return false
	return not FileAccess.file_exists(command)


static func _same_project(binding: String, project: String) -> bool:
	var normalized := binding.strip_edges().replace("\\", "/")
	if normalized.is_empty() or not normalized.is_absolute_path() or "://" in normalized:
		return false
	normalized = normalized.simplify_path().trim_suffix("/")
	if OS.get_name() == "Windows":
		return normalized.to_lower() == project.to_lower()
	return normalized == project
