@tool
extends RefCounted
## 多实例注册表的磁盘布局权威。
##
## 解析机器级注册表目录(因操作系统而异:Windows 上为 %APPDATA%,
## macOS 上为 ~/Library/Application Support,Linux/BSD 上为 $XDG_DATA_HOME)
## 以及其中的每个规范文件路径 —— 聚合的 projects.json、本实例的
## 编辑器/运行时条目文件,以及注册表锁文件。每实例条目文件名
## 以 ProjectKey.current_hash() 为键 —— 采用同一种规范化配方,
## 因此条目文件与 user:// 实例目录共享同一个身份。
##
## 所有方法都是静态的 —— 没有实例状态。对编辑器无污染:本文件位于
## 运行时自动加载的预加载闭包中(经 registry_client.gd),因此不引用任何
## 仅编辑器可用的类(godot#91713)。

# 直接 preload(不经过 core/modules.gd):本文件位于运行时自动加载的
# 预加载闭包中(registry_client.gd → mcp_runtime_server.gd),因此必须保持
# 对编辑器无污染 —— core/modules.gd 引用了 EditorInterface,会在导出中
# 污染自动加载(godot#91713)。
const _ProjectKey := preload("res://addons/godot_mcp_toolkit/paths/project_key.gd")

const _REGISTRY_FILENAME := "projects.json"
const _ENTRIES_DIR := "entries"


# -- 注册表目录 + projects.json ---------------------------------------


## 机器级注册表目录,首次未命中时创建。有意带副作用
## (一个会确保目录存在的路径查询):调用方依赖它必然存在。
static func registry_dir() -> String:
	var dir: String
	match OS.get_name():
		"Windows":
			var appdata := OS.get_environment("APPDATA")
			if appdata.is_empty():
				appdata = OS.get_environment("USERPROFILE").path_join("AppData/Roaming")
			dir = appdata.path_join("godot-mcp-toolkit")
		"macOS":
			dir = OS.get_environment("HOME").path_join(
				"Library/Application Support/godot-mcp-toolkit")
		_:  # Linux / BSD
			var data_home := OS.get_environment("XDG_DATA_HOME")
			if data_home.is_empty():
				data_home = OS.get_environment("HOME").path_join(".local/share")
			dir = data_home.path_join("godot-mcp-toolkit")
	if not DirAccess.dir_exists_absolute(dir):
		DirAccess.make_dir_recursive_absolute(dir)
	return dir


static func registry_path() -> String:
	return registry_dir().path_join(_REGISTRY_FILENAME)


# -- 每实例条目文件 --------------------------------------------------


## entries/ 子目录,首次未命中时创建(与 registry_dir 相同的 CQS 例外)。
static func entry_dir() -> String:
	var d := registry_dir().path_join(_ENTRIES_DIR)
	if not DirAccess.dir_exists_absolute(d):
		DirAccess.make_dir_recursive_absolute(d)
	return d


## 本编辑器实例的条目文件:entries/<hash>.json。
static func entry_file_path() -> String:
	return entry_dir().path_join(_ProjectKey.current_hash() + ".json")


## 运行时子进程自己的条目文件:entries/<hash>.runtime.json。运行中的游戏
## 写这里;编辑器写 <hash>.json —— 两个互不相同的文件、各有一个写入者,
## 因此编辑器与运行时绝不会对同一文件做读-改-写。
static func runtime_entry_file_path() -> String:
	return entry_dir().path_join(_ProjectKey.current_hash() + ".runtime.json")


# -- 锁文件 -----------------------------------------------------------------


## 注册表锁文件:projects.json + ".lock"。在多个并发编辑器实例之间,
## 把机器级读-改-写(注册表自身的重建,以及同目录的无焦点休眠备份)
## 串行化。
static func lock_path() -> String:
	return registry_path() + ".lock"
