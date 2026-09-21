@tool
extends RefCounted
## 在上下文协议(MCP)客户端连接期间调低/恢复机器级的失焦休眠 EditorSetting,
## 通过先写者获胜备份实现崩溃安全。
##
## 当编辑器失去焦点时,Godot 的 unfocused_low_processor_mode_sleep_usec
## (默认约 100000 µs ≈ 10 fps)会限流 _process。由于服务器在 _process 中
## 轮询 WebSocket,这会把上下文协议交互拖慢到约 2-3 Hz —— 而上下文协议会话期间
## 编辑器通常处于失焦状态(用户在聊天),所以这是常见情形,而非边缘情况。
## 在已鉴权的客户端连接期间 且 用户已选择启用时,
## 这里会调低休眠值,让编辑器在失焦时保持响应,
## 并在最后一次断开/停止时恢复。
##
## 该键是机器级 EditorSetting(影响此编辑器版本上的每个项目),
## 且 set_setting 要等到之后的 save() 才落盘 —— 因此崩溃(在刷盘后)
## 或并发的第二个编辑器可能把它滞留在提升值上,更糟的是,
## 下次启动时会把提升值当作“原始值”重新读取,丢失真实默认值。
## 我们用机器级、按版本作键、先写者获胜的真实原始值备份
## (存于注册表目录,在注册表锁之下)加上
## 能识别冲突的恢复与启动自愈来防范这一点。选择启用与可调的速率
## 保存在 EditorSettings 中(注册于 plugin.gd::_register_editor_settings)。
##
## 仅限编辑器侧 —— 且刻意带有“污染”标记:它驱动 EditorSettings
## (通过注入的访问器)与注册表锁。之所以允许,是因为模式 B 的
## 运行时自动加载(Autoload)没有失焦休眠,且绝不能预加载本文件。#91713
## 因此不作为该子模块的门控,但运行时对它的引用会污染
## 自动加载(Autoload),所以请严格保持仅限编辑器:只有 mcp_server.gd(编辑器服务器)
## 预加载它。编排器拥有跨子系统的触发点(首次
## 鉴权连接 → 调低;最后一次断开 → 恢复;start() → 先自愈);
## 本子模块只拥有每个触发点背后的机制。
##
## 编排器注入的接缝(让本子模块可测试,同时把 EditorInterface
## 的触达留在编辑器文件中,与 scene_lease 的根解析器一致):一个
## EditorSettings 访问器 Callable。纯粹的备份/恢复决策逻辑位于
## unfocused_backup.gd(本子模块直接调用它,就像之前服务器那样),并在那里
## 做无头单元测试;本子模块只是把这些决策对照
## 实时设置与注册表锁进行排序编排。

const RegistryClient := preload("res://addons/godot_mcp_toolkit/registry/registry_client.gd")
const _UnfocusedBackup := preload("res://addons/godot_mcp_toolkit/core/unfocused_backup.gd")

# 我们要限流的全局 EditorSetting,以及两个选择启用/速率键(机器级)。
const _UNFOCUSED_SLEEP_KEY := "interface/editor/unfocused_low_processor_mode_sleep_usec"
const _RESPONSIVE_ENABLED_KEY := "mcp_toolkit/performance/keep_editor_responsive_unfocused"
const _RESPONSIVE_SLEEP_KEY := "mcp_toolkit/performance/unfocused_responsive_sleep_usec"
# 可调 EditorSetting unfocused_responsive_sleep_usec 的默认值。
const _ACTIVE_UNFOCUSED_SLEEP_USEC := 16666  # ~= 有客户端连接时 60 fps

# 注入的 EditorSettings 访问器:func() -> EditorSettings(无头模式下为 null)。
# 编辑器注入 EditorInterface.get_editor_settings;单元测试注入一个桩。
# 这是本子模块触达编辑器的唯一途径 —— 它绝不引用 EditorInterface,
# 因此 EditorInterface 的触达留在编辑器编排器中(与 scene_lease 一致)。
var _settings_accessor: Callable = Callable()

# 尽力而为的内存镜像:当本实例持有激活的提升时 >= 0,否则为 -1。
# 机器级备份文件才是恢复的事实依据(这里只决定
# 断开/停止路径是否运行)。
var _original_unfocused_sleep_usec: int = -1


## 接好 EditorSettings 访问器。在构造时调用一次,先于任何提升。
func set_settings_accessor(settings_accessor: Callable) -> void:
	_settings_accessor = settings_accessor


## 用户已选择启用时为 true(默认 true)。设置缺失/不可用时
## 回退到默认值,因此行为与本次迭代之前保持一致。
func is_unfocused_responsive_enabled() -> bool:
	var es = _editor_settings()
	if es == null or not es.has_setting(_RESPONSIVE_ENABLED_KEY):
		return true
	return bool(es.get_setting(_RESPONSIVE_ENABLED_KEY))


## 配置的提升值所隐含的 fps,用于停靠面板(dock)指示器与日志。
func get_unfocused_responsive_fps() -> int:
	var usec := _configured_responsive_usec()
	if usec <= 0:
		return 0
	return int(round(1_000_000.0 / float(usec)))


## 当本实例持有激活的提升时为 true(尽力而为;恢复以备份文件
## 为准)。由停靠面板(dock)的三态指示器使用。
func is_unfocused_boost_active() -> bool:
	return _original_unfocused_sleep_usec >= 0


