# 0007: 工具面按会话键分级

中文 | [English](0007-tool-face-per-session.en.md)

## 决策

未连接仅暴露 godot_connect；连接后是 core 9 与本键激活组的并集。目录共有 17 组；成员、计数与调用规则以 [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.md) 和 GroupCatalogue 源码为准，扩展另计。组内有意重叠，按并集重算可见集。

每个请求组装自己的 ToolCollection。34 个 C# 工具对象照常注册，按可见集裁剪；按需对象跨请求复用。tools/call 与 tools/list 使用同一可见规则，未命中由 CallToolHandler 返回 isError 原因。共享对象不按会话改写 description/schema；统一协议 schema 的幂等覆盖允许。

## 曾考虑的方案

- 把常驻工具全部包成 deferred eager：会丢失 LSP、构建等待和复合路由的实现，未采用。
- 只裁剪 list：隐藏工具仍可命中调用，未采用。
- 用调用过滤器再实现一套判定：可覆盖命中调用，但重复可见集逻辑，未采用。
- 按使用率删掉长尾：低频不等于不需要，以按需组保留能力。

EagerRegisteredToolNames 已扩为 25 个，用于避免同名影子实现，不代表永远可见。当前常驻 schema 回查尚有缺口。

## 缓存、开关与交付状态

列表入口固定第一，其余按协议表序。ttlMs=0、cacheScope=private 表达即时/私有目录意图，不能证明每个宿主自动重拉。

设计中的 debug 是“无 header 时全量且无状态”，eager 是“旧全局组行为，保留 version/unsafe 门控”。当前 debug 仅 34 个、非法头与开关优先级有偏差，eager 仍可能按键分桶；它们未达到设计矩阵验收。legacy 内部通知及重连已补齐；现代宿主重订阅、组替换与增量重放仍有已复现缺口。完整状态集中维护在 daemon README，不能将方案表当作当前运行事实。
