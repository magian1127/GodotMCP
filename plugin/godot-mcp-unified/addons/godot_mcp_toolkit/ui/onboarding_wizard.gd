@tool
extends RefCounted
## 首次激活插件时显示的引导向导(onboarding wizard)。
## 自包含的状态机，管理一个多步骤的 AcceptDialog。

const SettingsNavigator := preload("res://addons/godot_mcp_toolkit/ui/settings_navigator.gd")
const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
const ToolkitDialogPresenter := preload("res://addons/godot_mcp_toolkit/ui/toolkit_dialog_presenter.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")
const MCPJsonSync := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_sync.gd")

const _ONBOARDING_FLAG := "user://addons/godot_mcp_toolkit/mcp_onboarding_shown_v001"
const _ONBOARDING_PROGRESS := "user://addons/godot_mcp_toolkit/mcp_onboarding_progress"
const _STEP_COUNT := 3

var _plugin: EditorPlugin
var _server: Node  # 传给对话框呈现器(presenter)的信息/帮助对话框。
var _write_flow: MCPJsonWriteFlow
var _dialog_presenter: ToolkitDialogPresenter
var _language_provider: Callable
var _dialog: AcceptDialog = null
var _step: int = 0
var _mcp_exists: bool = false  # 跟踪 .mcp.json 的状态，用于第 1 步的两种变体。
var _buttons: Array = []  # 跟踪的自定义按钮，供每步清理使用。


func _init(
		plugin: EditorPlugin, server: Node,
		write_flow: MCPJsonWriteFlow, dialog_presenter: ToolkitDialogPresenter,
		language_provider: Callable = Callable()) -> void:
	_plugin = plugin
	_server = server
	_write_flow = write_flow
	_dialog_presenter = dialog_presenter
	_language_provider = language_provider


func check_and_show() -> void:
	if is_onboarding_complete():
		return

	# 从已保存的进度恢复（例如向导进行中重启之后）。
	_step = 0
	if FileAccess.file_exists(_ONBOARDING_PROGRESS):
		var f := FileAccess.open(_ONBOARDING_PROGRESS, FileAccess.READ)
		if f != null:
			_step = clampi(f.get_line().to_int(), 0, _STEP_COUNT - 1)
			f.close()
	_buttons.clear()
	var dialog := AcceptDialog.new()
	dialog.exclusive = false
	dialog.min_size = Vector2i(480, 260)

	# AcceptDialog 在确认后会自动隐藏——前进到下一步之后重新显示。
	dialog.confirmed.connect(_on_confirmed.bind(dialog))
	dialog.custom_action.connect(_on_custom_action.bind(dialog))
	dialog.canceled.connect(func():
		_write_flag()
		free_if_open()
	)

	_dialog = dialog
	_show_step(dialog)
	EditorInterface.get_base_control().add_child(dialog)
	dialog.popup_centered()


# 会话中途关闭——向导内的每个调用方都运行在对话框自己的
# 某个信号回调（confirmed / custom_action / canceled）内部，此时立即 free()
# 会在信号发射过程中删除发射者；queue_free() 才是这里的合法形式。
# 插件销毁清理改用 teardown()（立即 free——见下文）。
func free_if_open() -> void:
	_buttons.clear()
	if _dialog != null and is_instance_valid(_dialog):
		_dialog.queue_free()
	_dialog = null


# free_if_open() 在插件销毁清理时的对应版本：立即 free()，而不是 queue_free()。
# 它在插件的 _exit_tree 路径上运行，处于任何对话框信号发射之外；
# 延迟删除会让对话框的按钮/确认连接（以及本向导实例）存活到
# ObjectDB 的退出时泄漏检查之后 → 产生虚假的
# “resources still in use at exit”。对话框可能正处于打开状态——free() 会一步完成关闭并删除。
func teardown() -> void:
	_buttons.clear()
	if _dialog != null and is_instance_valid(_dialog):
		_dialog.free()
	_dialog = null


# -- 步骤渲染 -----------------------------------------------------------
#
# 每个步骤贡献一份“规格”(spec)——对其内容的纯数据描述——而单一的渲染器
# （_show_step）拥有每个步骤共享的样板代码：标题、上一步按钮的清理、
# 应用文本/OK 标签，以及添加并跟踪每个自定义按钮
# （跟踪是自动的，因此按钮不会从每步清理循环中泄漏）。
# 新增一个步骤意味着新增一份 spec，
# 而不是再写一条必须重新实现清理/跟踪/弹出契约的渲染分支。
#
# spec 的形状（除 on_enter 外所有值均为纯数据）：
#   text:      String    — 对话框正文
#   ok_label:  String    — OK 按钮的文案
#   buttons:   Array     — [ { label: String, action: String }, ... ]，按顺序
#   on_enter:  Callable  — 可选的副作用，在步骤显示时执行


