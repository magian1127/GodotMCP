@tool
extends RefCounted
## 插件的组合根 —— 构建并连接协作者对象图,
## 然后按相反顺序拆除。
##
## compose() 构建完整的对象图(注册表、服务器、调试桥接、
## 命令注册器、扩展、导出插件、日志缓冲、用户路径监视器、
## 注册表注册、试玩测试观察器、写入流程 + 对话框呈现器、停靠面板(dock))
## 并返回持有所有权的 Handle。
## 编排器(plugin.gd)在 _enter_tree 中调用一次 compose(),持有
## Handle,驱动它(每个 _process 调用 poll_playtest),并在 _exit_tree 中调用一次
## Handle.dispose()。这里只负责接线,不负责生命周期阶段:
## 阶段顺序、版本警告与新手引导仍留在编排器中。
## 注册表注册策略(初次注册 + 带抖动的复核,
## 以及令牌轮换时保留运行时的重新发布)
## 作为两个私有步骤折叠进来 —— 这属于构造期接线。
##
## 仅限编辑器:引用了 EditorInterface / EditorPlugin 宿主,因此只被仅限编辑器的
## plugin.gd 预加载,运行时自动加载(Autoload)永远不会触及它。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const RegistryClient = Modules.RegistryClient
const MCPServer := preload("res://addons/godot_mcp_toolkit/transport/mcp_server.gd")
const DockHost := preload("res://addons/godot_mcp_toolkit/core/dock_host.gd")
const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
const ToolkitDialogPresenter := preload("res://addons/godot_mcp_toolkit/ui/toolkit_dialog_presenter.gd")
const MCPAuth := preload("res://addons/godot_mcp_toolkit/security/auth.gd")
const ExtensionLoader := preload("res://addons/godot_mcp_toolkit/extensions/extension_loader.gd")
const BuiltinCommandRegistration := preload("res://addons/godot_mcp_toolkit/transport/builtin_command_registration.gd")
const PlaytestEndDetector := preload("res://addons/godot_mcp_toolkit/core/playtest_end_detector.gd")
const DebugBridge := preload("res://addons/godot_mcp_toolkit/transport/debug_bridge.gd")
# PlaytestCommands.register(..., debug_bridge) 的拆除对应物:dispose()
# 调用 PlaytestCommands.clear_debug_bridge(),作为该注册的 I12 逆操作。
const PlaytestCommands := preload("res://addons/godot_mcp_toolkit/commands/playtest/playtest_commands.gd")


