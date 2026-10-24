# 开发说明

## 仓库结构

```
src/
  index.ts            host 插件:inject ['systemPrompt','agents'];
                     按会话挂载官方 mcp-client(agent/created、agent-preset/selected)+
                     原版模式的 host 共享连接 + 路径设置路由装配
  mcp-mount.ts        官方 mcp-client 的纯辅助:server 入口启动方案(shim 直启 / 脚本经 node)、
                     Config 组装(env GODOT_MCP_PROJECT_PATH + 会话 cwd)、配置身份键、errorName
  section-text.ts     section 内容(双语,zhPrompt 切换;工具名按官方 mcp__<serverName>__ 生成)
  injection.ts        设置与挂载决策纯函数(resolveSettings/isGodotPreset/shouldInjectPrompt/
                     toolNamePrefix;node --test)
  profile-modules.ts  profile 解析加载 schemastery 与 @deepseek-ai/dsh-mcp-client(0.1.7-rc 起
                     require.resolve+exports import 条目+异步 import 预载(TLA),同步
                     require(esm) 会撞宿主 hooks 管线「not yet fully loaded」死锁)
  path-store.ts       路径持久存储(全局 serverDist + 按 cwd 批量项目条目,落 $DSH_HOME/godot/paths.json;
                     惰性加载 + **按文件 mtime 重读**(CLI 在另一个进程写入也能被长跑 host 读到);
                     会话挂载时按 cwd 读取,也是路径设置路由的写入目标)
  path-settings-route.ts  路径设置同源路由装配(GET/POST /godot-config/api/path-settings 合并为一条;
                     回环+同源三重围栏、405/413/400、可逆 teardown;webServer 缺席返回 undefined)
  constants.ts        host 侧常量(PKG/VERSION/BUNDLE_ROW_ID/SECTION_NAME/SECTION_ORDER/
                     GODOT_PRESET_ID/DEFAULT_SERVER_NAME)
  types.ts            最小类型面(Disposer/SystemPromptService/HostContext/PluginFiberShape/
                     AgentShape/AgentPresetsShape/WebServer 形状)
  client/             client 半边(仅 src/client/index.ts 被 tsdown 打包为 lib/client.js)
    index.ts          入口:plugins.bundle.config 配置表单注册
    config-page.ts    插件页配置表单(原版模式/工作流提示词/中文提示 + 路径区;DSH 0.1.7+ 经
                     configForms 读行 config,入口 id dsh-godot)
    api.ts            同源 fetch 封装(仅路径设置读写;相对路径)
  bin/
    dsh-godot.mts     CLI 入口(被 import 无副作用)
    patch-row.mts     受管块幂等读写(标记块 + 原子写 + 写锁 + 陈锁回收;仅供历史兼容/清理)
    resolve.mts       server dist/项目解析校验(纯函数)
    spawn.mts         Windows 命令解析与跨平台 spawn(.ps1 优先/Node shim 直连/PS 转发)
    cli/
      constants.mts   CLI 侧常量与类型(与 host 侧独立,测试守护对齐)
      paths.mts       DSH_HOME/profile/patch/注册表路径
      validate.mts    纯校验函数(profile 名/serverName/行 id/端口)
      probes.mts      bundles/注册表/端口/pid 探测
      main.mts        参数解析与命令分发(install/uninstall/status;写 paths.json,不写 mcp-client 行)
  tests/              node:test 单元测试(patch-row/resolve/plugin-smoke/注入/mcp-mount/路径路由/路径存储/preset patch)
cordis.patch.yml      bundle patch:insert 行 dsh-godot(本包自身) + preset-godot
                      (Godot agent preset 声明行;DSH 0.1.6+ 起 preset 只能由
                      @deepseek-ai/dsh-agent-preset 声明行注册,legacy 的
                      $DSH_HOME/.agent-presets/<id>/ 目录已不再被读取)
skills/               → 上游 GodotMCP plugin/godot-mcp-unified/skills 的 junction
                      (单一真身在上游,改上游即时生效;npm pack 不跟随 junction,
                      发布前需物化或由 CI 快照)
tsdown.config.ts      client 单文件经典脚本打包配置
verify-build.mjs      产物存在性/语法/patch 行形状/client bundle 格式/CLI usage 冒烟
```

