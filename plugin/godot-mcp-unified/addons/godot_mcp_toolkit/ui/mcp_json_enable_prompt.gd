@tool
extends RefCounted
## 启用插件时的 .mcp.json 写入提议——一次性的“写入/跳过”提示。
##
## 没有可用的工程或工作区配置时，提议创建可选配置(daemon HTTP 条目为常量内容,
## 无本地入口前置条件)。客户端插件或全局配置不依赖本文件；不能把文件缺失当成
## 连接故障。引导向导尚未完成时不显示——首次激活时由向导自己的 .mcp.json 步骤
## 负责该文件。按设计在每次启用时都会询问：启用操作并不频繁，因此无需维护
## “不再询问”的状态（与禁用时的孤儿提示相对称）。
##
## 仅限编辑器：对话框以编辑器基础控件为父节点，因此本脚本绝不能进入运行时
## 自动加载(Autoload)的 preload 闭包。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const MCPJsonSync = Modules.MCPJsonSync
const MCPJsonWriteFlow := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_write_flow.gd")
const OnboardingWizard := preload("res://addons/godot_mcp_toolkit/ui/onboarding_wizard.gd")
const DockConfirm := preload("res://addons/godot_mcp_toolkit/ui/dock/dock_confirm.gd")
const EditorLocale := preload("res://addons/godot_mcp_toolkit/ui/editor_locale.gd")


## 在 .mcp.json 缺失且引导向导已完成时提议写入该文件。提示对话框是瞬态的——
## 以基础控件为父节点，并在每次关闭时释放（确认对话框工厂的自释放机制）。
## “写入”委托给 [param write_flow]；
## 由于文件不存在，该流程会直接依据模板写入，
## 无需覆盖确认。“跳过”仅关闭对话框。
static func show_if_needed(write_flow: MCPJsonWriteFlow) -> void:
	if write_flow == null or MCPJsonSync.has_mcp_json():
		return
	if not MCPJsonSync.can_write_mcp_json():
		return
	if not OnboardingWizard.is_onboarding_complete():
		return
	DockConfirm.confirm(
		EditorLocale.pick("Optional project MCP configuration", "可选的项目 MCP 配置"),
		EditorLocale.pick(
			"No project .mcp.json was detected. Clients can also use plugin or global MCP configuration.\n\n"
				+ "Create a project file for clients that use .mcp.json?",
			"未检测到项目 .mcp.json。客户端也可以使用插件或全局 MCP 配置。\n\n"
				+ "是否为使用 .mcp.json 的客户端创建一份项目配置？"),
		EditorLocale.pick("Write .mcp.json", "写入 .mcp.json"),
		func() -> void: write_flow.write(),
		EditorLocale.pick("Skip", "跳过"),
	)
