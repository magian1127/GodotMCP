# 发布说明

## 发布前验证

1. `npm run typecheck && npm run build && npm test && npm run verify` 全绿。
2. `node bin/dsh-godot.mjs`（无参数）退出码 2 且打印用法。
3. 沙盒 install 冒烟：临时 `$DSH_HOME` 下 install → 检查 `paths.json` 只有 `serverDist` 与一条 `{cwd, projectPath}` 条目、且未向 profile 写 mcp-client 行 → uninstall → `projects` 归零（`{"projects": []}`）；若该 profile 的 patch 里有历史静态 mcp-client 受管块，确认已被 install 清理。
4. `dsh --profile <p> --dump-config | grep godot`：`dsh-godot` 与 `preset-godot` 行都在组合树中。
5. （有运行中的 Godot 编辑器时）一次性会话验证挂载链路：插件激活 → 官方 `@deepseek-ai/dsh-mcp-client` 挂到该会话 → 工具名以 `mcp__godot__` 开头 → 调一个只读工具成功 → 截图类工具返回**图片附件**（不再是文本占位）。
6. 归零构建目录再验：删除/改名源文件后清理 `lib/`、`bin/`、`.tsbuild/` 再 `npm run build`——`tsc` 不清理 `outDir`，已删模块/测试的陈旧产物仍会被 `node --test` 或宿主加载（v0.5 重写时实测）。

### 发布阻塞项：tarball 必须自带 skills 快照

`skills/` 是指向上游 GodotMCP 的 junction（本地开发即时同步）。**`npm pack` 不跟随
junction**（Node 将其归类为 symlink，npm-packlist 跳过其内容）——实测 0.4.0 的 tarball
`package/skills/` 条目数为 0。发布前必须先把技能物化进包再打包，例如在 `prepack`
里把 junction 目标内容复制到临时实体目录后再 `npm pack`（发布后由 `postpack` 还原
junction），或改由 CI 从上游快照生成；否则安装者拿到的是没有技能的 preset，
`skill-filesystem` 的 `customSkillDirs` 会指向空目录。

## npm 发布

```sh
npm run build
npm publish --access public
```

发布后用户通道：

```sh
dsh plugin --profile web add deepseek-harness-godot_unified   # bundle(提示词 section)
npx -y deepseek-harness-godot_unified ...                      # CLI(需要本地 checkout 的 bin,或全局安装)
```

注意：CLI 未发布独立 bin 包前，`npx` 通道要求先 `npm install -g deepseek-harness-godot_unified`（bin 名 `dsh-godot`）。

## 版本记录

### 0.1.0（2026-09）

- 首版：MCP 桥接行 CLI（install/uninstall/status）、Godot 工作流提示词 section（bundle 行）、14 例单元测试与 verify-build。
- 已验证：组合校验、一次性会话进程链（spawn → MCP 握手 → tools=20 → WS 认证 Godot 4.7.2）、web profile 实际安装。
- 未验证（待用户重启 web 后补做）：GUI 会话内的模型工具调用轮。

### 0.2.0（2026-10）

- 新增 **Godot 工作台** client 半边：DSH Web 会话顶部第三个 Tab「Godot」，提供全部工具浏览（Godot 置顶 + 按需组激活）、不经对话的手动工具调用/取消、结果（文本/JSON/截图内联/错误码）与历史（localStorage 上限 50）、技能与提示词面查看、桥接链路状态条。client 为 `lib/client.js` 单文件经典脚本（零模块注入、零 react 打包）。
- 新增 host 半边：经 `webServer` 暴露 `/godot-workbench/api/*` 共 6 条同源路由（status/tools/call/cancel/skills/prompt-sections），数据源 `ctx.tools`（schemas/get/execute 全管线）+ CLI 探测逻辑 host 化 + 技能文件读取；`dsh-godot install` 额外写入 bundle 行 config 覆盖。
- 构建链扩展：新增 `tsdown.config.ts` 与 `build:client`；`verify-build.mjs` 增加 client bundle 格式断言。`package.json` 的 `files` 补入 `tsdown.config.ts`（tarball 可重跑 client 构建）。
- 验证状态：静态全绿（typecheck/build/test/verify）已执行；client 交互与 GUI 视觉（Tab 出现、截图内联、组激活后树刷新、历史重放）**待用户重启 web 后执行 GUI 验收**。

### 0.3.0（2026-10）

