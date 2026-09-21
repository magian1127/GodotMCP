@tool
extends RefCounted
## 监视会改变 user:// 基础路径的 ProjectSettings。
##
## Godot 从三个设置推导 user:// 目录 —— config/name、
## use_custom_user_dir 与 custom_user_dir_name(OS::get_user_data_dir 在
## 每次调用时都会实时读取这三项)。重命名项目、切换自定义
## 用户目录或在运行时更改其名称,会让每个 user:// 路径悄悄
## 解析到新的操作系统目录。在偏移之前写入的文件
## (附属文件、鉴权令牌、审计日志、引导标志)会在旧路径下变成孤儿。
##
## 本监视器监听 ProjectSettings.settings_changed(4.2+ 可用),
## 检测三个推导 user:// 的设置中任何一个的变化,确保插件目录
## 存在于新的 user:// 路径,然后发出 user_path_changed,
## 让所有使用方都能在目录结构已就绪的前提下做出响应。
## 该信号不携带载荷:偏移可能来自三个设置中的任何一个
## (切换自定义目录不会改变名称),因此“用户路径已变更,请重新解析”
## 是唯一有意义的契约。
##
## 用法:
##   var monitor := UserPathMonitor.new()
##   monitor.start()
##   monitor.user_path_changed.connect(_on_user_path_changed)

const ProjectPaths := preload("res://addons/godot_mcp_toolkit/paths/project_paths.gd")

signal user_path_changed

var _cached_name: String = ""
var _cached_use_custom_dir: bool = false
var _cached_custom_dir_name: String = ""


## 开始监视。在插件初始化之后调用一次。
func start() -> void:
	_cache_settings()
	if not ProjectSettings.settings_changed.is_connected(_on_settings_changed):
		ProjectSettings.settings_changed.connect(_on_settings_changed)


## 停止监视。在 _exit_tree() 清理时调用。
func stop() -> void:
	if ProjectSettings.settings_changed.is_connected(_on_settings_changed):
		ProjectSettings.settings_changed.disconnect(_on_settings_changed)


func _on_settings_changed() -> void:
	var name := ProjectSettings.get_setting("application/config/name", "")
	var use_custom := bool(ProjectSettings.get_setting("application/config/use_custom_user_dir", false))
	var custom_name := ProjectSettings.get_setting("application/config/custom_user_dir_name", "")
	if name == _cached_name and use_custom == _cached_use_custom_dir and custom_name == _cached_custom_dir_name:
		return
	_cached_name = name
	_cached_use_custom_dir = use_custom
	_cached_custom_dir_name = custom_name
	push_warning("[MCP] user:// path-deriving setting changed - user:// path shifted. Re-creating addon state.")
	# 在通知使用方之前,确保插件目录存在于新的 user:// 路径 ——
	# 它们可以立即写入,无需调用 ensure_dirs()。
	ProjectPaths.ensure_dirs()
	user_path_changed.emit()


func _cache_settings() -> void:
	_cached_name = ProjectSettings.get_setting("application/config/name", "")
	_cached_use_custom_dir = bool(ProjectSettings.get_setting("application/config/use_custom_user_dir", false))
	_cached_custom_dir_name = ProjectSettings.get_setting("application/config/custom_user_dir_name", "")
