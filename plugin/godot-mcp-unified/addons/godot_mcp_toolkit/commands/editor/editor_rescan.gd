@tool
extends RefCounted
## editor.* 文件系统重新扫描:驱动并等待编辑器的资源文件系统
## 重新扫描 — 完整的 EditorFileSystem.scan(),或对每个路径做针对性的 update_file() —
## 然后经 0.1 秒轮询循环等待 is_scanning()。完整扫描之后,只重新加载
## 扫描真正改动过且不属于本工具包的已打开脚本
## (即重载过滤器)。同时服务于 refresh 工具与 wait_for_idle 工具
## (wait_for_idle 只做空闲等待;refresh 先重新扫描、再等待、最后重载)。
##
## 无状态 — 每个处理器接收 (parameters) 并返回响应 Dictionary。
## 错误缓冲区清空(clear_level)经由 Modules 别名访问;重新扫描本身
## 直接访问 EditorInterface.get_resource_filesystem() /
## get_script_editor()。编辑器命令组抽取出的子模块,
## 经由 `preload` 别名访问。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")


# -- 命令 ---------------------------------------------------------------------


static func cmd_refresh(parameters: Dictionary) -> Dictionary:
	# 在重载前清空过期错误 — 新的解析错误将以新 ID 捕获,
	# 使 log_read(channel:'editor') 只返回当前状态的错误。
	var errors_cleared := Modules.LogBuffer.clear_level("error")

	var file_paths_raw = parameters.get("file_paths", null)
	var targeted := file_paths_raw != null and typeof(file_paths_raw) == TYPE_ARRAY \
		and (file_paths_raw as Array).size() > 0

	var filesystem := EditorInterface.get_resource_filesystem()
	var scan_waited_ms := 0

	if targeted:
		# 针对性模式:对每个路径调用 update_file() — 每个文件 O(1)。
		var paths: Array = file_paths_raw as Array
		if filesystem != null:
			for path in paths:
				filesystem.update_file(str(path))
		var reloaded := 0
		var script_editor := EditorInterface.get_script_editor()
		if script_editor != null:
			var target_set := {}
			for path in paths:
				target_set[str(path)] = true
			for open_script in script_editor.get_open_scripts():
				if open_script is Script:
					if target_set.has(open_script.resource_path):
						open_script.reload(true)
						reloaded += 1
		return MCPToolkitSuccess.ok({"mode": "targeted", "file_count": paths.size(),
			"reloaded": reloaded, "errors_cleared": errors_cleared})

	# 完整模式:先 scan(),然后只重载扫描真正改动过的脚本
	# (经 resources_reload 信号捕获 — 4.2-4.6 全部可用),且
	# 仅当它们处于打开状态。绝不要重载未改动的脚本:reload(true) 会取消
	# 其中所有挂起的协程,这会破坏用户的 @tool 插件,并且
	# (对本工具包自己的脚本)会泄漏变更分发锁。
	# 即使有改动,本工具包自己的脚本也会被跳过。
	var changed := {}
	var collector := func(resources: PackedStringArray) -> void:
		for r in resources:
			changed[str(r)] = true
	if filesystem != null:
		filesystem.resources_reload.connect(collector)
		filesystem.scan()
		var scan_start := Time.get_ticks_msec()
		var scan_deadline := scan_start + 5000
		while filesystem.is_scanning() and Time.get_ticks_msec() < scan_deadline:
			await Engine.get_main_loop().create_timer(0.1).timeout
		scan_waited_ms = Time.get_ticks_msec() - scan_start
		if filesystem.resources_reload.is_connected(collector):
			filesystem.resources_reload.disconnect(collector)
	var reloaded := 0
	var script_editor := EditorInterface.get_script_editor()
	if script_editor != null:
		for open_script in script_editor.get_open_scripts():
			if not (open_script is Script):
				continue
			# 只重载扫描改动过的、非本工具包的已打开脚本(见
			# should_reload_open_script — 绝不取消未改动/自身的协程)。
			if not should_reload_open_script(str(open_script.resource_path), changed):
				continue
			open_script.reload(true)
			reloaded += 1
	return MCPToolkitSuccess.ok({"mode": "full", "reloaded": reloaded,
		"scan_waited_ms": scan_waited_ms, "errors_cleared": errors_cleared})


static func cmd_wait_for_idle(parameters: Dictionary) -> Dictionary:
	var timeout_ms: int = int(parameters.get("timeout_ms", 10000))
	if timeout_ms < 0 or timeout_ms > 30000:
		return MCPToolkitError.fail("INVALID_PARAMS",
			"timeout_ms must be in [0, 30000] (got %d)" % timeout_ms)
	var filesystem := EditorInterface.get_resource_filesystem()
	if not filesystem.is_scanning():
		return MCPToolkitSuccess.ok({"was_scanning": false, "waited_ms": 0})
	var start := Time.get_ticks_msec()
	while filesystem.is_scanning() and Time.get_ticks_msec() - start < timeout_ms:
		await Engine.get_main_loop().create_timer(0.1).timeout
	var elapsed := Time.get_ticks_msec() - start
	if filesystem.is_scanning():
		return MCPToolkitError.fail("TIMEOUT",
			"EditorFileSystem still scanning after %dms; consider increasing timeout_ms or checking editor.get_console for import errors" % elapsed)
	return MCPToolkitSuccess.ok({"was_scanning": true, "waited_ms": elapsed})


# -- 辅助函数 ------------------------------------------------------------------


## 完整 editor.refresh 扫描之后,某个已打开脚本是否应被重载:
## 只重载扫描真正改动过的脚本,绝不重载本工具包自己的(重载
## 未改动或工具包自身的脚本会取消挂起的协程 —
## 属于分发锁卡死/编辑器崩溃一类)。纯逻辑,便于单元测试。
static func should_reload_open_script(resource_path: String, changed: Dictionary) -> bool:
	return changed.has(resource_path) and not resource_path.begins_with("res://addons/godot_mcp_toolkit/")
