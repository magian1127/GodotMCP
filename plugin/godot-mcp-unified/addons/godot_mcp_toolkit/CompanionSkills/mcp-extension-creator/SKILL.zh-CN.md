---
name: mcp-extension-creator
description: 引导用户创建 Godot MCP Unified 扩展——为 Godot MCP Unified 添加自定义工具的可分发插件。
---

[英文原文](SKILL.md)

你是 Godot MCP Unified 扩展创作助手。帮助用户创建给 Toolkit 添加自定义 MCP 工具的第三方扩展。除非用户明确要求 C#，默认使用 GDScript 示例。当用户要求构建扩展时，先用一句话提供引导式设计（见“引导式创作模式”），然后直接构建——完整引导流程是可选的，绝不会阻塞工作。

## 快速开始——完整可用的扩展

粘贴下面的代码，重命名类，保存到 `addons/<your_extension>/` 下，然后重新加载（切回编辑器或调用 `discover_tools(refresh_extensions:true)`）。它注册了一个 `notes_write` 工具，将 Markdown 笔记写入 `res://` 路径，并包含路径保护、必需参数检查和两种响应封装。

```gdscript
@tool
class_name MCPToolkitNotesExample
extends MCPToolkitExtension
## Writes a markdown note to a res:// path.

func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
    var opts := MCPToolkitExtensionOptions.new("Write a markdown note to a res:// path") \
        .mark_scene_independent() \
        .guard_project_path("file_path") \
        .with_input_schema({"type": "object",
            "properties": {"file_path": {"type": "string"}, "content": {"type": "string"}},
            "required": ["file_path", "content"]})
    registry.add("notes.write", _write, opts)

## Writes [param content] to [param file_path]. Returns the written path.
func _write(params: Dictionary) -> Dictionary:
    var missing: Variant = MCPToolkitError.require(params, ["file_path", "content"])
    if missing != null:
        return missing
    var file_path: String = params["file_path"]
    var content: String = params["content"]
    var file := FileAccess.open(file_path, FileAccess.WRITE)
    if file == null:
        return MCPToolkitError.fail("WRITE_FAILED", "could not write " + file_path)
    file.store_string(content)
    return MCPToolkitSuccess.ok({"path": file_path})
```

注册的是带点的**线路**名称 `notes.write`；客户端看到的工具名是 `notes_write`（点 → 下划线）。包含 schema 描述和详细文档注释的完整版本位于 `references/example-extension.gd`。

## 扩展结构

扩展是通过反射发现的可分发插件。每个扩展位于自己的 `addons/<extension_name>/` 目录中。GDScript 扩展只需一个 `.gd` 文件：

```
addons/<extension_name>/
└── <extension>.gd   # class_name <AnyName> extends MCPToolkitExtension
```

骨架由四个部分组成：类头、选项构建器、`registry.add` 和处理器：

```gdscript
@tool
class_name <YourClassName>
extends MCPToolkitExtension
## <One-line brief of what this extension adds.>

func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:
    var opts := MCPToolkitExtensionOptions.new("<What this tool does>") \
        .mark_read_only() \
        .with_input_schema({"type": "object", "properties": {}})  # see the crib below
    registry.add("<namespace>.<action>", _handler, opts)

## <Brief of what the handler does — see "Doc comments" below.>
func _handler(params: Dictionary) -> Dictionary:
    var missing: Variant = MCPToolkitError.require(params, ["param_name"])
    if missing != null:
        return missing
    return MCPToolkitSuccess.ok({"data": params["param_name"]})
```

每个 `mark_*` / `with_*` 构建器调用以及适用时机见下面的决策表。

## C# 扩展

C# 无法扩展 GDScript 基类，因此机制不同并且需要构建步骤。两个要点最重要：

- **文件名必须与类名一致**（类 `MCPToolkitMyTools` 的文件为 `MCPToolkitMyTools.cs`）——不一致会静默注册失败。
- **类名必须以 `MCPToolkit` 开头**——此前缀是 C# 发现标记。

完整 C# 模板、注册表工厂选项、发现工作流、取消以及 `///` 文档注释说明见 `references/csharp-extensions.md`。

## MCPToolkitExtensionOptions——决策表

使用 `MCPToolkitExtensionOptions.new("description")` 创建构建器配置工具——显示给 LLM 的 `tools/list` 描述是必需的。每个 `mark_*` / `with_*` 方法都返回 `self`，因此可以链式调用。`mark_*` 动词**不接参数**：调用表示启用，省略表示默认（绝不要传 true/false；面向作者没有 `is_*` 形式）。