构建产物：`lib/`（host 为 tsc 多文件、`lib/client.js` 为 tsdown 单文件经典脚本）、`bin/`（CLI,.mts→.mjs 原地）、`.tsbuild/`（tests）。构建链：`build:host`（tsc）、`build:cli`（tsc）、`build:tests`（tsc）、`build:client`（tsdown `--config tsdown.config.ts`）；`npm run build` 依次执行这四步。**没有 clean 步骤**：删/改文件名后陈旧产物仍留在目录里，见「不可破坏的约束」第 1 条。

## 接入形态（官方 MCP + 按会话挂载）

v0.5 把 v0.4 的自研桥接整体删掉（`src/bridge/manager.ts`、`tools-cache.ts`、`upstream-zh.ts`）。原因与取舍：

1. **为什么不自己实现 MCP 客户端**：官方 `@deepseek-ai/dsh-mcp-client` 已经处理握手协议协商、分页发现、`list_changed` 增量、代际原子交换、断线退避重连、图片持久化与 `is_error` 语义；自研版本每升级一次 DSH 就要复刻一遍，且只能做到子集（图片降级为文本、resources 不透传）。接入层只应回答"哪个会话、哪个 server"，不该重写协议。
2. **为什么不写 profile 的 mcp-client 行**（历史方案，v0.4 之前）：行级 config 把 `GODOT_MCP_PROJECT_PATH` 固化在 profile 里；不同会话工作区可以是不同 Godot 项目。按会话挂载把 env 与 cwd 都变成会话维度，这个问题消失。
3. **为什么不在 preset 的 `plugins` 里加静态 mcp-client 行**：preset 树在 profile 加载时**急切挂载一次**（`agent-preset-registry` 的 `register → activate → mountPreset`），意味着没有任何 Godot 会话时也会 spawn shim 并握手；且 config 仍是静态的（无法按会话 cwd）。按会话挂载把成本推迟到真正需要它的会话。
4. **挂载点必须是 `agent.ctx`**：Agent 作用域才让工具只对该会话可见。挂到 host 上等于所有会话共享——那是原版模式刻意的语义，不是默认。
5. **作用域继承的事实的**：子代理通过 `agentPresets.composeFrom(childCtx, parent.ctx)` 加入**父级的 preset 版本**，不是父级 Agent 作用域；因此子代理看不到父会话 agent scope 里挂的工具。为保留"Godot 会话的子代理也能用 Godot 工具"，本插件对子代理同样挂载（各自一个 shim；机器级 daemon 仍是单例）。

### 挂载决策（`src/index.ts` 的 `syncAgent`）

```
agent/created / agent-preset/selected(仅空白会话)/ loader/volatile-update
  │
  ├─ 会话 id 缺失 → warn 并跳过(契约漂移;宁可少挂,不能按对象引用重复挂)
  ├─ preset != godot 或 原版模式 或 mcp-client 解析失败 → 拆掉已有挂载
  ├─ 解析路径:dist(全局 paths.json serverDist ?? 设置) + projectPath(按 cwd 查表 ?? 唯一条目 ?? 全局)
  ├─ 配置键(dist|project|cwd)与在册一致且活着 → 只同步提示词开关
  ├─ dist 为空 → 不挂载 + "如何配置"提示词(不臆造工具调用)
  ├─ buildMountConfig → 官方 Config 自校验 → agent.ctx.plugin(mcpClient, cfg)
  │    failOnStartupError:false;启动失败只 warn(按身份摘除 fiber,下次设置变更重挂)
  └─ agent/created 返回有上限的就绪宽限(READY_GRACE_MS=10s);其余入口不等待
```

