@tool
extends RefCounted
## 多实例注册表的规范项目根身份。
##
## 规范化路径是 TypeScript 桥接从 projects.json 读取的 `_key`;12 字符哈希
## 为每实例条目文件(entries/<hash>.json)命名,并且 —— 经由
## ProjectPaths —— 也是每实例 user:// 目录的名称。同一套规范化配方,
## 两个使用方:配方的任何变更都会同时改变桥接读取的键与磁盘上的
## 文件名,因此它是冻结的契约。
##
## 所有方法均为静态 —— 按值确定身份,没有实例状态,没有生命周期。


## 规范化项目根路径:反斜杠 → 正斜杠,去掉尾部斜杠,
## 在大小写不敏感的文件系统上转为小写。Windows 与 macOS 的默认
## 文件系统大小写不敏感;转小写可避免 TS 桥接读取
## 同一个注册表文件时,Godot 的 globalize_path 与 Node.js process.cwd()
## 之间出现不匹配。
static func canonical(path: String) -> String:
	var result := path.replace("\\", "/").rstrip("/")
	if OS.get_name() in ["Windows", "macOS"]:
		result = result.to_lower()
	return result


## 当前项目根的规范键(桥接读取的 `_key`)。
static func current() -> String:
	return canonical(ProjectSettings.globalize_path("res://"))


## 已规范化的键的 12 字符十六进制哈希。参数按原样哈希 ——
## 不会再次规范化,因此调用方必须传入来自
## canonical() / current() 的键;原始路径会哈希出错误、不匹配的值。
static func hash_of(canonical_key: String) -> String:
	return canonical_key.sha256_text().substr(0, 12)


## 当前项目根的 12 字符十六进制哈希 —— 条目文件/实例目录的
## 文件名令牌。唯一受认可的“从零开始”路径:构造上保证
## current_hash() == hash_of(current())。
static func current_hash() -> String:
	return hash_of(current())
