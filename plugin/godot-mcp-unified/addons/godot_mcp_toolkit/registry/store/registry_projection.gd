@tool
extends RefCounted
## 从每实例条目文件构建投影/读模型 projects.json。DDD(领域驱动设计):
## 这是一个投影(PROJECTION),而不是事务性聚合根 —— 每实例条目文件
## (<hash>.json、<hash>.runtime.json)才是写入侧的聚合(各有一个写入者);
## projects.json 是派生出来的、经汇聚(fan-in)而成的读模型,
## 由 TypeScript 桥接读取。它不归属于任何人,并以幂等方式重建
## (相同的条目文件 → 相同的输出)。
##
## 重建会扫描所有条目文件,按端口冲突剪除过期的编辑器条目,把运行时条目
## 按 _key 叠加到编辑器基础条目上,并原子地写入 projects.json。
## 锁由调用方持有(或接受良性的最后写入者获胜)——
## 见 RegistryClient 的生命周期方法。
##
## 所有方法都是静态的 —— 没有实例状态。对编辑器无污染:本文件位于
## 运行时自动加载的预加载闭包中(经 registry_client.gd),因此不引用任何
## 仅编辑器可用的类(godot#91713)。

# 直接 preload(不经过 core/modules.gd):本文件位于运行时自动加载的
# 预加载闭包中(registry_client.gd → mcp_runtime_server.gd),因此必须保持
# 对编辑器无污染 —— core/modules.gd 引用了 EditorInterface,会在导出中
# 污染自动加载(godot#91713)。RegistryPaths 对编辑器无污染,且负责磁盘布局。
const _RegistryPaths := preload("res://addons/godot_mcp_toolkit/registry/store/registry_paths.gd")


# -- 注册表 I/O(rebuild 使用) -------------------------------------------


static func write_atomic(data: Dictionary) -> void:
	# 单次尝试 + 一次性重试:瞬时失败(无焦点睡眠的 I/O 抖动、
	# 防病毒扫描争用等)会让第一次打开/重命名失败;数据未变,
	# 100ms 后重试一次即可覆盖这类偶发,避免聚合读模型缺失。
	# 注意本函数可能被运行时自动加载(Autoload)的预加载闭包调用
	# (mcp_runtime_server.gd → registry_client.gd → 本模块),
	# 因此重试路径只使用核心 API,不引入任何仅编辑器符号。
	if _write_atomic_once(data):
		return
	OS.delay_msec(100)
	_write_atomic_once(data)


## 单次原子写入;失败时 push_warning(4.5+ 自动进入编辑器日志缓冲,
## 可经 editor.get_console 检索)并返回 false,成功返回 true。
static func _write_atomic_once(data: Dictionary) -> bool:
	var path := _RegistryPaths.registry_path()
	var tmp_path := path + ".tmp"
	var bak_path := path + ".bak"
	var f := FileAccess.open(tmp_path, FileAccess.WRITE)
	if f == null:
		push_warning("[MCPRegistry] cannot write %s (err %d)" % [tmp_path, FileAccess.get_open_error()])
		return false
	f.store_string(JSON.stringify(data, "\t"))
	f.close()
	# 两阶段重命名:.tmp → 目标,带 .bak 安全网。
	# 在 Windows 上目标已存在时 DirAccess.rename 会失败,因此我们
	# 先把现有文件重命名为 .bak,再把 .tmp 重命名为目标。若第二次
	# 重命名失败,则从 .bak 恢复。
	if FileAccess.file_exists(path):
		# 阶段 1:现有文件 → .bak
		if FileAccess.file_exists(bak_path):
			DirAccess.remove_absolute(bak_path)
		var bak_err := DirAccess.rename_absolute(path, bak_path)
		if bak_err != OK:
			push_warning("[MCPRegistry] rename %s -> %s failed (err %d); aborting write" % [path, bak_path, bak_err])
			DirAccess.remove_absolute(tmp_path)
			return false
	# 阶段 2:.tmp → 目标
	var err := DirAccess.rename_absolute(tmp_path, path)
	if err != OK:
		push_warning("[MCPRegistry] rename %s -> %s failed (err %d); restoring from backup" % [tmp_path, path, err])
		# 若 .bak 存在,则恢复 .bak → 目标。
		if FileAccess.file_exists(bak_path):
			DirAccess.rename_absolute(bak_path, path)
		DirAccess.remove_absolute(tmp_path)
		return false
	# 成功后清理 .bak。
	if FileAccess.file_exists(bak_path):
		DirAccess.remove_absolute(bak_path)
	return true


# -- 从条目文件重建 projects.json ------------------------------------

# 运行时子进程拥有的字段。当某个 _key 已存在编辑器基础条目时,
# 只有这些字段会从 <hash>.runtime.json 叠加 —— 其余每个字段都保持编辑器的值。
# 仅运行时的条目(没有编辑器基础条目)自身即满足完整 schema。
const _RUNTIME_OWNED_FIELDS := ["runtime_port", "runtime_pid"]