- **幂等按会话 id**：`agent/created` 可能对同一 sessionId 触发两次且传不同 agent 对象；按键记账才挡得住第二次 `ctx.plugin()`（官方客户端只按 scope 对象防重名，挡不住新对象同 scope）。
- **不 await 连接**：等握手可能拖到 SDK 60s 超时；只给会话创建一个有上限的宽限，超时后工具由 `tools/change` 在下一轮就位。
- **原版模式**：`injectLegacyMode` 在 host 作用域挂一个共享连接（`ctx.plugin`），并注册全局 section；该模式下 `syncAgent` 直接短路（否则同名工具会挂两遍）。全局 section 与按会话 section 同一判据：server 入口为空时给"如何配置"文本，而不是完整工作流。

### 配置组装（`src/mcp-mount.ts`）

- 入口方案：`shim` 原生可执行文件直接 spawn（`command=dist,args=[]`）；`.mjs/.cjs/.js` 历史入口经 `process.execPath` 执行（`command=node,args=[dist]`）。
- Config：`transport:'stdio'`、`serverName`、`command/args`、`env:{GODOT_MCP_PROJECT_PATH}`（项目路径为空时**不设** env，让 shim 按 cwd 查注册表）、`cwd`=会话 cwd（为空则不覆盖）、`toolCallTimeoutMs:60000`、`failOnStartupError:false`。
- `mountConfigKey` 把 serverName/命令/参数/env/cwd 拼成身份键：设置或路径变化即"不是同一份配置"，需要重挂；只改提示词开关不重挂。
- **挂载前自校验**：调用官方 `Config` 工厂验证形状。原因是 Cordis 的 config 校验失败会把**整个 config**（含 env 值）写进它自己的日志；自校验让这类输入根本到不了 `ctx.plugin()`。诊断只记 `error.name`。

## 路径设置路由（`/godot-config/api/path-settings`）

- 唯一职责：让插件页配置表单读写 host 侧 `PathStore`（`src/path-store.ts`）。GET 返回整份 `{ serverDist, projects, currentCwd, currentProjectPath }`，POST 整份覆盖写回。
- **GET/POST 必须合并为同一条 `webServer.register` 调用**：该服务按 `path` 精确注册、不区分 HTTP 方法，同路径后注册会覆盖先注册的那个（表现为 405）。handler 内按 `req.method` 分支。
- 三重围栏沿用原工作台路由的实现：peer 回环（`isLoopback`）+ `Host` 白名单（`hostAllowed`）+ `sec-fetch-site`（`fetchSiteAllowed`）+ `Origin` 同源；非 GET/POST → 405，请求体超 8MiB → 413，非法 JSON → 400，500 只回固定文案（原始错误经 `warn` 进 host 日志）。
- 装配经 `ctx.inject(['webServer'], ...)`（`typeof ctx.inject === 'function'` 时），挂上后用 `mountCtx.effect(() => dispose, ...)` 登记 teardown；`webServer` 缺席时 `mountPathSettingsRoute` 返回 `undefined`，不登记 teardown 也不报错。路由装配与挂载策略无关。
- **已知局限（可忽略）**：装配时把 `serverDistPath` / `projectPath`（插件行 config 的空存储兜底值）按值快照，`loader/volatile-update` 之后 GET 的兜底值可能陈旧。这两个字段在 v0.5 的插件页表单里已不可编辑（表单只编辑开关 + 路径区），所以实际影响只出现在"手工改 profile 行 config 同时存储为空"的场景。

## CLI 语义（v0.5）

- `install`：校验 dist/项目 → 写 `$DSH_HOME/godot/paths.json`（`serverDist` + `{cwd, projectPath}` 条目，`--cwd` 默认当前目录）→ **清理历史静态 mcp-client 受管块**（`removeManagedRow`，防双轨）→ 提示插件页配置表单可按 cwd 增改。`--read-only/--unsafe/--rate-limit/--editor-port/--runtime-port/--timeout-ms` 为历史行 env 参数，不再写入并警告。
- `uninstall`：清空 paths.json（`{projects: []}`）+ 清理受管块。
- `status`：报告 paths.json（serverDist 存在性、项目条目有效性）、编辑器注册表体检、bundle 行状态。
- paths.json 读写与 host 侧 `PathStore` 同源形状（不 import host 模块；文件级最小实现，测试不直接覆盖）。
- `patch-row.mts` 只服务历史清理与幂等块读写；v0.5 不再生成新的 mcp-client 行。

