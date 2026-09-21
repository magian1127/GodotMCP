[英文原文](csharp-extensions.md)

# C# 扩展

C# 扩展与 GDScript 扩展提供相同工具，但 C# 无法扩展 GDScript 基类，因此机制有所不同。除非项目是 .NET 项目，否则优先使用 GDScript——C# 需要先构建，Toolkit 才能看到工具。

## 两个容易踩坑的要点（主技能中也有说明）

1. **文件名必须与类名一致**——类 `MCPToolkitMyTools` 的文件应为 `MCPToolkitMyTools.cs`。只有名称一致时，Godot 的源生成器才会生成脚本元数据；名称不一致会静默导致注册失败。
2. **类名必须以 `MCPToolkit` 开头**——此前缀是 C# 发现标记（C# 无法扩展 `MCPToolkitExtension`，因此加载器通过 `[GlobalClass]` 类上的此前缀识别扩展）。

## 模板

C# 直接使用 `RefCounted` 并采用鸭子类型。选项构建器从注册表工厂获取（C# 无法实例化 GDScript 构建器类）。

```csharp
using Godot;
using Godot.Collections;

[Tool, GlobalClass]
public partial class MCPToolkitNotesTools : RefCounted
{
    /// Registers this extension's tools. Called once at load.
    public void Register(GodotObject registry, Node server)
    {
        var opts = registry.Call("create_extension_options",
            "Write a markdown note to a res:// path").AsGodotObject();
        opts.Call("guard_project_path", "file_path");
        registry.Call("add", "notes.write",
            new Callable(this, MethodName.Write), opts);
    }

    /// Writes a note. Returns a success envelope or an error dict.
    public Dictionary Write(Dictionary parameters)
    {
        // Parameter extraction, validation, and business logic.
        // Return the same {"success": bool, ...} envelope as GDScript.
        return new Dictionary { { "success", true }, { "data", "written" } };
    }
}
```

## 要求

- `[Tool]` 属性是必需的——没有它，.NET 对象不会在编辑器中实例化（方法调用会返回 null）。
- `[GlobalClass]` 属性是必需的——它让类出现在 `ProjectSettings.get_global_class_list()` 中。
- 扩展 `RefCounted` 的 `partial class`（不是 `MCPToolkitExtension`）。
- 公共的 `Register(GodotObject registry, Node server)` 方法。
- 处理器方法接收并返回 `Godot.Collections.Dictionary`。
- Callable：`new Callable(this, MethodName.Method)` 或 `Callable.From<Dictionary, Dictionary>(Method)`。

## 从注册表工厂获取选项构建器

C# 无法直接构造 GDScript 构建器，因此调用工厂并按名称驱动。每个 `mark_*` / `with_*` 动词的行为都与 GDScript 相同。

```csharp
var opts = registry.Call("create_extension_options",
    "What this tool does").AsGodotObject();
opts.Call("mark_read_only");
opts.Call("mark_idempotent");
registry.Call("add", "namespace.action", callable, opts);
```

## 发现工作流（C# 需要构建）

1. 从项目根目录运行 `dotnet build`（或在编辑器中点击 **Build**）。
2. 这会生成程序集并更新 `global_script_class_cache.cfg`。
3. 切回编辑器（会触发文件系统扫描），或调用 `discover_tools(refresh_extensions:true)`。

无需重启编辑器——热重载监视器会在重新构建后检测到类变更。如果扩展仍未出现：确认文件名与类名一致，确认存在 `[Tool]` 与 `[GlobalClass]`，并检查 `.godot/global_script_class_cache.cfg` 中是否出现该类。

## 取消（C#）

上下文作为第二个 `GodotObject` 参数传入，并按名称驱动。

```csharp
public Dictionary HandleFetch(Dictionary parameters, GodotObject ctx)
{
    ctx.Connect("cancelled", Callable.From(OnCancelled));
    // ... work ...
    if ((bool)ctx.Call("is_cancelled")) return new Dictionary();
    return new Dictionary { { "success", true }, { "data", result } };
}
```

不要保存上下文——它只属于一次调用。

## 无头模式

与 GDScript 相同，通过 `DisplayServer.GetName() == "headless"` 进行保护。

## 导出

C# 扩展会编译进项目的 .NET 程序集，无法按类剥离。使用 `if (!Engine.IsEditorHint()) return;` 保护仅编辑器工作，确保构建中不会运行任何内容。

## 文档注释（`///`）

在类和每个已注册处理器上编写 `///` XML 文档注释——对阅读你所分发源码的人很有价值。但请注意，Godot **不会**将 C# `///` 收集到编辑器帮助（F1）页面；只有 GDScript `##` 文档注释会在那里显示。因此，`///` 是为源码读者记录代码，不会像 `##` 那样生成编辑器内的参考条目。

## 优雅处理缺少 Toolkit

C# 扩展扩展的是 `RefCounted`，因此即使缺少 Toolkit 也能正常编译——`Register()` 只是不被调用（静默无操作）。对于 `EditorPlugin` 包装器，在 `_enter_tree()` 中检查 `EditorInterface.is_plugin_enabled("godot_mcp_toolkit")`，并通过 `push_warning()` 给出安装说明（在 Godot AssetLib 中搜索 “Godot MCP Unified”）。无论如何，都应在 README 中显著说明该依赖。