## 扫描 entries/*.json(以及 *.runtime.json),并写出聚合的
## projects.json。OS.is_process_running() 在 Windows 上不可靠
## (对仍然活着的兄弟编辑器也返回 false),因此不使用基于 PID 的 GC。
## 改用端口冲突剪除来移除过期的编辑器条目:当两个条目声称同一个端口时,
## started_at 较旧的那个会被剪除(其条目文件会被删除,
## 以免在下次重建时重新出现)。
## 新近死亡的条目由 deregister() 在正常退出时清理,
## 或在同一个项目重新打开(相同哈希)时被覆盖。
## 并发重建是幂等的(相同文件 → 相同输出)。
## 调用方必须持有锁(或接受良性的最后写入者获胜)。
static func rebuild() -> void:
	var dir_path := _RegistryPaths.entry_dir()
	var dir := DirAccess.open(dir_path)
	if dir == null:
		write_atomic({"by_path": {}})
		return

	# 第 1 遍:把编辑器(<hash>.json)与运行时(<hash>.runtime.json)文件
	# 扫描进不同的桶。每个编辑器条目:{ data, fpath, port, started_at }。
	var editor_items: Array[Dictionary] = []
	var runtime_entries: Array[Dictionary] = []
	dir.list_dir_begin()
	var fname := dir.get_next()
	while fname != "":
		if not fname.ends_with(".json") or fname.ends_with(".tmp"):
			fname = dir.get_next()
			continue
		var fpath := dir_path.path_join(fname)
		var f := FileAccess.open(fpath, FileAccess.READ)
		if f == null:
			fname = dir.get_next()
			continue
		var text := f.get_as_text()
		f.close()
		var parsed = JSON.parse_string(text)
		if parsed == null or not parsed is Dictionary or not parsed.has("_key"):
			fname = dir.get_next()
			continue
		var entry: Dictionary = parsed
		if fname.ends_with(".runtime.json"):
			runtime_entries.append(entry)
		else:
			editor_items.append({
				"data": entry,
				"fpath": fpath,
				"port": int(entry.get("port", 0)),
				"started_at": int(entry.get("started_at", 0)),
			})
		fname = dir.get_next()
	dir.list_dir_end()

	# 第 2 遍:对每个端口,只保留最新的编辑器条目(started_at 最高者)。
	# 运行时条目的 port 为 -1,因此从不参与。
	var best_by_port: Dictionary = {}  # int → editor_items 的下标
	var stale_files: Array[String] = []
	var editor_entries: Array[Dictionary] = []
	for i in editor_items.size():
		var port: int = editor_items[i]["port"]
		if port <= 0:
			continue  # 无端口 —— 无条件保留。
		if not best_by_port.has(port):
			best_by_port[port] = i
		else:
			var prev_idx: int = best_by_port[port]
			if editor_items[i]["started_at"] > editor_items[prev_idx]["started_at"]:
				# 新条目更新 —— 剪除旧条目。
				stale_files.append(editor_items[prev_idx]["fpath"])
				editor_items[prev_idx]["_pruned"] = true
				best_by_port[port] = i
			else:
				# 旧条目更新 —— 剪除当前这条。
				stale_files.append(editor_items[i]["fpath"])
				editor_items[i]["_pruned"] = true
	for item in editor_items:
		if item.get("_pruned", false):
			continue
		editor_entries.append(item["data"])

	# 第 3 遍:按 _key 合并编辑器基础条目与运行时叠加条目(纯函数)。
	var by_path := merge_by_path(editor_entries, runtime_entries)

	# 删除过期的条目文件,以免它们在下次重建时重新出现。
	for stale_path in stale_files:
		DirAccess.remove_absolute(stale_path)
	write_atomic({"by_path": by_path})


## 纯函数:按 _key 对编辑器与运行时条目分组,产出 by_path 映射。
## 对每个 _key,该行即编辑器基础条目(去掉 _key),
## 再叠加来自匹配运行时条目的运行时自有字段。没有编辑器基础条目的
## 运行时条目贡献其完整(满足完整 schema)的形状;没有运行时叠加的
## 编辑器条目保留自己的 runtime_port/runtime_pid(null)。
## 不访问文件系统 —— 可直接单元测试。
static func merge_by_path(editor_entries: Array, runtime_entries: Array) -> Dictionary:
	# 以 _key 为键索引运行时条目,便于叠加查找。
	var runtime_by_key: Dictionary = {}
	for re in runtime_entries:
		var re_dict: Dictionary = re
		runtime_by_key[str(re_dict.get("_key", ""))] = re_dict

	var by_path := {}
	# 先处理编辑器基础条目 —— 在存在运行时条目之处叠加运行时自有字段。
	for ee in editor_entries:
		var ee_dict: Dictionary = ee
		var key := str(ee_dict.get("_key", ""))
		var row: Dictionary = ee_dict.duplicate()
		row.erase("_key")
		if runtime_by_key.has(key):
			var rt: Dictionary = runtime_by_key[key]
			for field in _RUNTIME_OWNED_FIELDS:
				row[field] = rt.get(field, null)
		by_path[key] = row
	# 仅运行时的条目(没有编辑器基础条目)—— 使用完整的运行时形状。
	for rkey in runtime_by_key:
		if by_path.has(rkey):
			continue
		var rt_only: Dictionary = runtime_by_key[rkey]
		var rt_row: Dictionary = rt_only.duplicate()
		rt_row.erase("_key")
		by_path[rkey] = rt_row
	return by_path
