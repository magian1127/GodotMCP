# 用户可见行为

## 环境门禁

- `package.json` 声明 `engines.dsh: ">=0.2.0-rc.2 <0.3.0-0"`（`engines.node` 保持独立键）。DSH 0.2.1 起加载器用 `engines.dsh` **单独**判定插件兼容性（此时 `peerDependencies` 被忽略），且判定带 `includePrerelease`——所以 `0.2.1-rc.2` 这类预发布运行时能正常装载。
- 区间含义：覆盖整个 0.2.x（含 `0.2.0-rc.2`、`0.2.1-alpha.*`、`0.2.1-rc.*`、`0.2.1`），拒绝 0.1.x 及更早，也拒绝 0.3.x。被拒绝时 DSH 不装载本包并在插件诊断里报 `requirements: {"engines.dsh":"…"}`。
- 注意：`0.2.0-rc.2` 是区间**下界本身**，因此它判定为**兼容**——它是支持范围内的版本，不能当作"旧版被拒"的负向样本。真正的负向样本用 `0.1.7-alpha.2`、`0.2.0-rc.1`、`0.3.0`。

## 接入形态（v0.5：宿主官方 MCP）

本包**不实现 MCP 客户端**。连接、握手、工具发现、`tools/list_changed`、断线重连与图片投影都由宿主自带的 `@deepseek-ai/dsh-mcp-client` 完成（运行时从当前 profile 解析，本包不把它写进依赖）。本包只决定两件事：**哪些会话需要它**、**它连哪个 server**。

- **工具名**：由官方固定约定给出——`mcp__<serverName>__<rawName>`，默认 `mcp__godot__scene_get_tree`。`serverName` 可由插件行 config 的 `serverName` 覆盖（须匹配 `/^[A-Za-z0-9_-]{1,32}$/`），改它会改掉模型看到的工具名。
- **工具清单仍然动态**：来自 server 的 `tools/list`（真实 schema/描述）。组激活（`mcp__godot__discover_tools`）之后 server 发 `list_changed`，官方客户端自动增量更新工具集。
- **挂载时机**：会话创建（`agent/created`）且该会话的 preset 是 `godot` 时，把官方 MCP 客户端挂进**该会话的 Agent 作用域**；**空白会话（首轮之前）**切到 Godot preset（`agent-preset/selected`）同样挂载——DSH 在首轮之后拒绝换预设（`agent-preset/locked`）。切走预设、会话销毁、插件卸载时卸载。
- **其他会话**：不挂载 → **根本没有这些工具**（不是注册后隐藏）。因此不再需要 `restrict`/deny 补丁、`tools/change` 合并窗口与工具清单缓存。
- **原版模式**（`injectLegacyMode`）：改为在 **host 作用域**挂**一个共享连接**，所有会话都能看到工具，提示词 section 也全局注入；此时不再按会话挂载（避免同名工具挂两遍、多起进程）。项目路径取全局设置，不随会话 cwd 切换。
- **子代理**：子代理会话继承父会话的 preset，因此也会各自挂载一份连接（一个轻量 stdio shim；机器级 daemon 仍是单例、由 shim 复用）。多会话/多子代理 = 多个 shim，这是官方客户端"每个插件实例一条连接"的固有形状。

## 运行时行为

