# 0002: daemon 的通信面——宿主统一 stdio shim，Godot 保持 WebSocket

中文 | [English](0002-daemon-listening-faces.en.md)

## 当前决策

所有宿主通过 stdio shim 接入；daemon 的 loopback Streamable HTTP 是 shim 后端。shim 负责自举、读取稳定 token 和携带会话键。Godot addon 仍是 WebSocket server，daemon 按机器级注册表发现并主动连接。编辑器默认扫描 6550–6560，运行时扫描 6570–6585。

SDK 基线为 ModelContextProtocol 2.2.0，支持无状态核心和 legacy 握手兼容。宿主与 shim 的协商版本不等于 listen 的信封版本；当前 listen 使用 2026-07-28。具体转发与未完成的通知适配见 [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.md)。

## 曾考虑的方案

- 反转 Godot 连接方向，使 addon 连出到 daemon：需要重写传输、鉴权和升级契约，未采用。
- 宿主直接 HTTP：少一跳 shim，但宿主不能可靠地自行拉起本机 daemon，且漏会话头会只得到入口工具。
- 所有宿主经 shim：增加每会话一个轻量进程，换取统一启动、会话键与本机凭据读取；当前采用。

## 修订与结果

最初为 Codex/ZCode/VS Code 选择 HTTP、仅为 stdio-only 宿主用 shim；2026-09-23 已由 [0005](0005-host-entry-stdio-shim.md) 改成统一 shim。旧 HTTP 条目应在升级时清理。此决策不改变 addon 的 WS 方向和公开命令契约。
