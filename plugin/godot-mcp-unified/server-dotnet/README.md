# godot-mcp-daemon 与 stdio shim

中文 | [English](README.en.md)

本目录是当前控制面的实现真源。支持的宿主链路是 **宿主 → stdio → godot-mcp-shim → loopback HTTP → godot-mcp-daemon → Godot 编辑器/运行时**。HTTP 是 shim 的后端；升级后请删除旧的 HTTP 直连条目。宿主无需配置 token，shim 从机器级注册表读取。

当前 shim 启动先执行自举，再读取 stdin；initialize/server-discover 并非本地零依赖直答。普通请求版本头复用 initialize 协商值，listen 固定 2026-07-28；计划中的逐请求协议元数据适配尚未完整落地。

工作目录头 x-godot-cwd 使用带 uri: 标记的百分号编码 ASCII；daemon 在会话与工具面入口统一解码，支持中文、空格与字面量 % 路径，并兼容旧的普通 ASCII 目录头。

## 会话与工具面

每个 shim 进程启动时生成一个 UUID，通过所有 HTTP 请求的 `x-godot-session` 头携带；`x-godot-cwd` 上报工作目录，`x-godot-shim-version` 上报版本。键提供状态隔离，身份验证仍由稳定 Bearer token 承担。

| 状态/口径 | 行为 |
| --- | --- |
| 未连接，包括只有 listen 流但尚未 connect | `tools/list` 仅有 `godot_connect` |
| 已连接 | 9 个核心工具 + 本键激活的组；扩展另计 |
| 内置目录 | 92 个唯一名称 = core 9 + 组内去重 83；17 组共 85 个成员槽位 |
| 表与实现 | `NodeToolTable.json` 有 91 条，`godot_connect` 是 daemon 表外入口；C# 注册 34 个工具对象不等于启动可见 34 个 |
| 可用性 | 实际列表还受引擎版本并集与 `GODOT_MCP_UNSAFE` 门控影响 |

core 9 是 `godot_connect`、`discover_tools`、`list_instances`、`list_operations`、`log_read`、`capture_screenshot`、`scene_query`、`scene_get_tree`、`node_inspect`。入口原生名称带 `godot_`，其余工具没有统一的 `godot_` 前缀；宿主命名空间以实际列表为准，DSH 默认 `mcp__godot__<原生名>`。

`godot_connect(project?, instance?, groups?)` 选择默认目标并声明初始组；空参依次用本键上次目标、cwd 本层的 `project.godot`、唯一活跃实例，不递归扫描。显式 project 即使当前未运行也可写入，响应附 `TARGET_NOT_RUNNING` 提示。没有候选返回 `NO_CANDIDATE_PROJECT`。

普通调用寻址：显式 `instance`（路径或 12 位短 id）> 本键默认目标 > 唯一活跃实例 > 报错列清单。`list_instances` 不接受 instance；`list_operations` 可查询全局或显式实例。多项目交错工作传显式 instance，避免同键在途切换。

`discover_tools(request, activate, include_schemas, reset, refresh_extensions)` 仅调整本键组。request 是组名/关键词字符串或数组，activate 是布尔；无 request 是浏览，include_schemas 单独使用不会激活。reset=true 全停，reset=[...] 按名停。分组和路由以 [GroupCatalogue.cs](src/godot-mcp-daemon/Groups/GroupCatalogue.cs) 与 [技能路由](../skills/godot-control/references/tool-routing.md) 为准。未分组扩展在连接后可见，分组扩展需激活；用 refresh_extensions=true 重新扫描。

工具列表由每请求集合裁剪，调用使用同一可见集；隐藏工具返回 isError，诊断区分未连接、组未激活、版本不支持和未知工具。`tools/list` 按固定顺序列出，入口第一，附 `ttlMs:0`、`cacheScope:"private"`；未就绪列表附 session-state/hint 元数据。TTL 是缓存意图，不能代替宿主重拉能力。

## 通知、重连与当前限制

daemon 在带键的 `subscriptions/listen` 长流上发 acknowledged 和带本流 subscription id 的 `notifications/tools/list_changed`；同键可有多流。没有合法头的 listen 返回 HTTP 400，无配额返回 503。其他无头/非法头请求提供受限工具面，不建孤儿键。

