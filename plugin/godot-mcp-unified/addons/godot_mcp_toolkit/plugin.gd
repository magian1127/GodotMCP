@tool
extends EditorPlugin
## EditorPlugin 入口 —— 委托给 PluginComposer 的轻量编排器。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const FileGuard = Modules.FileGuard
const SettingsRegistration := preload("res://addons/godot_mcp_toolkit/core/settings_registration.gd")
const OnboardingWizard := preload("res://addons/godot_mcp_toolkit/ui/onboarding_wizard.gd")
const PluginComposer := preload("res://addons/godot_mcp_toolkit/core/plugin_composer.gd")
const DockHost := preload("res://addons/godot_mcp_toolkit/core/dock_host.gd")
const ToolMenu := preload("res://addons/godot_mcp_toolkit/core/tool_menu.gd")
const DisableCleanupCoordinator := preload("res://addons/godot_mcp_toolkit/core/disable_cleanup_coordinator.gd")
const MCPJsonEnablePrompt := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_enable_prompt.gd")
const AutoloadRegistration := preload("res://addons/godot_mcp_toolkit/core/autoload_registration.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")

# 组合好的协作者对象图(服务器、停靠面板(dock)、导出插件、各观察器、调试
# 桥接、用户路径监视器、试玩测试观察器)。由 PluginComposer.compose() 构建;
# 编排器驱动它,并在退出时调用 _handle.dispose()。
var _handle = null

var _tool_menu: ToolMenu = null
var _wizard: OnboardingWizard = null
# daemon auto-spawn 边车(issue 07):独立模块,不触碰既有通信面与 C1–C22 契约;
# 设置 mcp_toolkit/daemon/autostart 或环境变量 GODOT_MCP_DAEMON_AUTOSTART=0 可关闭。
var _daemon_sidecar = null


func _enter_tree() -> void:
	# 品牌行,而非日志行:最先打印,即使启动失败也已盖上版本戳。
	print("Godot MCP Unified v%s — local integrated build" % get_plugin_version())

	# 生命周期阶段序列。“为什么是这个顺序”的说明在这里;组合器
	# 拥有它所构建对象图的内部构造顺序。
	Modules.EditorAccess.set_plugin(self)
	SettingsRegistration.register_all()

	# 在对象图接线之前重新断言“插件已启用 ⟹ 运行时自动加载(Autoload)已注册”。
	# _enable_plugin() 只在禁用→启用的切换时触发,因此一个打开时已是启用状态
	# 但自动加载(Autoload)缺失的项目(带外编辑的 project.godot、经版本控制传播的
	# 启用标志、模板)会静默地只以模式 A 运行。放在
	# 靠前位置 —— compose() 之前 —— 使自愈的 settings_changed 发出时
	# 监听者为零(扩展观察器与其他 settings_changed 使用方
	# 在下文的 compose() 内部接线)。
	AutoloadRegistration.ensure_registered()

	# 构建 + 接线完整的协作者对象图(注册表、服务器、调试
	# 桥接、命令注册器、扩展、导出插件、日志缓冲、用户路径
	# 监视器、注册表注册、试玩测试观察器、写入流程 + 对话框
	# 呈现器、停靠面板(dock)),并在系统级注册表中注册。
	_handle = PluginComposer.compose(self, _on_user_path_changed)

	# daemon 边车:对象图就绪后启动(探活/拉起只依赖环境与核心单例,
	# 与对象图无耦合;失败静默重试,绝不影响编辑器主流程)。
	_daemon_sidecar = Modules.DaemonSidecar.start_if_enabled()

	# “工具 > Godot MCP Unified”子菜单 + 命令面板(需要组合器刚构建的
	# 服务器、停靠面板(dock)与共享 UI 协作者)。
	_tool_menu = ToolMenu.new(
			self, _handle.server(), _handle.dock(),
			_handle.write_flow(), _handle.dialog_presenter())
	_tool_menu.install()

	# -- 每用户的 EditorSettings --
	_register_editor_settings()

	# 就未经测试的未来 Godot 版本发出警告(但不阻塞)。
	var _engine_ver := Modules.VersionUtils.get_engine_version_pair()
	if not Modules.VersionUtils.is_at_most(_engine_ver, Modules.VersionUtils.GODOT_TESTED_MAX_VERSION):
		push_warning(("[MCP] Godot %s detected - latest tested version is %s. "
			+ "The plugin will run normally but some features may behave unexpectedly. "
			+ "See the bundled local compatibility guide under addons/godot_mcp_toolkit/docs.")
			% [_engine_ver, Modules.VersionUtils.GODOT_TESTED_MAX_VERSION])

	_wizard = OnboardingWizard.new(
			self, _handle.server(), _handle.write_flow(), _handle.dialog_presenter())
	call_deferred("_check_onboarding")


func _check_onboarding() -> void:
	_wizard.check_and_show()


# 显示工具集停靠面板(dock)(选中其标签页 + 展开底部面板)。新手引导向导的
# 最后一步会调用这里;通过 DockHost 委托,把编辑器↔版本相关的
# 停靠面板(dock)接缝留在一个适配器中,而不是在这里泄漏 make_visible。
func reveal_dock() -> void:
	if _handle != null:
		DockHost.reveal(self, _handle.dock(), _handle.dock_host())


