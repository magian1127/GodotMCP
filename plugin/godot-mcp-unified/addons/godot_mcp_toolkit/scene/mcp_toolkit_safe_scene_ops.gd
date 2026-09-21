@tool
class_name MCPToolkitSafeSceneOps
extends RefCounted
## 面向命令处理器的编辑器安全场景保存。
##
## 命令处理器(工具包或扩展)必须经由本类保存 — 绝不要直接调用
## [code]EditorInterface.save_scene()[/code] / [code]save_scene_as()[/code] —
## 这样派发安全守卫才能生效:扫描空闲等待(C2),以及围绕同步保存的重入
## 防护(C1),二者共同避免一次保存破坏进行中的导入或另一次保存。在
## GDScript 中,用 [code]await[/code] [method save_scene] 直接取得结果;从
## 同步调用方(尤其是无法 await 的 C# 处理器)出发,则调用 [method queue_save]
## 并轮询 [method check_save]。参见 [code]docs/extending.md[/code] 与
## [code]docs/advanced_configuration.md[/code].[br]
## [br]
## 示例(GDScript 处理器):
## [codeblock]
## var result := await MCPToolkitSafeSceneOps.save_scene()
## if not result.get("success", false):
##     return result
## [/codeblock]

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard

# 仅在同步保存调用(C1)周围置位。派发循环在每个帧驱动的入口检查
# is_dispatching(),并在一次保存的 Main::iteration() 重入进行期间跳过该
# tick。持有时长仅数微秒(一次同步调用),因此绝不可能卡在 true 上、
# 让轮询瘫痪。
static var _in_dispatch := false

# queue_save() 结果跟踪:save_id -> {done, success, result}。有上限
# (最旧的先丢弃),这样从不带 clear 轮询的调用方也不会泄漏内存。
const _MAX_TRACKED_SAVES := 100
static var _save_counter := 0
static var _save_results: Dictionary = {}


## 当一次保存的同步 [code]EditorInterface.save_scene[/code]/
## [code]save_scene_as[/code] 调用仍在进行时返回 [code]true[/code]。
## 帧驱动的派发发起方在本值为 true 时提前返回,确保在保存的重入主循环
## 泵送期间没有命令被派发(C1 守卫,适用于所有引擎版本)。
static func is_dispatching() -> bool:
	return _in_dispatch


## 等待 EditorFileSystem 扫描结束(这是协程 — 请用 [code]await[/code] 调用)。
## 达到空闲返回 [code]true[/code],超时返回 [code]false[/code]。负的
## [param timeout_ms](默认值)使用已配置的默认值
## ([code]mcp_toolkit/concurrency/scan_idle_timeout_ms[/code],默认 5000);
## [code]0[/code] 快速失败(除非已经空闲,否则立即返回)。
static func wait_for_scan_idle(timeout_ms := -1) -> bool:
	var efs := EditorInterface.get_resource_filesystem()
	if efs == null or not efs.is_scanning():
		return true
	if timeout_ms < 0:
		timeout_ms = ProjectSettings.get_setting(
			"mcp_toolkit/concurrency/scan_idle_timeout_ms", 5000)
	var start := Time.get_ticks_msec()
	while efs.is_scanning() and Time.get_ticks_msec() - start < timeout_ms:
		await Engine.get_main_loop().create_timer(0.1).timeout
	return not efs.is_scanning()


