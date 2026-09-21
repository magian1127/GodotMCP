@tool
extends RefCounted
## 解析本编辑器的 GDScript LSP 端点,把它发布到注册表,
## 并在设置变更(去抖)时重新发布 —— 另外还持有
## 停靠面板(dock)渲染的服务器上报 LSP 存活镜像。
##
## 编辑器无法读取自身的 LSP 绑定状态,因此同一个“LSP 判定”关注点
## 分为两半。(1) 出站:解析本编辑器设置所指向的端点
## (network/language_server/remote_host/port,默认 127.0.0.1:6005),
## 并把 lsp_host/lsp_port 发布进注册表条目;EditorSettings.settings_changed
## 是全局触发的,因此该监视通过比较重新解析的端点与
## 上次发布的端点来去抖(变更时实时重新发布)。(2) 入站:上下文协议(MCP)
## 服务器经 set_reported_lsp_status 上报权威判定(它能做编辑器做不到的
## 可靠跨进程存活检测);停靠面板(dock)渲染最近一次上报的内容,
## 并只在变化时刷新。
##
## 仅限编辑器侧 —— 且刻意带有“污染”标记:resolve_lsp_endpoint() 与该监视
## 直接引用 EditorInterface(它们读取 network/language_server/* 并连接到
## EditorSettings.settings_changed)。之所以允许,是因为模式 B 的运行时
## 自动加载(Autoload)没有 LSP 发布(它发布 runtime_port,不是 LSP 端点),
## 且绝不能预加载本文件。#91713 因此不作为该子模块的门控,但运行时对它的引用
## 会污染自动加载(Autoload),所以请严格保持仅限编辑器:
## 只有 mcp_server.gd(编辑器服务器)预加载它。编排器拥有
## 跨子系统的触发点(start() → 解析 + 发布 + 连接监视;
## stop() → 断开监视;服务器上报状态 → set_reported_lsp_status);本
## 子模块只拥有每个触发点背后的机制,而 lsp_status_changed 信号
## 留在编排器上(停靠面板(dock)在那里绑定它)—— 本子模块经注入的
## 状态变更处理器报告变化,由编排器重新发出。
##
## resolve_lsp_endpoint() 保持为静态的 EditorInterface 读取器
## (与服务器中原样一致,plugin.gd 与注册表调用方在那里静态调用它):它把
## 解析出的 host/port 值传入 RegistryClient.register / ensure_registered,
## 因此 registry_client.gd 对模式 B 的运行时自动加载(Autoload)保持编辑器干净
## (它自己从不解析端点 —— 见 registry_client.gd::_build_entry)。与同类的
## 受污染子模块(scene_lease / unfocused)不同,这一个不接收
## 注入的 EditorSettings 访问器:注入访问器会迫使静态
## 解析器变成实例方法,平白破坏那条静态调用图而没有污染上的收益
## (该文件绝不会被运行时预加载),因此风险更低的选择是
## 直接引用 EditorInterface。

const RegistryClient := preload("res://addons/godot_mcp_toolkit/registry/registry_client.gd")
const MCPAuth := preload("res://addons/godot_mcp_toolkit/security/auth.gd")

# 最近发布到注册表的 LSP 端点 —— 重新发布的基线。该监视把重新解析的
# 端点与这两个值比较,以对无关的设置变动去抖。
var _last_lsp_host: String = ""
var _last_lsp_port: int = -1
# 上下文协议服务器最近上报的权威 LSP 判定(editor.set_lsp_status)。编辑器
# 无法读取自身的 LSP 绑定状态,因此由服务器告知我们,
# 停靠面板(dock)负责渲染。服务器连接之前为 {}。
var _reported_lsp_status: Dictionary = {}

# 注入的接缝(由 mcp_server._init_lsp_publisher 接线一次)。把
# lsp_status_changed 信号与传输层留在编排器上 —— 本子模块
# 两者都不拥有。
# _on_status_changed: func() -> void —— 当上报的判定变化时,
#   编排器重新发出 lsp_status_changed(停靠面板(dock)在服务器上绑定该信号)。
var _on_status_changed: Callable = Callable()
# _bound_port_provider: func() -> int —— 实时绑定的 WS 端口(传输层拥有它;
#   该监视需要它,以便在服务器开始监听之前/之后跳过重新发布,
#   与抽取前监视读取 _transport.get_bound_port() 的方式完全一致)。
var _bound_port_provider: Callable = Callable()


