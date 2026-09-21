[英文原文](extending.md)

# 扩展 Godot MCP Unified

## 扩展（支持）

Toolkit 通过 `MCPToolkitExtension` 基类和基于反射的发现机制支持第三方扩展。每个扩展是 `addons/<extension_name>/` 中的独立可分发目录；GDScript 直接继承该基类，C# 使用 `[GlobalClass]` 的 `RefCounted` 类。扩展注册工具、参数 schema、行为标记和错误响应，随后可像内置工具一样被 MCP 客户端发现。

### 快速开始（GDScript）

下面的布局和示例注册 `physics.list_bodies`；线路名使用点，客户端看到的 MCP 名称会把点转换成下划线。保存后切回编辑器或调用 `discover_tools(refresh_extensions:true)`。

```
addons/my_physics_tools/
└── physics_extension.gd   ← class_name PhysicsTools (any name works)
```

```gdscript
# addons/my_physics_tools/physics_extension.gd
@tool
class_name PhysicsTools
extends MCPToolkitExtension

func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
	registry.add("physics.list_bodies", _list_bodies,
		MCPToolkitExtensionOptions.new("List all physics bodies in the current scene")
			.with_input_schema({
				"type": "object",
				"properties": {
					"body_type": {
						"type": "string",
						"enum": ["rigid", "static", "character", "all"]
					}
				}
			})
			.mark_read_only()
			.mark_idempotent()
			.with_group("physics_tools", "Physics inspection and manipulation"))

func _list_bodies(params: Dictionary) -> Dictionary:
	var body_type: String = params.get("body_type", "all")
	# ... scene tree traversal logic ...
	return MCPToolkitSuccess.ok({"data": bodies})
```

### 快速开始（C#）

C# 文件名必须与类名一致，类名必须以 `MCPToolkit` 开头；构建后切回编辑器或调用 `discover_tools(refresh_extensions:true)`。

```
addons/my_dialogue_tools/
└── MCPToolkitDialogueTools.cs   ← [GlobalClass] MCPToolkitDialogueTools (file name matches class name)
```

```csharp
// addons/my_dialogue_tools/MCPToolkitDialogueTools.cs
using Godot;
using Godot.Collections;

[Tool, GlobalClass]
public partial class MCPToolkitDialogueTools : RefCounted
{
	public void Register(GodotObject registry, Node server)
	{
		var opts = registry.Call("create_extension_options",
			"List all dialogue nodes in the current scene").AsGodotObject();
		opts.Call("mark_read_only");
		opts.Call("mark_idempotent");
		registry.Call("add", "dialogue.list_nodes", new Callable(this,
			MethodName.ListNodes), opts);
	}

	public Dictionary ListNodes(Dictionary parameters)
	{
		// ... scene tree traversal logic ...
		return new Dictionary { { "success", true }, { "data", nodes } };
	}
}
```

### `registry.add()` API

线路注册接口是：

```
registry.add(method: String, handler: Callable, options: MCPToolkitCommandOptions)
```

工具描述（显示给 LLM）是必填项：

```gdscript
var opts = MCPToolkitExtensionOptions.new("Describe what your tool does")
```

扩展返回 Dictionary；使用 `MCPToolkitSuccess.ok()` / `MCPToolkitError.fail()` 保证响应契约。

**路径安全——保护每个 LLM 提供的路径。**如果工具接收 LLM 填写的路径，请验证它，使遍历 / 越界路径（`res://../../secret`、`/etc/passwd`、盘符、UNC 共享）无法到达文件操作。有两种方式：

- **声明式（推荐）：**在构建器上声明参数。分发器会验证它，并在处理器运行**之前**返回 `PATH_DENIED`。

  ```gdscript
  var opts = MCPToolkitExtensionOptions.new("Read a config file") \
      .mark_read_only() \
      .guard_project_path("file_path")   # res:// path parameter
      # .guard_user_path("slot")          # user:// path parameter
  ```

- **命令式：**声明式保护不适用时（例如参数确实接受**绝对**文件系统路径），在处理器中使用内置工具相同的保护进行验证：

  ```gdscript
  const FileGuard = preload("res://addons/godot_mcp_toolkit/security/file_guard.gd")
  var guard := FileGuard.resolve_safe(params.get("file_path", ""))   # res://, path-boundary checked
  if guard["error"] != null:
	  return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
  # user:// paths: FileGuard.resolve_safe_user(path) → {ok, error_code, error_message}
  ```

