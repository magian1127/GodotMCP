@tool
extends RefCounted
## 本地扩展目录数据层——随附 JSON、解析、本地路径守卫与版本比较。
## 该入口绝不会发起网络请求。

const CATALOG_PATH := "res://addons/godot_mcp_toolkit/extensions/catalog.json"
const EXTENSIONS_ROOT := "res://addons/godot_mcp_toolkit/extensions"
const SUPPORTED_CATALOG_VERSION := 1


## 读取并解析随附目录。数据缺失或不可读属于可见的本地打包错误，
## 绝不构成联系远程回退(fallback)的理由。
static func read_catalog() -> Dictionary:
	if not FileAccess.file_exists(CATALOG_PATH):
		return {"ok": false, "extensions": [], "error": "LOCAL_CATALOG_MISSING"}
	var f := FileAccess.open(CATALOG_PATH, FileAccess.READ)
	if f == null:
		return {"ok": false, "extensions": [], "error": "LOCAL_CATALOG_UNREADABLE"}
	var text := f.get_as_text()
	f.close()
	return parse_catalog(text)


## 解析目录 JSON 文本。
## 返回 { "ok": bool, "extensions": Array, "error": String }。
## 特殊错误值 "NEWER_VERSION" 表示随附目录与插件版本不一致。
static func parse_catalog(json_text: String) -> Dictionary:
	var parsed = JSON.parse_string(json_text)
	if parsed == null or not parsed is Dictionary:
		return {"ok": false, "extensions": [], "error": "Invalid JSON"}

	var version = parsed.get("catalog_version", null)
	if version == null or not (version is int or version is float):
		return {"ok": false, "extensions": [], "error": "Missing or invalid catalog_version"}

	var ver_int := int(version)
	if ver_int == 0:
		return {"ok": false, "extensions": [], "error": "Invalid catalog_version (0)"}
	if ver_int > SUPPORTED_CATALOG_VERSION:
		return {"ok": false, "extensions": [], "error": "NEWER_VERSION"}

	var extensions = parsed.get("extensions", [])
	if not extensions is Array:
		return {"ok": false, "extensions": [], "error": "Invalid extensions field"}

	return {"ok": true, "extensions": extensions, "error": ""}


## 在目录路径传入 OS.shell_open 之前进行把关。只允许随附的本地扩展目录树；
## 绝对路径、URL 与目录穿越(traversal)都会被拒绝。
static func is_allowed_local_path(path: String) -> bool:
	var normalized := path.strip_edges().replace("\\", "/").trim_suffix("/")
	if normalized == EXTENSIONS_ROOT:
		return true
	if not normalized.begins_with(EXTENSIONS_ROOT + "/"):
		return false
	return ".." not in normalized.split("/", false)


## 比较两个 semver 版本字符串（"1.2.3" 与 "1.3.0"）。
## a < b 时返回 -1，相等时返回 0，a > b 时返回 1。
## 只假定数值点分版本（"1.2.3"）；
## 某一段带预发布/构建标签时（"1.0.0-beta"），
## 不会与其正式版进行排序——只比较每段开头的数字串并忽略后缀。目录内容由
## 作者控制，因此非数值版本超出处理范围，不算错误。
static func compare_versions(a: String, b: String) -> int:
	var pa := a.split(".")
	var pb := b.split(".")
	for i in maxi(pa.size(), pb.size()):
		var va := _leading_int(pa[i]) if i < pa.size() else 0
		var vb := _leading_int(pb[i]) if i < pb.size() else 0
		if va < vb:
			return -1
		if va > vb:
			return 1
	return 0


## 点分版本段开头的数值部分：即任何“-”/“+”预发布或构建标签之前的部分。
## 对于纯数值段，其结果与 int(part) 完全一致，
## 因此合法数值版本的排序不变。
static func _leading_int(part: String) -> int:
	var head := part
	var dash := head.find("-")
	if dash != -1:
		head = head.substr(0, dash)
	var plus := head.find("+")
	if plus != -1:
		head = head.substr(0, plus)
	return int(head)


## 检查扩展条目是否与当前 Toolkit 版本和 Godot 版本兼容。
static func is_compatible(entry: Dictionary, toolkit_version: String, godot_version: String) -> bool:
	var min_tk: String = str(entry.get("min_toolkit_version", ""))
	if not min_tk.is_empty() and compare_versions(toolkit_version, min_tk) < 0:
		return false
	var max_tk = entry.get("max_toolkit_version", null)
	if max_tk != null and not str(max_tk).is_empty():
		if compare_versions(toolkit_version, str(max_tk)) > 0:
			return false
	var min_gd: String = str(entry.get("min_godot_version", ""))
	if not min_gd.is_empty() and compare_versions(godot_version, min_gd) < 0:
		return false
	var max_gd = entry.get("max_godot_version", null)
	if max_gd != null and not str(max_gd).is_empty():
		if compare_versions(godot_version, str(max_gd)) > 0:
			return false
	return true


## 从 plugin.cfg 读取 Toolkit 版本。
static func get_toolkit_version() -> String:
	var cfg := ConfigFile.new()
	var err := cfg.load("res://addons/godot_mcp_toolkit/plugin.cfg")
	if err != OK:
		return "0.0.0"
	return cfg.get_value("plugin", "version", "0.0.0")


## 以“major.minor”格式的字符串读取 Godot 版本。
static func get_godot_version() -> String:
	var vi := Engine.get_version_info()
	return "%d.%d" % [vi["major"], vi["minor"]]
