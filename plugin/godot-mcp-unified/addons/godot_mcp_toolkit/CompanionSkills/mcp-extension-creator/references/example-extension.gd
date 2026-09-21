@tool
class_name MCPToolkitNotesExample
extends MCPToolkitExtension
## 示例扩展:添加一个单一的笔记写入工具。
##
## 注册 [code]notes.write[/code],它把一条 Markdown 笔记写入
## [code]res://[/code] 路径。这是"一个好的扩展长什么样"的典范参考:
## 路径守卫、必填参数检查、成功与
## 失败信封,以及会出现在编辑器类
## 参考中的文档注释。复制它,重命名类,然后改造处理器。[br]
## [br]
## 示例:
## [codeblock]
## notes.write(file_path="res://notes/todo.md", content="- ship it")
## [/codeblock]


## 把本扩展的命令注册到活动的 [param registry] 上。
## [param server] 是上下文协议(MCP)服务器节点(此处未使用)。加载时调用一次。
func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	var opts := MCPToolkitExtensionOptions.new("Write a markdown note to a res:// path") \
		.mark_scene_independent() \
		.guard_project_path("file_path") \
		.with_input_schema({
			"type": "object",
			"properties": {
				"file_path": {"type": "string", "description": "res:// path to the note (.md)"},
				"content": {"type": "string", "description": "Markdown body to write"},
			},
			"required": ["file_path", "content"],
		})
	# 非只读:该工具会写入。场景无关:它只触碰一个文件路径。
	registry.add("notes.write", _write, opts)


## 把一条 Markdown 笔记写入磁盘。
##
## 把 [param content] 写入 [param file_path] 处的文件(一个 [code]res://[/code]
## 路径,在本函数运行前已做目录穿越防护)。返回携带已写入路径的成功信封,
## 或在缺少参数或发生 I/O
## 错误时返回失败信封。
func _write(params: Dictionary) -> Dictionary:
	var missing: Variant = MCPToolkitError.require(params, ["file_path", "content"])
	if missing != null:
		return missing

	var file_path: String = params["file_path"]
	var content: String = params["content"]

	var file := FileAccess.open(file_path, FileAccess.WRITE)
	if file == null:
		return MCPToolkitError.fail("WRITE_FAILED",
			"could not open %s for writing" % file_path,
			"Check the folder exists — use folder.create first if needed.")

	file.store_string(content)
	file.close()

	return MCPToolkitSuccess.ok({"path": file_path, "bytes": content.length()})