**包装返回的不可信内容。**只要工具返回的内容字节来自**自有代码之外**——读取的文件、项目 / 场景数据、回显的用户输入、外部工具输出——就包装该字段，使 LLM 将其视为数据而不是指令：

```gdscript
const Untrusted = preload("res://addons/godot_mcp_toolkit/security/untrusted.gd")

func _read_config(params: Dictionary) -> Dictionary:
    var text := FileAccess.get_file_as_string(params["file_path"])
    return {
        "success": true,
        # kind + source are labels; body is the untrusted text.
        # JSON.stringify(...) a Dictionary/Array body first.
        "content": Untrusted.wrap("config", params["file_path"], text),
    }
```

读取大型数据必须分页并遵守统一的 `has_more` / `total_<unit>` / `returned` / `next_<cursor>` 契约；以下示例展示字节窗口与 `offset`：

```gdscript
const CAP_BYTES := 256 * 1024   # better: read a live ProjectSettings limit here

func _read_blob(params: Dictionary) -> Dictionary:
	var offset: int = max(0, int(params.get("offset", 0)))
	var total := _blob_size(params)                       # full source size
	var want: int = clampi(int(params.get("max_bytes", CAP_BYTES)), 1, CAP_BYTES)
	if want > CAP_BYTES:                                   # window exceeds the cap
		return MCPToolkitError.fail("FILE_TOO_LARGE",
			"Requested window exceeds the %d-byte cap." % CAP_BYTES,
			"Lower max_bytes (cap %d) or page with offset." % CAP_BYTES)

	var chunk := _read_range(params, offset, want)        # at most `want` bytes
	var end := offset + chunk.size()
	var more := end < total
	var out := {
		"success": true,
		"returned": chunk.size(),
		"offset": offset,
		"total_bytes": total,        # total_<unit> — always present
		"has_more": more,            # has_more — always present
		"content": Untrusted.wrap("blob", str(params.get("path", "")), chunk),
	}
	if more:
		out["next_offset"] = end     # next_<cursor> — only when has_more
		out["hint"] = "more bytes remain — re-call with offset = next_offset (%d) until has_more is false" % end
	return out
```

## 安全保存场景、取消和并发

处理器不得直接调用 `EditorInterface.save_scene()`；使用 Toolkit 的可重入安全保存。GDScript 与 C# 的正确模式如下：

```gdscript
func _my_handler(params: Dictionary) -> Dictionary:
	# Waits out any EditorFileSystem scan, escapes the deferred context, and wraps
	# the synchronous save in a re-entrancy flag. path == "" → active scene.
	return await MCPToolkitSafeSceneOps.save_scene()
```

```csharp
// Start-and-poll. queue_save returns a save id immediately; the editor-safe
// save runs after your handler returns. path "" = active scene.
string id = (string)registry.Call("queue_save", path);
// ...poll until done (e.g. from a companion status tool the client calls):
var status = (Godot.Collections.Dictionary)registry.Call("check_save", id, /*clear*/ true);
// status = {done:bool, success:bool, result:{...}}  (or {done:false, unknown:true})
```

`with_group` 可使专用工具按需加载：

```gdscript
MCPToolkitExtensionOptions.new("List all physics bodies in the current scene")
	.with_group("physics_tools", "Physics inspection and manipulation")
```

长任务使用 `.mark_cancellable()`，处理器接收第二参数并观察 `cancelled` 信号或轮询 `ctx.is_cancelled()`：

```gdscript
registry.add("weather.fetch", _handle_weather,
	MCPToolkitExtensionOptions.new("Fetch weather data from external API")
		.with_timeout_ms(60000)
		.mark_cancellable())

func _handle_weather(params: Dictionary, ctx: MCPToolkitToolContext) -> Dictionary:
	# Reactive: connect a cleanup action to the cancelled signal.
	# If cancelled during the await, the HTTP request is aborted.
	ctx.cancelled.connect(_http_request.cancel_request)

	var result = await _fetch_api(params.query)

	# Polling: check between discrete steps.
	if ctx.is_cancelled():
		return {}

	var processed = await _process_result(result)
	if ctx.is_cancelled():
		return {}

	return MCPToolkitSuccess.ok({"data": processed})
```