func _process(_delta: float) -> void:
	if _handle != null:
		_handle.poll_playtest()
	if _daemon_sidecar != null:
		_daemon_sidecar.tick(_delta)


func _on_user_path_changed() -> void:
	# 无法自行连接信号的静态使用方。
	# 实例使用方(feature_settings、server)直接
	# 经由 bind_user_path_monitor() 连接。
	OnboardingWizard.migrate_flag_after_rename()
	Modules.LogBuffer.reset_tail_path()


func _exit_tree() -> void:
	# 拆除对称性 —— _enter_tree 各阶段的相反顺序。
	# 边车先落引用(探活句柄随实例释放;daemon 本身归其空闲超时自管,绝不在这里杀)。
	_daemon_sidecar = null

	# 新手引导向导(如果仍然打开)—— teardown() 立即释放对话框,
	# 使它不会存活到 ObjectDB 退出时的泄漏检查之后。
	if _wizard != null:
		_wizard.teardown()
		_wizard = null

	# 菜单 + 命令面板。
	if _tool_menu != null:
		_tool_menu.uninstall()
		_tool_menu = null

	# 组合对象图(停靠面板(dock)、监视器、各观察器、调试桥接、导出插件、
	# 服务器 + 注册表)—— 以相反的构造顺序拆除。
	if _handle != null:
		_handle.dispose()
		_handle = null

	# 插件引用 —— 最后清除(与 _enter_tree 的拆除对称性)。
	Modules.EditorAccess.clear_plugin()


func _enable_plugin() -> void:
	# 首次启用的注册与加载时自愈共用一条路径,使二者永远不会分歧 ——
	# 无撤销的 set_setting + save,持久化到 project.godot,这正是
	# 游戏在 F5 时读取的内容(理由见 AutoloadRegistration)。
	AutoloadRegistration.ensure_registered()

	# 提供创建缺失的 .mcp.json 的选项 —— 仅在启用切换时,绝不在
	# 打开项目时。编辑器在调用 _enable_plugin 之前就把插件加入树中,
	# 因此组合对象图(及其写入流程)已经存在。
	if _handle != null:
		MCPJsonEnablePrompt.show_if_needed(_handle.write_flow())


func _disable_plugin() -> void:
	AutoloadRegistration.unregister(self)

	# 无条件擦除项目本地的 mcp_toolkit/* ProjectSettings ——
	# 它们属于 project.godot,插件移除后便毫无意义。
	# (EditorSettings 是机器级的,需要确认 —— 见下文。)
	SettingsRegistration.unregister_all()

	# 就孤立的 .mcp.json 发出警告,然后(链接在其结果之后)就机器级
	# EditorSettings 键发出警告。本方法返回(且 _exit_tree 运行)的瞬间,
	# 编辑器就会释放本插件 —— 早于用户回答
	# 提示 —— 因此该序列绝不能由绑定到插件的回调驱动
	# (它们会触发到已释放的对象上并静默空转)。把它交给一个
	# 比插件活得更久、拥有对话框流程的解耦协调器。
	DisableCleanupCoordinator.new().start()


# -- EditorSettings 注册(每用户,不提交到版本控制) -------------


func _register_editor_settings() -> void:
	var es := EditorInterface.get_editor_settings()
	# [type, default, hint_string]。这些键位于编辑器设置(每用户、
	# 机器级)中,而非项目设置:失焦响应键控制的是
	# 机器全局的编辑器效果,属于个人的电池/CPU 偏好,
	# 因此绝不能提交到 project.godot / 版本控制。
	var settings := {
		"mcp_toolkit/personal/dock_default_visible": [TYPE_BOOL, true, ""],
		"mcp_toolkit/performance/keep_editor_responsive_unfocused": [TYPE_BOOL, true,
			EditorLocale.pick(
				"Keep the editor responsive (raise its unfocused frame rate) while an MCP client is connected, so commands stay snappy when the editor is unfocused. Off uses Godot's default low-power unfocused throttle. Raises background CPU. A toggle is also in the Godot MCP Unified dock.",
				"连接 MCP 客户端期间保持编辑器响应（提高失去焦点时的帧率），使编辑器在后台时也能及时执行命令。关闭后使用 Godot 默认的低功耗限速。启用会增加后台 CPU 占用；Godot MCP Unified 工具坞中也有此开关。")],
		"mcp_toolkit/performance/unfocused_responsive_sleep_usec": [TYPE_INT, 16666,
			EditorLocale.pick(
				"Unfocused process sleep in µs applied while a client is connected (lower = higher fps = snappier but more CPU). 16666 ≈ 60 fps (default); 33333 ≈ 30 fps (power-saver). Not clamped.",
				"连接客户端时，编辑器失去焦点后的进程休眠时间，单位微秒。数值越低，帧率和响应速度越高，但 CPU 占用也越高。16666 ≈ 60 fps（默认）；33333 ≈ 30 fps（省电）。不做范围限制。")],
	}
	for key in settings:
		if not es.has_setting(key):
			es.set_setting(key, settings[key][1])
		es.set_initial_value(key, settings[key][1], false)
		var info := {"name": key, "type": settings[key][0]}
		if settings[key][2] != "":
			info["hint"] = PROPERTY_HINT_NONE
			info["hint_string"] = settings[key][2]
		es.add_property_info(info)
