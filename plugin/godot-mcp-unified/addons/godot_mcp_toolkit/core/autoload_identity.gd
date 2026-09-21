@tool
extends RefCounted
## 工具集运行时自动加载(Autoload)身份的唯一事实来源 —— 即插件必须注册的
## [name, res:// 路径] 对,以及每一对所映射到的
## ProjectSettings 键/值的纯推导。
##
## 一个纯常量叶子模块:不预加载任何内容,除推导之外不包含任何行为。
## 它存在的意义是让插件生命周期域(注册 + 加载时自愈)与导出剥离域
## (烘焙时置空,之后恢复)共享同一份身份而不互相耦合 —— 双方都预加载本叶子,
## 彼此互不预加载,因此重命名或路径变更只会落在一处。
## 不使用 [code]class_name[/code]:
## 使用方以 [code]const AutoloadIdentity := preload(...)[/code] 的方式访问它。

## 模式 B —— 承载游戏侧 WebSocket 服务器的运行时自动加载(Autoload)。启用的
## 插件保证该自动加载(Autoload)已被注册;注册是幂等的,因此 project.godot 中
## 已带有该条目的项目会保留其现有值。
const RUNTIME_AUTOLOAD_NAME := "MCPRuntimeServer"
const RUNTIME_AUTOLOAD_PATH := "res://addons/godot_mcp_toolkit/runtime/mcp_runtime_server.gd"

## 启用的插件必须保证存在的自动加载(Autoload),以 [name, res:// 路径] 对表示。
## 当前只有一个条目(运行时服务器);列表这一形态为将来加入第二个条目预留了空间。
const REQUIRED_AUTOLOADS := [[RUNTIME_AUTOLOAD_NAME, RUNTIME_AUTOLOAD_PATH]]


## 自动加载(Autoload)对应的 ProjectSettings 键 —— [code]"autoload/<name>"[/code]。
## [param autoload_name] 是单例名称(例如 "MCPRuntimeServer")。
static func settings_key(autoload_name: String) -> String:
	return "autoload/" + autoload_name


## 自动加载(Autoload)脚本对应的 ProjectSettings 值 —— [code]"*<path>"[/code]。前导的
## "*" 表示启用该单例,与编辑器写入自动加载(Autoload)的方式一致。
static func settings_value(script_path: String) -> String:
	return "*" + script_path