```csharp
public void Register(GodotObject registry, Node server)
{
	var opts = registry.Call("create_extension_options",
		"Fetch weather data from external API").AsGodotObject();
	opts.Call("with_timeout_ms", 60000);
	opts.Call("mark_cancellable");
	registry.Call("add", "weather.fetch", new Callable(this,
		MethodName.HandleWeather), opts);
}

public Dictionary HandleWeather(Dictionary parameters, GodotObject ctx)
{
	// Reactive: connect the cancelled signal
	ctx.Connect("cancelled", Callable.From(OnCancelled));

	// ... do work ...

	// Polling: check is_cancelled()
	bool cancelled = (bool)ctx.Call("is_cancelled");
	if (cancelled) return new Dictionary();

	return new Dictionary { { "success", true }, { "data", result } };
}
```

### 使变更可撤销

用 `MCPToolkitUndoRedoAction` 将变更记录到正确的场景历史；`begin()` 的第二参数应是仍在场景树中的节点或父节点，headless 下 `is_active()` 会返回 false。

```gdscript
func _set_custom_prop(params: Dictionary) -> Dictionary:
	var node = get_tree().edited_scene_root.get_node(params.node_path)
	var old_val = node.get(params.property)
	node.set(params.property, params.value)

	MCPToolkitUndoRedoAction.begin(
		"set %s.%s" % [params.node_path, params.property], node) \
		.do_property(node, params.property, params.value) \
		.undo_property(node, params.property, old_val) \
		.commit_recorded()

	return MCPToolkitSuccess.ok()
```

```gdscript
func _create_marker(params: Dictionary) -> Dictionary:
	var parent = get_tree().edited_scene_root.get_node(params.parent_path)
	var root = get_tree().edited_scene_root
	var marker = Marker2D.new()
	marker.name = params.get("name", "Marker")
	parent.add_child(marker)
	marker.set_owner(root)

	MCPToolkitUndoRedoAction.begin("create %s" % marker.name, parent) \
		.do_method(parent.add_child.bind(marker)) \
		.do_method(marker.set_owner.bind(root)) \
		.do_reference(marker) \
		.undo_method(parent.remove_child.bind(marker)) \
		.commit_recorded()

	return MCPToolkitSuccess.ok({"data": {"node_path": str(marker.get_path())}})
```

```gdscript
var action = MCPToolkitUndoRedoAction.begin("expensive op", node)
if action.is_active():
	var snapshot = _capture_expensive_state()
	action.undo_method(Callable(self, "_restore_state").bind(snapshot))
_apply_mutation(params)
if action.is_active():
	action.do_method(Callable(self, "_apply_mutation").bind(params))
	action.commit_recorded()
```

```gdscript
# WRONG — missing context, routes to global history:
MCPToolkitUndoRedoAction.begin("add to group") \
	.do_method(node.add_to_group.bind("enemies")) \
	...

# CORRECT — node is in the scene tree, routes to scene history:
MCPToolkitUndoRedoAction.begin("add to group", node) \
	.do_method(node.add_to_group.bind("enemies")) \
	...
```

```gdscript
# WRONG — node already removed, resolves to global history:
parent.remove_child(node)
MCPToolkitUndoRedoAction.begin("delete node", node) ...

# CORRECT — parent is still in the tree:
parent.remove_child(node)
MCPToolkitUndoRedoAction.begin("delete node", parent) ...
```

```csharp
private GodotObject _registry;

public void Register(GodotObject registry, Node server)
{
	_registry = registry;
	var opts = registry.Call("create_extension_options",
		"Set custom property on a node").AsGodotObject();
	registry.Call("add", "custom.set_prop",
		new Callable(this, MethodName.SetCustomProp), opts);
}

public Dictionary SetCustomProp(Dictionary parameters)
{
	var nodePath = (string)parameters["node_path"];
	var prop = (string)parameters["property"];
	var node = GetTree().EditedSceneRoot.GetNode(nodePath);
	var oldVal = node.Get(prop);
	node.Set(prop, parameters["value"]);

	var action = _registry.Call("create_undo_action",
		$"set {nodePath}.{prop}", node).AsGodotObject();
	action.Call("do_property", node, prop, parameters["value"]);
	action.Call("undo_property", node, prop, oldVal);
	action.Call("commit_recorded");

	return new Dictionary { { "success", true } };
}
```

Godot 4.3 的 `editor_description` 有 tooltip 计时器问题，设置前调用辅助函数：

