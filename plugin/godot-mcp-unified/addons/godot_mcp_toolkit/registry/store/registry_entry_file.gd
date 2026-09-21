@tool
extends RefCounted
## 多实例注册表的单条目文件 I/O。
##
## 原子地读、写、删除一个(ONE)条目文件,并构建写入其中的条目字典。
## 以路径为键:调用方传入确切的文件路径
## (RegistryPaths.entry_file_path() / runtime_entry_file_path())——
## 这里没有"当前实例"便捷包装,这使得本模块成为一个无状态、
## 不依赖 ProjectSettings 的叶子,可以轻而易举地做单元测试。
##
## 所有方法都是静态的 —— 没有实例状态。对编辑器无污染:本文件位于
## 运行时自动加载的预加载闭包中(经 registry_client.gd),
## 因此不引用任何仅编辑器可用的类(godot#91713)。
## 编辑器侧负责解析 LSP 端点,并把 lsp_host/lsp_port 传入 build_entry,
## 因此本文件从不触碰 EditorInterface。

const _VersionUtils := preload("res://addons/godot_mcp_toolkit/versioning/mcp_version_utils.gd")


# -- 单条目文件 I/O -----------------------------------------------------


# 每个写入者拥有自己的文件(编辑器:<hash>.json,运行时:<hash>.runtime.json),
# 因此按路径派生的 .tmp 文件名绝不会在两个进程之间冲突。
static func write(path: String, entry: Dictionary) -> void:
	var tmp := path + ".tmp"
	var f := FileAccess.open(tmp, FileAccess.WRITE)
	if f == null:
		push_warning("[MCPRegistry] cannot write entry %s (err %d)" % [tmp, FileAccess.get_open_error()])
		return
	f.store_string(JSON.stringify(entry, "\t"))
	f.close()
	# 原子重命名:先移除目标(Windows 上目标已存在时重命名会失败)。
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)
	var err := DirAccess.rename_absolute(tmp, path)
	if err != OK:
		push_warning("[MCPRegistry] rename %s -> %s failed (err %d)" % [tmp, path, err])
		DirAccess.remove_absolute(tmp)


static func read(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return {}
	var text := f.get_as_text()
	f.close()
	var parsed = JSON.parse_string(text)
	if parsed == null or not parsed is Dictionary:
		return {}
	return parsed


static func delete(path: String) -> void:
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)


# -- 条目字典构建器 --------------------------------------------------------


## 根据给定的事实构建注册表条目字典。纯函数 —— 不访问文件系统,
## 不引用 EditorInterface:编辑器侧解析 LSP 端点
## (MCPServer.resolve_lsp_endpoint)并把 lsp_host/lsp_port 传入,因此本文件
## 保持对编辑器无污染,Mode-B 运行时自动加载可以放心地 preload 它
## (这里若引用仅编辑器可用的类,会导致自动加载在导出中解析失败 ——
## godot#91713)。lsp_port/runtime_* 不带类型标注:已知时为 int,否则为 null。
static func build_entry(key: String, port: int, token_path: String,
		lsp_host: String, lsp_port, runtime_port, runtime_pid) -> Dictionary:
	return {
		"_key": key,
		"port": port,
		"token_path": token_path,
		"pid": OS.get_process_id(),
		"started_at": int(Time.get_unix_time_from_system()),
		"godot_version": _VersionUtils.get_engine_version_pair(),
		"runtime_port": runtime_port,
		"runtime_pid": runtime_pid,
		"lsp_host": lsp_host,
		"lsp_port": lsp_port,
	}
