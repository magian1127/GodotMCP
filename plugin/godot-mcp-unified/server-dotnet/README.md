# godot-mcp-daemon

机器级单例的常驻 C# daemon,取代每会话 Node 桥(ADR-0001 ~ ADR-0004)。
对 MCP host 暴露 loopback Streamable HTTP;GDScript addon 的通信面与 C1–C22 契约零改动。
`godot-mcp-shim`(同模块)为 stdio-only host(DSH mini client)提供 stdio↔HTTP 自举转发。

## shim(stdio 自举器)

- 启动即探活 loopback 端口:daemon 缺席则拉起(`GODOT_MCP_DAEMON_EXE` 可覆盖,`*.dll` 经 dotnet exec;缺省为发布同目录可执行文件),已有实例绝不重复拉起。
- stdio 行帧 ↔ daemon HTTP 双向转发(携带 token);daemon 重启期间重试并重新自举,全败时向 host 输出 JSON-RPC 错误,绝不静默挂死;stdout 只承载协议消息。
- 环境变量与 daemon 同名同义(`GODOT_MCP_DAEMON_PORT` / `GODOT_MCP_DAEMON_STATE_DIR`)。

## 能力范围(issue 03 骨架 + issue 05 注册表消费)

- 机器级单例文件锁(`daemon.lock`):第二个实例以退出码 2/3 退出(见退出码表),绝不双监听。
- 机器级稳定 token(`daemon-token`):首次生成、存于状态目录、跨重启稳定;HTTP 面要求 `Authorization: Bearer <token>`,其余一律 401。
- loopback Streamable HTTP(stateless,官方 `ModelContextProtocol` SDK 2.2.0,2026-07-28 协议修订);ServerInfo name 为 `godot-mcp-unified`(对 host 无感迁移)。
- **实例表(`list_instances`)**:只读消费机器级注册表(`projects.json` by_path,含真实 addon 的 float 形态数字),watch 增量 + 1s 周期轮询;对每个活跃条目发起出站 WS 连接并完成首帧鉴权(`{auth, version}`,token 按 ADR-0011 结构性校验、每次连接前重读);返回项目路径、短 id(12 位哈希)、引擎版本(连接后取 ack 补丁级)、端口、pid、连通状态。
- **断线纪律**:指数退避 1·2·4·…·60s,成功鉴权重置;端口/进程/令牌路径变化(编辑器重启)自动重连;进程消失的残留条目经活性判定(pid)移出实例表——注册表文件只读,绝不写/删。
- **实例寻址(issue 06,ADR-0003)**:每个工具接受可选 `instance` 参数——规范化项目路径为主标识、12 位短 id 为别名(大小写不敏感);恰好一个活跃实例时隐式选中,多实例未指定报 `AMBIGUOUS_INSTANCE` 并列出实例清单,目标不存在报 `INSTANCE_NOT_FOUND` 并附清单。首个迁移工具 `editor_sync`(`editor.refresh` → `editor.wait_for_idle`,schema 与 Node 桥同名工具一致)。
- **eager 编辑器工具(issue 09)**:场景/节点/编辑器/项目类 13 个工具(场景树、建删节点、场景建开、查询、脚本检查、节点检查/属性/脚本/结构管理、保存、项目设置)已迁移,schema 与 Node 同名工具对齐;真实 Godot 建/改/存冒烟通过。
- **LSP 通道(issue 11)**:五工具(`lsp_diagnostics`/`lsp_symbols`/`lsp_hover`/`lsp_completion`/`lsp_navigate`)经 daemon 自持 LSP 客户端直连 Godot GDScript LSP(端口经注册表发现,不经 WS 桥);端点解析含受保护的 6005 与存活佐证冲突检测(Node ADR 0008/0025 语义);项目级扫描分块聚合。
- **playtest/运行时通道(issue 10)**:`game_start`/`game_stop` 经编辑器拉起/停止;六工具(`game_start`/`game_stop`/`capture_screenshot`/`input_simulate`/`runtime_inspect_node`/`log_read`)按 Node 同名 handler 语义——运行时优先 + 编辑器回退链路 + 崩溃上下文;Mode B 通道按注册表 `runtime_port/runtime_pid` 拆装(noReconnect)。
- **只读状态面(issue 15)**:`list_operations` 列出经本 daemon 观测的在途操作(在飞/排队/执行中,含 `waited_ms`;数据源 = 在途请求表 + `_queued`/`_executing` 进度通知);场景租约不可查询处以 `lease.queryable:false` + note 如实标注。省略 instance 为全局只读视图,给定 instance 严格解析。
- **按需工具组(issue 12)**:`discover_tools` 元工具——31 组目录、关键词匹配(Node groupMatch 同算式)、激活/reset 语义;激活状态为 daemon 全局(跨会话一致),每个请求会话经 `ConfigureSessionOptions` 对齐工具面;未激活组工具不出现在 `tools/list`。`tools/list_changed` 经 `subscriptions/listen` 长流投递(SEP-2575 / 2026-07-28 修订——无状态 HTTP 下 SDK 内建 listen 按设计不授予通知,daemon 自持该流:唯一 acknowledged + 标注订阅 id 的变更扇出,幂等操作不推送)。代表性组 `cleanup`(`scene_close`/`project_delete`,含 dry_run 计划)端到端可用。
- **全量工具面(issue 13)**:31 组 / 64 个按需工具 + 常驻面 = 与 Node 桥全激活 `tools/list` 逐条对齐(名称 84/84、schema 逐字节一致——由 `server/scripts/dump-group-tools.ts` 从 Node 定义以 SDK 同源转换导出为嵌入表 `NodeToolTable.json`,常驻工具的 schema 亦经该表覆盖);64 工具调用路由(含 11 个特例处理器:双方法分支/键名映射/channel 路由/dry_run 计划)对 fake 编辑器与运行时替身全绿;unsafe 组经 `GODOT_MCP_UNSAFE=1` 门控(默认隐藏)。已记录差异:daemon 注入 `instance` 寻址参数;附加 `list_instances`/`list_operations`;zod 字符串强转未复制。
- **扩展投影 + 版本门控(issue 14)**:addon 动态扩展经 `discover_tools(refresh_extensions:true)`(extensions.refresh,回退 list)与 `extensions.changed` 广播两条路径投影进工具面——未分组即时可见、分组经扩展组激活,`tools/list_changed` 照常投递;实例间引擎版本不一致时 `tools/list` 呈现并集(任一实例可提供即可见,单实例/版本未知时隐藏——Node 注册门控同规),对不支持的实例调用返回 `UNSUPPORTED` + `(connected: 4.4)` + `Requires Godot 4.5 or newer.` 明确错误(Node 调用期门控同文案);扩展命令的 `min/max_godot_version` 同样门控。
- 空闲退出:全部 host 请求结束**且全部 Godot 实例断开**超过阈值后自退;最后一个实例断开的那一刻重置计时(spec US12)。
- 日志落 stderr 与注册表目录下的 `daemon.log`(不含接入令牌);stdout 保留给协议通道。