## 组合插件的协作者对象图并返回持有所有权的 Handle。
## on_user_path_changed 是编排器自己的处理器,用于用户路径
## 监视器的信号 —— 注入进来是为了让处理器留在编排器上,
## 而不必让组合器去戳插件的私有成员。
static func compose(plugin: EditorPlugin, on_user_path_changed: Callable) -> Handle:
	var handle := Handle.new()
	handle._plugin = plugin

	var registry := MCPToolkitCommandRegistry.new()
	var server := MCPServer.new()
	server.name = "MCPServer"
	server.set_registry(registry)
	server.editor_plugin = plugin

	# 调试器桥接 —— 尽早创建,使命令注册器可以引用它。
	var debug_bridge := DebugBridge.new()
	plugin.add_debugger_plugin(debug_bridge)

	BuiltinCommandRegistration.register_all(registry, server, debug_bridge)

	# 第三方扩展 —— 总是加载。
	ExtensionLoader.load_all(registry, server)
	# 实时热重载:监视 EditorFileSystem 中扩展的新增/移除。
	var extension_watcher := ExtensionLoader.start_watcher(registry, server)

	var export_plugin := preload("res://addons/godot_mcp_toolkit/core/export_strip.gd").new()
	plugin.add_export_plugin(export_plugin)

	Modules.LogBuffer.setup()

	# 监视会改变 user:// 路径的 ProjectSettings(config/name、
	# use_custom_user_dir、custom_user_dir_name)。每个使用方
	# 自行连接并处理各自的恢复逻辑。
	var user_path_monitor = Modules.UserPathMonitor.new()
	user_path_monitor.start()
	user_path_monitor.user_path_changed.connect(on_user_path_changed)

	# 边沿检测“运行→停止”切换;每个 _process 轮询一次。
	var playtest_end_detector := PlaytestEndDetector.new(server)

	plugin.add_child(server)
	server.bind_user_path_monitor(user_path_monitor)
	# 用户路径变更后,服务器会重写令牌并通告新路径;我们以保留运行时的方式
	# 重新发布注册表条目(用 ensure_registered,
	# 而非 register),让跨越重命名的运行中游戏保持模式 B 的发现能力。
	server.token_rewritten.connect(func(token_path: String): _republish_on_token_rewrite(server, token_path))
	# 在每一次全新绑定上都向系统级项目注册表注册,让 TS
	# 桥接能按项目路径发现我们。采用信号驱动,而不是 start() 之后的
	# 一次性调用:固定端口可能在启动时被占用,稍后才能绑定
	# (一旦持有者释放),而那时仍必须把实际绑定的端口
	# 发布出去 —— 服务器的失步交叉检查把它当作事实依据读取。
	# 在 start() 之前连接,使正常的立即绑定(start() 内部、
	# 同步执行)恰好注册一次,与旧的一次性方式相同。
	server.port_bound.connect(func(_port: int) -> void: _register_in_registry(server))
	server.start()

	# -- 横切的 UI 协作者,在停靠面板(dock)之前构建 -------------------
	# 共享的 .mcp.json 先确认后写入流程与编辑器全局的对话框
	# 呈现器被同样注入停靠面板(dock)、工具菜单与新手引导
	# 向导:停靠面板(dock)像其他界面一样消费它们 —— 它是一个 UI
	# 界面,而不是服务定位器。
	var write_flow := MCPJsonWriteFlow.new()
	var dialog_presenter := ToolkitDialogPresenter.new()
	handle._write_flow = write_flow
	handle._dialog_presenter = dialog_presenter

	# -- 工具集停靠面板(dock)(≤4.5 上为底部面板,4.6+ 上为 EditorDock;接缝由 DockHost 掌管) --
	var dock: Control = preload("res://addons/godot_mcp_toolkit/ui/dock/dock.tscn").instantiate()
	dock.bind(server, Modules.Audit.get_log_path(), write_flow, dialog_presenter)
	handle._dock = dock
	handle._dock_host = DockHost.add(plugin, dock, "Godot MCP Unified")

	handle._server = server
	handle._export_plugin = export_plugin
	handle._extension_watcher = extension_watcher
	handle._debug_bridge = debug_bridge
	handle._user_path_monitor = user_path_monitor
	handle._playtest_end_detector = playtest_end_detector
	return handle


# 把本编辑器注册进系统级项目注册表。在每次全新绑定时运行
# (服务器的 port_bound 信号 —— 正常情况下启动时一次;若启动冲突后
# 固定端口迟迟才释放,则再次运行):先 register(),再进行一次带抖动的
# 延迟 ensure_registered() 复核(并发的其他编辑器可能在我们
# 的初次校验通过之后覆盖我们的条目)。两次解析都在触发时
# 重新读取 LSP 端点,这样窗口期内 LSP 设置的变更不会被回退。端口守卫使
# 没有绑定端口的杂散调用成为空操作。
static func _register_in_registry(server: Node) -> void:
	var bound_port: int = server.get_bound_port()
	if bound_port > 0:
		var lsp := MCPServer.resolve_lsp_endpoint()
		RegistryClient.register(bound_port, MCPAuth.get_published_token_path(), lsp["host"], lsp["port"])
		# 延迟复核:并发的其他编辑器可能在我们
		# 的初次校验通过之后覆盖我们的条目。带抖动的延迟确保所有编辑器
		# 都已完成各自的初次注册,我们才重新检查。
		var _jitter := randf_range(5.0, 10.0)
		server.get_tree().create_timer(_jitter).timeout.connect(
			func():
				var lsp_re := MCPServer.resolve_lsp_endpoint()
				RegistryClient.ensure_registered(bound_port, MCPAuth.get_published_token_path(), lsp_re["host"], lsp_re["port"]))


