@tool
extends EditorExportPlugin
## 从导出的构建中自动剥离 godot_mcp_toolkit 插件文件、GDScript 扩展以及
## res://.mcp.json。C# 扩展会编译进 .NET 程序集,
## 无法按类剥离 —— 参见 docs/extending.md。
## 由 plugin.gd 注册;防止上下文协议(MCP)代码被打进游戏 PCK。
##
## 二进制令牌脚本导出模式(Godot 4.3+ 默认)会在本剥离逻辑运行之前,
## 于内置的 EditorExportGDScript 插件中把插件/扩展的 .gd 编译为 .gdc,
## 因此这些脚本会以惰性的孤立 .gdc 形式打包。不存在安全的插件内剥离手段
## (set_exclude_filter 直到 4.6 都未绑定 —— godotengine/godot#4054);该泄漏
## 只是外观问题(运行时绝不会加载)。我们只发出警告,不做剥离。

# 运行时自动加载(Autoload)身份(名称/路径 + 键/值推导)由本共享叶子模块持有,
# 因此这里的烘焙置空与插件的注册路径永远不会漂移 ——
# 导出需要置空和恢复的那一对数据只有一个家。
const AutoloadIdentity := preload("res://addons/godot_mcp_toolkit/core/autoload_identity.gd")

const _ADDON_PREFIX := "res://addons/godot_mcp_toolkit/"
const _MCP_JSON_PATH := "res://.mcp.json"

# “什么算扩展”的唯一事实来源 —— 必须与
# extension_loader.gd 的 _is_extension_candidate() GDScript 检查保持一致:扩展是
# MCPToolkitExtension 的直接子类。多级继承被有意不支持,
# (此类孤立文件在构建中无害),
# 因此剥离也刻意只做单级判断。
const _EXTENSION_BASE := "MCPToolkitExtension"

# EditorExportPlatform.ExportMessageType.EXPORT_MESSAGE_WARNING。硬编码为
# 整数序号(在 4.2–4.6 间稳定,已在引擎源码中核实),因为该枚举与
# add_message() 只在 4.4+ 上才有 GDScript 绑定 —— 静态引用在 4.2/4.3 上
# 即使位于死分支内也会解析报错。投递使用 has_method()+call(),
# 出于同样的原因。
const _EXPORT_MESSAGE_WARNING := 2

# 在 _export_begin() 中根据全局类列表构建。键 = GDScript 扩展文件
# (MCPToolkitExtension 的直接子类)的 res:// 路径。
var _extension_strip_paths: Dictionary = {}

# 泄漏检测:观察哪些插件/扩展文件真正到达我们的
# _export_file。在二进制令牌脚本模式下,内置的 EditorExportGDScript
# 插件会在本剥离逻辑运行之前吞掉所有 .gd,因此插件/扩展的脚本
# 永远到不了我们这里,而插件的非脚本文件(plugin.cfg、.uid、图标)仍会到达。
# 这种不对称告诉我们:脚本已作为孤立的 .gdc 泄漏。在
# _export_begin 中重置,在 _export_end 中读取。与版本无关 —— 无需读取预设。
var _saw_addon_script: bool = false
var _saw_addon_nonscript: bool = false
var _seen_ext: Dictionary = {}  # 扩展的 res:// 路径 → true(到达过 _export_file)


func _get_name() -> String:
	return "MCPExportStrip"


func _export_begin(_features: PackedStringArray, _is_debug: bool, _path: String, _flags: int) -> void:
	_extension_strip_paths = _compute_strip_paths(ProjectSettings.get_global_class_list())
	_saw_addon_script = false
	_saw_addon_nonscript = false
	_seen_ext = {}
	# 为烘焙把每个必需的自动加载(Autoload)置空,使其不进入 PCK;在
	# _export_end 中恢复。每一对都从共享的身份叶子模块推导。
	for entry in AutoloadIdentity.REQUIRED_AUTOLOADS:
		var key := AutoloadIdentity.settings_key(str(entry[0]))
		if ProjectSettings.has_setting(key):
			ProjectSettings.set_setting(key, null)


func _export_file(path: String, _type: String, _features: PackedStringArray) -> void:
	if path.begins_with(_ADDON_PREFIX):
		# 为泄漏检测做跟踪:在二进制模式下只有非脚本的插件文件
		# 会到达我们这里(内置的令牌化器先吃掉了 .gd)—— 参见 _decide_warning。
		if path.ends_with(".gd"):
			_saw_addon_script = true
		else:
			_saw_addon_nonscript = true
		skip()
		return
	if path == _MCP_JSON_PATH:
		skip()
		return
	if _extension_strip_paths.has(path):
		_seen_ext[path] = true
		skip()
		return