## client 半边（插件页配置表单）

- client 作为 client bundle（`lib/client.js`）随插件冷启动挂载；`dsh.client` 声明为 `{ platform:'web', immediately:true, inject:[] }`。
- **fiber 注入 `['slots','locale','configForms']`（bundle exports.inject）**：浏览器端 Loader 从 bundle exports 读 `inject` 构建 fiber 服务注入集；配置表单经 `ctx.slots`（`plugins.bundle.config` 槽位）、`ctx.locale`（文案字典）与 `ctx.configForms`（按 profile 行 id 取 ConfigForm）访问宿主。注意与 `dsh.client.inject`（包名级 factory 先行边，本包无，保持 `[]`）是两回事。
- **零包依赖/零 react 依赖决策**：数据走浏览器原生 `fetch` 同源相对路径；`react` 不打进 bundle，运行时经 DSH 模块表供应（tsdown `deps.neverBundle: ['react']`）；client 不进 tsc include，无 `@types/react`。源码 `.ts` + `React.createElement`（无 JSX/TSX）。
- **`slots.inject` 回调必须返回注册清理句柄**（ui-chat 同款契约）：epoch 变化（HMR/重挂）时先撤销旧注册再重跑回调；缺失则旧注册泄漏。
- **路径区读写走 `/godot-config/api/path-settings`**：草稿由配置页统一管理，只有点「保存」才 POST 整份覆盖；读取失败在表单错误区可见。

## 不可破坏的约束

1. **构建目录没有自动清理**：`tsc` 不删 `outDir`。删除/改名源文件后，`lib/`、`bin/`、`.tsbuild/` 里的旧产物会被 `verify-build.mjs`、`node --test` 甚至宿主继续加载（本仓库 v0.5 重写时实测：已删的 `bridge-manager.test.mjs` 仍被 `node --test` 执行）。改动文件集后回收这三个目录再 `npm run build`。
2. **不写死工具清单**：本插件**根本不注册工具**——工具由官方 mcp-client 按 server 的 `tools/list` 提供。不得再引入离线占位/内置工具表/描述翻译表。
3. **不自建 MCP 客户端**：拿不到官方 `mcp-client` 时降级为"不挂载 + 错误日志"，不得退回 `spawn + JSON-RPC` 的自研实现。
4. **会话创建路径不抛异常**：`syncAgent` 全部路径 try/catch，失败只 warn；`agent/created` 监听器返回 promise 时也必须不 reject。
5. **挂载记账按会话 id**（不是对象引用），卸载按身份（`mounts.get(key) === entry`）摘除。
6. **先自校验再挂载**：`validateMountConfig` 必须在 `ctx.plugin()` 之前；诊断只记 `error.name`，不记 message/config。
7. **受管块边界**（历史兼容/清理用）：`# dsh-godot:begin/end` 之外内容只读；卸载只剩注释时写回 `[]`；原子写（tmp+rename）+ 同目录写锁。
8. **常量双侧同步**：host `src/constants.ts` 与 CLI `src/bin/cli/constants.mts` 独立定义，`patch-row.test.mts` 对齐断言守护；改行 id/标记两侧同改。
9. **spawn 安全**（CLI 侧）：不用 `shell: true`；无法安全转发的参数（`%`、`!`、引号、尾反斜杠+空格）报错而非篡改；`dsh plugin` 的 `--profile` 必须跟在子命令后面。
10. **section 注册容错**：重复注册（双行并存）按 duplicate 吞掉并 warn；`systemPrompt` 服务缺失降级 warn 不抛错；teardown 经 `ctx.effect` 可逆。
11. **不修改 GodotMCP server 仓库**：上游行为按现状接受（不改他人仓库），本仓库文档只记录接入语义。

## 测试策略

