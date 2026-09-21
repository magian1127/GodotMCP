@tool
extends Node
## 工具巡检(tool-sweep)环境探针（已提交的测试夹具）。
##
## 版本预检(Version Preflight)阶段经 node_call_method 调用其 @tool 方法，
## 读取权威的运行中引擎版本：通过真实的 callv() 分发到达
## Engine.get_version_info()——沙箱化的 execute_code / Expression 路径做不到
## （Expression 解析不到引擎单例）。提交此夹具是为了让巡检直接打开场景并调用
## 该方法，无需任何每次运行的准备工作。
## 另见 Validations/tool-sweep.md -> Version Preflight。


## 运行中编辑器二进制的自身版本，直接取自引擎：
## {major, minor, patch, hex, status, string, build, hash, year}。
func get_engine_version() -> Dictionary:
	return Engine.get_version_info()