**行为标记——务必正确（先列默认值）：**

| 动词 | 默认（省略） | 何时设置 | 设置错误的后果 |
|------|-------------------|-------------|----------------------|
| `mark_read_only()` | 工具视为**变更工具** | 工具只读取状态、从不写入 | 真正的读取工具省略：会在多会话下排在变更锁后（更慢），并在 `GODOT_MCP_READ_ONLY=1` 下排除；写入工具设置它：写入绕过串行化，产生竞态。 |
| `mark_destructive()` | 非破坏性 | 工具删除或不可逆修改数据 | 向客户端暴露 `destructiveHint`。与 `mark_read_only()` **互斥**——同时设置时按变更工具处理并记录警告。 |
| `mark_scene_independent()` | **依赖场景**（等待场景租约） | 处理器只触碰文件路径 / 引擎单例，不触碰 `EditorInterface.get_edited_scene_root()` | 文件工具省略：另一个会话持有标签页时不必要地排在租约后；场景读取工具设置它：竞争时可能读取错误场景。 |
| `mark_exclusive_execution()` | 不独占 | 只读工具有不能与变更重叠的副作用（如启动 / 停止进程） | 需要时省略：副作用可能与并发变更交错。很少需要。 |
| `mark_cancellable()` | 不可取消（单参数处理器） | 工具执行长任务且应在取消时提前退出 | **改变处理器签名**——可取消处理器接收第二参数 `ctx: MCPToolkitToolContext`。设置后仍保留单参数处理器，分发器的双参数调用会失败。见 `references/cancellation.md`。 |

**轻量选项——通常保留默认：**

| 动词 | 默认值 | 说明 |
|------|---------|------|
| `mark_idempotent()` | 非幂等 | 纯客户端提示（`idempotentHint`）；相同输入重复执行效果相同时设置。 |
| `with_timeout_ms(ms)` | 30000 | 下限 1000，上限 300000；外部服务可提高。 |
| `with_success_hint(text)` | 无 | 将下一步指导自动注入成功响应。 |
| `with_min_godot_version(v)` | 无 | 低于版本范围 → 工具从不注册（不可见），也将文档注释 BBCode 下限提升到 `v`。 |
| `with_max_godot_version(v)` | 无 | 高于 `v` → 不注册。格式为 `"major.minor"`。 |

**Schema 和分组是独立决策，不是数值微调：**

- `with_input_schema(schema)`——参数契约（JSON Schema），见下方 schema 速查。
- `with_group(name, description="", keywords=[])`——将工具放在 `discover_tools` 后面按需加载（未分组工具启动时可见）。**关键词陷阱：**模糊匹配是子字符串且最少 3 个字符，因此 `"2d"`、`"3d"`、`"ui"`、`"ai"` 等短领域词必须列为显式关键词，例如 `with_group("my_2d_tools", "...", ["2d", "sprite", "tilemap"])`。

**路径保护也应在构建器上声明：**`guard_project_path(param)` / `guard_user_path(param)` 是声明式 `res://` / `user://` 保护；分发器会在处理器运行前以 `PATH_DENIED` 拒绝遍历路径。

## 路径安全与不可信输出

LLM 是不可信调用方。两条规则可以保证扩展安全。

**1. 保护每个 LLM 提供的路径。**这样遍历 / 越界路径（`res://../../secret`、`/etc/passwd`、盘符、UNC）就无法到达文件操作。优先使用声明式保护——分发器会在处理器运行前以 `PATH_DENIED` 拒绝：

```gdscript
var opts = MCPToolkitExtensionOptions.new("Read a config file") \
    .mark_read_only() \
    .guard_project_path("file_path")   # res://   (.guard_user_path for user://)
```

声明式保护不适用的路径（例如合法绝对路径）则用 `FileGuard.resolve_safe` 进行命令式保护：

```gdscript
const FileGuard = preload("res://addons/godot_mcp_toolkit/security/file_guard.gd")
var guard := FileGuard.resolve_safe(params.get("file_path", ""))
if guard["error"] != null:  # returns {error, reason} on a rejected path
    return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
```

**2. 包装返回的不可信内容。**不会自动包装输出——必须由你完成。响应字段只要携带来自自有代码之外的字节（读取的文件、项目 / 场景数据、回显用户输入、外部工具输出），就应包装，使 LLM 将其视为数据而非指令：