## 本编辑器设置所指向的 GDScript LSP 端点(默认
## 127.0.0.1:6005)。--lsp-port 覆盖在这里不可见 —— 引擎会在
## OS.get_cmdline_args() 之前消费它,且从不把它写入设置 —— 因此那种情况
## 由服务器上的 GODOT_MCP_LSP_PORT 承载(见 docs/multi-instance.md)。
## 静态 + 仅限编辑器(引用 EditorInterface);注册表调用方把
## 结果传入 register()/ensure_registered(),使 registry_client.gd 对
## 模式 B 的运行时自动加载(Autoload)保持编辑器干净。
static func resolve_lsp_endpoint() -> Dictionary:
	var host := "127.0.0.1"
	var port := 6005
	var es := EditorInterface.get_editor_settings()
	if es != null:
		if es.has_setting("network/language_server/remote_host"):
			host = str(es.get_setting("network/language_server/remote_host"))
		if es.has_setting("network/language_server/remote_port"):
			port = int(es.get_setting("network/language_server/remote_port"))
	return {"host": host, "port": port}


## 接好编排器的 lsp_status_changed 重新发出。在构造时调用一次,
## 先于任何上报状态到达。
func set_status_changed_handler(on_status_changed: Callable) -> void:
	_on_status_changed = on_status_changed


## 接好实时绑定 WS 端口的来源(端口由传输层拥有;本子模块
## 不拥有)。在构造时调用一次,先于 connect_settings_watch / 任何重新发布。
func set_bound_port_provider(bound_port_provider: Callable) -> void:
	_bound_port_provider = bound_port_provider


## 上下文协议服务器在这里上报权威的 LSP 判定 —— 它能做可靠的
## 跨进程存活检测(process.kill)与真正的连接/根校验,
## 而编辑器不能(没有针对自身 LSP 绑定状态的引擎 API)。停靠面板(dock)渲染
## 最近一次上报的内容。键:state("active"/"conflict"/"unavailable")、host、
## port、detail。上下文协议服务器连接之前为空。通知编排器,
## 使其重新发出 lsp_status_changed(信号留在服务器上,供停靠面板(dock)使用)。
func set_reported_lsp_status(status: Dictionary) -> void:
	_reported_lsp_status = status.duplicate()
	if _on_status_changed.is_valid():
		_on_status_changed.call()


func get_reported_lsp_status() -> Dictionary:
	return _reported_lsp_status


## 当编辑器的 GDScript LSP 端口/主机设置在会话中途变化时,
## 重新发布注册表条目,使已发布的端点永不过期。
## EditorSettings.settings_changed 全局触发;我们通过比较重新解析的
## 端点与上次发布的端点来去抖。在 start() 中连接,
## 在 stop() 中断开(I12 对称性)。
func connect_settings_watch() -> void:
	var lsp := resolve_lsp_endpoint()
	_last_lsp_host = lsp["host"]
	_last_lsp_port = lsp["port"]
	var es := EditorInterface.get_editor_settings()
	if es != null and es.has_signal("settings_changed") \
			and not es.settings_changed.is_connected(_on_editor_settings_changed):
		es.settings_changed.connect(_on_editor_settings_changed)


func disconnect_settings_watch() -> void:
	var es := EditorInterface.get_editor_settings()
	if es != null and es.has_signal("settings_changed") \
			and es.settings_changed.is_connected(_on_editor_settings_changed):
		es.settings_changed.disconnect(_on_editor_settings_changed)


func _on_editor_settings_changed() -> void:
	var bound_port: int = _bound_port_provider.call() if _bound_port_provider.is_valid() else -1
	if bound_port <= 0:
		return
	var lsp := resolve_lsp_endpoint()
	if lsp["host"] == _last_lsp_host and lsp["port"] == _last_lsp_port:
		return  # LSP 端点未变 —— 忽略无关的编辑器设置变动。
	_last_lsp_host = lsp["host"]
	_last_lsp_port = lsp["port"]
	RegistryClient.ensure_registered(bound_port, MCPAuth.get_published_token_path(), lsp["host"], lsp["port"])
	print("[MCPServer] LSP endpoint changed -> re-published %s:%d" % [lsp["host"], lsp["port"]])