- 单元（node:test）：标记块生命周期（新建/幂等/参数重写/用户内容保全/`[]` 恢复/往返解析）、常量对齐、resolve 优先级。
- **挂载策略**（`src/tests/plugin-smoke.test.mts`）：用临时 `$DSH_HOME` + 临时 profile 里的**桩** `@deepseek-ai/dsh-mcp-client`（CJS）走真实 profile 解析路径：默认不挂/非 Godot 不挂、Godot 会话挂一次且 Config 形状正确、重复 `agent/created` 幂等、原版模式走 host 共享连接（含"入口为空 → 未配置提示词，配置后切回完整工作流"）、切预设卸载、`agent/disposed` 回收、插件 teardown 回收、设置变更重挂、`zhPrompt` 只改文本不重挂、section 重复注册容错、服务缺失降级。
- **Config 组装**（`src/tests/mcp-mount.test.mts`）：shim/脚本启动方案、env 与 cwd 的有无、非法 serverName/空 dist 抛错、配置身份键稳定性、`errorName` 不回显 message。
- 注入纯函数（`src/tests/injection.test.mts`）：设置解析、`isGodotPreset`、`shouldInjectPrompt`、`toolNamePrefix`。
- 路径存储（`src/path-store.ts`）：`normalizeProjects` 去重/上限 100/字段校验、`cleanPath` 引号剥除、`projectPathToDir` 文件→目录、惰性加载与原子持久化（跨实例读回）、**外部进程改写后按 mtime 热生效**。
- 路由冒烟（`path-settings-route.test.mts`，mock webServer）：GET 整份读取与 POST 整份覆盖、同源/回环三重围栏、405/413/400。
- 组件与 bundle 形状：由 `verify-build.mjs` 断言（client 产物存在、经典脚本格式、`plugins.bundle.config` 注册、路径设置路由前缀、无 `conversation.view`/`/godot-workbench` 残留、未打包 react）；交互与视觉效果由 GUI 验收覆盖。
- **preset patch 形状**（`src/tests/preset-patch.test.mts`）：`preset-godot` 行存在且仍是 `@deepseek-ai/dsh-agent-preset` 声明、id/显示名/description 非空、关键能力行在场、`isolate` realm 在场、`customSkillDirs` 从本包 realpath 解析；**DSH 0.2.1 起另加三条**——非注释行里不得出现 `{{cwd}}`、persona 行不得声明 `suffix`、subagent 行不得出现 `backgroundMode`/`enableRunInBackground`，且 `time-context`/`tool-schedule` 两行必须在场。
- 运行期验证（不重启 web）：`dsh-godot status` + `dsh --profile <p> --dump-config`（组合校验）；一次性会话（`dsh --profile <p> --patch <行文件> "任务"`）验证挂载链路（插件激活 → `mcp__godot__*` 工具出现 → 调用）。

## 与 v0.4 的差异（迁移记录）

删除：`src/bridge/manager.ts`（自研 stdio JSON-RPC 客户端）、`src/bridge/tools-cache.ts`（工具清单缓存）、`src/bridge/upstream-zh.ts`（中文描述映射）、`src/tests/bridge-manager.test.mts`、`src/tests/tools-cache.test.mts`；`src/injection.ts` 的 deny/描述翻译/错误包装纯函数。

新增：`src/mcp-mount.ts`、`src/tests/mcp-mount.test.mts`；`profile-modules.ts` 的 `loadMcpClient()`；`section-text.ts` 的 `notConfiguredSectionText()`；`path-store.ts` 的 mtime 重读。

配置面收窄：插件行 config 只声明/解析 `serverName`（v0.4 的 `godotMcpRoot` / `projectPath` / `unsafe` 删除——host 从未消费它们，留着会制造"填了却无效"的假象）。

行为变化：工具名 `godot_*` → `mcp__godot__*`；进入 Godot 会话即连接（不再懒启动/空闲关闭）；缓存文件不再读取；`zhPrompt` 只影响提示词；`injectLegacyMode` 从"全局注册工具"变为"host 共享连接"；`agent-preset/selected` 只在**空白会话**（首轮前）有效（DSH 首轮后拒绝换预设）。版本 0.5.0（破坏性：工具名与接入形态）。