shim 会自举 daemon，按连接错误与 `x-godot-daemon-epoch` 变化检测重启，尽力重放最近的 connect 和会改变状态的 discover 请求。重新启动 shim 会生成新键，不恢复上一 shim 的默认目标/组。

**当前实现仍有以下已复现限制，尚未作为完整发布验收通过：**

- legacy shim 已建立内部 listen、吞掉内部 ack，并转换成普通 list_changed；断流会重连并通知宿主刷新。现代宿主自己开的 listen 尚无自动重放；TTL=0 仍不能证明所有宿主自动更新。五宿主 × 版本的通知重拉与混跑矩阵仍需真实宿主验收。
- 默认目标每次子调用重新读取，同键在途切换可使复合工具跨项目。顺序切换，或在多项目调用中显式传 instance。
- connect 的 groups 替换只改注册表，旧 GroupService 桶可被后续浏览写回。现有组的停用使用 discover reset，并核对列表。
- include_schemas 对部分常驻组工具只返回名称；应读取激活后的 tools/list 完整 schema。
- eager 回滚的组仍可能按键分桶；debug 新 daemon 的无头面仅 34 个，并未达到设计的 92 个全量面，非法头处理和双开关优先级也有偏差。两开关不作为已验证的恢复方案。
- 仅重放最后一条增量 discover 不能恢复最终组集合，也可能复活已被后一次 connect 替换的组。重启后重新核对目标与所需组。
- 当前 C# daemon 不读取旧 GODOT_MCP_READ_ONLY 过滤变量；addon 只读 UI 的兼容状态不能证明变更被拦截。应配置宿主工具策略并验证，见 [安全建议](../addons/godot_mcp_toolkit/docs/security-recommendations.zh-CN.md)。
- DSH 默认按会话挂载时，单改 toolCallTimeoutMs 尚未使旧连接重挂；新会话读取新值，详见 [DSH 行为](../../../adapters/dsh/deepseek-harness-godot_unified/docs/behavior.md)。

这些边界不会由“所有现有测试通过”自动消除。协议客户端的成功与当前聊天原生工具发现成功也应分别记录。

## 会话键生命期与诊断

`SessionKeyRegistry` 中 Active 表示至少一条 listen 流，最后一流关闭进入 Dormant；无流的普通请求完成后进入 IdleNoStream。流或在途请求存在时不回收。时间使用单调时钟。

当前 `SessionKeyPolicy`：未连接 Dormant 宽限 30 秒，未连接 IdleNoStream TTL 10 分钟，已 connect 的无流键（含 Dormant）TTL **24 小时**；上限 **1024**。容量淘汰仅考虑无流、无在途、未连接的 IdleNoStream 或过宽限的 Dormant；已连接键不参与容量淘汰。没有名额时 connect 返回 SESSION_LIMIT 和键表摘要。已连接键不是“永久活跃”，最终仍按 TTL 回收。

reaper 每秒扫描。会话摘要每 5 秒及部分状态变化时写入 `state/sessions.json`（相对状态目录）；含短键、组、目标、流/在途数、cwd 与 shim 版本，不含完整键。`godot-mcp-daemon --status` 读取该文件，不取单例锁；这是可能滞后的诊断快照，不是实时键数断言。

## 调用超时与重试

| 层 | 当前默认 |
| --- | --- |
| daemon 单次 wire 操作 | 普通工具 30 秒；`ToolCallTimeouts` 名单内长工具 180 秒 |
| shim tools/call | 210 秒，环境变量按毫秒覆盖 |
| shim 其他短请求 | 30 秒 |
| DSH 宿主 | 240 秒，配置项 toolCallTimeoutMs |
| C# build wait | 默认等待 180000ms，范围 1000..200000ms，另加单次状态查询耗时 |

长工具包括 execute_code、node_call_method、editor_save_scene、editor_build_csharp/status/wait、editor_sync，名单以 [ToolCallTimeouts.cs](src/godot-mcp-daemon/Tools/ToolCallTimeouts.cs) 为准。组合工具的每段预算不等于整个 tools/call 的总预算；扩展可声明自身 timeout_ms。

tools/call 超时不重试；只有能确定请求尚未发送的连接失败才可重试。等待放弃或不确定的传输失败返回“操作可能已经执行，请先查询状态”的 isError。先检查效果，再决定是否重提变更。