# 为当前步骤构建 spec。就对话框而言是纯的：它返回数据
# 并可能设置 _mcp_exists（导航逻辑会读取它），但绝不触碰对话框
# ——因此每个步骤的文本、OK 标签与按钮都可以
# 在没有编辑器的情况下做单元级检查。
func _spec_for_step() -> Dictionary:
	match _step:
		0:
			return _spec_welcome()
		1:
			# 在这里探测文件系统（不纯的部分）并记录结果供导航使用；
			# spec 本体完全由该结果纯构建。
			_mcp_exists = MCPJsonSync.has_mcp_json()
			return _spec_mcp_json(_mcp_exists)
		2:
			return _spec_dock_overview()
	return {}


func _spec_welcome() -> Dictionary:
	if _uses_chinese_copy():
		return {
			"text": (
				"欢迎使用 Godot MCP Unified！\n\n"
				+ "本插件通过 MCP 向 AI 智能体开放 Godot 编辑器。\n"
				+ "部分工具能够修改项目，或执行任意代码。\n\n"
				+ "每个工具都会通过 MCP annotation 标出风险。\n"
				+ "如需查看说明和可直接使用的屏蔽配置，请打开：\n"
				+ "  addons/godot_mcp_toolkit/docs/security-recommendations.zh-CN.md\n\n"
				+ "常规工具默认可用。execute_code 和 node_call_method 只有在设置\n"
				+ "GODOT_MCP_UNSAFE=1 后才会出现。还可以使用智能体的\n"
				+ "allowlist/blocklist 进一步限制工具。"),
			"ok_label": "下一步",
			"buttons": [{"label": "打开安全文档", "action": "open_security"}],
		}
	return {
		"text": (
			"Welcome to the Godot MCP Unified!\n\n"
			+ "This plugin exposes your Godot editor to AI agents via MCP.\n"
			+ "Some tools can modify your project or execute arbitrary code.\n\n"
			+ "Risk is communicated per-tool via MCP annotations.\n"
			+ "For details and copy-pasteable agent blocking configs, see:\n"
			+ "  addons/godot_mcp_toolkit/docs/security-recommendations.md\n\n"
			+ "Typed tools are available by default; unsafe escape hatches require\n"
			+ "GODOT_MCP_UNSAFE=1. Use your agent's allowlist/blocklist to\n"
			+ "restrict specific tools if needed."),
		"ok_label": "Next",
		"buttons": [{"label": "Open Security Doc", "action": "open_security"}],
	}


# 纯函数：.mcp.json 步骤按文件是否已存在分为两种变体。
# 继续按钮始终保留当前客户端配置；创建文件必须由单独按钮明确触发。
func _spec_mcp_json(mcp_exists: bool) -> Dictionary:
	if _uses_chinese_copy():
		var intro_zh := (
			".mcp.json 是一种可选的项目 MCP 配置。"
			+ "Codex 等客户端也可以使用插件或全局 MCP 配置。\n\n"
			+ "MCP 服务器桥接需要本机安装 Node.js 22 或更高版本。"
			+ "安装说明见 addons/godot_mcp_toolkit/docs/advanced_configuration.zh-CN.md。\n\n")
		if mcp_exists:
			return {
				"text": intro_zh + "已找到适用于当前工程的 .mcp.json。",
				"ok_label": "继续（保留现有 .mcp.json）",
				"buttons": [{
					"label": "在工程根目录写入 .mcp.json",
					"action": "overwrite_mcp",
				}],
			}
		return {
			"text": intro_zh + "未检测到项目配置；如果已在客户端配置 MCP，可以直接继续。",
			"ok_label": "继续（使用客户端配置）",
			"buttons": [{"label": "创建 .mcp.json", "action": "create_mcp"}],
		}
	var intro := (
		".mcp.json is an optional project MCP configuration. "
		+ "Clients such as Codex can also use plugin or global MCP configuration.\n\n"
		+ "The local MCP server bridge requires Node.js 22+ to run. "
		+ "See addons/godot_mcp_toolkit/docs/advanced_configuration.md for local setup.\n\n")
	if mcp_exists:
		return {
			"text": intro + "An .mcp.json configuration for this project was found.",
			"ok_label": "Continue (keep existing .mcp.json)",
			"buttons": [{
				"label": "Write .mcp.json at the project root",
				"action": "overwrite_mcp",
			}],
		}
	return {
		"text": intro + "No project configuration was detected. Continue if MCP is already configured in your client.",
		"ok_label": "Continue (use client configuration)",
		"buttons": [{"label": "Create .mcp.json", "action": "create_mcp"}],
	}