## DSH 0.2.1 适配（2026-10）

改动性质：**加载门禁 + preset 形状对齐**。

- `package.json` 新增 `engines.dsh: ">=0.2.0-rc.2 <0.3.0-0"`（`engines.node` 原样保留，`engines` 仍是对象）。
  DSH 0.2.1 的 `packages/boot/app-boot/src/plugin-compatibility.ts` 里 `unmetRequirements()` 规则是：**有 `engines.dsh` 就单独决定，`peerDependencies` 全被忽略**；判定用 `semver.satisfies(version, range, { includePrerelease: true })`（不带该选项时 `0.2.1-rc.2` 这类预发布会被误判不通过）；`engines` 非对象或 `engines.dsh` 非字符串会抛错并拒绝加载；`engines.dsh` 里写 `workspace:*` 一律视为不兼容。
- 验证命令（在 DSH 检出目录跑，用官方实现判定，不复刻 semver）：
  `npx tsx <workspace>/temp/check-engines-dsh.mjs deepseek-harness-godot_unified` → 退出码 0；
  负向 `--runtime 0.1.7-alpha.2` / `0.2.0-rc.1` / `0.3.0` → 退出码 1。
  **`0.2.0-rc.2` 是区间下界本身，判定为兼容，不能当负向样本。**
- `cordis.patch.yml` 的 `preset-godot`：
  - persona 行去掉 `suffix`（0.2.1 移除 `{{cwd}}` 提示词变量，残留会在请求准入前被严格插值直接拒绝；目录改由 working-directory 服务以用户上下文供给）。`{{model}}` 仍注册，保留。
  - 补 `time-context` 与 `tool-schedule` 两行（shipped `standard` 在 0.2.1 已含，preset 是它的副本必须跟随）。
  - 删除四处 `backgroundMode`（`continuable` ×2、`one-shot` ×2）：0.2.1 移除该键、`enableRunInBackground` 与模型参数 `run_in_background`，改由 activation + 父级完成通知交付结果。**实测 Schemastery 不拒绝未知键**（用官方 `Config` schema 验证 `backgroundMode` 仍被原样接受），所以残留不会报错、只会无声失效——因此必须靠测试锁住。
- 逐个核对的官方包（0.2.1 全部仍在、未改名、未拆包）：`dsh-agent-preset`、`dsh-persona`、`dsh-agent-instructions`、`dsh-time-context`、`dsh-tool-fs`、`dsh-tool-present`、`dsh-tool-skill`、`dsh-tool-subagent`、`dsh-tool-web`、`dsh-skill-filesystem`、`dsh-mcp-client`、`dsh-tool-schedule`。各包 `Config` 键逐一用官方 schema 实跑验证。
- **`dshHome` 已从 `agent-instructions` 行移除**（`docs/upgrade-guide/v0.2.1-alpha.1/instruction-home-override`）：该插件改用 `resolveDshHome()` 统一解析，profile/patch 行上残留的 `dshHome` 会保留但被忽略。实测官方 schema 仍接受该键（Schemastery 不拒绝未知键），所以只能靠"本包 patch 从未设置它"这一事实保证无影响——本包 `agent-instructions` 行只有 `maxBytes`。注意 `dsh-skill-filesystem` **保留**了自己的 `dshHome`/`agentsHome`/`customSkillDirs` 字段，本次删除只覆盖指令加载器。
- **`{{cwd}}` 的注册事实**：0.2.1 里 `systemPrompt.variable()` 的注册点只剩 `provider` 与 `model`；`Session.header.cwd` **仍然存在**（本包按 cwd 解析 Godot 项目路径依赖它）。
- **skills 契约与本包无关**：本包没有 `ctx.skills` 调用点，技能文件由 Godot preset 的 `dsh-skill-filesystem` 行从包内 `skills/` 目录加载。**不要**为"对齐"引入官方 skill provider 改写接入方式。

## 官方 mcp-client 的行为契约（本包依赖的真值）

