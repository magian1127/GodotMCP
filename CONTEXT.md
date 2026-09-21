# GodotMCP

为 Godot 引擎提供 MCP(Model Context Protocol) 控制面的插件生态：AI 助手会话通过常驻 daemon 同时操控多个 Godot 项目与实例。

## Language

**守护进程(daemon)**:
机器级单例的常驻 MCP 进程，多路复用所有会话与所有 Godot 实例之间的通信。
_Avoid_: broker、hub、中心服务

**会话(session)**:
作为 MCP 客户端接入 daemon 的一次 AI 助手会话（ZCode、Codex、DSH 或 VS Code）。
_Avoid_: client、用户

**Godot 实例(instance)**:
加载了 godot_mcp_toolkit 插件、正在运行的 Godot 编辑器进程。
_Avoid_: editor、project、目标

**运行时实例(runtime instance)**:
playtest 启动的游戏进程，独立于编辑器进程提供控制面。
_Avoid_: game 进程

**注册表(registry)**:
机器级共享目录，各实例在其中发布自己的端口与凭据，daemon 据此发现它们。
_Avoid_: projects.json（那是文件名，不是概念）

**单例锁(singleton lock)**:
防止 daemon 多开的机器级文件锁；锁句柄持有至进程终止，无论正常退出还是崩溃都由操作系统释放。
_Avoid_: pid 文件、互斥量

**接入令牌(daemon token)**:
daemon 的 HTTP 面要求的机器级稳定 Bearer token；首次生成后存于注册表目录，host 静态配置与 shim plumbing 共用同一值。
_Avoid_: 密钥、鉴权串

**操作队列(dispatch lane)**:
Godot 实例内对所有变异操作串行化执行的机制，是多会话并发不交错的基础。
_Avoid_: 队列、锁

**场景租约(scene lease)**:
会话在编辑某个场景期间持有的限时排他标记。
_Avoid_: 场景锁

**桥(bridge)**:
把 MCP 工具调用翻译为 Godot 实例控制面消息的组件；也特指 daemon 的 Node 前身。
_Avoid_: 代理、转发器

**自举器(shim)**:
以 stdio 面向 host、把流量转发给 daemon 的小进程，并负责在 daemon 缺席时拉起它。
_Avoid_: 启动器、代理