- **server 入口**：`$DSH_HOME/godot/paths.json` 的全局 `serverDist`，或插件行/设置里的 `godotServerDist`。随包 shim 是原生可执行文件（`server-dotnet/publish/<rid>/godot-mcp-shim[.exe]`），直接 spawn；历史 Node 入口（`.mjs`/`.js`）经当前 node 执行。
- **按会话 cwd 传项目路径**：挂载时按**该会话的工作目录**解析 Godot 项目路径——先查 `paths.json` 的 `projects` 按 cwd 条目，无匹配时若只有唯一项目条目则用它兜底，否则回退全局 `godotProjectPath`。解析结果作为 `GODOT_MCP_PROJECT_PATH` 传给 server；子进程的 cwd 也设为会话 cwd（shim 在没拿到显式 env 时会按 cwd 查机器级注册表兜底）。不同会话工作区可以是不同 Godot 项目。
- **连接生命周期归官方客户端**：`failOnStartupError: false`——编辑器没开、daemon 没起、路径暂时写错都不会让会话创建失败；错误进日志，客户端从 500ms 起、每次翻倍、上限 30s 自动重连。连续失败 10 次后该服务器的工具被移除、重连停止。此时**恢复手段只有**：重开会话（销毁后重建，插件会重新挂载）、修改 server 入口/项目路径（配置键变化触发重挂）、在空白会话换一次预设、或重启 DSH；只改 `zhPrompt`/`promptGuidance` 不会重挂。连接稳定超过退避上限后重试预算会重置。
- **就绪宽限**：会话创建最多等待 10 秒连接就绪（让首条消息通常能看到工具）；超时则工具在后台就绪后由 `tools/change` 让模型在**下一轮**看到。挂载本身**永不阻塞或打断会话创建**。
- **工具调用**：每次调用/资源请求默认 60 秒超时，可取消；server 报错时调用明确失败（不会伪造成功）。图片（PNG/JPEG/WebP/GIF）在模型支持图片输入且附件功能启用时以图片附件出现——**截图验收现在是真正的图片**，不再是文本占位；不支持时给诊断文本。
- **server 指令**：server 返回的非空 instructions 会作为字面文本进入系统提示词（GodotMCP 当前未提供 instructions 时无影响）。MCP 提示词模板（prompts）不受官方客户端支持。
- **资源**：由宿主的 `dsh-mcp-resources` 提供（若 profile 启用该行）；本包不自行透传资源。
- **提示词 section**（`godot-mcp-workflow`，约 7 行，双语）由本插件注入：仅对挂了 Godot MCP 的会话（Godot preset 的 scoped section，或原版模式的全局 section）。`zhPrompt` 只切换该段文本——**工具描述与错误消息来自上游 server，保持原文**。
- **server 入口为空**：不挂载，但 Godot 会话会得到一段简短的"如何配置"提示词（告诉模型不要臆造 `mcp__godot__*` 调用，并引导用户去插件页填路径）；原版模式的全局 section 也按同一判据切换成这段文本；其他会话没有任何提示。

## 安装行为

| 项 | 默认 | 语义 |
| --- | --- | --- |
| server dist | 解析顺序 | `--server-dist` > `$GODOT_MCP_SERVER_DIST` > `--godot-mcp-root`/`$GODOT_MCP_ROOT` + 固定相对路径；全部缺失 → 报错并给指引 |
| 项目校验 | 必须 | `--project` 必须是含 `project.godot` 的绝对路径目录 |
| cwd 绑定 | 当前目录 | `--cwd <dir>` 指定该条目绑定到的会话工作目录（按 cwd 匹配生效） |
| bundle 行 | 可选 | 仅 `--link <dir>` 时经 `dsh plugin add link:` 安装；失败不阻断路径配置写入 |

`install` **不写 profile 的 mcp-client 行**：校验后把 `serverDist` + `{cwd, projectPath}` 条目写入 `$DSH_HOME/godot/paths.json`（host 侧 PathStore 同源），并**清理历史静态 mcp-client 受管块**（防双轨）。正式入口是侧栏「插件」页 → 本包页面的 Godot 路径区（DSH 0.1.6+），保存即时生效、无需重启。install 是幂等的：重复执行按最新参数覆盖对应 cwd 条目；`--read-only`/`--unsafe`/`--rate-limit`/端口/超时等历史 env 参数**不再写入**（警告提示，能力由 server 默认/项目设置提供）。

## 注入策略（插件页配置表单）

