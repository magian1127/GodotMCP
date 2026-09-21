@tool
extends RefCounted
## 配置发现只读执行。父目录配置必须明确绑定当前工程，绝不借用邻近工程。

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


static func _same_project(binding: String, project: String) -> bool:
	var normalized := binding.strip_edges().replace("\\", "/")
	if normalized.is_empty() or not normalized.is_absolute_path() or "://" in normalized:
		return false
	normalized = normalized.simplify_path().trim_suffix("/")
	if OS.get_name() == "Windows":
		return normalized.to_lower() == project.to_lower()
	return normalized == project
