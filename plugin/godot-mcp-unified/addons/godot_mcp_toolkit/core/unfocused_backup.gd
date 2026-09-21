@tool
extends RefCounted
## 机器级、按版本作键、先写者获胜的备份,保存全局 EditorSetting
## `interface/editor/unfocused_low_processor_mode_sleep_usec`
## 的真实原始值。
##
## 工具集会调低该键,让编辑器在失去焦点且有上下文协议(MCP)客户端连接时保持响应,
## 并在最后一个连接断开时恢复它。由于实时恢复只发生在内存中,
## 一次崩溃(在设置的 `save()` 把提升值刷入磁盘之后)或并发的第二个编辑器
## 可能把全局键滞留在提升值上 —— 更糟的是,下次启动时会把
## 该提升值当作“原始值”重新读取,
## 永久丢失真实默认值
## (崩溃与并发两种情形的根因相同:
## 把提升值当成了原始值)。
##
## 本辅助模块把 `{original, boosted}` 持久化到机器级注册表目录下的一个
## 伴生文件中,让真实原始值既能挺过崩溃,也能挺过并发实例。
## 决策逻辑(`resolve_restore`、`should_capture_boost`)是纯函数,
## 可在无头模式下单元测试;文件 I/O 接收显式的 `dir` 参数,
## 测试使用临时目录。跨实例的原子性(先写者获胜)
## 由调用方在 `capture_if_absent` / `delete_backup` 周围
## 持有注册表文件锁来保证
## (见 mcp_server.gd)—— 本脚本自身不做加锁,以保持纯粹。
##
## 文件名:`unfocused_sleep_backup_<major.minor>.json` —— 按版本作键,
## 两个同时运行的 Godot 编辑器版本绝不会混淆各自独立的
## EditorSettings。

const _FILENAME_PREFIX := "unfocused_sleep_backup_"


## 运行中引擎的 "<major>.<minor>",用于为备份文件作键。
## 传入一个 `{major, minor}` 字典可覆盖(单元测试用)。
static func version_key(version_info: Dictionary = {}) -> String:
	var vi := version_info if not version_info.is_empty() else Engine.get_version_info()
	return "%d.%d" % [int(vi.get("major", 0)), int(vi.get("minor", 0))]


static func backup_path(dir: String, ver: String) -> String:
	return dir.path_join(_FILENAME_PREFIX + ver + ".json")


static func has_backup(dir: String, ver: String) -> bool:
	return FileAccess.file_exists(backup_path(dir, ver))


## 读取备份文件。若缺失或格式错误则返回 {},否则返回一个包含
## int "original" 与 int "boosted" 的字典。
static func read_backup(dir: String, ver: String) -> Dictionary:
	var path := backup_path(dir, ver)
	if not FileAccess.file_exists(path):
		return {}
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return {}
	var text := f.get_as_text()
	f.close()
	var parsed = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY:
		return {}
	if not parsed.has("original") or not parsed.has("boosted"):
		return {}
	return {"original": int(parsed["original"]), "boosted": int(parsed["boosted"])}


## 先写者获胜:仅当尚无备份时才写入 `{original, boosted}`。
## 若本次调用写入了文件则返回 true;若备份已存在则返回 false
## (本实例不是所有者 —— 它绝不能覆盖真实原始值,
## 也绝不能把可能已被提升的实时值重新读取为原始值)。调用方
## 必须在它周围持有注册表锁,以保证跨实例原子性。
static func capture_if_absent(dir: String, original: int, boosted: int, ver: String) -> bool:
	if has_backup(dir, ver):
		return false
	var f := FileAccess.open(backup_path(dir, ver), FileAccess.WRITE)
	if f == null:
		push_warning("[MCPUnfocused] cannot write backup %s (err %d)" % [
			backup_path(dir, ver), FileAccess.get_open_error()])
		return false
	f.store_string(JSON.stringify({"original": original, "boosted": boosted}))
	f.close()
	return true


static func delete_backup(dir: String, ver: String) -> void:
	var path := backup_path(dir, ver)
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)


## 纯函数的、能识别冲突的恢复决策。给定实时值 `current` 与
## 存储的备份字典,判断是否把该键回退为原始值:
##   - 备份为空/格式错误 → 不动该键(没有可依据的信息)。
##   - current == boosted     → 提升期间没人改过它 → 恢复原始值。
##   - current != boosted     → 有人/其他工具改过它 → 保留 current。
## 返回 `{"restore": bool, "value": int}`。当 restore 为 true 时,`value` 是
## 要写入的原始值;为 false 时,`value` 原样返回 `current`(无需写入)。
static func resolve_restore(current: int, backup: Dictionary) -> Dictionary:
	if backup.is_empty() or not backup.has("original") or not backup.has("boosted"):
		return {"restore": false, "value": current}
	if current == int(backup["boosted"]):
		return {"restore": true, "value": int(backup["original"])}
	return {"restore": false, "value": current}


## `unfocused_sleep_controller.lower()` 的纯函数闸门:仅当用户已选择启用 且
## 本实例尚未在提升时(幂等)才提升。把退出选项与
## 防重复提升的守卫折叠为一个可测试的谓词。
static func should_capture_boost(enabled: bool, already_active: bool) -> bool:
	return enabled and not already_active
