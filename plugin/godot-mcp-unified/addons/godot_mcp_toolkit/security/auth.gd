@tool
extends RefCounted
## 上下文协议(MCP)WebSocket 传输层的会话令牌(session token)鉴权。
##
## 令牌按项目实例持久化(稳定令牌,stable token):首次生成一个 32 字节
## 十六进制令牌,写入
## user://addons/godot_mcp_toolkit/project_instance_<hash>/mcp_token
## (或 GODOT_MCP_TOKEN_PATH),此后每次服务器启动都复用文件中的现有值
## (见 load_or_create_token)。跨编辑器重启复用是刻意的:运行中的游戏只在
## 自己启动时读一次令牌并常驻内存,若编辑器每次重启都轮换文件值,跨会话
## 存活的游戏将永远无法通过鉴权。每个连接的客户端都要把令牌作为首条
## WebSocket 消息发送。显式轮换只经 regenerate_token(菜单/停靠面板),
## 轮换后仍在运行的游戏需要重启才能重新鉴权。

const ProjectPaths := preload("res://addons/godot_mcp_toolkit/paths/project_paths.gd")


## 生成一个全新的 64 字符十六进制令牌(32 个随机字节)。
static func generate_token() -> String:
	return Crypto.new().generate_random_bytes(32).hex_encode()


## 令牌路径 — 每个实例独立的 user:// 路径,位于
## user://…/project_instance_<hash>/mcp_token(同一仓库的两个工作树
## 会得到各自独立的文件),或 GODOT_MCP_TOKEN_PATH 覆盖的绝对路径。
static func get_token_path() -> String:
	var env_path := OS.get_environment("GODOT_MCP_TOKEN_PATH")
	if not env_path.is_empty():
		return env_path
	return ProjectPaths.instance_dir() + "mcp_token"


## 注册表发布(registry-publish)形式的令牌路径:一个绝对的全局化路径,
## 供引擎外的服务器直接打开。globalize_path() 会实时读取 use_custom_user_dir,
## 因此重定位过的 user:// 会被尊重,服务器也永远不会重新推导用户目录。
## 引擎内的调用方使用 get_token_path() 的 user:// 形式;注册表发布点
## 则使用这个绝对路径形式。
static func get_published_token_path() -> String:
	return ProjectSettings.globalize_path(get_token_path())


## 将令牌写入磁盘。返回 OK 或错误码。
static func write_token(token: String) -> int:
	ProjectPaths.ensure_dirs()
	var path := get_token_path()
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return FileAccess.get_open_error()
	file.store_string(token)
	file.close()
	# 文件权限:Godot 4.x 没有跨平台的 chmod API。
	# 在 Unix 上,用户数据目录(~/.local/share/godot/...)本身已限制为
	# 仅属主可访问;在 Windows 上 %APPDATA% 也是按用户隔离的。
	# 此处记录为已知限制 — 敏感数据不会泄漏到当前操作系统用户边界之外。
	return OK


## 读取现有令牌文件;缺失/为空时生成新令牌并写盘。返回最终生效的令牌。
## 编辑器服务器与运行时服务器共用的"读或建"入口:令牌按项目实例持久化,
## 跨编辑器重启复用,让存活的运行中游戏、编辑器与外部桥接始终读到同一份。
static func load_or_create_token() -> String:
	var file := FileAccess.open(get_token_path(), FileAccess.READ)
	if file != null:
		var token := file.get_as_text().strip_edges()
		file.close()
		if not token.is_empty():
			return token
	var token := generate_token()
	var write_err := write_token(token)
	if write_err != OK:
		push_warning("[MCPAuth] failed to write token (err %d); auth will still be enforced but the bridge may not find the file" % write_err)
	return token


## 校验解析后的鉴权消息。令牌匹配时返回 true。
static func validate(message: Dictionary, expected_token: String) -> bool:
	return str(message.get("auth", "")) == expected_token
