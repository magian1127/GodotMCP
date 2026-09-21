@tool
extends RefCounted
## 集中计算每个项目实例的 user:// 路径。
##
## 每实例文件位于
##   user://addons/godot_mcp_toolkit/project_instance_<hash>/
## 之下,其中 <hash> 是 SHA-256(规范化项目根)的前 12 个字符。
## 这隔离了多实例与多工作树配置 —— 在同一仓库的不同工作树上
## 打开的两个编辑器会得到各自独立的目录。
##
## 共享文件(例如引导标记)仍直接位于
##   user://addons/godot_mcp_toolkit/


const _PLUGIN_DIR := "user://addons/godot_mcp_toolkit/"
const _ProjectKey := preload("res://addons/godot_mcp_toolkit/paths/project_key.gd")


## 规范项目根路径的 12 字符十六进制哈希。委托给唯一的
## 规范化事实来源,因此该哈希绝不会与注册表的
## 条目文件哈希产生漂移(二者必须一致 —— 同一实例,同一身份)。
static func project_hash() -> String:
	return _ProjectKey.current_hash()


## 每实例目录路径(带尾部斜杠)。
## 例如 "user://addons/godot_mcp_toolkit/project_instance_a1b2c3d4e5f6/"
static func instance_dir() -> String:
	return _PLUGIN_DIR + "project_instance_%s/" % project_hash()


## 确保插件目录与每实例子目录都存在。
## 可安全多次调用(幂等)。
## 使用全局化(绝对操作系统)路径,因此即使在 user:// 根
## 尚不存在时(例如 config/name 重命名把 user:// 移到了
## Godot 尚未实体化的路径之后)也能创建目录。
static func ensure_dirs() -> void:
	var inst := instance_dir()
	if not DirAccess.dir_exists_absolute(inst):
		var abs_path := ProjectSettings.globalize_path(inst).rstrip("/")
		var err := DirAccess.make_dir_recursive_absolute(abs_path)
		if err != OK:
			push_warning("[ProjectPaths] ensure_dirs failed for %s (err %d)" % [abs_path, err])
