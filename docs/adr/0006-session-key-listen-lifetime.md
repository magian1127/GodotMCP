# 0006: 会话键与 listen 流生命期

中文 | [English](0006-session-key-listen-lifetime.en.md)

## 决策

无状态 MCP 不提供应用所需的组与默认目标归属。shim 每次启动生成 UUID，所有请求携带 x-godot-session；daemon 按键存储已连接标志、组与目标。会话键不等于项目路径，也不是权限边界。

Active 表示至少一条流；最后一流关闭进入 Dormant，普通无流请求完成后进入 IdleNoStream。listen 与 connect 都可建合法键；建键与加流在一个临界区。流或在途请求存在时不回收。

## 当前实现策略

当前源码增加了 Connected 状态标志和 ConnectedTtl：未连接 Dormant 宽限 30 秒、未连接 IdleNoStream TTL 10 分钟、已连接的无流键 TTL 24 小时，上限 1024。已连接键不参与容量淘汰。三个秒数支持测试环境覆盖，生效值以 SessionKeyPolicy 为准。

这修订了初稿“所有 Dormant 30 秒、所有 IdleNoStream 10 分钟、常量不读环境”的表述。原因是 legacy 宿主可能没有 listen，而最初的 shim 尚无内部流（现已为 legacy 补齐）；只按短 TTL 会回收仍在使用的连接状态。不能把已 connected 当作永远 Active，仍要按 ConnectedTtl 回收。

## 取舍与不变式

- 不借协议 session id，也不把键绑定项目：同一会话可跨项目。
- 只靠断流事件清理会泄漏从未有流的键，使用独立 reaper。
- 使用单调时钟，避免墙上时间回跳影响过期。
- 普通请求包含 ping，不能仅给工具方法白名单续期。
- listen 关闭不触发普通请求完成迁移；不绕过 Dormant 宽限。
- 流、在途计数与容量判断由注册表统一管理；无名额返回 SESSION_LIMIT，不挤掉活跃键。

通知由同键所有流投递，带每条流自己的 id。计划中的内部通知泵和重订阅仍未交付；最近 connect/discover 帧重放也不等于完整状态恢复。当前边界与摘要诊断见 [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.md)。