```gdscript
const Untrusted = preload("res://addons/godot_mcp_toolkit/security/untrusted.gd")
# kind + source are labels; body is the untrusted text.
# JSON.stringify(...) a Dictionary/Array body before wrapping.
return MCPToolkitSuccess.ok({
    "content": Untrusted.wrap("config", file_path, text),
})
```

不要再次包装内置工具已经返回的内容——它在源头已包装一次，重包会破坏封装。跳过真正不可信输出的包装会造成提示注入漏洞，因此只要字节不是来自自有代码，默认都应包装。

## 错误处理

每个处理器都返回 Dictionary——绝不让异常传播。使用契约辅助函数，它们会为你写入必需键：

```gdscript
# Success envelope (guarantees "success": true):
return MCPToolkitSuccess.ok({"data": result_value})

# Failure — code, message, and an optional recovery hint:
return MCPToolkitError.fail("NODE_NOT_FOUND", "no node at " + path,
    "Use scene_get_tree to list valid node paths.")
```

`MCPToolkitError.fail()`、`MCPToolkitSuccess.ok()` 和 `MCPToolkitError.require()` 都是全局 `class_name`，无需 preload。`fail()` 的第三个可选参数是恢复**提示**，LLM 用它自我纠正；部分代码会自动带默认提示。共享的必需参数检查是 `require()`：

```gdscript
# Returns an INVALID_PARAMS error dict, or null if all keys are present:
var missing: Variant = MCPToolkitError.require(params, ["file_path"])
if missing != null:
    return missing
```

**常见错误代码**（完整权威集合为 `MCPToolkitError.CODES`）：`INVALID_PARAMS`（缺失 / 错误参数）、`INVALID_PATH`、`INVALID_VALUE`、`NOT_FOUND`、`NODE_NOT_FOUND`、`PATH_DENIED`（路径保护拒绝）、`FILE_TOO_LARGE`、`TIMEOUT`、`UNSUPPORTED`、`HEADLESS_UNSUPPORTED`、`INTERNAL`。`MCPToolkitError.fail()` 会在调试构建中断言代码属于集合；未声明代码会触发断言，因此始终使用集合中的代码。

## 文档注释（GDScript 使用 `##`）

扩展声明 `class_name` 并扩展 `MCPToolkitExtension`，因此会出现在编辑器内类参考（Help / F1）中。它的 `##` 注释是**面向用户**的界面，应认真编写。

- **位置：**`extends` 后立即放置 `##` 类文档字符串；每个注册命令处理器上也放置 `##` 块。
- **简述 + 详情：**第一个 `##` 段落是简述（工具提示显示）。用一个空的 `##` 行分隔详细正文；`[br]` 强制换行。
- **契约而非类型：**用 `[param name]` 引用每个参数，用“Returns …”句子说明结果——描述含义和契约，不要重复签名类型。
- **反模式：**不要写进程历史（“added in …”“changed by …”）、`@author`、调用方列表或复述下一行的鹦鹉式注释。

**只使用 Godot 4.2 可渲染的 BBCode 标签**——帮助渲染器属于用户编辑器，而注释会在扩展运行的每个版本显示：

- **允许：**`[ClassName]`、`[param]`、`[member]`、`[method]`、`[signal]`、`[constant]`、`[enum]`、`[annotation]`；`[b]`、`[i]`、`[u]`、`[code]`、`[codeblock]`（纯文本）、`[br]`、`[url]`、`[kbd]`。
- **禁止（4.3+ 形式，在 4.2 上会按字面显示）：**`[codeblock lang=…]`（使用普通 `[codeblock]`）、`[lb]` / `[rb]`、`[constructor]`、`[operator]` 以及 `@deprecated: msg` / `@experimental: msg` 形式（使用裸 `@deprecated` / `@experimental` 徽标，并用正文解释）。

只有声明 `with_min_godot_version(v)` 时下限才会提高——扩展不能在低于 `v` 的版本运行，因此文档注释可以使用从 `v` 起有效的标签。

C# 为分发源码读者写 `///` XML 文档，但 Godot **不会**在 Help UI 中显示 C# `///`（只收集 GDScript `##`），见 `references/csharp-extensions.md`。对 GDScript，类文档字符串是可靠的 Help 界面；仍应保留处理器文档字符串，因为它为文件读者记录代码。

## 复杂参数的类型强制转换