- **自研桥接 + 动态工具（v0.2 升级版）**：插件自己 spawn GodotMCP server（stdio）+ 内嵌极简 MCP client（`src/bridge/manager.ts` 恢复并沿用）；工具清单**来自 server 的 `tools/list`**（真实 schema/描述，随组激活动态增删），**删除 v0.2 的写死离线占位清单**（`src/bridge/manifest.ts` 移除）——工具不写死，只有桥接与翻译表（`upstream-zh.ts`）。
- **按会话 cwd 传参**：桥接按**当前调用会话的工作目录**解析项目路径（`paths.json` 按 cwd 批量条目，无匹配回退全局）并作为 `GODOT_MCP_PROJECT_PATH` 传给 server；cwd 变化时重启切换——不同会话工作区可以做不同 Godot 项目，不再绑死目录（官方 mcp-client 行方案因此弃用）。
- **工具清单持久缓存（零默认开销）**：连接成功后把 server `tools/list` 快照缓存到 `$DSH_HOME/godot/tools-cache.json`（与 server dist 绑定校验）；缓存命中时打开 DSH 无需连接（工具直接可见、零闲置开销），**只有首次工具调用或工作台「连接 Godot」按钮才拉起桥接**；无缓存时工作台点「连接」获取并缓存（新增 `POST /godot-workbench/api/connect` 路由 + 状态条按钮）。
- **无默认连接**：server 默认不启动、不连编辑器；Godot preset 会话创建只从持久缓存加载清单（不 spawn）；空闲 10 分钟自动关闭；工具门面在 server 退出后保持在册，调用时自愈。
- **防卡死**：门面注册清单 diff（无变化跳过）；`tools/change` 增量 deny 经 150ms 合并窗口（无 diff 不调 restrict，防事件回声）；工作台 `/api/tools` TTL 缓存 + 仅 `discover_tools` 在场才调用 + 前端描述截断（沿用 2026-09-05/06 修复）。
- **CLI 语义更新**：`install` 不再写官方 mcp-client 行（自研桥接不需要；安装时清理历史受管块防双轨），改把路径写入 `$DSH_HOME/godot/paths.json`；`status` 报告路径配置 + 编辑器注册表；`--read-only` 等原官方行 env 参数标记为不再生效并警告。
- 验证状态：typecheck/build/test（65 例）/verify 全绿；未执行运行时 GUI 验收（不改运行中 web；安装层组合校验与一次性会话进程链待用户环境）。

### 0.4.0（2026-10）

- **首次 npm 发布**：0.1.0–0.3.0 为开发期里程碑记录，均未发布至 npm；本次以 0.4.0 首次发布。
- 功能基线：自研桥接 + 动态工具（清单来自 server `tools/list`，随组激活 `discover_tools` 动态增删）、按会话 cwd 传参（`$DSH_HOME/godot/paths.json`）、工具清单持久缓存（`$DSH_HOME/godot/tools-cache.json`，与 server dist 绑定校验）、Godot 工作台（会话顶部第三 Tab + `/godot-workbench/api/*` 六条同源路由）、`dsh-godot` CLI（install/uninstall/status，v0.4 起不再写官方 mcp-client 行）。
- 环境要求：DeepSeek Harness ≥ `0.1.5-rc.1`；Node.js `^22.19.0 || >=24.0.0`。
- 验证状态：typecheck/build/test/verify 随提交执行；运行时 GUI 验收（工作台交互、Godot preset 会话工具调用）待用户环境。

### 0.4.1（2026-09-29）

- **DSH 桌面版（Electron）支持**：profile 探测识别 Electron Host，CLI 对 desktop 只做 home 级操作。
- 环境要求不变。

### 0.4.2（2026-10-18）

- **适配 DSH 0.2.1-rc.2**（加载门禁 + preset 形状对齐，桥接与工作台逻辑未动）：
  - `package.json` 新增 `engines.dsh: ">=0.2.0-rc.2 <0.3.0-0"`（`engines.node` 原样保留）。0.2.1 起加载器用 `engines.dsh` 单独判定插件兼容性，旧版运行时（0.1.x）与 0.3.x 会被拒绝加载。
  - `cordis.patch.yml` 的 `preset-godot`：persona 行去掉 `suffix`（0.2.1 移除 `{{cwd}}` 提示词变量，残留会在请求准入前被严格插值直接拒绝）；补 `time-context` 与 `tool-schedule` 两行以跟随 shipped `standard`；删除四处已移除的 `backgroundMode`（残留不报错但无声失效）。
  - `src/tests/preset-patch.test.mts` 新增两条守护（不得有 `{{cwd}}`/`suffix`/`backgroundMode`；`time-context`+`tool-schedule` 必须在场）。
