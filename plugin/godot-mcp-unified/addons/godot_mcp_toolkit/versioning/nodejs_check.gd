@tool
extends RefCounted
## 检测是否已安装 Node.js 且满足最低版本要求。
## 检测逻辑集中于此,让所有 UI 界面走同一条路径,并统一采用
## macOS/Linux 登录 shell 回退(针对 nvm 之类的版本管理器)。


## 检查是否已安装 Node.js 且满足最低版本要求(22+)。
## 返回 { "found": bool, "version": String, "meets_minimum": bool }。
## 在 macOS/Linux 上,若直接执行 "node" 命令失败,则回退到登录
## shell 检查 —— GUI 应用(例如从 Finder 打开的 Godot)继承不到安装了
## 版本管理器(nvm、fnm、volta)的那个 shell PATH。
## 上下文协议(MCP)客户端从终端运行,无论如何都能找到 Node,因此
## 登录 shell 命中即视为 "found",无需警告。
static func check(min_major: int = 22) -> Dictionary:
	var result := _try_direct(min_major)
	if result["found"]:
		return result
	var os_name := OS.get_name()
	if os_name == "macOS" or os_name == "Linux":
		return _try_login_shell(min_major)
	return result


static func _try_direct(min_major: int) -> Dictionary:
	var output := []
	var exit_code := OS.execute("node", ["--version"], output, true)
	if exit_code != 0 or output.is_empty():
		return {"found": false, "version": "", "meets_minimum": false}
	return _parse_version(output[0], min_major)


static func _try_login_shell(min_major: int) -> Dictionary:
	var output := []
	var exit_code := OS.execute(_login_shell(), ["-l", "-c", "node --version"], output, true)
	if exit_code != 0 or output.is_empty():
		return {"found": false, "version": "", "meets_minimum": false}
	return _parse_version(output[0], min_major)


# 用户由 $SHELL 指定的登录 shell(未设置时默认 /bin/bash)—— 正是这个接缝
# 让版本管理器安装的 Node 能被解析到,版本探测会用到它。
static func _login_shell() -> String:
	var shell: String = OS.get_environment("SHELL")
	return shell if not shell.is_empty() else "/bin/bash"


static func _parse_version(raw_output: String, min_major: int) -> Dictionary:
	var raw: String = raw_output.strip_edges()
	if not raw.begins_with("v"):
		return {"found": true, "version": raw, "meets_minimum": false}
	var parts := raw.substr(1).split(".")
	if parts.is_empty():
		return {"found": true, "version": raw, "meets_minimum": false}
	var major := parts[0].to_int()
	return {"found": true, "version": raw, "meets_minimum": major >= min_major}