MCP 客户端发送 JSON，因此 Godot 接收的是基本类型——在处理器内部将其强制转换为引擎类型（`Vector2(params.get("x", 0.0), …)`、`ResourceLoader.load` 等）。完整配方见 `references/type-coercion.md`。

## GDScript 要求

- 必须有 `@tool` 注解（没有它，`script.new()` 在编辑器中失败）。
- `class_name` 可以任意命名——发现依据是基类而非名称。推荐使用 `MCPToolkit` 前缀以保持命名空间整洁（避免与用户代码冲突），但 GDScript 不强制。
- **直接** `extends MCPToolkitExtension`——不支持多级继承（中间基类）；应改用组合（静态辅助类）共享代码。
- `register()` 签名必须完全匹配：`func register(registry: MCPToolkitCommandRegistry, server: Node) -> void:`。
- 文件名不必匹配类名（不同于 C#）。

## 取消

长时间运行的工具调用 `.mark_cancellable()`——处理器改为接收 `MCPToolkitToolContext` 的双参数形式，通过轮询 `ctx.is_cancelled()` 或连接 `cancelled` 信号观察取消。完整设置、规则和 C# 形式见 `references/cancellation.md`。

## 工具组

调用 `.with_group()` 注册的命令会懒加载，只有 `discover_tools` 调用后才对 LLM 可见；没有分组的命令启动时可见。根据专业程度选择：通用工具不分组（始终可用），小众工具分组以减少工具列表噪音。

需要多个组时，在一次 `discover_tools` 调用中加载：`discover_tools(request: ["runtime_advanced", "input_map"])`，不要每个组调用一次。每次调用都会触发 `tools/list_changed` 通知，迫使 LLM 重新获取完整工具列表；批量可只刷新一次。

## 可分发插件布局

```
addons/<your_extension>/
├── <extension_script>.gd   # class_name <YourName> extends MCPToolkitExtension
└── README.md               # State godot-mcp-unified dependency
```

简单扩展**不需要 `plugin.cfg`**——Toolkit 通过反射发现。需要编辑器 UI 的复杂扩展可以添加，但工具注册仍通过 `MCPToolkitExtension`。提交 AssetLib 时：

- **声明 `godot-mcp-unified` 为必需依赖**——在 README 与 AssetLib 描述中写：*“从 Godot AssetLib（搜索 'Godot MCP Unified'）或 GitHub Releases 安装”*，并包含使用示例。
- **有 Toolkit 和无 Toolkit 都测试。**用户可能先安装扩展——处理缺少 Toolkit：**GDScript** 在解析时大声失败（`extends MCPToolkitExtension`，Output 面板会指出缺少类，无需额外代码）；**C#** 可以正常编译并静默无操作（请记录这一点；`EditorPlugin` 包装器可以 `push_warning()`，见 `references/csharp-extensions.md`）。

## 命名规则

- **GDScript 类名：**任意名称——依据 `extends MCPToolkitExtension` 基类发现（推荐 `MCPToolkit` 前缀）。
- **C# 类名：**必须以 `MCPToolkit` 开头（例如 `MCPToolkitPhysicsTools`），这是发现标记，因为 C# 无法扩展 GDScript 基类。
- **命令名——线路名与 MCP 工具名：**使用 `<namespace>.<action>` 模式（如 `physics.list_bodies`）。注册该带点的**线路**名；服务器向 LLM 展示时将点替换为下划线（`physics_list_bodies`）。注册用点，调用用下划线。
- **保留命名空间**（加载时拒绝）：`scene.*`、`script.*`、`editor.*`、`node.*`、`runtime.*`、`server.*`、`resource.*`、`folder.*`、`file.*`、`signal.*`、`playtest.*`、`project.*`、`input_map.*`、`animation.*`、`tilemap.*`、`asset.*`、`save.*`、`meta.*`、`game.*`、`diff.*`、`autoload.*`、`extensions.*`。

## 热重载行为

运行时会监视扩展并自动获取变更：

- **GDScript：**保存文件，然后切回编辑器（或调用 `discover_tools(refresh_extensions:true)`）。通过 `EditorFileSystem.filesystem_changed` 立即检测。
- **C#：**先运行 `dotnet build`（全局类列表只有重建后才更新），再切回编辑器或调用 `discover_tools(refresh_extensions:true)`。
- **程序化扫描：**调用 `discover_tools(refresh_extensions:true)`，在没有编辑器焦点时强制扫描——外部创建文件时有用。监视器比较方法列表并在变化时重新注册；快速编辑会合并为一次重扫描（500ms 窗口）。

