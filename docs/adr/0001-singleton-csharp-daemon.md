# 0001: 以机器级单例 C# daemon 取代每会话 Node 桥

MCP 接入点从"每个 host 会话拉起一个 stdio Node 进程、绑定单一项目"改为机器级单例的常驻 C# daemon（位于 `plugin/godot-mcp-unified/server-dotnet/`，基于官方 `ModelContextProtocol` SDK），多路复用所有会话与所有 Godot 实例：这是同时满足"多会话×多 Godot 不冲突、一个会话同时操作多个 Godot、Godot 主动拉起 MCP"三个目标的最小拓扑。Node 桥按替代线处理（adapters 逐个迁移后退役），第一期即全量对齐现有 83 工具/159 操作（含 `discover_tools` 的 31 个 group 按需激活）。并发安全沿用 Godot 实例侧既有的操作队列串行化与场景租约，daemon 不引入逻辑层互锁，仅以只读工具暴露队列与租约状态。

## Considered Options

- 每会话 C# 进程（等价重写现状）：无法支持一个会话操作多个 Godot，也无法由 Godot 侧拉起，进程数还会随会话数膨胀。

## Consequences

- daemon 自身需要单例保证与生命周期管理（后续 ADR 单独记录）。
- 首期全量 parity 使移植面变大，但消除了双桥并存期的 schema 双份维护。
