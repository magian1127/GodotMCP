@tool
extends RefCounted
## 在试玩测试(playtest)期间边沿检测“运行→停止”的切换。
##
## 插件在每个 _process 中轮询 poll()。在“运行→停止”的边沿上,这里会清空
## 运行时注册表,并主动告知上下文协议(MCP)服务器桥接:游戏已停止,
## 从而立即拆除运行时通道 —— 无需等待下一次
## callRuntime() 才发现连接已死。
##
## 仅限编辑器:使用 EditorInterface.is_playing_scene(),因此由仅限编辑器的
## plugin.gd 构造,运行时自动加载(Autoload)永远不会触及它。
##
## 为什么每帧轮询而不是连接信号? Godot 没有暴露任何公开的、
## 经 ClassDB 绑定的、跨版本(4.2–4.7)的运行状态信号可供插件连接。 引擎的
## 运行/停止信号位于内部的运行栏节点上
## (EditorNode.project_run_bar ≤4.4 / EditorRunBar 4.5+),它未在 ClassDB 中注册,
## 只能从编辑器内部访问。 因此 is_playing_scene() 是
## “运行→停止”边沿唯一公开的跨版本接口,而
## 每帧轮询是务实的主流做法。 粗粒度的 Timer 被否决:它会把
## 运行时清空 + game_stopped 广播推迟到停止边沿之后;轮询
## 本身是 O(1)(一次绑定的 bool 调用 + 一次缓存标志比较)。
## (已对照 Godot 4.2–4.7 引擎源码核实。)

const RegistryClient := preload("res://addons/godot_mcp_toolkit/registry/registry_client.gd")

var _server: Node = null
# 用于运行时端口清理的试玩测试(playtest)结束检测。
var _was_playing: bool = false


func _init(server: Node) -> void:
	_server = server


func poll() -> void:
	var playing := EditorInterface.is_playing_scene()
	if _was_playing and not playing:
		RegistryClient.clear_runtime()
		# 主动通知:告知上下文协议服务器桥接游戏已停止,
		# 使其能立即拆除运行时通道 —— 无需等待
		# 下一次 callRuntime() 才发现连接已死。
		_server.broadcast_notification("game_stopped")
	_was_playing = playing