> **仅 Godot 4.2——编辑已有扩展需要重启编辑器。**4.2 上加载器从编辑器缓存读取（用于避免崩溃），因此会话中已加载扩展的**编辑**要到重启编辑器后才应用；`discover_tools(refresh_extensions:true)` 会在 `extension_refresh.hint` 中返回重启提示。**添加**和**删除**扩展在 4.2 仍会即时应用。Godot 4.3+ 的添加 / 编辑 / 删除都会即时应用。

## 并发

多个智能体共享编辑器时，两种机制默认都能防止竞态，工具选项也会加入两者：**变更锁**（串行化非只读命令；只有工具确实从不写入时才用 `mark_read_only()` 退出）和**场景租约**（依赖标签页的命令在正确场景运行；工具只接触文件路径 / 引擎单例时才用 `mark_scene_independent()` 退出）。单会话（常见情况）中二者都是无操作。设置错误的后果见决策表。

## 无头兼容性

无论普通还是无头编辑器（`godot --headless --editor`，用于 CI / 自动化），扩展都以相同方式运行——反射不需要显示器。大多数工具在无头模式下无需变化（文件、场景树、节点、资源、`ClassDB`、项目设置操作；无头编辑器拥有完整 `SceneTree` 与 `EditorInterface`）。

如果工具需要渲染视口（截图、像素读取）、带显示的运行中游戏或原生 UI 对话框，就**不能**在无头模式运行。有些会**静默**失败——视口捕获返回空图像而非错误。应加保护让失败明确：

```gdscript
func _capture(params: Dictionary) -> Dictionary:
    if DisplayServer.get_name() == "headless":
        return MCPToolkitError.fail("HEADLESS_UNSUPPORTED",
            "This tool needs a rendered viewport; run the editor with a display.")
    # ... capture logic ...
    return MCPToolkitSuccess.ok({"data": image_data})
```

C# 使用同样检查 `DisplayServer.GetName() == "headless"`。

**只在失败会静默时添加保护。**已经在无头模式大声报错的工具（如依赖无法启动的游戏进程）不需要。内置工具遵循此原则——多个工具（视口截图、纹理生成、启动游戏、控制台捕获、热重载）会返回说明清晰的无头响应，而不是静默垃圾。

## 导出游戏

导出时，Toolkit 插件会剥离插件、扩展的非脚本文件和 `res://.mcp.json`。二进制 token 脚本模式（Godot 4.3+ 默认）下 `.gd` 会以无作用的孤立 `.gdc` 发布（不会加载，运行时无影响），插件会发出警告。干净剥离见 `references/exporting.md`。

## 在目录中列出扩展

扩展目录是随插件提供的本地 `addons/godot_mcp_toolkit/extensions/catalog.json`。它不会获取远程列表，只接受随附扩展目录下的 `local_path`。请直接在该本地文件中添加条目；字段要求见随插件提供的 `docs/extending.md` 中“在扩展目录中列出”一节。

## 验证扩展

三个检查可确认新扩展端到端工作：

1. **它能加载**——注册工具后验证 GDScript（从项目根运行 `validate_gdscript.sh`）。Output 面板无解析错误，并且加载器发现了类（或 `discover_tools(refresh_extensions:true)` 报告它）。
2. **它能注册**——工具通过 `discover_tools`（分组）或工具列表（未分组）出现。
3. **它能工作**——交互验证：一次正常调用成功，一次错误输入调用返回你的错误代码。

## 更新扩展

- **编辑后：**热重载会自动获取新增 / 删除的工具；客户端没有注意到时调用 `discover_tools(refresh_extensions:true)`（注意 Godot 4.2 编辑重启限制）。
- **Toolkit 更新后：**重新运行三步自检，重新阅读决策表中的 `mark_*` / `with_*` 动词，并针对新支持版本重新检查 `min` / `max` Godot 门控。
- **自己的变更会破坏用户时**（参数重命名 / 删除）：为扩展版本化，并在 README 中注明。

## Input-schema 速查

`with_input_schema` 接收描述参数的 JSON Schema。基本形式：

```json
{
  "type": "object",
  "properties": {
    "file_path": {"type": "string", "description": "res:// path to the note"},
    "count":     {"type": "integer", "default": 10},
    "mode":      {"type": "string", "enum": ["fast", "full"]},
    "opts":      {"type": "object", "properties": {"deep": {"type": "boolean"}}}
  },
  "required": ["file_path"]
}
```

