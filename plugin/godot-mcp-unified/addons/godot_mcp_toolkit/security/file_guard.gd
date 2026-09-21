@tool
extends RefCounted
## 文件系统边界强制。
##
## 每个触及文件系统的命令都会调用 resolve_safe(),在任何 I/O 之前
## 校验并规范化路径。默认只允许 res://;调用方可通过 allowed_prefixes
## 参数选择加入额外前缀(例如 user://screenshots/)。
##
## resolve_safe_user() 校验 user:// 路径:拒绝目录穿越,在规范化之后
## 重新确认 user:// 边界,然后返回全局化的绝对路径。
##
## 两个解析器把共享的穿越拒绝(_has_traversal)和规范化后的边界复查
## (_within_boundary)各自经由一个辅助函数处理。它们刻意保持不同的
## 返回形态(见各函数处的文档):resolve_safe() 返回原始的虚拟
## res:// 路径 — 调用方直接把它交给引擎 I/O,而引擎说的是 res://;
## resolve_safe_user() 返回全局化(GLOBALIZED)的绝对路径 —
## save.* 需要真实的操作系统路径才能使用 FileAccess。


## 校验并解析一个 user:// 路径。
## 成功时返回 { ok: true, absolute_path },
## 失败时返回 { ok: false, error_code, error_message }。
static func resolve_safe_user(path: String) -> Dictionary:
	var normalized := path.replace("\\", "/")
	# 拒绝 .. 段 — 目录穿越。(resolve_safe_user 把穿越报告为
	# INVALID_PATH,而 resolve_safe 报告为 PATH_DENIED,因此共享检查
	# 只返回一个类别,由每个调用方映射到自己的错误码。)
	if _has_traversal(normalized):
		return {
			"ok": false,
			"error_code": "INVALID_PATH",
			"error_message": "path contains '..': %s" % path,
		}
	# 前缀检查。
	if not path.begins_with("user://"):
		return {
			"ok": false,
			"error_code": "INVALID_PATH",
			"error_message": "path must start with user:// (got %s); res:// paths use the res:// tool family (scene.*, script.*, resource.*, folder.*)" % path,
		}
	# 拒绝工具包内部路径(令牌、审计日志、引导标记)。这是 res:// 解析器
	# 所镜像的基石:插件自己的用户数据目录不可触碰,这样工具调用就无法
	# 篡改鉴权令牌或审计日志。
	var rel := path.trim_prefix("user://")
	if rel.begins_with("addons/godot_mcp_toolkit/"):
		return {
			"ok": false,
			"error_code": "PATH_DENIED",
			"error_message": "user://addons/godot_mcp_toolkit/ is reserved for plugin internals (auth token, audit log)",
		}
	# 在规范化之后重新确认 user:// 边界(词法上的 simplify 加前缀替换 —
	# 不是操作系统符号链接解析;针对 localhost 单用户威胁模型),然后返回
	# 全局化的绝对路径(save.* 需要真实的操作系统路径才能使用 FileAccess —
	# 这就是返回形态与 resolve_safe 不同的原因)。
	if not _within_boundary(path, "user://"):
		return {
			"ok": false,
			"error_code": "PATH_DENIED",
			"error_message": "path %s resolves outside the user data dir after canonicalization" % path,
		}
	return {"ok": true, "absolute_path": ProjectSettings.globalize_path(path)}