func _spec_dock_overview() -> Dictionary:
	# 显示停靠面板(dock)是一个副作用，因此作为 on_enter 搭载，
	# 而不是烘焙进（本应纯的）spec。作为单独语句赋值
	# （而不是内联字典值），以保持多行 lambda 的解析无歧义。
	var reveal_dock := func() -> void:
		if _plugin != null:
			_plugin.call("reveal_dock")  # 动态分发：基类 EditorPlugin 没有 reveal_dock
	var spec: Dictionary
	if _uses_chinese_copy():
		spec = {
			"text": (
				"Godot MCP Unified 工具坞位于底部面板（Output 和 Debugger 旁边）。"
				+ "你可以在这里：\n\n"
				+ "  \u2022 监控服务器状态——连接状态、连接数量、运行时端口\n"
				+ "  \u2022 查看审计日志，了解 AI 智能体执行了哪些操作\n"
				+ "  \u2022 调整安全设置，包括令牌轮换和响应上限\n\n"
				+ "“信息 / 帮助”按钮会显示工具列表和文档链接。\n"
				+ "Codex 配套技能也会随插件一并安装。\n\n"
				+ "如果要在受监督环境中使用，请在 .mcp.json 中设置\n"
				+ "GODOT_MCP_READ_ONLY=1，将 Toolkit 限制为只读工具。\n\n"
				+ "MCP 会话期间，即使编辑器窗口失去焦点，Toolkit 仍会保持响应\n"
				+ "（默认开启，会增加后台 CPU 占用）。可在工具坞的服务器状态中切换，\n"
				+ "也可前往 Editor Settings → Mcp Toolkit → Performance。\n\n"
				+ "工具坞里的响应上限和审计控件可以直接打开 Project Settings →\n"
				+ "Mcp Toolkit。所有项目级选项（包括高级选项）都可以在那里找到。\n\n"
				+ "设置完成！"),
			"ok_label": "关闭",
			"buttons": [
				{"label": "返回", "action": "back"},
				{"label": "打开信息", "action": "open_info"},
			],
		}
	else:
		spec = {
			"text": (
				"The Godot MCP Unified dock is in the bottom panel (next to Output and Debugger). "
				+ "From here you can:\n\n"
				+ "  \u2022 Monitor server status — connection state, peer count, runtime port\n"
				+ "  \u2022 Review the audit log — see what the AI agent did\n"
				+ "  \u2022 Adjust security settings — token rotation, response limits\n\n"
				+ "The 'Info / Help' button shows tool list and documentation links.\n"
				+ "Bundled Codex skills are included in the plugin package.\n\n"
				+ "For supervised environments, set GODOT_MCP_READ_ONLY=1 in\n"
				+ ".mcp.json to restrict the toolkit to read-only tools.\n\n"
				+ "The toolkit keeps the editor responsive while it's unfocused during\n"
				+ "MCP sessions (ON by default; raises background CPU). Toggle it in the\n"
				+ "dock's Server Status, or in Editor Settings → Mcp Toolkit → Performance.\n\n"
				+ "The dock's limits and audit controls are shortcuts into\n"
				+ "Project Settings → Mcp Toolkit, where all per-project options\n"
				+ "live (including advanced ones the dock doesn't surface).\n\n"
				+ "You're all set!"),
			"ok_label": "Close",
			"buttons": [
				{"label": "Back", "action": "back"},
				{"label": "Open Info", "action": "open_info"},
			],
		}
	spec["on_enter"] = reveal_dock
	return spec


