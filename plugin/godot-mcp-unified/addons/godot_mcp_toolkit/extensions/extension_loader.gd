@tool
extends RefCounted
## 为扩展子系统的生命周期编排先后顺序,并对外呈现组合器所绑定的 load_all /
## start_watcher 门面(façade)。
##
## load_all() 执行一次性的启动发现,并注册 extensions.list 元命令。
## start_watcher() 创建实时热重载监视器(watcher),并把注册表 + 服务器交给它。
## 这里只负责接线,不包含领域逻辑:
## 发现逻辑、元命令的线上形状、热重载差异引擎各自位于独立的模块
## (extension_discovery、extension_meta_commands、extension_watcher);
## 共享的加载/探测/版本主干则位于 extension_support。

const _Discovery := preload("res://addons/godot_mcp_toolkit/extensions/services/extension_discovery.gd")
const _MetaCommands := preload("res://addons/godot_mcp_toolkit/extensions/services/extension_meta_commands.gd")
const _Watcher := preload("res://addons/godot_mcp_toolkit/extensions/services/extension_watcher.gd")


static func load_all(registry: MCPToolkitCommandRegistry, server: Node) -> int:
	# 执行一次性的启动发现流程 —— 它把每个已启用的扩展候选加载进注册表,
	# 并将保留下来的 C# 实例存为注册表元数据(meta),
	# 使其存活期超过本次调用。
	var loaded := _Discovery.discover_and_register(registry, server)
	if loaded > 0:
		print("[MCPExtensions] Discovered %d extension(s) via reflection" % loaded)
	# 注册用于桥接发现的元命令。
	_MetaCommands.register_list_command(registry)
	return loaded


## 创建一个持久的监视器(watcher),监视 EditorFileSystem 上的扩展变更,
## 并广播 "extensions.changed" 通知。调用方必须(MUST)保留返回的引用
## (防止被 GC 回收)。
static func start_watcher(registry: MCPToolkitCommandRegistry, server: Node) -> RefCounted:
	var watcher := _Watcher.new()
	watcher.setup(registry, server)
	return watcher