## 环境变量

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `GODOT_MCP_DAEMON_PORT` | `6590` | loopback 监听端口 |
| `GODOT_MCP_DAEMON_STATE_DIR` | 机器级注册表目录(Windows `%APPDATA%\godot-mcp-toolkit`;macOS `~/Library/Application Support/godot-mcp-toolkit`;Linux `$XDG_DATA_HOME`(缺省 `~/.local/share`)+ `/godot-mcp-toolkit`) | 单例锁与 token 所在目录 |
| `GODOT_MCP_DAEMON_IDLE_SECONDS` | `600` | 全部连接断开后的空闲退出阈值(秒) |

## 退出码

与 `ExitCodes` 常量同源;拉起方(shim/addon)据此决定放弃、重试或报错。

| 退出码 | 含义 |
| --- | --- |
| `0` | 正常退出(含空闲超时自退) |
| `1` | 启动期环境故障(状态目录/锁/token/日志的 IO 失败)—— 拉起方按可重试处理 |
| `2` | 单例锁被另一 daemon 明确持有(Windows 精确归因)—— 拉起方应放弃(已有实例) |
| `3` | 单例锁状态不明确(Unix 无法区分锁冲突与环境故障)—— 拉起方应视为可重试 |
| `4` | 监听地址绑定失败(仅此场景;通常为端口被其它进程占用) |

其余非零码为未处理异常(运行时默认行为)。

## 构建 / 测试

```bash
dotnet build godot-mcp-daemon.slnx
dotnet test tests/godot-mcp-daemon.tests
```

测试以真实子进程拉起 daemon 与 shim,通过官方 SDK 客户端直连 loopback HTTP / stdio 驱动全部行为
(spec 预约定的唯一新测试 seam);单例锁、idle 退出、stdout 纯净、实例路由均为进程级断言。

### 验收 harness(issue 16,回归入口)

```bash
dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"
```

`AcceptanceHarnessTests` 一条命令覆盖:M×N 矩阵(3 fake 实例 × 3 并发 MCP 客户端 × 交错变更/读取,
替身按 addon MutationLane 语义串行化并记录执行窗口,断言每实例变更严格串行、参数级零串台、
`_queued`/`_executing` 真实发生);在途租约可见(`list_operations` 按实例呈现 executing/queued,
完成后清空);生命周期四场景(addon/shim 双路拉起竞态 → 单例必然、防双开、空闲自退退出码 0、
daemon 硬杀后 shim 自愈 + token 稳定)。失败信息携带 (client, round, instance, tool) 定位。

## 发布(self-contained 单文件)

```bash
dotnet publish src/godot-mcp-daemon -c Release -p:PublishProfile=<rid>
```

发布参数固化在 `src/godot-mcp-daemon/Properties/PublishProfiles/*.pubxml`;产物不依赖
目标机器的 .NET runtime,addon/shim 可无条件拉起。

| RID | 状态 |
| --- | --- |
| `win-x64` | 冒烟验证通过(可执行、idle 自退、stdout 干净、日志落盘) |
| `linux-x64` / `osx-arm64` / `osx-x64` | 交叉构建验证通过;真机运行验证随验收 harness(issue 16/17) |
