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
## 可执行文件解析顺序(对 junction 安装与复制安装两种形态都成立;
## addon_dir 经 globalize_path 解析,联接(junction)对 res:// 透明):
##   1. GODOT_MCP_DAEMON_EXE(显式覆盖;审计/测试用);
##   2. <addon_dir>/bin/<rid>/godot-mcp-daemon[.exe](复制安装的发布位置);
##   3. <addon_dir>/../../server-dotnet/publish/<rid>/godot-mcp-daemon[.exe]
##      (仓库 junction 安装的开发布局)。
## rid 取 .NET RID 命名(win-x64 / linux-x64 / osx-arm64 / osx-x64 …)。
##
## 本文件必须保持"对导出友好"(模块表 modules.gd 位于运行时预加载闭包):
## 只使用 OS / Time / FileAccess / ProjectSettings / StreamPeerTCP / Engine 等
## 核心单例,不引用任何仅编辑器可用的类。

const _ENV_EXE := "GODOT_MCP_DAEMON_EXE"
const _ENV_PORT := "GODOT_MCP_DAEMON_PORT"
const _ENV_AUTOSTART := "GODOT_MCP_DAEMON_AUTOSTART"
const _SETTING_AUTOSTART := "mcp_toolkit/daemon/autostart"

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
				+ "请设置 %s 或放置发布产物(addons/godot_mcp_toolkit/bin/<rid>/)。" % _ENV_EXE)
			_missing_logged = true
		return
	_missing_logged = false
	var pid := OS.create_process(exe, PackedStringArray())
	if pid <= 0:
		push_warning("[DaemonSidecar] 拉起失败:%s" % exe)
	else:
		print("[DaemonSidecar] 已拉起 daemon(pid %d):%s" % [pid, exe])


func _resolve_daemon_executable() -> String:
	var env_exe := OS.get_environment(_ENV_EXE)
	if not env_exe.is_empty():
		return env_exe if FileAccess.file_exists(env_exe) else ""
	var rid := _rid()
	var exe_name := "godot-mcp-daemon.exe" if OS.get_name() == "Windows" else "godot-mcp-daemon"
	# 本脚本位于 <addon_root>/daemon/ 下。发布产物落到 addon 本地 bin/<rid>/:
	# 对 junction 安装(联接目标是仓库 addon 目录)与复制安装(addon 被整个复制)
	# 都经 res:// 透明命中 —— 联接下的 ".." 是词法折叠,不能依赖 addon 之外的相对路径。
	var script_dir := ProjectSettings.globalize_path(get_script().resource_path.get_base_dir())
	var addon_root := script_dir.path_join("..")
	var candidate := addon_root.path_join("bin").path_join(rid).path_join(exe_name)
	return candidate if FileAccess.file_exists(candidate) else ""


static func _rid() -> String:
	var arch := Engine.get_architecture_name()
	match OS.get_name():
		"Windows":
			return "win-x64" if arch == "x86_64" else "win-arm64"
		"macOS":
			return "osx-arm64" if arch == "arm64" else "osx-x64"
		_:
			return "linux-x64" if arch == "x86_64" else "linux-arm64"