```gdscript
const Helpers := preload("res://addons/godot_mcp_toolkit/commands/editor_helpers.gd")

func _annotate(params: Dictionary) -> Dictionary:
	var node := _resolve(params)  # a node in the edited scene
	Helpers.disarm_tooltip_uaf(node, "editor_description")  # no-op off 4.3
	node.set("editor_description", str(params.get("text", "")))
	# ... register undo (see "Making mutations undoable" above) ...
	return MCPToolkitSuccess.ok({})
```

只读但具有不能与变更重叠的副作用的工具使用 `mark_exclusive_execution()`：

```gdscript
registry.add("physics.recalculate", _recalculate,
	MCPToolkitExtensionOptions.new("Recalculate all physics caches")
		.mark_read_only()
		.mark_exclusive_execution())
```

耗时等待必须让编辑器循环继续运行：

```gdscript
# BAD — freezes the editor:
while some_condition():
	OS.delay_msec(100)

# GOOD — editor stays responsive:
while some_condition():
	await Engine.get_main_loop().create_timer(0.1).timeout
```

## 无头兼容性

扩展在普通和 `godot --headless --editor` 中均可工作；文件、场景树、节点、资源、`ClassDB` 和项目设置操作不需要显示器。需要渲染视口、显示器上的游戏或原生 UI 的工具必须检测 headless；静默失败的工具应显式返回 `HEADLESS_UNSUPPORTED`：

```gdscript
func _screenshot_bodies(params: Dictionary) -> Dictionary:
	if DisplayServer.get_name() == "headless":
		return MCPToolkitError.fail("HEADLESS_UNSUPPORTED",
			"physics.screenshot_bodies needs a rendered viewport.",
			"Run the editor with a display (omit --headless) to use this tool.")
	# ... capture logic ...
	return MCPToolkitSuccess.ok({"data": image_data})
```

## 发现、命名和错误契约

未分组工具启动时可见，`.with_group()` 工具由 `discover_tools` 按需加载。命名使用 `<namespace>.<action>`；`scene.*`、`script.*`、`editor.*`、`node.*`、`runtime.*`、`server.*`、`resource.*`、`folder.*`、`file.*`、`signal.*`、`playtest.*`、`project.*`、`input_map.*`、`animation.*`、`tilemap.*`、`asset.*`、`save.*`、`meta.*`、`game.*`、`diff.*`、`autoload.*`、`extensions.*` 是保留命名空间。

成功响应至少包含 `success: true`：

```gdscript
return MCPToolkitSuccess.ok({"data": result_data})
# => {"data": result_data, "success": true}
```

错误响应包含 `success: false`、`error`、`code` 和可选 `hint`：

```gdscript
return MCPToolkitError.fail("NOT_FOUND", "Node not found",
	"Use scene.get_tree to list valid node paths.")
# Returns: {"success": false, "error": "Node not found", "code": "NOT_FOUND", "hint": "..."}
```

部分删除的结果仍应报告成功数与失败数，而不是丢弃信息：

```gdscript
return MCPToolkitSuccess.ok({
	"files_removed": 8,
	"files_failed": 2,
	"errors": ["res://locked.cfg: permission denied", "res://other.cfg: in use"]
})
```

`MCPToolkitError.fail()` 的代码必须来自 `MCPToolkitError.CODES`；`require()` 可生成带上下文提示的 `INVALID_PARAMS`：

```gdscript
# Structured error with explicit hint:
return MCPToolkitError.fail("NOT_FOUND", "Node not found",
	"Use scene.get_tree to list valid node paths.")

# Auto-hint — TIMEOUT gets its default hint automatically:
return MCPToolkitError.fail("TIMEOUT", "Editor busy")
# → includes hint: "The editor may be busy. Try editor.wait_for_idle before retrying."

# Parameter validation with auto-contextual hints:
var err = MCPToolkitError.require(params, ["node_path", "file_path"])
if err != null:
	return err  # hint auto-attached based on parameter name
```

```csharp
// Structured error:
var err = _registry.Call("fail", "NOT_FOUND", "Node missing",
	"Use scene.get_tree to find valid paths").AsGodotDictionary();

// Parameter validation:
var reqErr = _registry.Call("require", parameters,
	new Godot.Collections.Array { "node_path" });
if (reqErr.Obj != null) return reqErr.AsGodotDictionary();
```

成功提示可以在注册时设置，也可以由特定结果覆盖：

```gdscript
registry.add("physics.simulate", _simulate,
	MCPToolkitExtensionOptions.new("Run physics simulation step")
		.with_success_hint("Call physics.get_results to see the simulation output."))
```