正式服务在全机只允许一个：启动先检查旧版同名进程，再获取与路径、端口、STATE_DIR 无关的命名互斥量。已有实例时新进程立即以退出码 2 结束，原进程继续服务。退出/崩溃由 OS 释放互斥量；--status 是直接读取摘要的诊断命令。进程回归须在没有正式 daemon 运行时串行执行，不能用隔离状态目录多开。

## 配置与进程

| 环境变量 | 默认/作用 |
| --- | --- |
| GODOT_MCP_DAEMON_PORT | 6590，loopback 后端端口 |
| GODOT_MCP_DAEMON_STATE_DIR | 机器级注册表目录，同时保存锁、token、日志与会话摘要 |
| GODOT_MCP_DAEMON_EXE | shim 使用的 daemon 路径；缺省同发布目录，开发时可指向 dll |
| GODOT_MCP_DAEMON_IDLE_SECONDS | 未设或 0 禁用自退；正数才启用 |
| GODOT_MCP_TOOL_CALL_TIMEOUT | shim tools/call 单次毫秒数，默认 210000 |
| GODOT_MCP_UNSAFE | daemon 为 1 时允许 unsafe 组，默认关闭 |
| GODOT_MCP_DEBUG_UNKEYED | 诊断开关，当前限制见上文 |
| GODOT_MCP_TOOLFACE | eager 回滚开关，当前限制见上文 |
| GODOT_MCP_SESSION_GRACE_SECONDS | 测试策略覆盖，默认 30 |
| GODOT_MCP_SESSION_NO_STREAM_TTL_SECONDS | 测试策略覆盖，默认 600 |
| GODOT_MCP_SESSION_CONNECTED_TTL_SECONDS | 测试策略覆盖，默认 86400 |

默认目录：Windows `%APPDATA%/godot-mcp-toolkit`，macOS `~/Library/Application Support/godot-mcp-toolkit`，Linux `$XDG_DATA_HOME/godot-mcp-toolkit`（缺省 `~/.local/share/godot-mcp-toolkit`）。状态目录也是注册表消费目录；隔离测试的编辑器与 daemon 应使用同一注册表，单改 daemon 目录不会发现原目录的实例。

daemon 默认常驻。有请求在途、listen 流或 Godot 实例连接时不算空闲；启用自退后须全部结束并超过阈值才退出。daemon 不启动编辑器。日志落 stderr 与 daemon.log，shim stdout 仅承载协议消息。token 存于机器级注册表，不写入宿主配置或用户环境变量。

退出码：0 正常；1 启动环境故障；2 单例锁已被持有；3 锁状态不明确；4 监听绑定失败。其它非零值为运行时异常。

## 构建、验证与发布

从本目录运行：

```bash
dotnet build godot-mcp-daemon.slnx
dotnet test tests/godot-mcp-daemon.tests
dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"
dotnet publish src/godot-mcp-daemon -c Release -p:PublishProfile=<rid>
dotnet publish src/godot-mcp-shim -c Release -p:PublishProfile=<rid>
```

测试用独立状态目录、随机端口、真实 daemon/shim 进程及编辑器替身验证。真实宿主目录更新、真实 Godot 编辑器调用和 GUI/渲染行为分别验收；测试通过只覆盖已断言的输入与序列。

发布 profile 位于各工程 `Properties/PublishProfiles/`。daemon 为自包含单文件；shim 在目标系统有工具链时发布 NativeAOT，跨系统或缺工具链时可用自包含单文件：
```text
-p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true
```
Windows AOT 需 Visual Studio C++ 桌面工作负载，macOS 需 Xcode，Linux 需 clang/zlib 开发包。RID 使用 win-x64、linux-x64、osx-arm64 或 osx-x64。

架构决策：[会话键](../../../docs/adr/0006-session-key-listen-lifetime.md)、[工具面](../../../docs/adr/0007-tool-face-per-session.md)、[默认目标](../../../docs/adr/0008-per-key-default-target.md)。

## 平台验收记录

macOS 适配提交 7ac9b3a 记录了 Apple Silicon 与 Godot 4.7.2 mono 的本机发布、原有单测及真实编辑器全链路验证。本次新增的会话工具面、全机互斥与目录头编码尚未在 Mac 真机复测；发布前应按当前代码重新验证，不能把历史平台记录当成本次功能证明。
