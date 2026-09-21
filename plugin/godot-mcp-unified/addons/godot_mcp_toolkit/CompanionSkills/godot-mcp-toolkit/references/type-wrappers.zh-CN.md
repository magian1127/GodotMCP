[英文原文](type-wrappers.md)

# `node_set_property` 的类型包装器

`node_set_property`（及其 `batch` 形式）会自动强制转换普通标量：普通数字、字符串和布尔值**不需要**包装器。引擎类型必须作为 `{type: "...", ...}` 对象传入。未知的 `type` 标签会随支持标签列表一起被拒绝，因此拼写错误会明确失败，而不会静默处理。

## 标量和向量类型

| 类型标签 | 格式 | 示例 |
|----------|--------|---------|
| `Vector2` | `{x, y}` | `{type: "Vector2", x: 100, y: 200}` |
| `Vector3` | `{x, y, z}` | `{type: "Vector3", x: 1, y: 2, z: 3}` |
| `Vector4` | `{x, y, z, w}` | `{type: "Vector4", x: 1, y: 2, z: 3, w: 4}` |
| `Vector2i` | integer `{x, y}` | `{type: "Vector2i", x: 10, y: 20}` |
| `Vector3i` | integer `{x, y, z}` | `{type: "Vector3i", x: 1, y: 2, z: 3}` |
| `Color` | `{r, g, b, a}` (a defaults to 1.0) | `{type: "Color", r: 1, g: 0, b: 0}` |
| `Rect2` | `{x, y, w, h}` | `{type: "Rect2", x: 0, y: 0, w: 100, h: 50}` |
| `Rect2i` | integer `{x, y, w, h}` | `{type: "Rect2i", x: 0, y: 0, w: 64, h: 64}` |
| `NodePath` | `{path}` | `{type: "NodePath", path: "../Player"}` |

## 资源

| 类型标签 | 格式 | 说明 |
|----------|--------|-------|
| `Resource` | `{path}` | Bind an existing resource file: `{type: "Resource", path: "res://icon.png"}` |
| `NewResource` | `{class, properties}` | 构建内联子资源，例如形状：`{type: "NewResource", class: "CircleShape2D", properties: {radius: 16}}` |

## 变换

- `Transform2D`：`{type: "Transform2D", x_axis: {x, y}, y_axis: {x, y}, origin: {x, y}}`
- `Transform3D`：`{type: "Transform3D", x_axis: {x, y, z}, y_axis: {x, y, z}, z_axis: {x, y, z}, origin: {x, y, z}}`

## Packed 数组

元素值本身也需要包装：

- `PackedVector2Array`：`{type: "PackedVector2Array", values: [{type: "Vector2", x: 0, y: 0}, {type: "Vector2", x: 1, y: 1}]}`
- `PackedVector3Array`：`{type: "PackedVector3Array", values: [{type: "Vector3", x: 0, y: 0, z: 0}, ...]}`
- `PackedColorArray`：`{type: "PackedColorArray", values: [{type: "Color", r: 1, g: 0, b: 0}, ...]}`

## 层掩码

`LayerMask` 通过层**编号**或层**名称**设置碰撞 / 可见性位掩码（名称来自此前的 `layer_names_set` 调用）。`category` 默认为 `2d_physics`。

- 按编号：`{type: "LayerMask", category: "2d_physics", layers: [1, 3]}`
- 按名称：`{type: "LayerMask", category: "2d_physics", layers: ["player", "enemy"]}`

## 复合属性路径

- `/` 分隔子属性：`property: "position:x"` 设置一个分量。
- `:` 链接到子资源：`property: "material:shader_parameter/value"`。
- `make_unique: true`（单个条目或每个批处理条目）会在编辑前复制共享的外部 `.tres`，因此更改不会泄漏到该资源的其他使用者。

## 锚点注意事项

仅设置 `anchors_preset` 可能不会应用对应偏移。请显式设置 `anchor_*` / `offset_*` 各边，或使用 `control_set_layout`（一次调用设置锚点预设 + 可选边距，并返回最终矩形）。
