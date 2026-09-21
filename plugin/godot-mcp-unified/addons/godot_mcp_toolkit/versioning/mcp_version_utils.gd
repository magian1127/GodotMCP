@tool
extends RefCounted
## 版本辅助工具:引擎版本比较,外加插件自身的版本读取。
##
## 提供 is_at_least / is_at_most / is_version_in_range 检查,针对当前运行的
## 引擎版本进行比较,用于按命令的最低/最高 Godot 版本要求做门控;
## 另有 read_plugin_version(),读取工具集自己的 plugin.cfg。

## 已测试的最新版本。高于此版本的仍可运行,但会记录一条提示。
const GODOT_TESTED_MAX_VERSION := "4.7.0"


## 在 `godot --headless`(无显示服务器)下运行时为真。
## 用于对需要视口或正在运行游戏的工具做门控。
static func is_headless() -> bool:
	return DisplayServer.get_name() == "headless"


static func get_engine_version_pair() -> String:
	var info := Engine.get_version_info()
	return "%d.%d" % [info["major"], info["minor"]]


## 当前运行引擎的 "major.minor" 组合等于 [param target] 时为真
## (例如 "4.2" —— 精确匹配,因此 4.2.x 的每个补丁版本都会命中,而未来的
## 5.2 不会)。仅用于精确锁定单个次版本的情形;版本区间请使用
## [method is_at_least] / [method is_at_most]。
static func is_engine_version_pair(target: String) -> bool:
	return get_engine_version_pair() == target


## 当前运行引擎的 major/minor,以整数对表示 —— [code]Vector2i(major, minor)[/code]
## ([code].x[/code] = major,[code].y[/code] = minor)。供把版本当作纯数据使用
## 的判定谓词(例如陈旧活动实例提示的输入);同一组合的字符串形式见
## [method get_engine_version_pair]。
static func get_engine_version_ints() -> Vector2i:
	var info := Engine.get_version_info()
	return Vector2i(int(info["major"]), int(info["minor"]))


## 工具集自身的版本字符串,读取自它的 plugin.cfg。
## 配置无法加载时返回 "unknown"。
static func read_plugin_version() -> String:
	var cfg := ConfigFile.new()
	var err := cfg.load("res://addons/godot_mcp_toolkit/plugin.cfg")
	if err != OK:
		return "unknown"
	return cfg.get_value("plugin", "version", "unknown")


static func is_at_least(engine_ver: String, min_ver: String) -> bool:
	return _compare(_parse(engine_ver), _parse(min_ver)) >= 0


static func is_at_most(engine_ver: String, max_ver: String) -> bool:
	return _compare(_parse(engine_ver), _parse(max_ver)) <= 0


static func is_version_in_range(engine_ver: String, min_ver: String, max_ver: String) -> bool:
	var engine := _parse(engine_ver)
	if min_ver != "" and _compare(engine, _parse(min_ver)) < 0:
		return false
	if max_ver != "" and _compare(engine, _parse(max_ver)) > 0:
		return false
	return true


static func _parse(v: String) -> Array[int]:
	var parts := v.split(".")
	return [int(parts[0]) if parts.size() > 0 else 0,
			int(parts[1]) if parts.size() > 1 else 0]


static func _compare(a: Array[int], b: Array[int]) -> int:
	if a[0] != b[0]:
		return a[0] - b[0]
	return a[1] - b[1]
