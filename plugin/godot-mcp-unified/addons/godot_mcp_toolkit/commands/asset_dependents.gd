@tool
extends RefCounted
## 反向依赖(reverse dependency)索引 — 回答"谁引用我"。正向依赖
## (ResourceLoader.get_dependencies)由 asset.get_dependencies 提供;本模块扫描
## EditorFileSystem 已索引的资源文件,把每条正向依赖边倒排成
## 依赖路径 → [引用者路径],供两个消费方使用:
##  - asset.get_dependents:只读查询(直接或反向 BFS 传递);
##  - file.delete / resource.delete:删除前引用安全检查(被引用默认拒绝)。
## 参考 GDEditorBridge asset_deps.gd 的倒排索引思路(MIT)。
##
## 索引生命周期:首次使用时全量构建一次并缓存;EditorFileSystem 的
## filesystem_changed 信号(扫描/导入完成后发出)把缓存标记为脏,下次使用时
## 重建。外部写文件在扫描发生前不会触发失效 —— 删除检查结果随附
## index_built_at/index_age_ms,调用方据此判断是否需要 refresh 重建。

## 索引:依赖路径(String)→ 引用者路径 Array[String]。空 = 未构建/已失效。
static var _index: Dictionary = {}
## 索引构建完成时刻(Time.get_ticks_msec());0 = 脏/未构建。
static var _built_at_ms := 0
## filesystem_changed 信号是否已连接(连接一次,插件重载后随静态态重置)。
static var _signal_connected := false

## 参与正向依赖扫描的扩展名:所有有意义的引用边都从这些文件类型发出
## (纹理/网格等被引用者自身没有依赖边)。
const DEPENDABLE_EXTENSIONS := ["tscn", "scn", "tres", "res", "gd", "gdshader"]
## 单次索引构建扫描的文件上限(与 scene.query 的 20000 节点上限同一量级哲学)。
const MAX_INDEX_FILES := 20000


## filesystem_changed → 缓存标脏(扫描/导入完成后引擎发出)。
static func _mark_dirty() -> void:
	_built_at_ms = 0


## 连接失效信号(幂等;编辑器侧专用)。
static func _connect_signals() -> void:
	if _signal_connected:
		return
	_signal_connected = true
	var filesystem := EditorInterface.get_resource_filesystem()
	filesystem.filesystem_changed.connect(_mark_dirty)


## 确保索引可用:缓存新鲜则复用,否则(且允许时)重建。
## 返回 {ok, index, built_at_unix, age_ms, cached, scanned, truncated} 或
## {ok:false, reason:"scanning"} —— 中途的扫描会与全量遍历竞争,调用方如实处理。
static func ensure_index(force_rebuild: bool = false) -> Dictionary:
	if _built_at_ms == 0 or force_rebuild:
		var filesystem := EditorInterface.get_resource_filesystem()
		if filesystem.is_scanning():
			if _built_at_ms != 0 and not force_rebuild:
				# 扫描进行中无法重建,但旧索引仍在:返回它并披露年龄。
				return _index_payload(true)
			return {"ok": false, "reason": "scanning"}
		var paths: Array[String] = []
		_walk_all_paths(filesystem.get_filesystem(), paths)
		var truncated := paths.size() >= MAX_INDEX_FILES
		var index: Dictionary = {}
		for path in paths:
			for dep in _forward_dep_paths(path):
				if not index.has(dep):
					index[dep] = [] as Array[String]
				(index[dep] as Array[String]).append(path)
		_index = index
		_built_at_ms = Time.get_ticks_msec()
		_connect_signals()
		var payload := _index_payload(false)
		payload["scanned"] = paths.size()
		payload["truncated"] = truncated
		return payload
	return _index_payload(true)


static func _index_payload(cached: bool) -> Dictionary:
	var built_unix := 0.0
	var age_ms := -1
	if _built_at_ms != 0:
		age_ms = Time.get_ticks_msec() - _built_at_ms
		built_unix = Time.get_unix_time_from_system() - age_ms / 1000.0
	return {
		"ok": true,
		"index": _index,
		"built_at_unix": built_unix,
		"age_ms": age_ms,
		"cached": cached,
	}


## 收集 EditorFileSystem 里全部可发出依赖边的文件路径(深度优先,带上限)。
static func _walk_all_paths(directory: EditorFileSystemDirectory, into: Array[String]) -> void:
	if into.size() >= MAX_INDEX_FILES:
		return
	for index in range(directory.get_file_count()):
		var file_name := directory.get_file(index)
		if file_name.get_extension().to_lower() in DEPENDABLE_EXTENSIONS:
			into.append(directory.get_file_path(index))
			if into.size() >= MAX_INDEX_FILES:
				return
	for subdir_index in range(directory.get_subdir_count()):
		_walk_all_paths(directory.get_subdir(subdir_index), into)


## 一个文件的正向依赖边,解析为 res:// 路径(uid:// 经 ResourceUID 倒回;
## 解析不了的 token 保留原样 —— 宁可多一条不可匹配的键,不丢一条真边)。
static func _forward_dep_paths(path: String) -> Array[String]:
	var resolved: Array[String] = []
	var seen: Dictionary = {}
	for raw_dependency in ResourceLoader.get_dependencies(path):
		var parts := String(raw_dependency).split("::")
		var stripped := parts[0]
		if stripped.begins_with("uid://"):
			var id := ResourceUID.text_to_id(stripped)
			if id != ResourceUID.INVALID_ID and ResourceUID.has_id(id):
				var known_path := ResourceUID.get_id_path(id)
				if known_path != "":
					stripped = known_path
		if not stripped.is_empty() and not seen.has(stripped):
			seen[stripped] = true
			resolved.append(stripped)
	return resolved


## 删除前的引用安全检查:file_path 被谁直接引用。
## 返回 {checked, blocked, referencers, total, index_built_at, index_age_ms, note};
## checked=false 表示索引无法构建(如扫描进行中),调用方如实降级为警告。
static func referenced_by(file_path: String) -> Dictionary:
	var ensured := ensure_index(false)
	if not ensured.get("ok", false):
		return {
			"checked": false,
			"blocked": false,
			"referencers": [],
			"total": 0,
			"note": "reverse-dependency index unavailable (%s); reference check skipped" % str(ensured.get("reason", "unknown")),
		}
	var index: Dictionary = ensured["index"]
	var dependents: Array = index.get(file_path, [])
	return {
		"checked": true,
		"blocked": not dependents.is_empty(),
		"referencers": dependents.duplicate(),
		"total": dependents.size(),
		"index_built_at": ensured["built_at_unix"],
		"index_age_ms": ensured["age_ms"],
	}
