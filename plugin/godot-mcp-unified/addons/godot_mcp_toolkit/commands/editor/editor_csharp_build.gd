@tool
extends RefCounted
## editor.build_csharp / editor.build_csharp_status 命令处理器 — C# 宿主项目
## (MiJing 等)的 dotnet 构建任务与程序集(assembly)热重载观测。
## 参考实现:GDEditorBridge csharp_build_job.gd(MIT,出处见 ATTRIBUTIONS.md),
## 其在官方 Godot 4.7.x mono build 上实测锚定的语义全部保留:
##
## 成功语义(不可简化):
## - exit 0 ≠ 成功:引擎实际加载的 DLL(res://.godot/mono/temp/bin/<Config>/
##   <assembly>.dll,Godot.NET.Sdk 的固定输出位,引擎从该位加载)必须存在;
## - DLL mtime 未动 → succeeded + alc.status=unchanged(无可重载),不是失败;
## - ALC 探针(probe/McpToolkitAlcProbe.cs,编入宿主程序集)把"热重载了吗"
##   从不可观测变为可观测量:毫秒级标记文件 + 每次加载全新的 ALC id;
## - unknown(项目无标记生产者,如探针尚未编入)是正常结果,不是失败;
## - 仅"程序集被加载进了本该卸载的 ALC"这一种可证明失败建议重启编辑器。
##
## 拉取式(pull)生命周期:本模块不装后台计时节点,状态迁移在每次 status
## 调用时惰性求值(查 pid 存活/退出码、日志、DLL mtime、ALC 标记文件)。
## MCP 形态下消费方本就以 status/wait 轮询,超时也随观测推进 —— 无人观测
## 时 dotnet 子进程自然跑完,不占编辑器。
##
## 与参考实现的差异(有意为之):不扫描引擎控制台的 .NET 放弃重载消息
## (那需要日志环;引擎消息仍可经 editor.get_console 读取),失败判定完全
## 以探针标记为准;无自有 ProjectSettings 面,参数即配置(csproj/configuration/
## timeout_s/alc_timeout_s),默认值与参考实现一致。

## 任务阶段。内部值互不相同(building/alc_wait 区分轮询分支);
## 对外快照统一映射为 "running"。
const PHASE_IDLE := "idle"
const PHASE_BUILDING := "building"
const PHASE_WAITING_ALC := "alc_wait"
const PHASE_SUCCEEDED := "succeeded"
const PHASE_BUILD_FAILED := "build_failed"
const PHASE_ALC_FAILED := "alc_failed"

## ALC 判定七态。
const ALC_PENDING := "pending"
const ALC_UNKNOWN := "unknown"
const ALC_UNCHANGED := "unchanged"
const ALC_RELOADED := "reloaded"
const ALC_FAILED := "failed"
const ALC_UNLOADED := "unloaded"
const ALC_LOADED := "loaded"

## 标记文件里视为"加载完成"的 phase。
const ALC_LOAD_PHASES := ["loaded", "deserialized"]
## 标记文件里视为"卸载"的 phase。
const ALC_UNLOAD_PHASES := ["unloading", "unloaded"]

## 构建日志(msbuild 文件日志,双斜杠转义交给调用点)。
const BUILD_LOG_RES := "res://.godot/mcp-toolkit-build.log"
## ALC 标记文件(探针双写,新者为准)。
const ALC_USER := "user://mcp-toolkit-alc.json"
const ALC_MIRROR := "res://.godot/mcp-toolkit-alc.json"

const DEFAULT_CONFIGURATION := "Debug"
const DEFAULT_TIMEOUT_S := 300.0
const DEFAULT_ALC_TIMEOUT_S := 45.0
const CSPROJ_SUFFIX := "csproj"
## 日志统计/尾部读取的有界窗口(尾部 64 KiB,不整读大日志)。
const LOG_TAIL_WINDOW_BYTES := 64 * 1024
const LOG_TAIL_LINES := 40

