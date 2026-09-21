# 0004: daemon 按需拉起、单例与本地安全面

daemon 以机器级单例锁防多开，默认固定监听 loopback 6590（env 可覆盖），host 侧地址因此稳定。由 host 侧 shim 或 Godot addon 任一方在探活失败时拉起；所有 Godot 实例与所有 host 连接全部断开后 idle 10 分钟退出（对齐 DSH 空闲自杀先例）。HTTP 面采用机器级稳定 token：首次生成后存于注册表目录、跨重启保持稳定（否则 host 的静态 Authorization 配置会因 daemon 重启而断），安装脚本统一为宿主侧用户级配置（ZCode 用户级 config 的 Authorization 头、Codex 环境变量）与 shim plumbing——2026-09-15 审核修复起真值只落本机用户级配置，tracked 的 `.mcp.json` 仅留占位符。发布为 self-contained 单文件（net10.0），保证 addon 在没有 .NET runtime 的机器上也能无条件拉起。

## Consequences

- token 防的是其他本机工具误碰端口与纵深一致性，不防拥有用户文件读权限的恶意进程——与 addon WS 侧 token 同一威胁模型（token 路径同样发布在注册表里）。
- 单例锁需可移植实现（文件锁），不能只依赖 Windows named mutex。