## 当用户切换选择启用的开关时立即应用或恢复提升,
## 而不是只等到下一次连接/断开。authed_peer_count 让编排器
## 传入实时计数(本子模块不拥有传输层)。
func notify_unfocused_responsive_setting_changed(authed_peer_count: int) -> void:
	if is_unfocused_responsive_enabled():
		if authed_peer_count > 0:
			lower()
	else:
		restore()


## 在首次鉴权连接时把全局键调低为提升值(经 should_capture_boost
## 实现幂等)。先在注册表锁之下把真实原始值捕获到机器级备份中,
## 这样并发的实例就无法把
## 已提升的值记录为原始值。
func lower() -> void:
	if not _UnfocusedBackup.should_capture_boost(
			is_unfocused_responsive_enabled(), is_unfocused_boost_active()):
		return
	var es = _editor_settings()
	if es == null:
		return
	var live := int(es.get_setting(_UNFOCUSED_SLEEP_KEY))
	var boosted := _configured_responsive_usec()
	# 在注册表锁之下做机器级、先写者获胜的真实原始值备份,
	# 这样并发的实例就无法把已提升的
	# 值捕获为原始值。
	var dir := RegistryClient.registry_dir()
	var ver := _UnfocusedBackup.version_key()
	RegistryClient.acquire_lock()
	_UnfocusedBackup.capture_if_absent(dir, live, boosted, ver)
	RegistryClient.release_lock()
	es.set_setting(_UNFOCUSED_SLEEP_KEY, boosted)
	_original_unfocused_sleep_usec = live
	print("[MCPServer] unfocused-responsive mode ON: %s %d -> %d (~%d fps while unfocused)" % [
		_UNFOCUSED_SLEEP_KEY, live, boosted, get_unfocused_responsive_fps()])


## 在最后一次鉴权断开/停止时恢复全局键。能识别冲突:若有人
## 或其他工具在提升期间改过该键,则保留其值,
## 只清除备份。
func restore() -> void:
	if not is_unfocused_boost_active():
		return
	var es = _editor_settings()
	if es == null:
		_original_unfocused_sleep_usec = -1
		return
	var dir := RegistryClient.registry_dir()
	var ver := _UnfocusedBackup.version_key()
	RegistryClient.acquire_lock()
	var backup := _UnfocusedBackup.read_backup(dir, ver)
	var current := int(es.get_setting(_UNFOCUSED_SLEEP_KEY))
	var decision := _UnfocusedBackup.resolve_restore(current, backup)
	if decision["restore"]:
		es.set_setting(_UNFOCUSED_SLEEP_KEY, int(decision["value"]))
	_UnfocusedBackup.delete_backup(dir, ver)
	RegistryClient.release_lock()
	_original_unfocused_sleep_usec = -1
	if decision["restore"]:
		print("[MCPServer] unfocused-responsive mode OFF: %s restored to %d" % [
			_UNFOCUSED_SLEEP_KEY, int(decision["value"])])
	else:
		print("[MCPServer] unfocused-responsive mode OFF: %s left at %d (changed during boost; backup cleared)" % [
			_UNFOCUSED_SLEEP_KEY, current])


## 启动自愈:如果上一次会话(崩溃)或并发实例留下了备份,
## 则以识别冲突的方式回退全局键并清除备份,使提升
## 绝不可能在没有活动连接的情况下持续存在。无论选择启用的
## 设置如何都会运行(即使之后用户把它关闭,
## 之前开启时留下的残留也必须清理)。在 start() 时安全:此时不可能有已鉴权的对端,authed_peer_count 为 0。
func self_heal(authed_peer_count: int) -> void:
	var dir := RegistryClient.registry_dir()
	var ver := _UnfocusedBackup.version_key()
	if not _UnfocusedBackup.has_backup(dir, ver):
		return
	if authed_peer_count > 0:
		return  # 防御性 —— 在 start() 时绝不会为 true(传输层尚未构建 / 没有已鉴权对端)
	var es = _editor_settings()
	if es == null:
		return
	RegistryClient.acquire_lock()
	var backup := _UnfocusedBackup.read_backup(dir, ver)
	var current := int(es.get_setting(_UNFOCUSED_SLEEP_KEY))
	var decision := _UnfocusedBackup.resolve_restore(current, backup)
	if decision["restore"]:
		es.set_setting(_UNFOCUSED_SLEEP_KEY, int(decision["value"]))
	_UnfocusedBackup.delete_backup(dir, ver)
	RegistryClient.release_lock()
	if decision["restore"]:
		print("[MCPServer] unfocused-responsive self-heal: reverted leftover %s to %d (crash/concurrent leftover)" % [
			_UNFOCUSED_SLEEP_KEY, int(decision["value"])])
	else:
		print("[MCPServer] unfocused-responsive self-heal: %s changed since boost (now %d); kept it, cleared stale backup" % [
			_UNFOCUSED_SLEEP_KEY, current])


# 配置的提升后休眠值,单位 µs(默认 16666 = 60 fps;不做范围限制)。
func _configured_responsive_usec() -> int:
	var es = _editor_settings()
	if es == null or not es.has_setting(_RESPONSIVE_SLEEP_KEY):
		return _ACTIVE_UNFOCUSED_SLEEP_USEC
	return int(es.get_setting(_RESPONSIVE_SLEEP_KEY))


# 通过注入的访问器解析实时 EditorSettings(无头模式 / 未接线时为 null)。
# 让 EditorInterface 的触达留在编辑器编排器中。
func _editor_settings():
	if not _settings_accessor.is_valid():
		return null
	return _settings_accessor.call()