func _export_end() -> void:
	# 无条件恢复 —— 若上一次导出在烘焙中途崩溃,可借此自愈。
	for entry in AutoloadIdentity.REQUIRED_AUTOLOADS:
		ProjectSettings.set_setting(
				AutoloadIdentity.settings_key(str(entry[0])),
				AutoloadIdentity.settings_value(str(entry[1])))
	# 若二进制令牌脚本模式把插件/扩展的 .gd 以孤立 .gdc 的形式打包,则发出警告
	# (不做剥离)。不存在安全的插件内剥离手段;该泄漏只是外观问题。
	var decision := _decide_warning(_saw_addon_script, _saw_addon_nonscript, _extension_strip_paths, _seen_ext)
	if decision["warn"]:
		_emit_warning(str(decision["message"]))
	_extension_strip_paths.clear()
	_seen_ext.clear()


# 投递二进制令牌泄漏警告。get_export_platform()/add_message() 只在 4.4+ 上
# 才有 GDScript 绑定,因此使用 has_method()+call() 动态派发 ——
# 静态引用在 4.2/4.3 上即使位于死分支内也会解析报错。在 4.3 上(以及
# 作为通用回退),push_warning() 会把它显示在输出日志中,
# 而不是导出对话框里。4.2 永远不会走到这里(没有二进制模式 → 脚本以文本打包
# → 会被看到并剥离,因此 _decide_warning 返回 warn=false)。
func _emit_warning(message: String) -> void:
	if has_method("get_export_platform"):
		var platform = call("get_export_platform")
		if platform != null and platform.has_method("add_message"):
			platform.call("add_message", _EXPORT_MESSAGE_WARNING, "Godot MCP Unified", message)
			return
	push_warning(message)


# ── 扩展扫描(纯函数;在 test/run_unit_tests.gd 中有单元测试) ──────────
# 刻意只做单级 —— 与加载器对扩展的定义(MCPToolkitExtension 的
# 直接子类)一致。引擎会把基于路径的
# `extends "res://.../some_extension.gd"` 展平为具名基类,因此无论用哪种写法,
# 直接子类报告的 base 都是 MCPToolkitExtension,都会被捕获。
# 多级链(深两层及以上的类)不是扩展,
# 会作为无害的孤立文件打包。
static func _compute_strip_paths(classes: Array) -> Dictionary:
	var strip := {}  # 路径 → true
	for entry in classes:
		if entry.get("base", "") != _EXTENSION_BASE:
			continue
		var p: String = entry.get("path", "")
		if not p.is_empty() and p.ends_with(".gd"):
			strip[p] = true
	return strip


# ── 泄漏警告决策(纯函数;在 test/run_unit_tests.gd 中有单元测试) ───────
# 根据到达 _export_file 的内容,判断某个二进制令牌脚本模式
# 是否把插件/扩展脚本作为孤立的 .gdc 泄漏,并组装警告消息。
#   插件泄漏  ⟺  插件的非脚本文件被导出,但没有任何插件 .gd 被导出
#                    (被某种二进制模式吞掉了)。非脚本信号是防误报的守卫:
#                    如果用户已经排除了插件,
#                    就不会有任何插件文件到达我们这里 → saw_addon_nonscript=false
#                    → 不发警告(排除插件正是我们建议的做法)。
#   扩展泄漏    =  在 _export_begin 中计算出的、从未到达
#                    _export_file 的扩展路径。(被手动排除的单个扩展
#                    同样会被视为“未见到” —— 已在 docs/extending.md 中记为注意事项。)
# 返回 {warn, addon_leaked, leaked_ext_count, message}。
static func _decide_warning(saw_addon_script: bool, saw_addon_nonscript: bool, extension_strip_paths: Dictionary, seen_ext: Dictionary) -> Dictionary:
	var addon_leaked := saw_addon_nonscript and not saw_addon_script
	var leaked_ext_paths: Array = []
	for p in extension_strip_paths:
		if not seen_ext.has(p):
			leaked_ext_paths.append(p)
	var leaked_ext := leaked_ext_paths.size()
	if not addon_leaked and leaked_ext == 0:
		return {"warn": false, "addon_leaked": false, "leaked_ext_count": 0, "leaked_ext_paths": [], "message": ""}
	var subject := ""
	if addon_leaked:
		subject = "the Godot MCP Unified addon's scripts"
	if leaked_ext > 0:
		if subject != "":
			subject += " and "
		subject += "%d extension script(s)" % leaked_ext
	# 排除过滤器配方,逐一列明泄漏的内容,让用户可以把插件通配符
	# 以及每个具体的扩展路径直接复制进预设的过滤器。
	var filter_parts: PackedStringArray = []
	if addon_leaked:
		filter_parts.append("`res://addons/godot_mcp_toolkit/*`")
	if leaked_ext > 0:
		var quoted: PackedStringArray = []
		for p in leaked_ext_paths:
			quoted.append("`%s`" % str(p))
		filter_parts.append("the extension script(s) " + ", ".join(quoted))
	var message := (
		"Script Export Mode is binary tokens — %s ship as inert, orphaned `.gdc` " % subject
		+ "(never loaded at runtime; no effect on your game). For a clean strip, set "
		+ "Script Export Mode to \"Text\", or add %s to this preset's exclude filter." % " and ".join(filter_parts)
	)
	return {"warn": true, "addon_leaked": addon_leaked, "leaked_ext_count": leaked_ext, "leaked_ext_paths": leaked_ext_paths, "message": message}
