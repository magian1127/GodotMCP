# 用户可见行为

## 安装行为

| 项 | 默认 | 语义 |
| --- | --- | --- |
| server dist | 解析顺序 | `--server-dist` > `$GODOT_MCP_SERVER_DIST` > `--godot-mcp-root`/`$GODOT_MCP_ROOT` + 固定相对路径；全部缺失 → 报错并给指引 |
| 项目校验 | 必须 | `--project` 必须是含 `project.godot` 的绝对路径目录 |
| cwd 绑定 | 当前目录 | `--cwd <dir>` 指定该条目绑定到的会话工作目录（按 cwd 匹配生效） |
| bundle 行 | 可选 | 仅 `--link <dir>` 时经 `dsh plugin add link:` 安装；失败不阻断路径配置写入 |

v0.4 起 `install` 不再写官方 mcp-client 行（自研桥接）：校验后把 `serverDist` + `{cwd, projectPath}` 条目写入 `$DSH_HOME/godot/paths.json`（host 侧 PathStore 同源），并**清理历史官方 mcp-client 受管块**（防双轨）。路径配置供 Godot preset 会话创建时自动加载；正式入口是侧栏「插件」页 → 本包页面的 Godot 路径区（DSH 0.1.6+）（按 cwd 批量条目 + 精简路径条），保存即时生效、无需重启。install 是幂等的：重复执行按最新参数覆盖对应 cwd 条目；`--read-only`/`--unsafe`/`--rate-limit`/端口/超时等原官方行 env 参数**不再写入**（警告提示，能力由 server 默认/项目设置提供）。

## 运行时行为（重启 dsh 后）

- **接入形态（自研桥接 + 动态工具）**：插件自己 spawn GodotMCP server（stdio）并经内嵌极简 MCP client 桥接——**不写死工具清单**，门面工具由 server 的 `tools/list` 动态提供（真实 schema/描述；组激活/扩展变化经 `tools/list_changed` 重同步，清单无变化时跳过避免 re-sync 风暴）。
- 启动面 19 常驻 + 1 元工具（`discover_tools`），约 8.8K tokens；模型调用 `godot_discover_tools` 搜索/激活 31 个按需组。
- **server 生命周期（零默认开销）**：默认不启动、不连编辑器——**工具清单持久缓存在 `$DSH_HOME/godot/tools-cache.json`**（连接成功后从 server `tools/list` 拉取并缓存全部工具 schema，跨会话/跨重启复用）。**缓存命中时**：打开 DSH 无需任何 Godot 相关操作，工具即从缓存可见；**只有首次工具调用或工作台点「连接 Godot」才拉起桥接**（spawn server + 连接编辑器），成功后刷新缓存。**无缓存时**：工作台点「连接 Godot」获取并缓存；缓存与当前 server dist 不匹配时视为陈旧、忽略并提示重连。
- 工具门面在 server 关闭/崩溃后**保持在册**，调用时 `ensureStarted` 按需重启自愈；首次成功调用后补一次 `tools/list` 同步与缓存写入（之后不再随调用刷新，`tools/list_changed` 负责增量）。
- **按会话 cwd 传参**：桥接按**当前调用会话的工作目录**解析生效路径——项目路径查 `$DSH_HOME/godot/paths.json` 的按 cwd 批量条目（无匹配回退全局设置），server dist 为全局单值；cwd 变化时重启 server 以切换项目配置（不同会话可以做不同 Godot 项目，不绑死目录）。
- server 崩溃 → 桥接退避重试；编辑器重启 → server 每次连接重读 token 自愈。编辑器未启动时 server 冷启动不再无限重连：从未认证成功的通道失败后静默待命（无 `[bridge]` 重连刷屏），下一次工具调用按需重试连接；编辑器在场时行为不变。
- 提示词 section（`godot-mcp-workflow`，约 7 行，双语）由本插件注入；`config.serverName` 可覆盖工具前缀（`<serverName>_*`）。

## 注入策略（插件页配置表单）

| 设置 | 默认 | 语义 |
| --- | --- | --- |
| 注入原版模式（`injectLegacyMode`） | 关 | 开=所有会话全局注入 `godot_*` 工具与提示词 section（原版 bundle 全局行为，设置变更即时生效）；关=仅 Godot preset 的会话注入，其余会话在 Agent 自身作用域 deny 这些工具（schema 面不出现），也无提示词 |
| 注入工作流提示词（`promptGuidance`） | 开 | 是否注入工作流提示词 section；仅对会注入工具的会话生效（Godot preset 或原版模式） |
| 提示词中文化（`zhPrompt`） | 关 | 注入的工作流提示词、桥接工具说明（上游中文翻译表映射，缺失回退原文）与插件错误包装消息使用中文（默认英文，与内置工具一致）；工具名保持英文。切换后即时重注册门面（用已解析清单，不重启 server） |