```gdscript
func _simulate(params: Dictionary) -> Dictionary:
	var result = _run_simulation(params)
	if result.collisions > 0:
		return MCPToolkitSuccess.ok({"data": result,
			"hint": "%d collisions detected. Call physics.get_collisions for details." % result.collisions})
	return MCPToolkitSuccess.ok({"data": result})
	# ↑ Falls back to the registered with_success_hint() text
```

```csharp
var opts = registry.Call("create_extension_options",
	"Run physics simulation step").AsGodotObject();
opts.Call("with_success_hint",
	"Call physics.get_results to see the simulation output.");
```

## 分发、目录和热重载

简单扩展只需 `.gd` 与 README；不要求 `plugin.cfg`。README 应声明 `godot-mcp-unified` 依赖，并同时测试插件存在和缺失的情形。GDScript 缺失时会在解析阶段明确失败；C# 会编译但 `Register()` 不调用。扩展目录只读取随包本地 `extensions/catalog.json`，条目必须使用随附扩展目录下的 `local_path`，不接受仓库 URL。

热重载：GDScript 保存后切回编辑器或调用 `discover_tools(refresh_extensions:true)`；C# 先 `dotnet build`。Godot 4.2 编辑已有扩展需要重启（增删仍实时），4.3+ 增删改都实时。扩展可申请进入目录，完整字段要求见 `addons/godot_mcp_toolkit/docs/extending.md`。

缺少依赖时，提供包装插件可发出警告：

```gdscript
# Optional: plugin.cfg wrapper for active dependency detection
@tool
extends EditorPlugin

func _enter_tree() -> void:
	if not EditorInterface.is_plugin_enabled("godot_mcp_toolkit"):
		push_warning("MyExtension requires the Godot MCP Unified plugin. "
			+ "Install it from the Godot AssetLib (search 'Godot MCP Unified') "
			+ "or from GitHub: bundled local addon documentation/releases")
```

AssetLib 描述可使用：*“需要 Godot MCP Unified，可从 Godot AssetLib 或 GitHub Releases 安装。”*

## C# 检查示例、导出和限制

C# 扩展需 `dotnet build`，可以将构建 / 诊断包装成只读工具：

```csharp
[Tool, GlobalClass]
public partial class MCPToolkitCSharpCheck : RefCounted
{
	public void Register(GodotObject registry, Node server)
	{
		var opts = registry.Call("create_extension_options",
			"Run dotnet build and return C# diagnostics").AsGodotObject();
		opts.Call("mark_read_only");
		opts.Call("mark_idempotent");
		registry.Call("add", "csharp.check", new Callable(this,
			MethodName.CheckScript), opts);
	}

	public Dictionary CheckScript(Dictionary parameters)
	{
		// Shell out to dotnet build, parse MSBuild output,
		// return structured diagnostics
		// ...
	}
}
```

导出时 Toolkit 会剥离插件、扩展非脚本文件及 `res://.mcp.json`。Godot 4.3+ 的 binary-token 模式会先生成孤立 `.gdc`，可按 `references/exporting.md` 的说明使用 Text 模式或排除过滤器。C# 无法按类剥离，因此保护编辑器专用工作：

```csharp
public void Register(GodotObject registry, Node server)
{
	if (!Engine.IsEditorHint()) return; // defence-in-depth
	// ...
}
```

## 已知限制、Hooks、技能与 Resources

扩展 API 覆盖注册、发现、执行、错误、提示、取消、Undo/Redo 与热重载。当前限制包括：没有场景变更 / 文件保存生命周期钩子、没有持久化配置 API、长操作无进度流、扩展之间没有正式通信、Godot 4.2 会话内编辑已有扩展不生效。这些都有实际替代方案：连接 `EditorInterface` 信号、使用 `ProjectSettings` / `ConfigFile`、注册轮询状态工具、直接调用注册表命令，并在 4.2 重启编辑器。

TypeScript bridge 内部为每次工具调用运行前后钩子和审计日志。可复用工作流放在随附的 Codex 技能中，而不是 MCP prompts；这样程序性说明不会进入始终公布的协议面，每个技能也只需加载自己需要的工具组。服务器仍提供只读 resources（`godot://scene/{path}`、`godot://script/{path}`、`godot://project/info`、`godot://roots`），其提供器目前硬编码在 TypeScript 源码中。post-1.0 可沿用工具扩展模式开放 resource providers；新的可复用工作流通常应新增为技能。