## 任务静态态(单任务语义:同会话同时最多一个构建;插件重载即重置)。
static var _phase := PHASE_IDLE
static var _pid := -1
static var _exit_code: Variant = null
static var _started_usec := 0
static var _finished_usec := 0
static var _timeout_s := DEFAULT_TIMEOUT_S
static var _alc_timeout_s := DEFAULT_ALC_TIMEOUT_S
static var _aborted := false
static var _note := ""
static var _errors := 0
static var _warnings := 0
static var _resolved: Dictionary = {}
static var _dll_mtime_before := 0
static var _alc_started_usec := 0
static var _alc_status := ""
static var _alc_suggest_reload := false
static var _alc_message := ""
static var _alc_source := ""
static var _alc_marker_phase := ""
static var _alc_marker_alc := ""
static var _alc_marker_unix_ms := 0
static var _baseline_alc := ""
static var _baseline_unix_ms := 0
static var _saw_unload := false
static var _unloaded_alc := ""
static var _loaded_alc := ""
static var _marker_start: Dictionary = {}

## 已挂载的探针节点(宿主为 C# 项目且程序集已含探针类时非空)。
static var _probe: Node = null


static func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	registry.add("editor.build_csharp", func(parameters: Dictionary) -> Dictionary:
		return _cmd_build(parameters)
	, MCPToolkitCommandOptions.new().mark_scene_independent())
	registry.add("editor.build_csharp_status", func(parameters: Dictionary) -> Dictionary:
		return _cmd_status(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())
	# 尽力挂载 ALC 探针:宿主无 C# 或探针尚未编入程序集时安静跳过
	# (首次安装探针后需要一次编辑器重启 + 一次构建让它进入程序集)。
	_mount_probe(server)


# -- 命令 ---------------------------------------------------------------------


## 启动构建:立即返回 running 与解析出的 csproj/assembly/dll 路径。
## 已有构建在跑 → BUSY;项目无法解析(无 .csproj/多个 .csproj)→ INVALID_PARAMS。
static func _cmd_build(parameters: Dictionary) -> Dictionary:
	if is_running():
		return MCPToolkitError.fail("BUSY",
			"a C# build is already running (pid %d); poll editor.build_csharp_status for its snapshot" % _pid)
	var resolved := _resolve_project(
		str(parameters.get("csproj", "")),
		str(parameters.get("configuration", DEFAULT_CONFIGURATION)))
	if not bool(resolved.get("ok", false)):
		return MCPToolkitError.fail("INVALID_PARAMS", str(resolved.get("error", "")))
	_reset_job()
	_resolved = resolved
	_timeout_s = maxf(float(parameters.get("timeout_s", DEFAULT_TIMEOUT_S)), 5.0)
	_alc_timeout_s = maxf(float(parameters.get("alc_timeout_s", DEFAULT_ALC_TIMEOUT_S)), 1.0)
	_dll_mtime_before = _file_mtime(str(resolved.get("dll_absolute", "")))
	# ALC 握手基线:构建*开始前*的标记。编辑器可能在首次 poll 观测到退出码
	# 之前就已完成重载,_begin_alc_wait() 不得把那次重载误判为陈旧标记。
	_marker_start = _read_alc_marker()
	_started_usec = _now_usec()
	_phase = PHASE_BUILDING

	var log_absolute := ProjectSettings.globalize_path(BUILD_LOG_RES)
	var args := PackedStringArray(["build"])
	if bool(parameters.get("no_incremental", false)):
		args.append("--no-incremental")
	args.append(ProjectSettings.globalize_path(str(resolved["csproj"])))
	args.append("/flp:v=m;logfile=%s" % log_absolute.replace("\\", "/"))
	_pid = OS.create_process("dotnet", args)
	if _pid == -1:
		_finish_build_failed(-1,
			"could not start 'dotnet': the .NET SDK is not installed or 'dotnet' is not on PATH "
			+ "(OS.create_process, so the lookup is PATH-only). Install the .NET SDK 8+ and retry.")
		return _status_envelope()
	return MCPToolkitSuccess.ok({
		"status": "running",
		"pid": _pid,
		"csproj": str(resolved["csproj"]),
		"assembly": str(resolved["assembly"]),
		"dll_path": str(resolved["dll"]),
		"log_path": BUILD_LOG_RES,
		"note": "poll editor.build_csharp_status (or the daemon-side editor_build_csharp_wait) "
			+ "until status leaves 'running'; gate on ok and alc.status, never on exit_code alone",
	})


## 状态快照:惰性推进状态机后返回全量字段。观察工具恒为成功信封,
## 构建成败看快照里的 ok/status。
static func _cmd_status(_parameters: Dictionary) -> Dictionary:
	return _status_envelope()


static func is_running() -> bool:
	return _phase == PHASE_BUILDING or _phase == PHASE_WAITING_ALC


# -- 状态机 -------------------------------------------------------------------


## 惰性推进:构建阶段查进程/超时;ALC 等待阶段查标记/超时。
static func _poll() -> void:
	if _phase == PHASE_BUILDING:
		_poll_build()
	elif _phase == PHASE_WAITING_ALC:
		_poll_alc()


static func _poll_build() -> void:
	if (_now_usec() - _started_usec) / 1000000.0 > _timeout_s:
		if _pid != -1 and OS.is_process_running(_pid):
			# OS.kill() 同时收割句柄与引擎 process_map 条目(Windows 语义)。
			OS.kill(_pid)
			_pid = -1
		_finish_build_failed(-1,
			"the dotnet build timed out after %ss (timeout_s; no observer polled earlier does "
			+ "not stop the child, the timeout fires on the poll that sees it)" % int(_timeout_s))
		return
	if _pid != -1 and OS.is_process_running(_pid):
		return
	var code := -1
	if _pid != -1:
		code = _exit_code_of(_pid)
		_reap(_pid)
		_pid = -1
	_exit_code = code
	var parsed := _parse_build_log()
	_errors = int(parsed["errors"])
	_warnings = int(parsed["warnings"])
	if code != 0:
		_finish_build_failed(code, "")
		return
	# exit 0 不是成功证明:验证引擎实际加载位上的程序集存在。
	var dll_absolute := str(_resolved.get("dll_absolute", ""))
	if not FileAccess.file_exists(dll_absolute):
		_finish_build_failed(code,
			"dotnet build exited 0 but no assembly was produced at %s — check the configuration "
			+ "(currently %s) against what is really built, and the resolved assembly name (%s); "
			+ "build once from the Godot editor to see the real error" % [
				dll_absolute, str(_resolved.get("configuration", "")), str(_resolved.get("assembly", ""))])
		return
	if _file_mtime(dll_absolute) == _dll_mtime_before:
		_alc_status = ALC_UNCHANGED
		_alc_suggest_reload = false
		_note = "the build produced no new assembly: it was already up to date, so there is nothing to reload"
		_finish(PHASE_SUCCEEDED)
		return
	_begin_alc_wait()


static func _begin_alc_wait() -> void:
	_saw_unload = false
	_unloaded_alc = ""
	_loaded_alc = ""
	var marker := _read_alc_marker()
	if not bool(marker.get("available", false)):
		# 项目里没有标记生产者:热重载不可观测。不等待、不超时、不失败 ——
		# 报告 unknown,构建结果本身(已验证 DLL)才是结论。
		_alc_status = ALC_UNKNOWN
		_alc_suggest_reload = false
		_alc_message = ("no assembly-reload marker producer exists in this project, so the reload "
			+ "is not observable; the shipped probe is res://addons/godot_mcp_toolkit/probe/"
			+ "McpToolkitAlcProbe.cs — it compiles into the host assembly (one editor restart "
			+ "after first install), after which reloads become observable")
		_note = _alc_message
		_finish(PHASE_SUCCEEDED)
		return
	# 基线取 start() 时点的标记,而非此刻:编辑器在 DLL 变化后立刻重载,
	# 可能早于首次 poll。
	var start_available := bool(_marker_start.get("available", false))
	_baseline_alc = str(_marker_start.get("alc", "")) if start_available else ""
	_baseline_unix_ms = int(_marker_start.get("unix_ms", 0)) if start_available else 0
	_alc_marker_alc = _baseline_alc
	_alc_marker_phase = str(_marker_start.get("phase", "")) if start_available else ""
	_alc_marker_unix_ms = _baseline_unix_ms
	if start_available and _apply_marker(marker) and _reload_completed():
		# 构建收尾期间重载已经完成。
		_alc_status = ALC_RELOADED
		_finish(PHASE_SUCCEEDED)
		return
	# 否则此标记是待超越的基线。
	_alc_marker_alc = str(marker.get("alc", ""))
	_alc_marker_phase = str(marker.get("phase", ""))
	_alc_marker_unix_ms = int(marker.get("unix_ms", 0))
	_alc_source = str(marker.get("source", ""))
	_baseline_alc = _alc_marker_alc
	_baseline_unix_ms = _alc_marker_unix_ms
	_alc_started_usec = _now_usec()
	_alc_status = ALC_PENDING
	_phase = PHASE_WAITING_ALC


static func _poll_alc() -> void:
	var applied := _apply_marker(_read_alc_marker())
	# 1. 真正的"重载进了同一个 ALC"失败:宣布卸载 X 后 X 又回来加载。
	#    这是唯一建议重启编辑器的分支 —— 只有重启能改变这个结局。
	if applied and _loaded_same_alc():
		_finish_alc_failed(
			"the assembly was loaded into the ALC (%s) that should have been unloaded, so the "
			+ "new build is not active — restart the editor to load it" % _alc_marker_alc, true)
		return
	# 2. 成功:一个任务从未见过的 ALC 报告了完成加载(陈旧标记伪造不了的
	#    唯一成功条件)。
	if applied and _reload_completed():
		_alc_status = ALC_RELOADED
		_note = ""
		_finish(PHASE_SUCCEEDED)
		return
	# 3. 超时:如实区分"卸载了但没加载"与"整轮未见"。
	if (_now_usec() - _alc_started_usec) / 1000000.0 > _alc_timeout_s:
		var message := "no assembly reload was observed within %ss (alc_timeout_s)" % int(_alc_timeout_s)
		if _saw_unload:
			message += " (ALC %s was unloaded, but no new ALC was loaded)" % _unloaded_alc
		else:
			message += " (no unload/load cycle was observed)"
		_finish_alc_failed(message, false)


## 应用新标记(毫秒级比较; unload 记账)。返回真 = 标记有推进。
static func _apply_marker(marker: Dictionary) -> bool:
	if not bool(marker.get("available", false)):
		return false
	var unix_ms := int(marker.get("unix_ms", 0))
	if unix_ms < _alc_marker_unix_ms:
		return false
	var alc := str(marker.get("alc", ""))
	var phase := str(marker.get("phase", ""))
	if alc == _alc_marker_alc and phase == _alc_marker_phase and unix_ms == _alc_marker_unix_ms:
		return false
	# 只在毫秒时间戳真的前进时记账(unix 相等且 alc/phase 变化也视为推进;
	# 同秒内的卸载+加载是常态,秒级比较会两者皆丢 —— 毫秒是刻意选择)。
	_alc_marker_alc = alc
	_alc_marker_phase = phase
	_alc_marker_unix_ms = unix_ms
	if _alc_source.is_empty():
		_alc_source = str(marker.get("source", ""))
	if phase in ALC_UNLOAD_PHASES:
		_saw_unload = true
		_unloaded_alc = alc
		if _alc_status == ALC_PENDING:
			_alc_status = ALC_UNLOADED
	elif phase in ALC_LOAD_PHASES:
		_loaded_alc = alc
		if _alc_status == ALC_PENDING or _alc_status == ALC_UNLOADED:
			_alc_status = ALC_LOADED
	return true


## 卸载 X 后又加载 X = 真失败。
static func _loaded_same_alc() -> bool:
	if not _saw_unload or _unloaded_alc.is_empty():
		return false
	if not (_alc_marker_phase in ALC_LOAD_PHASES):
		return false
	return _alc_marker_alc == _unloaded_alc


## 成功 = 基线之外的新 ALC 报告完成加载。
static func _reload_completed() -> bool:
	if _alc_marker_alc.is_empty():
		return false
	if _alc_marker_alc == _baseline_alc:
		return false
	return _alc_marker_phase in ALC_LOAD_PHASES


static func _finish_build_failed(code: int, detail: String) -> void:
	_alc_suggest_reload = false
	_exit_code = code
	if detail.is_empty():
		var parsed_note := "read the log tail in this snapshot (and %s) for the compiler errors" % BUILD_LOG_RES
		_note = parsed_note
	else:
		_note = detail
	_finish(PHASE_BUILD_FAILED)


static func _finish_alc_failed(detail: String, suggest_reload: bool) -> void:
	_alc_status = ALC_FAILED
	_alc_suggest_reload = suggest_reload
	_alc_message = detail
	_note = detail
	_finish(PHASE_ALC_FAILED)


static func _finish(phase: String) -> void:
	_phase = phase
	_finished_usec = _now_usec()


static func _reset_job() -> void:
	_exit_code = null
	_started_usec = 0
	_finished_usec = 0
	_aborted = false
	_note = ""
	_errors = 0
	_warnings = 0
	_alc_status = ""
	_alc_suggest_reload = false
	_alc_message = ""
	_alc_source = ""
	_alc_marker_alc = ""
	_alc_marker_phase = ""
	_alc_marker_unix_ms = 0
	_baseline_alc = ""
	_baseline_unix_ms = 0
	_saw_unload = false
	_unloaded_alc = ""
	_loaded_alc = ""


# -- 快照 ---------------------------------------------------------------------


static func _status_envelope() -> Dictionary:
	_poll()
	var resolved: Dictionary = _resolved if not _resolved.is_empty() else _resolve_project("", DEFAULT_CONFIGURATION)
	var log_absolute := ProjectSettings.globalize_path(BUILD_LOG_RES)
	var ok := _phase in [PHASE_IDLE, PHASE_BUILDING, PHASE_WAITING_ALC, PHASE_SUCCEEDED]
	var payload := {
		"ok": ok,
		"status": "running" if is_running() else _phase,
		"exit_code": _exit_code,
		"elapsed_ms": _elapsed_ms(),
		"errors": _errors,
		"warnings": _warnings,
		"csproj": str(resolved.get("csproj", "")),
		"assembly": str(resolved.get("assembly", "")),
		"configuration": str(resolved.get("configuration", "")),
		"dll_path": str(resolved.get("dll", "")),
		"dll_absolute": str(resolved.get("dll_absolute", "")),
		"log_path": BUILD_LOG_RES,
		"log_absolute": log_absolute,
		"tail": _log_tail(),
		"alc": _alc_payload(),
	}
	if _pid != -1:
		payload["pid"] = _pid
	if not _note.is_empty():
		payload["note"] = _note
	return MCPToolkitSuccess.ok(payload)


static func _alc_payload() -> Dictionary:
	var payload := {
		"status": _alc_status,
		"suggest_reload_editor": _alc_suggest_reload,
	}
	if not _alc_message.is_empty():
		payload["message"] = _alc_message
	if not _alc_source.is_empty():
		payload["source"] = _alc_source
	if not _alc_marker_phase.is_empty():
		payload["phase"] = _alc_marker_phase
	if not _unloaded_alc.is_empty():
		payload["unloaded_alc"] = _unloaded_alc
	if not _loaded_alc.is_empty():
		payload["loaded_alc"] = _loaded_alc
	return payload


static func _elapsed_ms() -> int:
	if _phase == PHASE_IDLE and _started_usec <= 0 and _finished_usec <= 0:
		return 0
	var end_usec := _finished_usec if _finished_usec > 0 else _now_usec()
	return int((end_usec - _started_usec) / 1000.0)


# -- 项目解析 -----------------------------------------------------------------


## csproj 显式参数 > res:// 根单个 *.csproj(非递归,Godot C# 项目文件与
## project.godot 同层)。assembly 名解析链:<AssemblyName> →
## dotnet/project/assembly_name → 项目名 sanitize。
## DLL 输出位是 Godot.NET.Sdk 的固定约定:
## res://.godot/mono/temp/bin/<Configuration>/<AssemblyName>.dll,
## 引擎编辑器构建恒从该位加载(编辑器期望的配置恒为 Debug)。
static func _resolve_project(csproj_param: String, configuration: String) -> Dictionary:
	var csproj := csproj_param.strip_edges()
	if not csproj.is_empty():
		if not FileAccess.file_exists(csproj):
			return _resolution_error(configuration,
				"csproj parameter points at a file that does not exist: %s" % csproj)
	else:
		var found := _find_csproj_at_root()
		if found.is_empty():
			return _resolution_error(configuration,
				"this project has no C# project (*.csproj at the res:// root) — this is a GDScript-only project; editor.build_csharp applies to C# hosts only")
		if found.size() > 1:
			return _resolution_error(configuration,
				"several C# projects found; pass the csproj parameter explicitly")
		csproj = found[0]
	var assembly := _csproj_assembly_name(csproj)
	if assembly.is_empty():
		assembly = str(ProjectSettings.get_setting("dotnet/project/assembly_name", "")).strip_edges()
	if assembly.is_empty():
		assembly = _sanitize_assembly_name(str(ProjectSettings.get_setting("application/config/name", "")))
	var dll := "res://.godot/mono/temp/bin".path_join(configuration).path_join(assembly + ".dll")
	return {
		"ok": true,
		"csproj": csproj,
		"assembly": assembly,
		"dll": dll,
		"dll_absolute": ProjectSettings.globalize_path(dll),
		"configuration": configuration,
	}


static func _resolution_error(configuration: String, message: String) -> Dictionary:
	return {
		"ok": false,
		"csproj": "",
		"assembly": "",
		"dll": "",
		"dll_absolute": "",
		"configuration": configuration,
		"error": message,
	}


static func _find_csproj_at_root() -> PackedStringArray:
	var found := PackedStringArray()
	var dir := DirAccess.open("res://")
	if dir == null:
		return found
	dir.list_dir_begin()
	var entry := dir.get_next()
	while not entry.is_empty():
		if not dir.current_is_dir() and entry.get_extension().to_lower() == CSPROJ_SUFFIX:
			found.append("res://" + entry)
		entry = dir.get_next()
	dir.list_dir_end()
	found.sort()
	return found


static func _csproj_assembly_name(csproj: String) -> String:
	var file := FileAccess.open(csproj, FileAccess.READ)
	if file == null:
		return ""
	var text := file.get_as_text()
	file.close()
	# 有界读取(头部 256 KiB 足够覆盖 PropertyGroup)后取首个 <AssemblyName>。
	if text.length() > 256 * 1024:
		text = text.substr(0, 256 * 1024)
	var idx := text.find("<AssemblyName>")
	if idx < 0:
		return ""
	var end_idx := text.find("</AssemblyName>", idx)
	if end_idx < 0:
		return ""
	return text.substr(idx + "<AssemblyName>".length(), end_idx - idx - "<AssemblyName>".length()).strip_edges()


## 引擎对无效程序集名字符的 sanitize 近似:字母数字下划线保留,
## 其余替换为下划线;首字符非字母则加前导下划线。
static func _sanitize_assembly_name(raw: String) -> String:
	var cleaned := ""
	for c in raw.strip_edges():
		var lower_c := c.to_lower()
		if (lower_c >= "a" and lower_c <= "z") or (c >= "0" and c <= "9") or c == "_":
			cleaned += c
		else:
			cleaned += "_"
	if cleaned.is_empty():
		return ""
	var first := cleaned[0].to_lower()
	if not (first >= "a" and first <= "z") and cleaned[0] != "_":
		cleaned = "_" + cleaned
	return cleaned


# -- 日志/标记/进程 -----------------------------------------------------------


## msbuild 诊断行统计:"Game.cs(3,5): error CS0103: ..." 形态
## (msbuild 把 severity 放在冒号后,与文件/行号前缀区分)。
static func _parse_build_log() -> Dictionary:
	var absolute := ProjectSettings.globalize_path(BUILD_LOG_RES)
	if not FileAccess.file_exists(absolute):
		return {"errors": 0, "warnings": 0}
	var file := FileAccess.open(absolute, FileAccess.READ)
	if file == null:
		return {"errors": 0, "warnings": 0}
	var errors := 0
	var warnings := 0
	while not file.eof_reached():
		var line := file.get_line()
		var lower := line.to_lower()
		if lower.contains(": error") or lower.begins_with("error "):
			errors += 1
		elif lower.contains(": warning") or lower.begins_with("warning "):
			warnings += 1
	file.close()
	return {"errors": errors, "warnings": warnings}


## 日志尾部:有界窗口(LOG_TAIL_WINDOW_BYTES)内的最后 LOG_TAIL_LINES 行。
static func _log_tail() -> Array:
	var absolute := ProjectSettings.globalize_path(BUILD_LOG_RES)
	if not FileAccess.file_exists(absolute):
		return []
	var file := FileAccess.open(absolute, FileAccess.READ)
	if file == null:
		return []
	var length := int(file.get_length())
	file.seek(maxi(0, length - LOG_TAIL_WINDOW_BYTES))
	var chunk := file.get_buffer(mini(length, LOG_TAIL_WINDOW_BYTES))
	file.close()
	var lines := chunk.get_string_from_utf8().split("\n")
	var tail: Array = []
	for line in lines:
		var trimmed := String(line).strip_edges()
		if not trimmed.is_empty():
			tail.append(trimmed)
	if tail.size() > LOG_TAIL_LINES:
		tail = tail.slice(tail.size() - LOG_TAIL_LINES, tail.size())
	return tail


## 读 ALC 标记:两处文件新者为准;无可用标记 → available:false。
static func _read_alc_marker() -> Dictionary:
	var best: Dictionary = {}
	for path in [ALC_USER, ALC_MIRROR]:
		var candidate := _read_marker_file(str(path))
		if candidate.is_empty():
			continue
		if best.is_empty() or int(candidate["unix_ms"]) > int(best["unix_ms"]):
			best = candidate
	if best.is_empty():
		return {"available": false}
	best["available"] = true
	return best


static func _read_marker_file(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return {}
	var parsed: Variant = JSON.parse_string(file.get_as_text())
	file.close()
	if not parsed is Dictionary:
		return {}
	var payload: Dictionary = parsed
	var alc := str(payload.get("alc", "")).strip_edges()
	var phase := str(payload.get("phase", "")).strip_edges()
	if alc.is_empty() or phase.is_empty():
		return {}
	var unix_ms := int(payload.get("unix_ms", 0))
	return {"source": "probe", "path": path, "alc": alc, "phase": phase, "unix_ms": unix_ms}


## 尽力挂载探针:先用"根下恰一个 .csproj"做安静守卫(GDScript-only 项目
## 从不进入加载分支);C# 脚本资源在宿主程序集未含该类时 new() 返回 null,
## 同样安静跳过(首次安装探针后需一次编辑器重启 + 一次构建进入程序集)。
static func _mount_probe(host: Node) -> void:
	if host == null or _probe != null:
		return
	if _find_csproj_at_root().size() != 1:
		return
	var probe_res := "res://addons/godot_mcp_toolkit/probe/McpToolkitAlcProbe.cs"
	if not ResourceLoader.exists(probe_res):
		return
	var script: Variant = load(probe_res)
	if script == null or not script is Script:
		return
	var instance: Variant = (script as Script).new()
	if instance == null or not instance is Node:
		return
	_probe = instance
	_probe.name = "McpToolkitAlcProbe"
	host.add_child(_probe)


static func _file_mtime(path: String) -> int:
	if path.is_empty() or not FileAccess.file_exists(path):
		return 0
	return int(FileAccess.get_modified_time(path))


## Windows 上被拉起的子进程会留在引擎 process_map 里(句柄未关),直到
## OS.kill() 被调用 —— get_process_exit_code() 只读 map 里已有的码。
## 先读码再收割;OS.kill() 对引擎没拉起过的 pid 会 OpenProcess+
## TerminateProcess 现在拥有该 pid 的任何进程,所以只对自家 pid 调用。
static func _reap(pid: int) -> void:
	if pid == -1 or OS.get_process_exit_code(pid) == -1:
		return
	OS.kill(pid)


static func _exit_code_of(pid: int) -> int:
	return OS.get_process_exit_code(pid)


static func _now_usec() -> int:
	return Time.get_ticks_usec()