| 设置 | 默认 | 语义 |
| --- | --- | --- |
| 原版模式（`injectLegacyMode`） | 关 | 开=host 上挂**一个共享连接** + 全局提示词 section（所有会话可见，项目路径取全局设置）；关=仅 Godot preset 会话挂载，其余会话没有这些工具 |
| 注入工作流提示词（`promptGuidance`） | 开 | 是否注入工作流提示词 section；仅对挂了 Godot MCP 的会话生效（Godot preset 或原版模式） |
| 提示词中文化（`zhPrompt`） | 关 | 工作流提示词 section 使用中文（默认英文，与内置工具一致）；工具描述与错误消息来自上游 server，保持原文。工具名保持英文 |

- 判定时机：`agent/created`、`agent-preset/selected`（**空白会话**换预设；首轮之后 DSH 拒绝换预设）、设置变更（`loader/volatile-update` 重算全部既有会话的挂载）。路径或 server 入口变化会**重挂**连接；只改 `zhPrompt`/`promptGuidance` 不重挂（提示词文本是动态读取的）。
- 幂等：按**会话 id** 记账，同一会话重复 `agent/created`（真实运行时会以不同 agent 对象重报）不会重复挂载；按配置键判断"已挂的是不是现在该挂的那份"。
- Godot preset（id `godot`，显示名 Godot）由本包 bundle patch 的 `preset-godot` 行声明（DSH 0.1.6+ 起 preset **只**能由 bundle patch 的 `@deepseek-ai/dsh-agent-preset` 声明行注册；`$DSH_HOME/.agent-presets/<id>/` 目录已不再被读取）。它的 plugins 是 standard 的完整副本 + 本包技能目录，同时是"本会话需要 Godot"的标记。
- **行 config 只读 `serverName`**：v0.4 的 `godotMcpRoot` / `projectPath` / `unsafe` 已删除（host 从未消费它们）；server 入口与项目路径一律走 `paths.json` 与 `godotServerDist` / `godotProjectPath`。
- **跟随 shipped `standard` 的 0.2.1 形状**：preset 的 plugins 含 `time-context`（时间上下文）与 `tool-schedule`（提醒工具）两行——standard 在 0.2.1 已包含它们，缺行会让 Godot 会话少这两项能力。`src/tests/preset-patch.test.mts` 锁住这两行在场。任何 `backgroundMode` / `enableRunInBackground` 都不该再出现（0.2.1 已从 `dsh-tool-subagent` 移除；残留不报错但是无声无效配置），同一测试也锁住这一点。

## 失败模式

| 症状 | 原因 | 恢复 |
| --- | --- | --- |
| Godot 会话里没有任何 `mcp__godot__*` 工具，提示词说"尚未连接" | server 入口路径为空 | 在插件页 → 本包页面 → 「Godot 路径」填写 server 入口并保存；重开会话（或等下一次解析）即挂载 |
| 日志出现"未能从 profile 解析到 @deepseek-ai/dsh-mcp-client" | 当前 profile 没装官方 MCP 客户端（DSH 发行版自带，正常不会发生） | 恢复 profile 的官方依赖后重启 DSH |
| 日志出现"挂载 Godot MCP 失败/启动失败" | server 入口不可执行、路径错误、同名 serverName 冲突等（只记错误类型，不回显配置） | 核对 server 入口路径；改设置触发重挂 |
| 工具调用返回 `AUTH_FAILED`/连接错误 | 目标项目的编辑器未运行或项目移动 | 打开编辑器（`godot --headless --editor --path <项目>` 亦可）；官方客户端下次连接自愈 |
| 长时间断线后工具消失 | 官方客户端连续 10 次重连失败后移除该服务器工具 | 打开编辑器后重开会话（会话重建时重新挂载）、改 server 入口/项目路径、在空白会话换预设，或重启 DSH；只改提示词开关不会重挂 |
| `dsh-godot status` 显示"server dist 缺失" | GodotMCP server 未构建 | 在 server 目录构建/发布后 reinstall |
| status 显示"编辑器注册表: 无条目" | 编辑器未开或项目未装 addon | 装 addon（上游 install-godot-project.ps1）并开编辑器 |
| 模式选择器里没有「Godot」 | DSH 升级后旧 `~/.dsh/.agent-presets/godot/` 目录不再被读取（历史目录残留不影响） | 确认 profile 里有 `preset-godot` 行（`dsh --profile <p> --dump-config`）；缺则重装本 bundle 使 patch 声明生效 |
| 编辑器端口固定后连接失败 | 只固定了一端 | 两端同值固定，或都不固定走注册表发现 |