- 判定时机：`agent/created`、`agent-preset/selected`（会话中途换预设）、设置变更（live 重装全部既有 agent）；门面注册/组激活新增的工具由 `tools/change` **合并窗口（150ms）** 增量补进 deny 名单（幂等，无 diff 不调用 restrict，避免事件回声）。
- deny 只是模型可见性控制：桥接 server 子进程与工具注册不受影响，Godot 工作台也不受影响。
- Godot preset（`~/.dsh/.agent-presets/godot/`，显示名 Godot）是空组合——它只作为"本会话需要 Godot"的标记；工具与提示词由本插件的 per-agent 策略提供。

## 失败模式

| 症状 | 原因 | 恢复 |
| --- | --- | --- |
| 工具调用返回 `AUTH_FAILED`/连接错误 | 目标项目的编辑器未运行或项目移动 | 打开编辑器（`godot --headless --editor --path <项目>` 亦可）；下次调用自愈 |
| `dsh-godot status` 显示"server dist 缺失" | GodotMCP server 未构建 | 在 server 目录 `npm run build` 后 reinstall |
| status 显示"编辑器注册表: 无条目" | 编辑器未开或项目未装 addon | 装 addon（上游 install-godot-project.ps1）并开编辑器 |
| 重启后工具未出现 | 尚未创建 Godot preset 会话（预热未触发）或 server dist 未配置 | 打开 Godot preset 会话；在插件页配置表单「Godot 路径」填写 server dist 后重试 |
| 工具出现但调用返回"server dist 未配置" | 预发生在路径存储为空且设置卡路径为空 | 在插件页配置表单配置路径（保存即生效,无需重启桥接行） |
| 编辑器端口固定后连接失败 | 只固定了一端 | 两端同值固定，或都不固定走注册表发现 |

## 数据边界

- 写盘内容仅限：profile 用户 patch 层受管块（server dist、项目路径、env 覆盖——均为机器本地配置）。
- 不读取/写入 Godot 项目文件本身（那是桥接 server 与编辑器 addon 的职责）；CLI 只读注册表 `projects.json` 与端口探活。
- 不处理任何凭据；上游认证用 per-project session token，由 server 自行从注册表读取。

## Godot 工作台（Web）行为

工作台是浏览器端的旁路调试面：不经对话、不写回会话，全程走同源 `/godot-workbench/api/*`。以下为其用户可见行为。

### 路径设置（位于 插件页配置表单）

- 路径设置位于侧栏**「插件」页 → 本包页面**的配置表单（`plugins.bundle.config`，DSH 0.1.6+）内的「Godot 路径」区：**server dist 全局单输入框** + 「项目路径（按工作目录）」批量条目区（添加行 + 已添加条目逐行编辑/删除 + 保存）。工作台（`conversation.view`）顶部另有一行**精简路径条**：显示「当前工作目录 + 按该 cwd 匹配的生效 Godot 项目路径」，并提供「修改」就地编辑（空值=清除该 cwd 条目），写回同一 `paths.json` 存储；完整批量管理仍在配置表单。
- **server dist 全局单值**：配置表单里只有一个全局 server dist（不受工作区区分）。保存后优先于该卡片上方 `godotServerDist` 设置字段；两者都为空时回退 CLI 解析链（`--server-dist` / `$GODOT_MCP_SERVER_DIST` / godotMcpRoot+相对路径）。
- **项目路径按工作目录（cwd）批量条目**：每条 `{ cwd, projectPath }`，可多条并存、按 cwd 唯一（重复 cwd 后写覆盖）。切换会话时按其**工作目录 cwd** 在该表里匹配 `projectPath`；**无匹配**时回退全局配置表单的 `godotProjectPath`（或为空）。
- 保存后落到 DSH host 持久文件（`$DSH_HOME/godot/paths.json`），跨会话保留，不依赖浏览器缓存。批量条目同样落 host 侧（桥接 server 在 host 启动、需 host 读到路径），而非浏览器 localStorage。
- **桥接按调用会话 cwd 取项目路径（per-cwd 传参）**：桥接 server 在模型调用 `godot_*` 工具时按**当前调用会话的 cwd** 解析生效路径（项目路径按 cwd 查表，否则全局设置）；server dist 为全局单值，不看 cwd。同一桥接在 cwd 变化时重启以切换项目路径配置——不同会话工作区可以做不同 Godot 项目，不绑死目录。
- 配置表单无会话 cwd 上下文：读取/保存只涉及全局 serverDist 与整份 projects，不显示 currentProjectPath。

### 状态条语义