来源：`packages/mcp/mcp-client/src/index.ts`、`transport.ts`、`README.zh.md`（DSH 0.2.1-rc.2）。

- `Config` 是 stdio / streamable-http 的 union；stdio 必填 `serverName`（`/^[A-Za-z0-9_-]{1,32}$/`）与 `command`，`args/env/cwd/toolCallTimeoutMs(60s)/failOnStartupError(false)` 有默认值。
- `apply` 在挂载时 await 首次连接 + 工具发现；失败时若 `failOnStartupError:false` 只记日志并进入重连。**这一点决定了本插件的挂载点与宽限设计**。
- 同 scope 内 `serverName` 唯一（`WeakMap<scope, Set<string>>`）；不同 Agent scope 可复用同名——多会话各挂各的 `godot` 是合法的。
- 工具名 `mcp__<serverName>__<rawName>` 是固定约定；原始名只用于 `tools/call`。
- stdio 子进程环境 = `scrubbedParentEnv()`（删 `/KEY|PASSWORD|SECRET|TOKEN/i` 与 `DSH_*`）+ 配置的 env。
- 重连默认：500ms 起翻倍、上限 30s、连续 10 次后移除工具；连接稳定超过 `maxDelayMs` 重置预算。
- 图片（PNG/JPEG/WebP/GIF）在模型/附件能力允许时投影为图片；音频/嵌入资源只给诊断；MCP prompts 模板不支持；资源由 `dsh-mcp-resources` 提供。

## 桌面版 DSH 适配（2026-09-27）

- `src/profile-modules.ts`：`profileName()` 拆出纯函数 `profileNameFrom(argv, electronVersion)`——桌面 Host（Electron RunAsNode）argv 不带 `--profile`，`process.versions.electron` 有值时判 `desktop`；显式 `--profile` 仍最优先。
- `src/bin/cli/main.mts`：`--profile desktop` 时 install 跳过 bundle 安装与 patch 行清理（paths.json 是 home 级共享、照常写入），uninstall 跳过 patch 行操作，提示走桌面应用插件页。
- `verify-build.mjs`：fiber 注入断言从已退役的 `settingsScope` 更正为 `configForms`（0.1.7 迁移遗留的陈旧校验）。
- 回归：`src/tests/patch-row.test.mts` 新用例（profile 探测矩阵）。shim 为原生 exe，不依赖 PATH 上的 node。
- 共性事实与验收记录见工作区根 `docs/dsh-desktop-support.md`。

## 已验证事实（2026-09 / 2026-10）

- GodotMCP 服务器 `--list-eager`：19 个常驻(eager)工具 + 1 个元(meta)工具；31 组按需工具。
- 注册表目录 `%APPDATA%/godot-mcp-toolkit/`,当前为 `entries/<hash>.json`(每条含 `_key` 小写规范路径、`port`/`pid`/`godot_version` 等);旧 `projects.json`(`by_path{}`)仅作兼容回退。
- paths.json 全局 serverDist + 按 cwd 项目条目；host 侧 PathStore 惰性加载、原子写、cwd 去重、上限 100。
- `discover_tools` 参数契约（实测 2026-09）：组名/关键词走 `request`（string|string[]），`activate` 是 boolean（默认 true 自动激活，false 仅浏览），`reset` 接受 boolean 或组名数组，`include_schemas` 布尔。既往 agent 误把组名传给 `activate`（expected boolean 报错）；section-text 提示词已写明该签名防复发。
- 按需工具组的工具参数契约：必填参数（如 classdb_query 的 `mode`）以每个工具自己的 schema 为准——不确定参数名/类型/必填项时先用 `include_schemas=true` 查看，凭记忆猜参数会导致 Invalid option。

## 路线图（未实现）

- 工具注解→权限映射（需 DSH mcp-client 支持工具注解桥接,当前版本不支持）。
- npm 发布后 `dsh plugin add deepseek-harness-godot_unified` 的正式通道。
- 若要"一个连接服务所有 Godot 会话"，需要 DSH 提供 preset 作用域的挂载入口（当前 preset 树急切挂载且无法按会话 cwd 传参，故未采用）。
