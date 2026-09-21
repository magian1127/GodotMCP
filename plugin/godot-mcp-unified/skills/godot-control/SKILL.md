---
name: godot-control
description: 通过 Godot MCP Unified 编辑器/运行时桥接控制和编辑 Godot 4 项目。适用于场景、节点、脚本、资源、项目设置、UI、2D/3D 创作或游戏开发变更。将已报告的失败转交 godot-debug，将游戏玩法验证转交 godot-playtest，将安装转交 godot-project-setup，将新的 MCP 工具转交 godot-mcp-extension。
---

语言：中文 | [英文版](SKILL.en.md)

# Godot 控制

通过 MCP 驱动实时编辑器，并让项目处于已验证状态。

## 确定目标

1. 在磁盘上定位目标 `project.godot`。将注册表条目视为发现线索，而不是项目仍然存在的证明。
2. 确认 `addons/godot_mcp_toolkit/plugin.cfg` 存在，并确认对应的编辑器实例已连接。任一条件缺失时，使用 godot-project-setup。
3. 在修改前读取当前场景/项目状态。保留无关的用户编辑和已打开场景的状态。

## 使用能力足够小的操作面

优先使用始终可见的工具。仅在当前工作阶段需要时，使用 `discover_tools(include_schemas=true)` 激活按需工具组，并记录本技能激活了哪些组。同时保持不超过 3 个组；只有跨领域复杂任务才可临时达到 5 个。阶段结束后通过 `discover_tools(reset=[...])` 释放本技能加载且下一阶段不再需要的组。在场景、资源、2D/3D、语言或运行时工具之间选择时，阅读 [tool-routing.md](references/tool-routing.md)。

- 相比 `execute_code`，优先使用专用工具。
- 使用 Codex 工作区文件工具读取和补丁式编辑脚本；随后加载 `editor_advanced`，调用 `editor_sync` 并运行 `script_check`。
- 对重复操作使用工具的批量输入，而不是进行许多次单独调用。
- 使用规范的 `res://` 路径传递项目文件，并显式保存已编辑的场景。
- 对创建操作要有意选择 `if_exists`。默认返回已有对象；只有请求的最终状态要求替换时才进行替换。
- 让工具包负责场景序列化和 UndoRedo。当编辑器正在管理某个场景时，不要手工编辑 `.tscn` 文件。

需要确定性占位纹理或声音时，阅读 [placeholder-assets.md](references/placeholder-assets.md)。用户要求编写测试时，阅读 [testing.md](references/testing.md)。

## 闭环验证

阅读 [verification.md](references/verification.md)，并根据变更规模进行相称的验证。只有在受影响的场景/资源已保存、变更后的脚本通过验证、项目可以运行或已明确说明无头环境限制，且观察到的编辑器/运行时状态符合请求时，普通创作变更才算完成。

对于破坏性操作、任意表达式、外部资源或项目设置变更，在调用前阅读 [security.md](references/security.md)。`unsafe` 组只有服务器以 `GODOT_MCP_UNSAFE=1` 启动时才可发现；该开关只用于用户明确授权且类型化工具无法表达的当前操作。
