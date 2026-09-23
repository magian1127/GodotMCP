@tool
extends RefCounted
## 本平台的 .NET RID 名(win-x64 / win-arm64 / linux-x64 / linux-arm64 /
## osx-arm64 / osx-x64)。
##
## 单一出处:daemon 边车的可执行文件解析(daemon/daemon_sidecar.gd)与
## .mcp.json 条目的 shim 路径解析(ui/mcp_json_sync.gd)都取这里——
## 发布产物落点 publish/<rid>/ → addons/godot_mcp_toolkit/bin/<rid>/ 与
## 插件写出的条目路径因此必然一致,两张 RID 表不会各自漂移。
##
## 必须保持"对导出友好":只引用 Engine / OS 两个核心单例,不引入任何
## 仅编辑器可用的类(本文件随 modules.gd 的预加载闭包进入运行时自动加载)。


## 当前平台的 RID;未知架构按同类平台的 x64 兜底。
static func current() -> String:
	var arch := Engine.get_architecture_name()
	match OS.get_name():
		"Windows":
			return "win-x64" if arch == "x86_64" else "win-arm64"
		"macOS":
			return "osx-arm64" if arch == "arm64" else "osx-x64"
		_:
			return "linux-x64" if arch == "x86_64" else "linux-arm64"