- 环境要求：DeepSeek Harness `>=0.2.0-rc.2 <0.3.0-0`；Node.js `^22.19.0 || >=24.0.0`。
- 验证状态：`npm run typecheck` / `npm test`（90 例）/ `npm run verify` 全绿；`dsh --profile web --dump-config` 组合校验通过（`preset-godot` 含新增两行、subagent 行已无 `backgroundMode`）；`check-engines-dsh.mjs` 正向退出 0、负向（`0.1.7-alpha.2`/`0.2.0-rc.1`/`0.3.0`）退出 1。**未执行**：GUI 内 Godot preset 会话的实际工具调用验收（需用户环境，且本包运行于已启动的 `dsh web`）。

### 0.5.0（2026-10）

- **破坏性变更：接入形态重写，不再自研 MCP 客户端**。连接、握手、工具发现、`list_changed` 增量、断线重连与图片投影全部交给宿主自带的 `@deepseek-ai/dsh-mcp-client`（运行时从 profile 解析，不写进 dependencies）。删除 `src/bridge/manager.ts`（自研 stdio JSON-RPC 客户端）、`src/bridge/tools-cache.ts`（工具清单缓存）、`src/bridge/upstream-zh.ts`（上游中文描述映射）与各自测试；`src/injection.ts` 的 deny/限制/描述翻译纯函数一并移除。
- **工具名变化**：`godot_*` → `mcp__godot__*`（官方固定约定 `mcp__<serverName>__<rawName>`；`serverName` 可由插件行 config 覆盖）。工具清单仍由 server 的 `tools/list` 动态提供，`discover_tools` 组激活仍工作（server 发 `list_changed`，官方客户端增量更新工具集）。
- **挂载时机**：`agent/created`（preset=`godot`）与空白会话（首轮之前）切到 godot 时挂进该会话的 `agent.ctx`；切走 / 会话销毁 / 插件卸载时卸载。非 Godot 会话**根本不挂载**（不是 deny 隐藏），因此不再需要 restrict 补丁与 `tools/change` 合并窗口（150ms 窗口一并删除）。
- **连接与就绪**：进入会话即建立连接（不再"首次调用才拉起"，也没有空闲 10 分钟自动关闭）；`failOnStartupError:false`，失败只记日志并进入官方客户端重连（500ms 起、每次翻倍、上限 30s，连续 10 次后移除工具）。会话创建最多等 10 秒（`READY_GRACE_MS`）让首条消息通常就能看到工具，超时则工具在后台就位、下一轮可见；挂载永不抛错打断会话创建。
- **按会话 cwd 传项目路径**：挂载时按该会话 cwd 解析项目路径（`paths.json` 按 cwd 条目 ?? 唯一条目兜底 ?? 全局 `godotProjectPath`），作为 `GODOT_MCP_PROJECT_PATH` 传入；子进程 cwd 也是会话 cwd（shim 没有显式 env 时按 cwd 查机器级注册表兜底）。不同会话可以是不同 Godot 项目。
- **新增 `src/mcp-mount.ts`**（入口启动方案 + Config 组装 + 挂载身份键 + `errorName`，纯函数，不做 I/O）与 `src/tests/mcp-mount.test.mts`；`profile-modules.ts` 新增 `loadMcpClient()`；`section-text.ts` 新增 `notConfiguredSectionText()`（server 入口为空时的"如何配置"提示词，原版模式的全局 section 同样按此判据切换）。
- **配置面收窄**：插件行 config 只声明/解析 `serverName`；v0.4 的 `godotMcpRoot` / `projectPath` / `unsafe` 删除（host 从未消费它们，留着只会让人以为填了会生效）。
- **路径热生效**：`PathStore` 按文件 mtime 重读——CLI 在另一个进程里 `install`/`uninstall` 写下的 `paths.json` 会被长跑 host 在下一次解析时读到，不必重启 host。
- **原版模式语义调整**：`injectLegacyMode` 从"全局注册工具"改为在 host 作用域挂**一个共享连接** + 全局提示词 section；该模式下不再按会话挂载（避免同名工具挂两遍、多起进程），项目路径取全局设置。
- **`zhPrompt` 收窄**：只影响工作流提示词 section；工具描述与错误消息来自上游 server，保持原文。
- **CLI 语义**：`install` 只写 `$DSH_HOME/godot/paths.json`（`serverDist` + `{cwd, projectPath}`）并清理历史静态 mcp-client 受管块，不写 profile 行；`uninstall` 清空 `paths.json`；`status` 体检不变。`patch-row.mts` 只保留历史清理能力。
- **已移除**：`$DSH_HOME/godot/tools-cache.json` 不再读取（旧文件残留在盘上无害、不自动删除）、上游中文描述翻译表、deny/restrict 注入补丁、150ms `tools/change` 合并窗口。
- **并入「工作台迁出」这一未发布里程碑**（见本文后段），preset 迁移里程碑已在 0.4.x 落地、本版沿用。
- 环境要求不变：DSH `>=0.2.0-rc.2 <0.3.0-0`、Node.js `^22.19.0 || >=24.0.0`。
- 验证状态：`npm run typecheck` 通过、`npm run verify` 全部通过、`node --test ".tsbuild/tests/*.test.mjs"` 69 例全绿（2026-10-24 于本仓库实测）。**未执行**真机 GUI 验收——Godot preset 会话内的工具调用轮与截图附件呈现、原版模式共享连接、子代理各自挂载、插件页「相关路径」区读写，均需在已启动的 `dsh web` 与运行中的 Godot 编辑器上确认。

