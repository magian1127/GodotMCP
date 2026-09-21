# 0002: daemon 的两个通信面——对 host 用 Streamable HTTP（DSH 走 stdio shim），对 Godot 保持 addon 为 WS server

daemon 对 MCP host 暴露 loopback Streamable HTTP（MCP 2026-07-28 的推荐路径）；stdio-only 的 host（DSH 的 mini client）通过 `godot-mcp-shim` 接入——shim 负责"确保 daemon 在跑 + stdio↔HTTP 转发"，同时承担 host 侧自举。对 Godot 方向维持现状：addon 仍是 WS server（编辑器 6550–6560、运行时 6570–6585），daemon 作为客户端经机器级注册表发现并主动连接、watch 增量扩连。GDScript addon 与 C1–C22 public contract 零改动。

## Considered Options

- 反转方向（daemon 为 WS listener，addon 连出注册）：更经典的 hub 形态，但需要重写 addon 传输层与认证方向，major bump 契约并迫使所有在跑的 Godot 升级插件——为形态纯度不值得。
- 全 stdio shim（所有 host 都经 shim）：adapter 配置改动最小，但每个会话多一跳转发进程，且放弃 HTTP 的多 host 直接复用。

## Consequences

- ZCode/Codex/VSCode 的 `.mcp.json` 需切换为 http 型（实现前先验证各 host 对 http 型 server 的支持度，不成立则全量退回 shim 方案）。
- daemon 侧需完整实现注册表消费（含 PID + 探针活性判定）、运行时通道与 LSP 通道——这是全量 parity 的必然要求。
