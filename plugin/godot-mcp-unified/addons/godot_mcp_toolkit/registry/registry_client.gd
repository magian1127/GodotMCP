@tool
extends RefCounted
## 多实例注册表的轻量生命周期编排器 + 稳定门面(façade)。
##
## 这是调用方绑定的公开接口(register / deregister /
## set_runtime / clear_runtime / ensure_registered、运行时端口与
## LSP 端点的回读,以及锁/目录的透传)。它只负责时序编排 ——
## 启动清理(reap)、重复打开警告、"先写入、再上锁、后重建"的顺序、
## 延迟复核,以及回读的组合。具体"怎么做"位于它所委托的
## 各注册表叶子模块:
##   • ProjectKey       —— 规范的项目身份(路径 + 12 字符哈希)
##   • RegistryPaths    —— 机器级注册表目录及其中所有文件路径
##   • RegistryEntryFile —— 单个条目文件的原子读/写/删 + 条目构建器
##   • RegistryProjection —— 把条目文件汇聚(fan-in)成 projects.json 读模型
##   • FileLock         —— 包裹每次投影重建的咨询锁(advisory lock)
##
## 所有方法都是静态的 —— 没有实例状态。保持 @tool 且对编辑器无污染
## (不引用任何 Editor* 符号):它位于运行时自动加载(autoload)的预加载闭包中
## (mcp_runtime_server.gd),因此这里若引用了被编辑器污染的符号,
## 会导致自动加载在导出中解析失败(godot#91713)。

const _VersionUtils := preload("res://addons/godot_mcp_toolkit/versioning/mcp_version_utils.gd")
const _FileLock := preload("res://addons/godot_mcp_toolkit/registry/store/file_lock.gd")
# 直接 preload(不经过 core/modules.gd):本文件位于运行时自动加载的
# 预加载闭包中(mcp_runtime_server.gd),因此必须保持对编辑器无污染 ——
# core/modules.gd 引用了 EditorInterface,会在导出中污染自动加载(godot#91713)。
const _ProjectKey := preload("res://addons/godot_mcp_toolkit/paths/project_key.gd")
# 直接 preload(不经过 core/modules.gd):与上文相同的运行时闭包洁净性原因 ——
# RegistryPaths 对编辑器无污染,且负责磁盘布局。
const _RegistryPaths := preload("res://addons/godot_mcp_toolkit/registry/store/registry_paths.gd")
# 直接 preload(不经过 core/modules.gd):同样的运行时闭包洁净性原因 ——
# RegistryEntryFile 对编辑器无污染,且负责单条目文件 I/O + 条目构建器。
const _RegistryEntryFile := preload("res://addons/godot_mcp_toolkit/registry/store/registry_entry_file.gd")
# 直接 preload(不经过 core/modules.gd):同样的运行时闭包洁净性原因 ——
# RegistryProjection 对编辑器无污染,并构建 projects.json 读模型。
const _RegistryProjection := preload("res://addons/godot_mcp_toolkit/registry/store/registry_projection.gd")


# -- 路径辅助方法(委托给 RegistryPaths —— 布局的权威) -----------


## 门面透传 —— 外部调用方(unfocused_sleep_controller、
## extension_catalog)绑定到 RegistryClient.registry_dir()。
static func registry_dir() -> String:
	return _RegistryPaths.registry_dir()


static func _project_key() -> String:
	return _ProjectKey.current()


static func _entry_file_path() -> String:
	return _RegistryPaths.entry_file_path()


static func _runtime_entry_file_path() -> String:
	return _RegistryPaths.runtime_entry_file_path()


# -- 锁文件 -----------------------------------------------------------------


## 公开的锁包装器,提供给需要在 registry_dir() 内、对某个同目录文件做
## 机器级读-改-写(read-modify-write)串行化的调用方,用于在多个并发的
## 编辑器实例之间互斥(例如无焦点休眠备份 —— 见 mcp_server.gd /
## unfocused_backup.gd)。与注册表自身写入用的是同一把锁,因此备份与
## 注册表操作互斥(两者都既少见又快速)。
##
## 这些锁/目录包装器是有意留在注册表门面上的:目前恰好只有一个外部锁客户端
## (unfocused_sleep_controller,仅使用 registry_dir + acquire_lock +
## release_lock),为此拆分出一个独立的锁角色为时过早。
## 若将来出现第二个非注册表的锁客户端,再提取一个专门的 RegistryLock 角色
## (三次法则(rule-of-three)/ 接口隔离原则(ISP)),并让两个客户端都指向它。
static func acquire_lock() -> bool:
	return _FileLock.acquire(_RegistryPaths.lock_path())