# 将当前步骤的 spec 渲染到对话框上。拥有每个步骤共享的样板代码：
# 标题、上一步按钮的清理、文本 + OK 标签，以及添加并跟踪每个自定义按钮
# （跟踪在这里进行，因此按钮绝不会从每步清理循环中泄漏）。
# on_enter（如存在）会在调用方弹出对话框之前触发。
func _show_step(dialog: AcceptDialog) -> void:
	dialog.title = (
		"Godot MCP Unified — 设置向导（第 %d/%d 步）" % [_step + 1, _STEP_COUNT]
		if _uses_chinese_copy()
		else "Godot MCP Unified — Setup Wizard (%d of %d)" % [_step + 1, _STEP_COUNT]
	)

	# 释放上一步跟踪的所有自定义按钮。
	for btn in _buttons:
		if is_instance_valid(btn):
			btn.queue_free()
	_buttons.clear()

	var spec := _spec_for_step()
	dialog.dialog_text = str(spec.get("text", ""))
	dialog.ok_button_text = str(spec.get("ok_label", ""))
	var buttons: Array = spec.get("buttons", [])
	for entry in buttons:
		var button: Dictionary = entry
		_buttons.append(dialog.add_button(
			str(button.get("label", "")), true, str(button.get("action", ""))))
	if spec.has("on_enter"):
		var on_enter: Callable = spec.get("on_enter")
		on_enter.call()


# -- 导航 ---------------------------------------------------------------


func _on_confirmed(dialog: AcceptDialog) -> void:
	if _is_last_relevant_step():
		# 最后一步——完成向导（写入完成标志）而不是继续前进。
		_write_flag()
		free_if_open()
		return
	_step += 1
	_save_progress()
	_show_step(dialog)
	# AcceptDialog 在确认后会自动隐藏——为下一步重新显示。
	dialog.popup_centered()


# 当前步骤是向导将显示的最后一步时为真——确认它即完成向导
# （写入完成标志）而不是继续前进。
func _is_last_relevant_step() -> bool:
	return _step >= _STEP_COUNT - 1


func _on_custom_action(action: StringName, dialog: AcceptDialog) -> void:
	match str(action):
		"back":
			if _step > 0:
				_step -= 1
				_save_progress()
				_show_step(dialog)
		"create_mcp", "overwrite_mcp":
			if _write_flow != null:
				_write_flow.write()
			_step += 1
			_save_progress()
			_show_step(dialog)
		"open_info":
			_write_flag()
			free_if_open()
			if _dialog_presenter != null:
				_dialog_presenter.show_info(_server)
		"open_security":
			var doc_path := _security_doc_path()
			var global_path := ProjectSettings.globalize_path(doc_path)
			OS.shell_open(global_path)


# -- 编辑器语言 ---------------------------------------------------------


## 中文编辑器区域使用随附的简体中文文案。其他所有区域有意回退(fallback)到
## 英文；向导恰好只有两套文案，
## 且不继承运行中项目的 TranslationServer 区域设置。
func _uses_chinese_copy() -> bool:
	if _language_provider.is_valid():
		return EditorLocale.is_chinese_locale(str(_language_provider.call()))
	return EditorLocale.is_chinese_editor()


static func _is_chinese_locale(locale: String) -> bool:
	return EditorLocale.is_chinese_locale(locale)


func _security_doc_path() -> String:
	return (
		"res://addons/godot_mcp_toolkit/docs/security-recommendations.zh-CN.md"
		if _uses_chinese_copy()
		else "res://addons/godot_mcp_toolkit/docs/security-recommendations.md"
	)


# -- 持久化 --------------------------------------------------------------


## 引导(onboarding)完成后（向导被完成或被关闭）为真。
## 这是完成标志唯一的共享查询：check_and_show() 以它为门槛；
## 引导尚未完成时，启用时的 .mcp.json 提示会自行抑制
## （那时由向导自己的 .mcp.json 步骤负责该文件）。
static func is_onboarding_complete() -> bool:
	return FileAccess.file_exists(_ONBOARDING_FLAG)


## 在 user:// 路径发生变化（config/name、use_custom_user_dir 或
## custom_user_dir_name 更改）后，在新的 user:// 路径重建引导完成标志。
## 防止路径移动后向导再次出现。
## 目录保证已存在（UserPathMonitor 在发出信号前
## 会调用 ensure_dirs()）。
static func migrate_flag_after_rename() -> void:
	var f := FileAccess.open(_ONBOARDING_FLAG, FileAccess.WRITE)
	if f != null:
		f.store_string("1")
		f.close()


func _write_flag() -> void:
	var f := FileAccess.open(_ONBOARDING_FLAG, FileAccess.WRITE)
	if f != null:
		f.store_string("1")
		f.close()
	# 清理进度文件——向导已完成。
	if FileAccess.file_exists(_ONBOARDING_PROGRESS):
		DirAccess.remove_absolute(_ONBOARDING_PROGRESS)


func _save_progress() -> void:
	var f := FileAccess.open(_ONBOARDING_PROGRESS, FileAccess.WRITE)
	if f != null:
		f.store_string(str(_step))
		f.close()
