@tool
extends RefCounted
## 导出干净的日志辅助 —— ANSI 剥离、日志级别检测与文件日志检测。
## 纯静态工具(RegEx + 字符串操作 + ProjectSettings/OS 读取),
## 不含任何仅编辑器符号,因此从运行时自动加载(Autoload)的依赖闭包预加载本模块是安全的
## (该闭包中任何位置出现仅编辑器名称都会导致自动加载(Autoload)在导出中解析失败 ——
## godotengine/godot#91713)。
## 必须使用 @tool,这样 `_ansi_re` 静态变量初始化器
## 才会在编辑器中运行:编辑器侧的日志工具会调用 strip_ansi,而非 @tool 脚本会跳过
## 编辑器中的静态初始化(正则会是 null → "Cannot call method
## 'sub' on a null value")。@tool 不引用任何编辑器符号,因此保持导出干净 ——
## 该注解在导出模板中被忽略(保持“即使被带入也无害”的特性)。


# -- ANSI 剥离 ------------------------------------------------------------


## 脚本加载时编译一次 —— 每个会话一次分配。
## CSI 序列:ESC [ <params> <final>  (例如 ESC[90m、ESC[0m)
## 简单转义:ESC <letter>            (例如 ESC c)
static var _ansi_re: RegEx = _compile_ansi_re()

static func _compile_ansi_re() -> RegEx:
	var re := RegEx.new()
	re.compile("\\x1b(?:\\[[0-9;]*[A-Za-z]|[A-Za-z])")
	return re


## 从字符串剥离 ANSI/VT100 转义序列。
## 在无头模式下,Godot 会在进度条和状态消息中发出 ANSI 颜色码。
## 它们包含原始的 ESC(0x1B)字节,Godot 的
## JSON.stringify() 不会转义这些字节,会产生无效的 JSON,
## 导致 TypeScript 桥接静默丢弃响应。
static func strip_ansi(text: String) -> String:
	return _ansi_re.sub(text, "", true)


# -- 日志级别检测 -------------------------------------------------------


static func detect_log_level(line: String) -> String:
	if line.begins_with("ERROR:") or line.begins_with("USER ERROR:") \
			or line.begins_with("SCRIPT ERROR:") or line.begins_with("SHADER ERROR:"):
		return "error"
	if line.begins_with("WARNING:") or line.begins_with("USER WARNING:") \
			or line.begins_with("SCRIPT WARNING:"):
		return "warning"
	return "info"


## 对 Godot 错误位置续行为 true —— 即紧跟在 ERROR:/WARNING:/SCRIPT ERROR: 消息之后
## 的缩进 "   at: …" 行(以及任何以前导空白开头的续行)。
## 用途是让多行错误作为一个单元定级:文件尾随缓冲
## 使这样的行继承前一个错误/警告级别(log_buffer.gd),
## 而 source=file 读取器把它合并进前一条记录(editor_commands.gd)。
## 传入原始(未做边缘剥离)的行,使前导空白仍然可见。
static func is_continuation_line(line: String) -> bool:
	if line.begins_with(" ") or line.begins_with("\t"):
		return true
	return line.strip_edges().begins_with("at:")


# -- 文件日志检测 ----------------------------------------------------


## 检查文件日志是否启用,包括平台特定的覆盖项。
## ProjectSettings.get_setting() 返回基础值;平台覆盖项
## (例如 debug/file_logging/enable_file_logging.windows)是单独的键。
static func is_file_logging_enabled() -> bool:
	var key := "debug/file_logging/enable_file_logging"
	if ProjectSettings.get_setting(key, false):
		return true
	for tag in ["pc", "windows", "linuxbsd", "macos", "android", "ios", "web"]:
		if OS.has_feature(tag):
			var override_key: String = key + "." + tag
			if ProjectSettings.has_setting(override_key) \
					and ProjectSettings.get_setting(override_key, false):
				return true
	return false


# -- 日志文件路径解析 --------------------------------------------------


## 配置的引擎日志文件,原始形式(未全局化):即
## debug/file_logging/log_path 项目设置的值,或引擎默认的
## user://logs/godot.log。做目录扫描的调用方(控制台文件来源)以及
## 在响应中展示该路径的调用方(运行时 debugger.get_log 读取器)使用本方法,
## 使它们保持在 user:// 空间内、默认情况下的输出逐字节不变;
## 打开文件并需要稳定绝对路径的调用方应使用 resolve_log_path()。
static func configured_log_path() -> String:
	return ProjectSettings.get_setting(
		"debug/file_logging/log_path", "user://logs/godot.log") as String


## 配置的引擎日志文件,作为绝对操作系统路径:即 configured_log_path()
## 把 user://res:// 全局化后的结果(绝对路径原样通过)。供需要稳定绝对路径、
## 不受会话中途 user:// 重映射影响的直接读取器使用 ——
## 即 4.2-4.4 的缓冲尾随与编辑器侧的 debugger.get_log 读取器。
static func resolve_log_path() -> String:
	var configured := configured_log_path()
	if configured.begins_with("user://") or configured.begins_with("res://"):
		return ProjectSettings.globalize_path(configured)
	return configured