### 历史里程碑：preset 迁移（legacy 目录 → bundle 声明行）

- **背景**：DSH 0.1.6+ 起不再读取 `$DSH_HOME/.agent-presets/<id>/` 目录，Godot preset 因此从模式选择器消失（用户报障）。preset 现在只能由 bundle patch 里的 `@deepseek-ai/dsh-agent-preset` 声明行注册。
- **变更**：`cordis.patch.yml` 增加 `preset-godot` 声明行（`plugins` 逐字迁自 `agent.cordis.yml`，并按当时安装的 shipped `standard` 校正了 `tool-ralph`（`disabled: true`）与 plan-mode section 文案两处漂移）；新增包内 `skills/` junction（→ 上游 `plugin/godot-mcp-unified/skills`，31 文件）与 `customSkillDirs` 的包 realpath 解析；新增 `src/tests/preset-patch.test.mts`（4 例）守护 patch 形状。
- **验证状态**：`npm run typecheck && npm test`（86 例）全绿；`dsh --profile web --dump-config` 出现 `preset-godot`；运行态 `include:preset-godot` 行 active；技能 realpath 实测解析到上游目录。**未执行**：GUI 模式选择器点击验收（需用户在刷新后的页面确认「Godot」出现并可选）。
- **注**：`npm run verify` 在本机为 1 项失败（client bundle 断言 `settingsScope`，而 `src/client/index.ts` 已有未提交 WIP 改用 `configForms`）；该失败与本迁移无关，属既有 WIP 与 verify 脚本的版本不同步。

### 0.5.0 合入：工作台迁出（移除会话 Tab，仅保留路径设置路由）

- **背景**：会话顶部第三个 Tab「Godot」那个工作台本质是**通用的工具调试界面**（工具树 / 手动调用 / 结果与历史 / 提示词面），与 Godot 无关；它读的是宿主全局工具面，只是恰好长在本包里。用户要求把它迁到 `deepseek-harness-project-mcp`，本包只保留与 Godot 真正相关的能力。
- **移除**：`src/workbench/`（executor / handlers / probes / routes / skills）、`src/client/` 的 Workbench / ToolTree / CallPanel / ResultPanel / SkillsPanel / css / grouping / history / locales / form-schema、对应 6 个测试文件、`conversation.view` 槽位注册，以及 `/godot-workbench/api/*` 全部路由（status / tools / connect / path-settings / call / cancel / skills / prompt-sections）。
- **保留并移动**：`src/workbench/path-store.ts` → `src/path-store.ts`（桥接按 cwd 解析路径与插件页配置表单都依赖它，不是工作台专属）；`path-settings` 一条路由摘成 `src/path-settings-route.ts`，前缀改为 `/godot-config/api/path-settings`，原三重同源校验、GET/POST 合并注册、405/413/400/500 分支原样保留。`src/client/index.ts` 的 `inject` 收为 `['slots','locale','configForms']`（`connection` 只有工作台用）。
- **替代**：需要手动调试工具调用时，用 `deepseek-harness-project-mcp` 的「工具调试」Tab（工具面来自宿主全局 `tools` 服务，包含本包挂载的 `mcp__<名称>__*`）。
- **验证状态**：`npm run typecheck` / `npm test`（65 例）/ `npm run verify` 全绿；`lib/` 无工作台残留，`lib/client.js` 中 `conversation.view` 与 `/godot-workbench` 均已消失（`verify-build.mjs` 新增反向断言守护）。**未执行**：GUI 内确认「Godot」Tab 消失、插件页配置表单仍可正常读写（需用户刷新页面确认；`/godot-config/api/path-settings` 只在单测级验证过，未经真实 `webServer` 请求）。
- **注**：`npm test` 用例数从 107 降到 65，因为此前 `.tsbuild/` 里残留着已删测试的陈旧编译产物仍在被 `node --test` 执行；65 是清掉残留后的真实数字。