## 安全地保存场景(这是协程 — 请用 [code]await[/code] 调用)。空的
## [param path](默认值)保存当前正在编辑的场景;非空的 [param path] 保存到
## 该 [code]res://[/code] 位置(另存为,带路径防护)。应用 C2 守卫(等待扫描
## 空闲,超时则中止)和 C1 守卫(把同步保存与任何其他进行中的保存串行化)。
## 返回携带已保存 [code]path[/code] 的成功字典,或错误字典
## ([code]NO_SCENE[/code]、[code]PATH_DENIED[/code]、[code]TIMEOUT[/code]、
## [code]BUSY[/code] 或 [code]SAVE_FAILED[/code])。
static func save_scene(path := "") -> Dictionary:
	var root := EditorInterface.get_edited_scene_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")
	if not path.is_empty():
		var guard := FileGuard.resolve_safe(path)
		if guard["error"] != null:
			return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	# 不要在活动扫描期间保存。
	if not await wait_for_scan_idle():
		return MCPToolkitError.fail("TIMEOUT",
			"EditorFileSystem still scanning; a recent import/refresh may still "
			+ "be indexing — call editor_sync or retry")
	# 在 ProgressDialog 之前脱离 call_deferred/flush 上下文。
	await (Engine.get_main_loop() as SceneTree).process_frame
	# 重入防护 — 在这里、即帧让出之后检查:一个已在进行中的
	# EditorInterface.save_scene() 会泵送 Main::iteration(),其 process_frame
	# 的发射可能在本泵送中途恢复本协程。如果另一次保存正持有同步窗口
	# (_in_dispatch),再打开一个 EditorProgress("save") 会报错
	# ("Task 'save' already exists")并执行一次冗余的嵌套保存 — 因此要干净地
	# 退出。本检查与下面的置位之间没有 await,在单线程 GDScript 中保持原子。
	if _in_dispatch:
		return MCPToolkitError.fail("BUSY",
			"a scene save is already in progress — retry once it completes")
	# --- 同步危险窗口:置位与清除之间不得有任何 await ---
	_in_dispatch = true
	var save_error := OK
	if path.is_empty():
		save_error = EditorInterface.save_scene()
	else:
		EditorInterface.save_scene_as(path)
	_in_dispatch = false
	# --- 危险窗口结束 ---
	if path.is_empty():
		if save_error != OK:
			return MCPToolkitError.fail("SAVE_FAILED",
				"EditorInterface.save_scene returned %d" % save_error)
		return MCPToolkitSuccess.ok({"path": root.scene_file_path})
	if not FileAccess.file_exists(path):
		return MCPToolkitError.fail("SAVE_FAILED",
			"save_scene_as did not produce %s" % path)
	return MCPToolkitSuccess.ok({"path": path})


## 面向同步调用方的编辑器安全保存 — 尤其是 C# 扩展处理器,它们无法 await
## GDScript 协程。把 [method save_scene](携带 [param path],语义相同)调度为
## 分离的(detached)协程 — C1 与 C2 守卫仍然生效 — 并在保存运行之前立即
## 返回一个保存 id。用 [method check_save] 轮询该 id 以获知结果。返回保存 id。
## 在 GDScript 中,若需要内联拿到结果,请优先直接 await [method save_scene]。
static func queue_save(path := "") -> String:
	_save_counter += 1
	var save_id := "save_%d" % _save_counter
	# 限制跟踪器大小(丢弃最旧的),让从不清理的调用方也不会泄漏。
	if _save_results.size() >= _MAX_TRACKED_SAVES:
		_save_results.erase(_save_results.keys()[0])
	_save_results[save_id] = {"done": false}
	_run_queued_save(save_id, path)  # 分离的协程 — 刻意不 await
	return save_id


static func _run_queued_save(save_id: String, path: String) -> void:
	var result := await save_scene(path)
	var ok := bool(result.get("success", false))
	_save_results[save_id] = {"done": true, "success": ok, "result": result}
	if not ok:
		push_warning("[MCPToolkitSafeSceneOps] queued save '%s' failed: %s" % [
			save_id, str(result.get("error", result))])


## 按 [param save_id] 轮询排队中的保存(id 来自 [method queue_save])。
## 仍在进行时返回 [code]{done = false}[/code];id 未知时返回
## [code]{done = false, unknown = true}[/code];完成后返回
## [code]{done = true, success = bool, result = {...}}[/code],其中
## [code]result[/code] 是 [method save_scene] 的字典。让出一帧并轮询,直到
## [code]done[/code]。当 [param clear] 为 [code]true[/code] 且保存已完成时,
## 该记录会被移除(之后再调用 [method check_save] 会返回
## [code]unknown[/code])。
static func check_save(save_id: String, clear := false) -> Dictionary:
	if not _save_results.has(save_id):
		return {"done": false, "unknown": true}
	var status: Dictionary = _save_results[save_id]
	if clear and bool(status.get("done", false)):
		_save_results.erase(save_id)
	return status.duplicate(true)