## 数据边界

- 写盘内容仅限：`$DSH_HOME/godot/paths.json`（server 入口 + 按 cwd 的项目路径，均为机器本地配置），以及 profile 用户 patch 层里由 CLI 清理的历史静态 mcp-client 受管块。
- 不读取/写入 Godot 项目文件本身（那是桥接 server 与编辑器 addon 的职责）；CLI 只读注册表 `projects.json`/`entries/` 与端口探活。
- 不处理任何凭据；上游认证用 per-project session token，由 server 自行从注册表读取。官方客户端 spawn 子进程时会清洗凭据样式变量（`/KEY|PASSWORD|SECRET|TOKEN/i`）与 `DSH_*`，本插件显式传入的 `GODOT_MCP_PROJECT_PATH` 不受影响。
- 旧版工具清单缓存 `$DSH_HOME/godot/tools-cache.json` 不再读取；残留在盘上无害，不会被自动删除。

## 路径设置（插件页配置表单）

- 路径设置位于侧栏**「插件」页 → 本包页面**的配置表单（`plugins.bundle.config`，DSH 0.1.6+）内的「Godot 路径」区：**server dist 全局单输入框** + 「项目路径（按工作目录）」批量条目区（添加行 + 已添加条目逐行编辑/删除 + 保存）。
- 浏览器端经同源 `GET/POST /godot-config/api/path-settings` 读写 host 侧 `PathStore`；路由仅接受本机回环 + 同源（`Origin`/`Host`/`sec-fetch-site` 三重校验），方法不是 GET/POST 时 405，请求体上限 8MiB（超限 413），非法 JSON 400。
- **server dist 全局单值**：配置表单里只有一个全局 server dist（不受工作区区分）。host 运行时按 `paths.json` 的 `serverDist` → 插件行 config 的 `godotServerDist` 取值；**两者都为空即"未配置"**（该会话不挂载）。CLI 的解析链（`--server-dist` / `$GODOT_MCP_SERVER_DIST` / `godotMcpRoot`+相对路径）只决定 `install` 时往 `paths.json` 写什么，host 不读这些环境变量。
- **项目路径按工作目录（cwd）批量条目**：每条 `{ cwd, projectPath }`，可多条并存、按 cwd 唯一（重复 cwd 后写覆盖）。会话创建时按其**工作目录 cwd** 在该表里匹配 `projectPath`；**无匹配**时，若全表只有一条则用它兜底，否则回退插件行 config 的 `godotProjectPath`。
- 保存后落到 DSH host 持久文件（`$DSH_HOME/godot/paths.json`），跨会话保留，不依赖浏览器缓存。批量条目同样落 host 侧（MCP 连接在 host 侧挂载、需 host 读到路径），而非浏览器 localStorage。`PathStore` 按文件 mtime 重读，因此**CLI 在另一个进程里 install/uninstall 写下的改动**会在 host 下一次解析路径时生效，不必重启 host。
- 配置表单无会话 cwd 上下文：读取/保存只涉及全局 serverDist 与整份 projects，不显示 currentProjectPath。

## 工具调试界面（已迁出）

通用工具调试界面（工具树 / 手动调用 / 结果与历史 / 状态条）与 Godot 无关，已迁至 `deepseek-harness-project-mcp` 的「工具调试」Tab；本包不提供 `conversation.view` 槽位 Tab，也不暴露 `/godot-workbench/api/*` 路由。
