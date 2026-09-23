@tool
extends RefCounted
## daemon auto-spawn 边车(issue 07)—— 编辑器侧独立模块,不触碰既有通信面
## 与 C1–C22 契约。
##
## 职责:周期性探活本地 daemon(loopback 端口);缺席则拉起目标平台的
## godot-mcp-daemon 可执行文件;daemon 消亡后在重试窗口内自愈重拉。
## 与 shim 的并发拉起竞态由 daemon 单例锁兜底(重复拉起会以退出码 2/3 自退)。
##
## 开关:ProjectSetting `mcp_toolkit/daemon/autostart`(默认 true)或
## 环境变量 GODOT_MCP_DAEMON_AUTOSTART=0 关闭 —— 关闭/不实例化时,
## addon 其余行为与现状完全一致。
##
## 可执行文件解析顺序(单一出处:paths/server_bin.gd——与 .mcp.json 条目的 shim
## 解析共用,保证"边车拉起的 daemon"与"shim 自举的 daemon"是同一份产物):
##   1. GODOT_MCP_DAEMON_EXE(显式覆盖;审计/测试用;设置即独占,不存在则不再回退);
##   2. 仓库的 server-dotnet/publish/<rid>/(本地开发:addon 链接安装时机器上只有这一份);
##   3. <addon_dir>/bin/<rid>/(随包分发:addon 被整份复制时的随包位置)。
## rid 取 .NET RID 命名(win-x64 / linux-x64 / osx-arm64 / osx-x64 …),
## 由 paths/platform_rid.gd 单一出处提供。
##
## 本文件必须保持"对导出友好"(模块表 modules.gd 位于运行时预加载闭包):
## 只使用 OS / Time / FileAccess / ProjectSettings / StreamPeerTCP / Engine 等
## 核心单例,不引用任何仅编辑器可用的类。

const _ENV_EXE := "GODOT_MCP_DAEMON_EXE"
const _ENV_PORT := "GODOT_MCP_DAEMON_PORT"
const _ENV_AUTOSTART := "GODOT_MCP_DAEMON_AUTOSTART"
const _SETTING_AUTOSTART := "mcp_toolkit/daemon/autostart"

const ServerBin := preload("res://addons/godot_mcp_toolkit/paths/server_bin.gd")

const _DEFAULT_PORT := 6590
const _PROBE_INTERVAL_S := 5.0
const _CONNECT_TIMEOUT_S := 2.0
const _SPAWN_RETRY_INTERVAL_S := 30.0

var _port: int = _DEFAULT_PORT
var _accum: float = 0.0
var _probe: StreamPeerTCP = null
var _probe_elapsed: float = 0.0
var _last_spawn_msec: int = -1_000_000
# 本次 spawn 后是否仍在等待其被探活确认(未确认期间按完整的 30s 重试窗口节流,
# 防止 daemon 慢启动被重复拉起);确认过一次(探活 up)即清除。
var _spawn_pending: bool = false
var _daemon_up_logged: bool = false
var _missing_logged: bool = false


## 启用时返回边车实例,否则返回 null(调用方 tick 即可)。
static func start_if_enabled() -> RefCounted:
	if OS.get_environment(_ENV_AUTOSTART) == "0":
		return null
	var enabled := true
	if ProjectSettings.has_setting(_SETTING_AUTOSTART):
		enabled = bool(ProjectSettings.get_setting(_SETTING_AUTOSTART))
	if not enabled:
		return null
	var sidecar: RefCounted = new()
	var port_env := OS.get_environment(_ENV_PORT)
	if not port_env.is_empty() and port_env.is_valid_int():
		sidecar.set("_port", int(port_env))
	return sidecar


func tick(delta: float) -> void:
	_accum += delta
	_poll_probe(delta)
	if _probe == null and _accum >= _PROBE_INTERVAL_S:
		_accum = 0.0
		_begin_probe()


func _begin_probe() -> void:
	var tcp := StreamPeerTCP.new()
	if tcp.connect_to_host("127.0.0.1", _port) != OK:
		_on_probe_result(false)
		return
	_probe = tcp
	_probe_elapsed = 0.0


func _poll_probe(delta: float) -> void:
	if _probe == null:
		return
	_probe_elapsed += delta
	_probe.poll()
	match _probe.get_status():
		StreamPeerTCP.STATUS_CONNECTED:
			_probe.disconnect_from_host()
			_probe = null
			_on_probe_result(true)
		StreamPeerTCP.STATUS_ERROR:
			_probe = null
			_on_probe_result(false)
		_:
			if _probe_elapsed >= _CONNECT_TIMEOUT_S:
				_probe.disconnect_from_host()
				_probe = null
				_on_probe_result(false)


func _on_probe_result(up: bool) -> void:
	if up:
		_spawn_pending = false
		if not _daemon_up_logged:
			print("[DaemonSidecar] daemon 在 127.0.0.1:%d 就绪" % _port)
			_daemon_up_logged = true
		return
	_daemon_up_logged = false
	_try_spawn()


func _try_spawn() -> void:
	var now := Time.get_ticks_msec()
	var since_last_attempt := now - _last_spawn_msec
	if since_last_attempt < 15_000:
		# 绝对下限:任何情况下都不过密尝试(崩溃回环保护)。
		return
	if since_last_attempt < 30_000 and _spawn_pending:
		# 拉起后尚未被探活确认——按完整重试窗口等待,避免慢启动被重复拉起。
		return
	_last_spawn_msec = now
	_spawn_pending = true
	var exe := _resolve_daemon_executable()
	if exe.is_empty():
		_spawn_pending = false
		if not _missing_logged:
			push_warning(
				"[DaemonSidecar] 未找到 godot-mcp-daemon 可执行文件,已跳过自动拉起;"
				+ "请设置 %s,或把服务发布到 server-dotnet/publish/<rid>/(本地开发)"
				% _ENV_EXE + "／addons/godot_mcp_toolkit/bin/<rid>/(随包分发)。")
			_missing_logged = true
		return
	_missing_logged = false
	var pid := OS.create_process(exe, PackedStringArray())
	if pid <= 0:
		push_warning("[DaemonSidecar] 拉起失败:%s" % exe)
	else:
		print("[DaemonSidecar] 已拉起 daemon(pid %d):%s" % [pid, exe])


func _resolve_daemon_executable() -> String:
	# 落点解析的单一出处(见文件头注释):本地开发指向仓库 publish/<rid>/,
	# 随包分发指向 addon 的 bin/<rid>/。
	return ServerBin.daemon_path()
