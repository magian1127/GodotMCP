@tool
extends RefCounted
## game.* / debugger.get_log 命令注册编排器 — 把游玩
## 会话生命周期(game.start / game.stop)与带缓存的 debugger.get_log
## 读取器挂载到注册表上,并注入/卸载调试桥接。不持有
## 子领域逻辑:game.start/stop 委派给游玩会话控制子模块,
## debugger.get_log 委派给调试日志获取子模块,而桥接
## 注入(register)与卸载(clear_debug_bridge)则转发给
## 拥有桥接状态的日志读取子模块。

const _Control := preload("res://addons/godot_mcp_toolkit/commands/playtest/playtest_control.gd")
const _LogReader := preload("res://addons/godot_mcp_toolkit/commands/playtest_log_reader.gd")


static func register(registry: MCPToolkitCommandRegistry, _server: Node,
		debug_bridge: RefCounted = null) -> void:
	_LogReader.set_debug_bridge(debug_bridge)
	# game.start / game.stop 会产生变更(启动/停止游玩会话),因此它们
	# 不是只读的。独占执行是唯一的串行化驱动:它
	# 强制"同一时刻只有一个在执行"的顺序,而不论只读状态如何,
	# 这正是这些会话生命周期变更操作所需要的。
	registry.add("game.start", func(parameters: Dictionary) -> Dictionary:
		return await _Control.cmd_game_start(parameters)
	, MCPToolkitCommandOptions.new().mark_exclusive_execution())
	registry.add("game.stop", func(parameters: Dictionary) -> Dictionary:
		return _Control.cmd_game_stop(parameters)
	, MCPToolkitCommandOptions.new().mark_exclusive_execution().mark_scene_independent())
	registry.add("debugger.get_log", func(parameters: Dictionary) -> Dictionary:
		return _LogReader.cmd_debugger_get_log(parameters)
	, MCPToolkitCommandOptions.new().mark_read_only().mark_scene_independent())


static func clear_debug_bridge() -> void:
	_LogReader.clear_debug_bridge()
