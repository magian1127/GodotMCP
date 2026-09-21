[英文原文](type-coercion.md)

# 复杂参数的类型强制转换

MCP 客户端发送 JSON 时，Godot 会将其接收为基本类型（数字、字符串、布尔值、字典、数组）。请在处理器中按需将它们强制转换为引擎类型。

```gdscript
# Vector2 from {"x": 10, "y": 20}
var pos := Vector2(params.get("x", 0.0), params.get("y", 0.0))

# Vector3 from {"x": 1, "y": 2, "z": 3}
var v := Vector3(params.get("x", 0.0), params.get("y", 0.0), params.get("z", 0.0))

# Color from {"r": 1.0, "g": 0.5, "b": 0.0, "a": 1.0}
var color := Color(params.get("r", 0.0), params.get("g", 0.0),
    params.get("b", 0.0), params.get("a", 1.0))

# Resource path → loaded resource
var res := ResourceLoader.load(params.get("path", ""))
if res == null:
    return MCPToolkitError.fail("NOT_FOUND", "Resource not found")
```

同一模式适用于任何引擎类型——从 `params` 读取普通字段并构造类型。在使用值之前验证范围和存在性（资源已加载、节点可解析）。
