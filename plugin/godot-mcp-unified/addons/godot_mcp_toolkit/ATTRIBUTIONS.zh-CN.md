[英文原文](ATTRIBUTIONS.md)

# 致谢（godot-mcp-toolkit）

此插件的 GDScript 源码为独立编写。本插件没有逐字或近乎逐字复制任何参考仓库的代码。下列条目涵盖本项目制作的原创美术，以及我们在设计插件时研究过的架构模式。

如果未来版本导入或改编下列任何来源的代码，将追加一行“Copied into: …”并列出文件路径，以保持本文件准确。

---

## 排除上游美术资产

上游项目的 logo 和主视觉横幅由 **Jessica Mariana Aisen** 创作，未授权复用。Godot MCP Unified 不再分发这些品牌资产；当前使用的几何 MCP 图标由本仓库自行创建，没有复用上游美术。

---

## Coding-Solo/godot-mcp

来源：<https://github.com/Coding-Solo/godot-mcp>
许可证：MIT

Copyright (c) 2025 Solomon Elias

贡献内容（仅作架构参考——未复制代码）：内置 GDScript 处理器模式（编辑器插件加载并运行命令处理器注册表）、跨平台 Godot 自动检测方案，以及调试输出捕获策略。

---

## tugcantopaloglu/godot-mcp

来源：<https://github.com/tugcantopaloglu/godot-mcp>
许可证：MIT

Copyright (c) 2025 Tugcan Topaloglu
Copyright (c) 2025 Solomon Elias

贡献内容（仅作架构参考——未复制代码）：代码执行模式（支持任意 GDScript 执行、返回值与 `await`）、信号管理系统概念（带超时的 connect/disconnect/emit/await）、通过 `get_property_list()` 检查通用节点属性，以及防止并发命令重入的保护模式。

---

## salvo10f/godotiq

来源：<https://github.com/salvo10f/godotiq>
许可证：MIT

Copyright (c) 2026 GodotIQ

贡献内容（仅作架构参考——未复制代码）：三层场景解析器架构（raw → resolved → indexed）、空间智能工具概念（`scene_map`、放置、空间审计）、token 优化方案（brief / normal / full detail 级别）、用于运行时访问的 `EngineDebugger` IPC 模式，以及 **用于编辑器安全删除节点的 UndoRedo + `add_undo_reference` 模式**（我们的场景删除处理器采用同一模式，代码则为独立编写）。

---

## youichi-uda/godot-mcp-pro（仅 GDScript 插件）

来源：<https://github.com/youichi-uda/godot-mcp-pro> — 仅限 `addons/godot_mcp/`。
许可证：MIT（插件组件）；TypeScript 服务器组件单独授权，本文未引用或复现。

Copyright (c) 2026 Youichi Uda (y1uda)

贡献内容（仅作架构参考——未复制代码）：WebSocket 桥接架构（Godot 编辑器插件 ↔ 外部 Node.js 进程）、基于 WebSocket 的 JSON-RPC 2.0 协议设计、UndoRedo 集成方案（参见 salvo10f/godotiq 条目；两个项目采用了相同模式）、此类插件通常使用的标准 MCP 端口 `6505`，以及 **通过 base64 + mime_type 内联返回截图字节**（我们的截图处理器采用相同结构，代码为独立编写）。

---

## ee0pdt/Godot-MCP

来源：<https://github.com/ee0pdt/Godot-MCP>
许可证：MIT

Copyright (c) 2025 (author unnamed in LICENSE)

贡献内容（仅作架构参考——未复制代码）：双层插件 + 外部服务器架构的结构参考。

---

## tomyud1/godot-mcp

来源：<https://github.com/tomyud1/godot-mcp>
许可证：MIT

Copyright (c) 2025-2026 Tomer Yud

贡献内容（仅作架构参考——未复制代码）：MCP 服务器 + Godot 插件集成的参考实现。

---

## rayxuln/hastur-operation-plugin

来源：<https://github.com/rayxuln/hastur-operation-plugin>
许可证：MIT

Copyright (c) 2026 Raiix

贡献内容（仅作架构参考——未复制代码）：GDScript 片段执行模式（将用户代码包装在 `@tool extends RefCounted` 中并调用 `execute(context)`），以及 broker-relay 架构参考。

---

## AndreaTerenz/WebSocket

来源：<https://github.com/AndreaTerenz/WebSocket>
许可证：MIT

Copyright (c) 2023 Andrea Terenziani

贡献内容（仅作架构参考——未复制代码）：Godot 4 `WebSocketPeer` 包装器模式。

---

## Delsin-Yu/GDEditorBridge

来源：<https://github.com/Delsin-Yu/GDEditorBridge>
许可证：MIT

复制到（Copied into）：`probe/McpToolkitAlcProbe.cs`（改编——重命名了 producer、
标记路径与日志前缀；AlcId/ISerializationListener 的标记机制是
`GdEditorBridgeAlcProbe.cs` 的近乎逐字移植）。

贡献内容（架构参考——重新实现，未复制代码）：C# 构建成功语义（对 exit 0 的
怀疑、引擎实际加载位 DLL 检查、mtime 未变的 unchanged 结论）、毫秒级标记比较
与"重载进同一 ALC"真失败规则的 ALC 七态判定机
（`commands/editor/editor_csharp_build.gd`）、4.7.x 上 `BaseButton.press()`
不暴露给脚本所要求的按类型按键分发（`commands/editor/editor_dialogs.gd`）、
编辑器额外窗口的枚举形状，以及反向依赖倒排索引思路
（`commands/asset_dependents.gd`）。

---

## 说明

MIT 仅要求为直接复制或实质性复现的代码保留声明。上述仓库的代码没有复制到本插件中——这些条目是出于礼貌而记录的架构研究致谢。

配套 npm 包（`@npgamedev/godot-mcp-server`）有自己的 `ATTRIBUTIONS.md`，其中包含与桥接器 / Node.js 侧相关的参考资料子集。