- **桥接**：`bridge.toolCount` 为当前可见的 `godot_*` 工具数；为 0 时显示「桥接行未挂载/编辑器未开」指引。
- **编辑器**：取注册表条目，显示 `项目名 · 端口 · Godot 版本`；`listening=false` 或 `port=null` 时黄标提示「编辑器未运行」。
- **只读标记**：`GODOT_MCP_READ_ONLY` 生效时显示 `READ_ONLY` 徽标。
- **server dist**：路径非空但文件缺失时显示「缺失」徽标；路径为空（`godotMcpRoot` 未配置）时显示安装指引文案（`dsh-godot install` 并刷新）。

### 组激活即元工具调用

- **技能/提示词面板（右侧「技能/提示词面」）**：技能数据源为 GodotMCP 工作区根（`<根>/plugin/godot-mcp-unified/skills/*/SKILL.md`）。工作区根**优先取组合行 `godotMcpRoot`；v0.4 后安装只写 serverDist 完整路径、不再存 godotMcpRoot，此时从生效 serverDist 向上推导**——serverDist 形如 `<根>/adapters/dsh/godot-http-bridge.mjs`（daemon 桥，候选优先）或旧 `<根>/plugin/godot-mcp-unified/server/dist/index.js`（legacy 兼容），去掉命中的固定相对后缀即得工作区根（仅当确实以候选后缀之一结尾时才推导，避免误判）。因此仅配置 serverDist（+projects）的场景也能读到技能，不会因缺 `godotMcpRoot` 显示空白。

- 点击某个未激活组的「激活」按钮 = 前端选中 `discover_tools` 元工具并预填组名（**不直接触发**）；用户再点「执行」即正常发起一次 `discover_tools` 的 `POST /call`（激活即工具调用）。激活成功后 `tools/change` 事件触发工具面刷新，树随之重拉。
- 工具集刷新的时机：Tab 获得焦点时（会话/视图切换会重挂载工作台）、每次调用执行成功后、以及组激活响应后；前端不轮询。
- **工具目录缓存（host 侧）**：`/api/tools` 的响应（全量 schema + 组目录）按 5s TTL 缓存在 host 进程内，`tools/change` 事件或任意 `/api/call` 完成即失效——工作台反复重挂载时不会每次都全量序列化 + 发起 `discover_tools` MCP 往返（2026-09-05 修复：此前编辑器在场时普通点击即可把 host 打满、整个 Web 卡死）。
- **「连接 Godot」按钮（状态条）**：显式拉起桥接（`POST /godot-workbench/api/connect`）——无缓存或需要刷新工具面时使用；连接成功即同步工具清单并写入缓存（`tools-cache.json`），工具树随之刷新。缓存命中时无需连接即可浏览/调用，首次调用自动建连（自愈）。**仅当工具清单未加载（`bridge.toolCount===0`，即未连接）时显示该按钮；已连接（工具数>0）时自动隐藏**，以免重复出现连接入口（需要重新同步工具面时改用「刷新」按钮）。

### `ask` 策略工具的边界

- `tools.execute` 恒走 pre-execute 瀑布；mcp-client 工具未注册 `ask` 策略 → 默认 allow，工作台调用直通。
- 若其它插件为某工具注册了 `'ask'` 策略，工作台调用（无 agent 上下文）会被**拒绝**——`{isError}` 结果原样呈现，**不做绕过**。

### 确认策略

- 因工具注解（readOnly/destructive）不可得，默认**每次执行都弹确认**（工具名 + 参数摘要）；提供「本会话跳过确认」开关（仅内存记忆、不落盘）。
- 工具名（或所选参数）含 `unsafe` 时**永远需要确认**，不受跳过开关影响。

### 图片 4MB 截断

- 结果渲染优先读 `.value.content` 原始块（不依赖模型图片能力）；`type='image'` 的 `data` 字段 char 数 **>4MB** 时截断为 `data.slice(0, 4MB)` 并置 `truncated: true` 标记。

### 历史上限

- 历史存 localStorage（键 `dsh-godot-workbench-history`），**上限 50 条**；每条仅存工具名、参数 JSON、是否成功、耗时、时间戳与**8KB 截断**的文本摘要。配额写满时放弃持久化；参数或摘要损坏时在载入/重放阶段静默忽略。

### 取消

- 前端发起调用时铸造 `gwbc-*` 形式的 `callId` 随 `/call` 提交；host 侧 `AbortController` 表按 `callId` 登记，`/cancel` 触发对应 abort，execute settle 后清理。
- 同一时刻允许多个并发调用（前端调用区串行执行，但历史中可有多个进行中条目）；编辑器侧 FIFO 由 server 串行化，`_queued` 语义在结果的错误/提示文本中原样呈现。
