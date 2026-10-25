# 0004: daemon 生命周期、单例与本地安全

中文 | [English](0004-daemon-lifecycle-and-local-security.en.md)

## 决策

daemon 默认监听 loopback 6590，以全机命名互斥量防止任何副本多开，并保留状态目录文件锁。Godot addon 或宿主 shim 可探活并拉起。默认常驻；GODOT_MCP_DAEMON_IDLE_SECONDS 为正数才启用空闲退出。无在途请求、无 listen 流、无已连接 Godot 实例，且超过阈值，才会退出。

HTTP 使用机器级稳定 Bearer token，首次生成在注册表目录，跨重启保持稳定。当前所有宿主走 stdio shim，由 shim 读 token；不再建议把真值写入 Authorization 配置或用户环境变量。历史安装/桥脚本仍有兼容路径，不能据此宣称旧分发实现已全部删除。

默认无合法会话键时只提供 godot_connect，不建孤儿键；合法 listen 可以建键。会话键隔离状态，不承担授权；token 也不防拥有本用户文件读取权限的恶意进程。

## 取舍

- 默认自退曾基于宿主定期保活的假设；宿主空闲时可能不发请求，因此 2026-09-23 改为默认常驻。
- 稳定 token 避免重启后配置失效。每项目编辑器 token 与 daemon token 职责不同，前者由 addon 注册、daemon 读取。
- 进程级 debug/eager 为诊断与迁移设计，不是新的鉴权机制；当前实现缺口见 [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.md)。

## 2026-10 会话修订

有 listen 在途即不空闲；Dormant 的键保留与进程空闲是两套状态。legacy shim 已维持内部 listen；其它协议的 shim 是否持流仍以实际订阅为准，不能只根据进程存活推断空闲状态。断流后的 grace/TTL 只在 daemon 存活期间保留状态，重启会丢失内存键表。

发布使用自包含 daemon；shim 在本机有工具链时用 NativeAOT。状态目录、锁、token 与隔离测试边界见运行文档。


## 全机单例修订

根据明确的运行约束，单例不再按状态目录划分。MachineSingleton 的命名互斥量覆盖所有副本/端口/状态目录，并在取锁前识别较早运行的旧版 exe。第二实例立即退出 2。测试集合串行且负责清理自举子进程；开发测试也不绕过全机互斥。