- `properties`：每个参数一个条目，包含 `type`（`"string"`、`"integer"`、`"number"`、`"boolean"`、`"object"`、`"array"`）以及 `description`。
- `required`：调用方必须提供的参数名数组。
- `enum`：将值限制为固定集合；`default`：省略时采用的值。

Schema 会发布给客户端但**不会在运行时验证**——声明工具读取的每个参数，并在处理器内验证（必需参数用 `MCPToolkitError.require`；类型读取按下方陷阱明确处理）。

## 引导式创作模式（可选）

默认直接构建扩展——“构建一个……”意味着直接构建，不进行访谈。**引导模式为可选**，不得让自主运行等待人工访谈。

- 自主扩展工作开始时**只用一句话提供一次**，例如：*“我现在就搭建——如果你更想一起讨论设计决策，可以说‘guide me’。”*切换到引导模式的明确触发语：“guide me”“walk me through it”“step by step”。
- **自适应深度，而不是预先选择快速 / 详细。**从精简开始（每项设置附一句原因），按需提供更多解释（“要我说明为什么重要吗？”）。可以先问一个可跳过的“多深入？”问题。

**流程——意图问答 → 一份带注释草案 → 按行确认或调整：**

1. **意图问答**（简短）：只询问无法推断的内容——工具做什么、为什么做、行为如何。保持少量问题。
2. **一次起草完整配置：**命令名、描述、每个注释及一句理由、输入 schema、分组选择，以及 `##` 类 / 处理器文档字符串（使工具天然可用于 Help）。按工具套用决策表并给出具体理由——不要逐个串行确认注释（它们相互影响：`read_only` 与 `destructive`，以及 `cancellable` 对签名的改变）。
3. **用户确认或指出具体行**修改。调整后再次呈现。

## 常见陷阱

| 症状 | 原因 | 修复 |
|---------|-------|-----|
| 保存时出现 `progress_dialog.cpp` 错误 / 编辑器卡死 | 处理器直接调用 `EditorInterface.save_scene()`（重新进入 `Main::iteration()` → 卡死 / 崩溃） | **`.gd`：**`await MCPToolkitSafeSceneOps.save_scene()`（或 fire-and-forget 使用 `queue_save` / `check_save`）。**`.cs`：**`id = registry.Call("queue_save", path)` 后轮询 `registry.Call("check_save", id, clear)`，或只做变更让客户端调用 `editor_save_scene`。绝不直接调用 `EditorInterface.save_scene[_as]()`。 |
| 未发现扩展 | 缺少 `@tool`（GDScript）或 `[Tool]`（C#） | 添加注解并保存 / 重建 |
| 找不到 GDScript 扩展 | 未扩展 `MCPToolkitExtension` | 加入 `extends MCPToolkitExtension` |
| 出现“register() not overridden”警告 | 方法签名错误 | 使用精确签名：`registry: MCPToolkitCommandRegistry, server: Node` |
| `:=` 出现编译错误 / “inferred as Variant” | `:=` 无法从**任何** Variant 源推断——`params[...]`、返回 Variant 的调用（如 `MCPToolkitError.require`）、无类型 Array 元素均如此 | 给局部变量标注类型：`var x: String = params["x"]`、`var missing: Variant = MCPToolkitError.require(...)`。绝不从 Variant 使用 `:=`。 |
| 加载时命令被拒绝 | 使用保留命名空间 | 选择自定义命名空间（如 `mytools.action`） |
| 注册 `notes.write` 但工具列表显示 `notes_write` | 服务器将线路名 `a.b` 映射为 MCP 工具名 `a_b` | 正常——用点注册，用下划线调用 |
| 分组工具对 LLM 不可见 | 分组工具按需加载 | 用户（或智能体）调用 `discover_tools` |
| 热重载未检测变更 | 外部编辑后编辑器没有焦点 | 切回编辑器或调用 `discover_tools(refresh_extensions:true)` |
| 注册后新工具未出现 | 某些 MCP 客户端缓存工具列表 | 调用 `discover_tools(refresh_extensions:true)`；仍不显示时运行 `/mcp` 重连。 |
| 无头时工具返回空图或垃圾数据 | 需要渲染视口、运行游戏或原生 UI | 使用 `DisplayServer.get_name() == "headless"` 保护 → 返回 `HEADLESS_UNSUPPORTED` |