# 在服务器把令牌重写到新的 user:// 路径之后,重新发布本编辑器的
# 注册表条目。使用 ensure_registered(而非 register),以保留
# 活动试玩测试的 runtime_port/runtime_pid —— 否则模式 B(游戏运行中)
# 的发现在本次会话余下时间都会失效。在触发时重新解析 LSP 端点,
# 与启动时的注册保持一致,这样并发的端点变更不会被
# 回退到过期值。
static func _republish_on_token_rewrite(server: Node, token_path: String) -> void:
	if server == null:
		return
	var bound_port: int = server.get_bound_port()
	if bound_port <= 0:
		return
	var lsp := MCPServer.resolve_lsp_endpoint()
	# 该信号以 user:// 形式携带重写后的路径;以引擎外服务器打开的
	# 绝对形式发布(参见 MCPAuth.get_published_token_path)。
	RegistryClient.ensure_registered(
		bound_port, ProjectSettings.globalize_path(token_path), lsp["host"], lsp["port"]
	)


## 持有 compose() 构建的协作者对象图,并按相反顺序拆除它。
##
## 编排器在组合完成后仍需要的协作者,通过 server()(菜单、
## 观察器接线)、dock()(菜单)、write_flow() + dialog_presenter()
## (菜单、新手引导向导)与 dock_host()(显示外观)访问;其余五个
## 实例保持私有,只由 dispose() 触碰。poll_playtest()
## 在每个 _process 中驱动试玩测试观察器;dispose() 以与 compose() 完全
## 相反的顺序拆除对象图(顺序对行为至关重要)。
class Handle:
	extends RefCounted

	const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
	const RegistryClient = Modules.RegistryClient
	const DockHost := preload("res://addons/godot_mcp_toolkit/core/dock_host.gd")
	const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
	const ToolkitDialogPresenter := preload("res://addons/godot_mcp_toolkit/ui/toolkit_dialog_presenter.gd")
	const PlaytestCommands := preload("res://addons/godot_mcp_toolkit/commands/playtest/playtest_commands.gd")
	const PlaytestEndDetector := preload("res://addons/godot_mcp_toolkit/core/playtest_end_detector.gd")

	var _plugin: EditorPlugin = null
	var _server: Node = null
	var _export_plugin: EditorExportPlugin = null
	var _dock: Control = null
	var _dock_host: Object = null  # 4.6+ 上为 EditorDock 包装器;≤4.5 上为 null(控件自身就是句柄)
	var _write_flow: MCPJsonWriteFlow = null  # 共享的 .mcp.json 先确认后写入流程
	var _dialog_presenter: ToolkitDialogPresenter = null  # 编辑器全局对话框(信息 + 目录)
	var _extension_watcher: RefCounted = null  # 实时热重载观察器(ExtensionLoader)
	var _debug_bridge: RefCounted = null  # 用于 debug.* 命令的 EditorDebuggerPlugin
	var _user_path_monitor = null  # UserPathMonitor —— 检测 config/name 变更
	var _playtest_end_detector: PlaytestEndDetector = null  # 边沿检测“运行→停止”切换

	## 上下文协议(MCP)服务器节点(编排器把它传给工具菜单)。
	func server() -> Node:
		return _server

	## 工具集停靠面板(dock)控件(编排器把它传给工具菜单,
	## 其审计动作仍由停靠面板(dock)持有)。
	func dock() -> Control:
		return _dock

	## 共享的 .mcp.json 先确认后写入流程(编排器把它传给
	## 工具菜单 + 新手引导向导)。
	func write_flow() -> MCPJsonWriteFlow:
		return _write_flow

	## 编辑器全局对话框呈现器(编排器把它传给工具菜单
	## + 新手引导向导)。
	func dialog_presenter() -> ToolkitDialogPresenter:
		return _dialog_presenter

	## 停靠面板(dock)宿主 —— 4.6+ 上为 EditorDock 包装器,≤4.5 上为 null。
	## 显示外观(plugin.reveal_dock)把它回传给 DockHost.reveal。
	func dock_host() -> Object:
		return _dock_host

	## 边沿检测试玩测试(playtest)结束。由编排器的 _process 调用。
	func poll_playtest() -> void:
		if _playtest_end_detector != null:
			_playtest_end_detector.poll()

	## 以构造的相反顺序拆除组合好的对象图。顺序对行为至关重要 ——
	## 停靠面板(dock) → 对话框呈现器 + 写入流程 → 用户路径监视器 →
	## 试玩测试观察器 → 扩展观察器 → 调试桥接 → 导出插件 →
	## 服务器+注册表。
	func dispose() -> void:
		# 停靠面板(dock)拆除。立即释放 CONTROL(绝不用 queue_free),
		# 让其 GDScript 预加载链在 ObjectDB 退出时的泄漏检查之前释放。
		# 在 ≤4.5 上控件就是整个句柄。在 4.6+ 上它是
		# EditorDock 包装器的子节点:先释放控件(它会自行脱离),
		# 再 queue_free 包装器 —— 不要用 free()。remove_dock() 会注销包装器,
		# 但会把它留在停靠面板管理器的 dirty_docks 集合中(remove_dock 只擦除
		# all_docks),而仍处于排队中的延迟 _update_dirty_dock_tabs
		# 会不加防护地解引用其中每个条目 —— 因此立即 free() 会造成
		# 释放后使用,在显示基础控件弹窗的同一帧内损坏编辑器
		# (禁用清理提示将永远无法渲染)。出于同样的原因,引擎自己的
		# EditorDock 拆除逻辑会对包装器调用 queue_free。包装器
		# 不持有任何 GDScript,因此对它延迟执行不会重新引入
		# 控件立即 free() 所规避的泄漏检查风险。
		if _dock != null:
			DockHost.remove(_plugin, _dock, _dock_host)
			if _dock_host != null:
				_dock.free()
				_dock_host.call("queue_free")
			else:
				_dock.free()
			_dock = null
			_dock_host = null

		# 编辑器全局对话框(信息 + 扩展目录)—— 呈现器会立即释放它们,
		# 理由与上面停靠面板(dock)控件被 free() 相同(泄漏检查):
		# 它们是基础控件的子节点,没有其他东西会释放它们。
		if _dialog_presenter != null:
			_dialog_presenter.dispose()
			_dialog_presenter = null

		# 写入流程 —— 无拥有节点的 RefCounted;直接丢弃引用。
		_write_flow = null

		# RefCounted 子系统 —— 丢弃我们的引用,使它们能在上面的
		# 停靠面板(dock)(它也持有它们)被释放后得到回收。
		if _user_path_monitor != null:
			_user_path_monitor.stop()
			_user_path_monitor = null

		# 试玩测试结束检测器(RefCounted —— 置空即可)—— 在拆除服务器之前丢弃,
		# 因为它持有服务器引用。
		_playtest_end_detector = null

		# 扩展观察器 —— 先断开全局信号,再在服务器拆除之前丢弃
		# (它持有注册表引用)。若不显式断开,
		# filesystem_changed / settings_changed 处理器会变成僵尸回调。
		if _extension_watcher != null:
			var efs := EditorInterface.get_resource_filesystem()
			if efs.filesystem_changed.is_connected(_extension_watcher.on_filesystem_changed):
				efs.filesystem_changed.disconnect(_extension_watcher.on_filesystem_changed)
			if ProjectSettings.settings_changed.is_connected(_extension_watcher.on_settings_changed):
				ProjectSettings.settings_changed.disconnect(_extension_watcher.on_settings_changed)
		_extension_watcher = null

		# 调试器桥接 —— 在服务器拆除之前注销(I12 对称性)。
		PlaytestCommands.clear_debug_bridge()
		if _debug_bridge != null:
			_debug_bridge.cleanup()
			_plugin.remove_debugger_plugin(_debug_bridge)
			_debug_bridge = null

		# 导出插件(RefCounted —— 不要 queue_free,置空即可)。
		if _export_plugin != null:
			_plugin.remove_export_plugin(_export_plugin)
			_export_plugin = null

		# 服务器 + 注册表 —— 先清空命令注册表以打断
		# Callable → GDScript 引用链,然后立即 free()。
		# 只用 queue_free() 会导致“退出时资源仍被占用”,
		# 因为延迟删除运行在 ObjectDB 泄漏检查之后。
		if _server != null:
			_server.stop()
			_server.clear_registry()
			RegistryClient.deregister()
			_server.free()
			_server = null
