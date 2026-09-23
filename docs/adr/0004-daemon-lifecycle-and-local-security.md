# 0004: daemon 按需拉起、单例与本地安全面

daemon 以机器级单例锁防多开，默认固定监听 loopback 6590（env 可覆盖），host 侧地址因此稳定。由 host 侧 shim 或 Godot addon 任一方在探活失败时拉起；空闲自退**默认关闭**（2026-09-23 修订，见文末修订记录；显式给正数阈值才启用）。HTTP 面采用机器级稳定 token：首次生成后存于注册表目录、跨重启保持稳定（否则 host 的静态 Authorization 配置会因 daemon 重启而断），安装脚本统一为宿主侧用户级配置（ZCode 用户级 config 的 Authorization 头、Codex 环境变量）与 shim plumbing——2026-09-15 审核修复起真值只落本机用户级配置，tracked 的 `.mcp.json` 仅留占位符。发布为 self-contained 单文件（net10.0），保证 addon 在没有 .NET runtime 的机器上也能无条件拉起。

## 修订记录

### 2026-09-23：空闲自退默认关闭（由用户要求）

**背景**：原文的"全部连接断开后 idle 10 分钟退出"建立在"host 侧会周期性发请求"的假设上。
但 HTTP 接入是无状态的——宿主**只在真正调用工具时**才发请求，空闲期没有任何在途请求，
也不持有连接。于是当用户把全部模型端（Codex / ZCode / VS Code / DSH）都配成 HTTP 或
长期挂着的 stdio 客户端时，daemon 会在无人察觉时自退，用户必须**手动再次启动 exe**
才能恢复——这正是本 ADR 想避免的"控制面静默消失"。

**新口径**：`GODOT_MCP_DAEMON_IDLE_SECONDS` **未设置或为 0 = 禁用自退（默认）**；
显式给正数（秒，支持小数）才启用，语义不变（无在途请求 + 无已连接 Godot 实例 + 超阈值）。
实现上 `IdleMonitor` 在禁用态恒不空闲，`IdleExitService` 直接返回、不注册轮询。

**代价与接受理由**：默认常驻意味着"最后一次使用后进程不自动消失"（机器上长期驻留一份≈79MB
的进程）。相较"服务静默消失、用户手动开 exe"，前者是可观察、可解释的成本；需要自动回收的
机器仍可显式打开（安装器/脚本给 `GODOT_MCP_DAEMON_IDLE_SECONDS=<秒>`）。


## Consequences

- token 防的是其他本机工具误碰端口与纵深一致性，不防拥有用户文件读权限的恶意进程——与 addon WS 侧 token 同一威胁模型（token 路径同样发布在注册表里）。
- 单例锁需可移植实现（文件锁），不能只依赖 Windows named mutex。
