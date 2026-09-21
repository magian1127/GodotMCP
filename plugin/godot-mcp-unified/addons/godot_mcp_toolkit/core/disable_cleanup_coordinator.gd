@tool
extends RefCounted
## 以与插件实例解耦的方式运行禁用时的清理对话框序列。
##
## 当用户禁用插件时,编辑器会在 [code]_disable_plugin()[/code] 返回、
## [code]_exit_tree()[/code] 运行的瞬间就释放 [EditorPlugin] —— 这发生在用户
## 回答清理提示之前。因此绑定到插件的回调会触发到已释放的对象上,
## 并静默空转(提示框毫无作用)。所以清理序列改放在这里:
## 在这里创建一个实例,并从 [code]_disable_plugin()[/code] 中调用
## [method start],这个实例自行活得比插件更久,不再依赖插件。
## 它的对话框回调引用的是本协调器(仍然存活),
## 绝不会引用已释放的插件。

const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")
##
## 生命周期:协调器保存一个指向自身的引用([member _self_ref]),
## 只有序列终止时才清除它,因此无论对话框如何设置父节点,
## 它都能挺过插件的释放以及用户的异步对话框交互。(双保险:绑定到
## RefCounted 的自引用 lambda 也会持有对它的强引用 —— 参见
## GDScriptLambdaSelfCallable —— 但显式的自引用让存活性一目了然,
## 且由协调器自己掌控。)

# 本插件注册的机器级 EditorSettings 键(与 plugin.gd::_register_editor_settings
# 中的键互为镜像)。这些是按用户、机器范围的偏好设置,
# 由所有使用本工具集的项目共享,因此 —— 与项目本地的 ProjectSettings 不同 ——
# 它们绝不会被静默清除:删除它们会影响用户的其他项目,
# 这正是删除前需要确认的原因。
const _EDITOR_SETTING_KEYS := [
	"mcp_toolkit/personal/dock_default_visible",
	"mcp_toolkit/performance/keep_editor_responsive_unfocused",
	"mcp_toolkit/performance/unfocused_responsive_sleep_usec",
]

# 让本 RefCounted 在异步对话框流程期间保持存活的自引用,
# 独立于插件(插件在用户点击之前就被释放)。在
# _done() 中清除,届时协调器即被回收。
var _self_ref: RefCounted = null

# 孤立的 .mcp.json 的绝对路径,提前捕获下来,
# 这样删除回调无需从插件获取任何东西。
var _mcp_json_path: String = ""


## 开始清理序列。先就孤立的 [code].mcp.json[/code] 进行提示(如果存在),
## 然后把机器级 EditorSettings 清理提示链接到该提示的结果上
## (无论确认还是取消);当没有 [code].mcp.json[/code] 可提示时,
## 直接显示 EditorSettings 提示。两个提示按先后顺序显示 ——
## 绝不会同时弹出两个。
func start() -> void:
	_self_ref = self
	_mcp_json_path = ProjectSettings.globalize_path("res://") + ".mcp.json"
	if FileAccess.file_exists(_mcp_json_path):
		_prompt_mcp_json_orphan()
	else:
		_prompt_editor_settings_cleanup()


# 就孤立的 .mcp.json 发出警告并提供删除选项。无论哪种结果
# 都会接续到 EditorSettings 提示。
func _prompt_mcp_json_orphan() -> void:
	var dialog := ConfirmationDialog.new()
	dialog.exclusive = false
	dialog.title = EditorLocale.pick("MCP Plugin Disabled", "MCP 插件已禁用")
	dialog.dialog_text = EditorLocale.pick(
		"The .mcp.json configuration file is still at your project root:\n"
			+ _mcp_json_path + "\n\n"
			+ "If you're uninstalling the plugin, you may want to remove it.\n"
			+ "If you're just disabling temporarily, keep it.",
		"项目根目录中仍保留 .mcp.json 配置文件：\n"
			+ _mcp_json_path + "\n\n"
			+ "如果正在卸载插件，可以将它删除。\n"
			+ "如果只是暂时禁用插件，请保留它。")
	dialog.ok_button_text = EditorLocale.pick("Delete .mcp.json", "删除 .mcp.json")
	dialog.cancel_button_text = EditorLocale.pick("Keep", "保留")
	dialog.confirmed.connect(func() -> void:
		DirAccess.remove_absolute(_mcp_json_path)
		print("[MCP] Deleted .mcp.json at %s" % _mcp_json_path)
		_advance_to_editor_settings_prompt.call_deferred(dialog)
	)
	dialog.canceled.connect(func() -> void:
		print("[MCP] .mcp.json kept at %s" % _mcp_json_path)
		_advance_to_editor_settings_prompt.call_deferred(dialog)
	)
	EditorInterface.get_base_control().add_child(dialog)
	dialog.popup_centered()


