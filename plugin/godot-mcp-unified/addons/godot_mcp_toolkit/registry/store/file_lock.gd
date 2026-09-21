@tool
extends RefCounted
## 一个感知 PID、可从过期状态恢复的机器级文件锁,
## 用于把对给定路径的跨进程读-改-写(read-modify-write)串行化。
##
## acquire() 会向锁路径写入 "<pid>:<unix_ts>";只要锁仍被持有且新鲜,
## 并发的获取者就会退避(backoff,指数式,50→1000 ms)。当锁的写入者 PID
## 已死亡、或其时间戳超出了过期判定窗口时,该锁会被视为过期 ——
## 并遭到覆盖 —— 因此崩溃的持有者永远不会把锁永久卡死。
## 作为最后手段,锁会被强制写入(force-write),
## 以保证调用方总能继续推进。
##
## 两个方法都是静态的 —— 没有实例状态;锁路径由参数传入。
## 对导出无污染(只使用 FileAccess/OS/Time/DirAccess),因此运行时可达的
## 调用方可以安全地 preload 它,而不会污染自己的静态引用图。

const _LOCK_STALE_SEC := 10
const _LOCK_BASE_RETRY_MS := 50
const _LOCK_MAX_RETRY_MS := 1000
const _LOCK_RETRIES := 10


## 若成功获取锁则返回 true。在检测到过期锁的情况下,
## 过期的文件会被覆盖。指数退避:50、100、200、…… ms,
## 每次重试的上限为 1000 ms。感知 PID 的过期检测
## 可以立即从已死亡的进程中恢复锁。
static func acquire(lock_path: String) -> bool:
	var lp := lock_path
	var delay_ms := _LOCK_BASE_RETRY_MS
	for attempt in _LOCK_RETRIES:
		if FileAccess.file_exists(lp):
			var f := FileAccess.open(lp, FileAccess.READ)
			if f != null:
				var content := f.get_as_text().strip_edges()
				f.close()
				var parts := content.split(":")
				var lock_pid := int(parts[0]) if parts.size() >= 2 else 0
				var lock_ts := int(parts[-1])
				# PID 检查:若持锁者已死亡,立即视为过期。
				var pid_dead := lock_pid > 0 and not OS.is_process_running(lock_pid)
				if not pid_dead:
					var age := int(Time.get_unix_time_from_system()) - lock_ts
					if age < _LOCK_STALE_SEC:
						if attempt > 0:
							push_warning("[MCPRegistry] lock contention (attempt %d/%d, held by PID %d)" % [attempt + 1, _LOCK_RETRIES, lock_pid])
						OS.delay_msec(delay_ms)
						delay_ms = mini(delay_ms * 2, _LOCK_MAX_RETRY_MS)
						continue
				# 过期锁(时间过旧或 PID 已死)—— 落入下方的覆盖逻辑。
		var f := FileAccess.open(lp, FileAccess.WRITE)
		if f == null:
			OS.delay_msec(delay_ms)
			delay_ms = mini(delay_ms * 2, _LOCK_MAX_RETRY_MS)
			continue
		f.store_string("%d:%d" % [OS.get_process_id(), int(Time.get_unix_time_from_system())])
		f.close()
		return true
	push_warning("[MCPRegistry] failed to acquire lock after %d retries; proceeding anyway" % _LOCK_RETRIES)
	# 作为最后手段强制写入锁,让调用方得以继续。
	var f := FileAccess.open(lp, FileAccess.WRITE)
	if f != null:
		f.store_string("%d:%d" % [OS.get_process_id(), int(Time.get_unix_time_from_system())])
		f.close()
	return true


static func release(lock_path: String) -> void:
	var lp := lock_path
	if FileAccess.file_exists(lp):
		DirAccess.remove_absolute(lp)