static func release_lock() -> void:
	_FileLock.release(_RegistryPaths.lock_path())


# -- 条目文件 I/O(委托给 RegistryEntryFile —— I/O 叶子模块) ------------


# 当前实例的便捷封装:绑定本编辑器的条目路径,并把原子 I/O 委托给
# RegistryEntryFile。下方的生命周期方法负责编排这些操作。
static func _write_entry(entry: Dictionary) -> void:
	_RegistryEntryFile.write(_entry_file_path(), entry)


static func _read_entry() -> Dictionary:
	return _RegistryEntryFile.read(_entry_file_path())


static func _delete_entry() -> void:
	_RegistryEntryFile.delete(_entry_file_path())


# 构建本实例的条目字典 —— 委托给 I/O 叶子模块的纯构建器。
static func _build_entry(key: String, port: int, token_path: String,
		lsp_host: String, lsp_port, runtime_port, runtime_pid) -> Dictionary:
	return _RegistryEntryFile.build_entry(
		key, port, token_path, lsp_host, lsp_port, runtime_port, runtime_pid)


# -- 投影重建(委托给 RegistryProjection —— 读模型) -----


# 从所有条目文件重建 projects.json。委托给投影构建器 RegistryProjection。
# 下方的生命周期方法把它包在 acquire_lock()/release_lock() 中 ——
# 锁由调用方持有(RegistryProjection 默认已持锁,
# 或接受良性的最后写入者获胜)。
static func _rebuild_projects_json() -> void:
	_RegistryProjection.rebuild()


# -- 生命周期(公开门面) -------------------------------------------------


static func register(port: int, token_path: String, lsp_host: String, lsp_port: int) -> void:
	# 启动清理(reap):清掉上一次试玩测试(playtest)崩溃遗留的过期
	# <hash>.runtime.json —— 它没等到 clear_runtime() 触发就崩溃了;否则其中
	# 已失效的 runtime_port 会一直叠加在本编辑器的条目之上,
	# 直到下一次试玩测试覆盖它。这里无条件删除是安全的:register() 只在
	# 编辑器启动时运行,而 <hash>.runtime.json 的唯一写入者是本项目的
	# 试玩测试子进程,它会随父编辑器一同消亡,因此现在不可能还活着
	# (不存在并发的读-改-写)。导出(EXPORTED)的游戏其 res:// 会哈希到
	# 另一个文件,因此绝不会碰到它。OS.has_feature("editor") 用于防范将来
	# 可能出现的非编辑器调用方(运行时安全,不污染编辑器符号)。
	if OS.has_feature("editor"):
		_RegistryEntryFile.delete(_runtime_entry_file_path())
	var key := _project_key()
	var my_pid := OS.get_process_id()
	var my_entry := _build_entry(key, port, token_path, lsp_host, lsp_port, null, null)
	# 重复打开时(同一项目在两个编辑器中)发出警告。
	var existing := _read_entry()
	if not existing.is_empty():
		var existing_pid := int(existing.get("pid", 0))
		if existing_pid > 0 and existing_pid != my_pid and OS.is_process_running(existing_pid):
			push_warning("[MCPRegistry] already registered from PID %d; overwriting with PID %d" % [existing_pid, my_pid])
	# 写入自己的条目文件 —— 无竞态:每个编辑器写入一个独一无二的文件。
	_write_entry(my_entry)
	# 从所有条目文件重建 projects.json(幂等)。
	acquire_lock()
	_rebuild_projects_json()
	release_lock()
	print("[MCPRegistry] registered %s on port %d" % [key, port])


static func deregister() -> void:
	var key := _project_key()
	_delete_entry()
	acquire_lock()
	_rebuild_projects_json()
	release_lock()
	print("[MCPRegistry] deregistered %s" % key)