# 两个提示之间的链式接续 —— 采用延迟的“先关闭后显示”,绝不
# 从第一个提示的信号回调中内联执行。这些提示是非独占的
# (在切换插件期间仍然打开的项目设置窗口占据着编辑器根节点
# 唯一的独占子节点槽位),而非独占对话框没有操作系统级的所有者 ——
# 没有任何结构性机制保证它位于编辑器窗口之上,因此其 z 顺序
# 完全由激活顺序决定。内联显示提示 2 时,提示 1 由引擎延迟执行的
# 隐藏仍然悬而未决;当该隐藏销毁了提示 1 的原生窗口后,
# 操作系统把编辑器重新激活到刚刚显示的提示 2 之上。从 Godot 4.6 起,
# 这次重新激活是同步处理的(4.6 之前它是定时器延迟的,并在生效前
# 就被取代),这正是被遮住的提示只在 4.6+ 上出现的原因。
# 把该接续延迟执行,会把它排在引擎排队中的隐藏之后:
# 在提示 2 弹出之前,提示 1 已完全关闭、编辑器的重新激活
# 也已尘埃落定 —— 在所有受支持版本上提示 2 都位于前台并获得焦点
# (在 <=4.5 上唯一的差别是提示 2 晚一个消息队列刷新才出现)。
# 这里调用 free() 是安全的:延迟运行意味着我们已处于
# 提示 1 的信号发射之外。
func _advance_to_editor_settings_prompt(finished_dialog: ConfirmationDialog) -> void:
	if is_instance_valid(finished_dialog):
		finished_dialog.free()
	_prompt_editor_settings_cleanup()


# 先确认再擦除本插件注册的机器级 EditorSettings 键。
# 这是最后一步 —— 无论哪种结果都会释放对话框并调用
# _done(),从而释放协调器。
func _prompt_editor_settings_cleanup() -> void:
	var dialog := ConfirmationDialog.new()
	dialog.exclusive = false
	dialog.title = EditorLocale.pick(
		"MCP Plugin Disabled — Editor Preferences", "MCP 插件已禁用 — 编辑器偏好设置")
	dialog.dialog_text = EditorLocale.pick(
		"The Godot MCP Unified also stored per-user editor preferences (dock "
			+ "visibility and the unfocused-responsive setting).\n\n"
			+ "These live in your EDITOR settings — they are machine-wide and "
			+ "SHARED across every project that uses the toolkit, so removing them "
			+ "affects your other projects too.\n\n"
			+ "If you're uninstalling everywhere, you may want to remove them.\n"
			+ "If you still use the Godot MCP Unified in another project, keep them.",
		"Godot MCP Unified 还保存了每位用户的编辑器偏好设置（工具坞可见性，以及失去"
			+ "焦点时保持响应的设置）。\n\n这些设置位于“编辑器设置”中，对整台机器生效，"
			+ "并由所有使用 Toolkit 的项目共享；删除后也会影响其他项目。\n\n"
			+ "如果要在所有项目中卸载 Toolkit，可以移除这些设置。\n"
			+ "如果其他项目仍在使用 Toolkit，请保留它们。")
	dialog.ok_button_text = EditorLocale.pick("Remove editor preferences", "移除编辑器偏好设置")
	dialog.cancel_button_text = EditorLocale.pick("Keep", "保留")
	dialog.confirmed.connect(func() -> void:
		var editor_settings := EditorInterface.get_editor_settings()
		for key in _EDITOR_SETTING_KEYS:
			editor_settings.erase(key)
		print("[MCP] Removed machine-wide editor preferences")
		dialog.queue_free()
		_done()
	)
	dialog.canceled.connect(func() -> void:
		print("[MCP] Kept machine-wide editor preferences")
		dialog.queue_free()
		_done()
	)
	EditorInterface.get_base_control().add_child(dialog)
	dialog.popup_centered()
	# 显式提升并聚焦:在没有操作系统所有者的情况下(非独占,见上文的链式
	# 接续说明),编辑器窗口一次迟来的意外激活仍可能把本提示埋到下面;
	# 若提示已在前台,则此调用为空操作。
	dialog.grab_focus()


# 序列完成后释放自引用。在没有其他引用持有的时候,
# 协调器就会在这里被回收。
func _done() -> void:
	_self_ref = null