static func resolve_safe(
	input: String, allowed_prefixes: Array = ["res://"],
) -> Dictionary:
	if input.strip_edges().is_empty():
		return _denied("empty path")

	var normalized := input.replace("\\", "/")

	# 拒绝 ".." 路径段 — 目录穿越。
	if _has_traversal(normalized):
		return _denied("path contains '..': %s" % input)

	# 拒绝绝对操作系统路径(盘符、UNC、Unix 根)。
	if normalized.length() >= 2 and normalized[1] == ":":
		return _denied("absolute OS path: %s" % input)
	# 以 "/" 开头也能覆盖 UNC 路径:上面的反斜杠->斜杠归一化已把
	# "\\server\share" 改写为 "//server/share",因此无需单独的
	# UNC 分支。
	if normalized.begins_with("/") \
			and not normalized.begins_with("res://") \
			and not normalized.begins_with("user://"):
		return _denied("absolute OS path: %s" % input)

	# 前缀允许列表。
	var matched := false
	for prefix in allowed_prefixes:
		if normalized.begins_with(str(prefix)):
			matched = true
			break
	if not matched:
		var allowed_str := ", ".join(
			allowed_prefixes.map(func(p: Variant) -> String: return str(p)))
		return _denied(
			"path must start with one of [%s] (got %s)" % [allowed_str, input])

	# 拒绝插件自己的源码目录,与上面的 user:// 基石相呼应:
	# res://addons/godot_mcp_toolkit/ 存放工具包的 GDScript,工具调用
	# 不得读取或改写它(FileGuard 与操作无关 — 在这里拒绝即可同时覆盖
	# 读取和写入)。检查的是简化后的虚拟路径(SIMPLIFIED VIRTUAL),这样
	# 落入其中的穿越(res://foo/../addons/godot_mcp_toolkit/x.gd)也会被
	# 捕获。结尾斜杠的形式是关键:不带斜杠的裸前缀
	# begins_with("res://addons/godot_mcp_toolkit") 会连带拒绝诸如
	# res://addons/godot_mcp_toolkit_extras/ 这样的同级目录 — 因此要
	# 匹配精确目录,或其以斜杠结尾的子树。其他插件(res://addons/<other>/)
	# 保持可编辑。
	var canonical := normalized.simplify_path()
	if canonical == "res://addons/godot_mcp_toolkit" \
			or canonical.begins_with("res://addons/godot_mcp_toolkit/"):
		return _denied(
			"res://addons/godot_mcp_toolkit/ is the plugin's own source and is protected")

	# 规范化并重新确认路径仍解析在其边界根之下(词法 simplify 加前缀
	# 替换 — 不是操作系统符号链接解析)。成功时返回的原始虚拟 res://
	# 路径保持不变:调用方直接把它交给引擎 I/O,而引擎说的是 res://
	# (这就是返回形态与 resolve_safe_user 不同的原因,后者返回全局化
	# 的绝对路径)。
	if normalized.begins_with("user://"):
		if not _within_boundary(input, "user://"):
			return _denied("path escapes user:// boundary: %s" % input)
	else:
		if not _within_boundary(input, "res://"):
			return _denied("path escapes project boundary: %s" % input)

	return {"path": input, "error": null}


## 若任何以斜杠分隔的段恰为 ".." 则返回 true。两个解析器共享此函数,
## 使穿越拒绝只定义一次;每个调用方把命中映射到自己的错误码
## (resolve_safe → PATH_DENIED,resolve_safe_user → INVALID_PATH)。
## 期望传入已按正斜杠归一化的路径。
static func _has_traversal(normalized: String) -> bool:
	for segment in normalized.split("/"):
		if segment == "..":
			return true
	return false


## 重新确认 `input` 在规范化之后仍解析在 `boundary_prefix`(例如 "res://"
## 或 "user://")之下。对两侧执行 globalize、simplify,再比较前缀。
## 纯词法操作 — simplify_path + globalize_path 是字符串操作,不是
## 操作系统符号链接解析。两个解析器共享此函数,使复查只定义一次;
## 每个调用方自行组织自己的拒绝消息。
static func _within_boundary(input: String, boundary_prefix: String) -> bool:
	var simplified := ProjectSettings.globalize_path(input).simplify_path()
	var root := ProjectSettings.globalize_path(boundary_prefix).simplify_path()
	return simplified.begins_with(root)


static func _denied(reason: String) -> Dictionary:
	return {"path": "", "error": "PATH_DENIED", "reason": reason}