## 由运行时自动加载(运行中的游戏)在 Mode-B WebSocket 服务器绑定时调用。
## 写入运行时自己的(OWN)条目文件(<hash>.runtime.json),因此绝不会对
## 编辑器的 <hash>.json 做读-改-写。该文件自身即满足完整 schema:
## 当编辑器条目存在时,_rebuild_projects_json 只把 runtime_port/runtime_pid
## 叠加到编辑器基础条目上;当不存在时,由本文件的完整形状顶上
## (port -1、token_path ""、lsp_port null —— 当时没有编辑器在场去解析
## LSP 端点,服务器会将其读作"未提供")。
static func set_runtime(runtime_port: int) -> void:
	var key := _project_key()
	var my_pid := OS.get_process_id()
	var entry := {
		"_key": key,
		"port": -1,
		"token_path": "",
		"pid": my_pid,
		"started_at": int(Time.get_unix_time_from_system()),
		"godot_version": _VersionUtils.get_engine_version_pair(),
		"runtime_port": runtime_port,
		"runtime_pid": my_pid,
		"lsp_host": "127.0.0.1",
		"lsp_port": null,
	}
	_RegistryEntryFile.write(_runtime_entry_file_path(), entry)
	acquire_lock()
	_rebuild_projects_json()
	release_lock()
	print("[MCPRegistry] runtime port %d registered for %s" % [runtime_port, key])


## set_runtime 的运行时对应操作 —— 删除运行时自己的文件,使叠加层在
## 下一次重建时消失。只触碰 <hash>.runtime.json。
static func clear_runtime() -> void:
	var path := _runtime_entry_file_path()
	if not FileAccess.file_exists(path):
		return  # 已清除 / 从未设置
	_RegistryEntryFile.delete(path)
	acquire_lock()
	_rebuild_projects_json()
	release_lock()


## 延迟复核:在初始 register() 之后几秒被调用,以确保我们的条目文件
## 仍然存在,且 projects.json 是最新的。
## 在条目文件架构下,这主要是一次重建触发 ——
## 我们的条目文件不会被其他编辑器覆盖(路径唯一)。
static func ensure_registered(port: int, token_path: String, lsp_host: String, lsp_port: int) -> void:
	var key := _project_key()
	var my_pid := OS.get_process_id()
	var entry := _read_entry()
	if not entry.is_empty() and int(entry.get("pid", 0)) == my_pid:
		# 条目文件存在且 PID 是我们的 —— 刷新 LSP 端点(一次实时的重新发布
		# 可能传入已变化的端口/主机),并重建 projects.json。
		entry["lsp_host"] = lsp_host
		entry["lsp_port"] = lsp_port
		_write_entry(entry)
		acquire_lock()
		_rebuild_projects_json()
		release_lock()
		return
	# 条目文件缺失或 PID 不符 —— 重新创建。编辑器条目从不携带运行时字段
	# (运行时子进程拥有 <hash>.runtime.json),因此传 null;
	# 运行时叠加层会在下一次重建时重新应用。
	push_warning("[MCPRegistry] entry file missing during deferred re-verify; re-creating for %s" % key)
	var new_entry := _build_entry(key, port, token_path, lsp_host, lsp_port, null, null)
	_write_entry(new_entry)
	acquire_lock()
	_rebuild_projects_json()
	release_lock()
	print("[MCPRegistry] re-registered %s on port %d (deferred)" % [key, port])


## 只读:返回本项目的 runtime_port,没有则返回 -1。读取运行时子进程
## 自己的文件(<hash>.runtime.json)—— 运行时端口存放在那里,
## 而不在编辑器的 <hash>.json 中。
static func get_runtime_port() -> int:
	var entry := _RegistryEntryFile.read(_runtime_entry_file_path())
	if entry.is_empty():
		return -1
	var rp = entry.get("runtime_port", null)
	if rp == null:
		return -1
	return int(rp)


## 只读:以 {host, port} 的形式返回本编辑器已发布的 LSP 端点,
## 尚未发布/不可用时返回 {}。支撑停靠面板指示器。纯函数。
static func get_lsp_endpoint() -> Dictionary:
	var entry := _read_entry()
	if entry.is_empty() or entry.get("lsp_port", null) == null:
		return {}
	return {
		"host": str(entry.get("lsp_host", "127.0.0.1")),
		"port": int(entry.get("lsp_port", 6005)),
	}
