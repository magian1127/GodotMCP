@tool
extends RefCounted
## 服务可执行文件（shim / daemon）落点解析——单一出处。
##
## 三种安装形态与各自的存放位置：
##   1. **本地开发（链接安装）**：每个 Godot 工程把 addon 目录链接到本仓库，
##      因此服务在整台机器上只有**一份**，位于仓库的
##      `server-dotnet/publish/<rid>/`；开发时不需要任何"发布"步骤。
##      链接下的 ".." 是词法折叠（Godot 看到的是工程侧路径，回退会落到
##      <工程>/server-dotnet/…），因此这里先显式解析链接目标再回退。
##   2. **随包分发（复制安装）**：addon 被整份复制，服务随包落在
##      `<addon>/bin/<rid>/`（由 server-dotnet 的发布/构建同步产出）。
##   3. **显式覆盖**：`GODOT_MCP_SHIM_EXE` / `GODOT_MCP_DAEMON_EXE`——
##      设置即独占（路径不存在时返回空，绝不静默回退到别的副本）。
##
## shim 与 daemon 必须同目录（shim 缺省从自身同目录解析 daemon），因此两者
## 共用同一套目录候选：shim 命中 publish/<rid>/ 时，daemon 必然也在那里。
##
## 必须保持"对导出友好"：只引用 Engine / OS / ProjectSettings / FileAccess /
## DirAccess 等核心单例（本文件随 modules.gd 的预加载闭包进入运行时自动加载）。

const PlatformRid := preload("res://addons/godot_mcp_toolkit/paths/platform_rid.gd")

const _ADDON_DIR := "res://addons/godot_mcp_toolkit"
# 仓库内存放位置：<该 addon 真实目录>/../../server-dotnet/publish/<rid>/
const _REPO_PUBLISH_SUBDIR := "server-dotnet/publish"
# 随包分发位置：<addon>/bin/<rid>/
const _BUNDLED_SUBDIR := "bin"

const _SHIM_BASENAME := "godot-mcp-shim"
const _DAEMON_BASENAME := "godot-mcp-daemon"

const _ENV_SHIM := "GODOT_MCP_SHIM_EXE"
const _ENV_DAEMON := "GODOT_MCP_DAEMON_EXE"


## 工程的 .mcp.json 条目要 spawn 的 shim 可执行文件路径；找不到时返回空串。
static func shim_path() -> String:
	return _resolve(_SHIM_BASENAME, _ENV_SHIM)


## daemon 可执行文件路径（编辑器边车与 shim 的自举目标）；找不到时返回空串。
static func daemon_path() -> String:
	return _resolve(_DAEMON_BASENAME, _ENV_DAEMON)


static func _resolve(basename: String, env_var: String) -> String:
	var override := OS.get_environment(env_var)
	if not override.is_empty():
		return override if FileAccess.file_exists(override) else ""
	var filename := basename + (".exe" if OS.get_name() == "Windows" else "")
	var rid := PlatformRid.current()
	for directory in _candidate_dirs():
		var candidate := directory.path_join(rid).path_join(filename)
		if FileAccess.file_exists(candidate):
			# 归一化:写进 .mcp.json 的 command 要是一条干净的绝对路径。
			return candidate.simplify_path()
	return ""


## 候选目录，按优先级：仓库存放位置（本地开发）→ 随包位置（复制安装）
## → 工程根恰为仓库时的词法回退（直接以仓库 addon 为工程的情形）。
## 返回 PackedStringArray（而非 Array）以便调用方的路径拼接保持静态类型推断。
static func _candidate_dirs() -> PackedStringArray:
	var addon_dir := ProjectSettings.globalize_path(_ADDON_DIR)
	var real_addon := _addon_real_dir(addon_dir)
	return PackedStringArray([
		real_addon.path_join("../..").path_join(_REPO_PUBLISH_SUBDIR),
		addon_dir.path_join(_BUNDLED_SUBDIR),
		addon_dir.path_join("../..").path_join(_REPO_PUBLISH_SUBDIR),
	])


## addon 目录的真实位置：链接（符号链接/联接）安装时解析链接目标，使模块内的
## ".." 回退作用在仓库的真实目录上；非链接安装 read_link 返回空、原样返回。
## 刻意不先判 is_link：Windows 上联接(junction)与符号链接的判定语义不同，
## 而 "read_link 返回空" 已经足以表达"不是链接"。
static func _addon_real_dir(addon_dir: String) -> String:
	var dir := DirAccess.open(addon_dir)
	if dir == null:
		return addon_dir
	var target := dir.read_link(addon_dir)
	if target.is_empty():
		return addon_dir
	if target.is_absolute_path():
		return target.simplify_path()
	return addon_dir.path_join(target).simplify_path()
