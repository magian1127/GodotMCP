# 开发说明

## 仓库结构

```
src/
  index.ts            host 插件:inject ['systemPrompt','tools','settings','agents'];
                     自研桥接(GodotBridge)+ 动态工具注册 + 注入策略 + 工作台装配
  bridge/
    manager.ts        极简 MCP stdio client(GodotBridge):spawn server + newline JSON-RPC;
                     懒启动/空闲关闭/超时/应答关联/per-cwd 传参
    upstream-zh.ts    上游中文描述翻译表(按工具名映射;是翻译,不是工具清单)
  section-text.ts     section 内容(双语,zhPrompt 设置切换;蒸馏自上游 godot-control/playtest 技能)
  injection.ts        注入策略纯函数(resolveSettings/godotToolNames/shouldHideAgent/needsScopedPrompt/
                     resolveToolDescription/toolFailureMessage;node --test)
  profile-modules.ts  loadSchemastery(profile require 上下文解析,hashline 同款)
  constants.ts        host 侧常量(PKG/BUNDLE_ROW_ID/SECTION_NAME/SECTION_ORDER)
  types.ts            最小类型面(Disposer/SystemPromptService/HostContext/Tools/WebServer 形状)
  workbench/          工作台 host 半边路由与数据源(只读 tools 面;不 spawn server)
    routes.ts         webServer 同源路由装配(6 条 + 2 条 /api/path-settings 读写,可逆;webServer 缺席返回 undefined)
    handlers.ts       /status /tools 数据组装 + ToolsCatalogueCache(statusData/resolveWorkbenchPaths:serverDist 全局单值,项目路径按 cwd 匹配)
    path-store.ts     路径持久存储(全局 serverDist + 按 cwd 批量项目条目,落 $DSH_HOME/godot/paths.json;桥接 per-cwd 传参的数据源)
    executor.ts       /call 执行(AbortController 取消表 + .value.content 原始块投影 + 图片 4MB 截断)
    probes.ts         status 探测(注册表/端口/pid,自 CLI probes 移植)
    skills.ts         技能文件读取 + systemPrompt 组装
  client/             工作台 client 半边(仅 src/client/index.ts 被 tsdown 打包为 lib/client.js)
    index.ts          入口:conversation.view slot 注册(id godot/order 15) + plugins.bundle.config 配置表单 + 样式注入 + locale 检测
    config-page.ts    插件页配置表单(注入原版模式/工作流提示词/中文提示 + 路径区;settingsScope 命名空间 godot)
    Workbench.ts      主组件(状态条 + 三栏 + 确认弹窗;React.createElement,非 JSX)
    ToolTree.ts       工具树(搜索/分组/组激活;description 截断 100/60 字符防 DOM 膨胀)
    CallPanel.ts      schema→表单生成 + 原始 JSON 模式 + 确认对话框
    ResultPanel.ts    结果渲染 + 历史
    SkillsPanel.ts    技能与提示词面
    api.ts            同源 fetch 封装(相对路径;类型与 host 侧 executor 同形,有意重复)
    grouping.ts       工具树分组纯函数(可 node --test)
    form-schema.ts    schema→表单映射纯函数(可 node --test)
    history.ts        localStorage 历史上限 50/摘要 8KB 截断(纯函数,可 node --test)
    locales.ts        zh/en 文案 + document.documentElement.lang 检测
    css.ts            主题 token 样式注入
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
      main.mts        参数解析与命令分发(install/uninstall/status;v0.4 写 paths.json,不写 mcp-client 行)
  tests/              node:test 单元测试(patch-row/resolve/plugin-smoke/client 纯逻辑/工作台)
cordis.patch.yml      bundle patch:insert 行 dsh-godot(本包自身)
tsdown.config.ts      client 单文件经典脚本打包配置
verify-build.mjs      产物存在性/语法/patch 行形状/client bundle 格式/CLI usage 冒烟
```

构建产物：`lib/`（host 为 tsc 多文件、`lib/client.js` 为 tsdown 单文件经典脚本）、`bin/`（CLI,.mts→.mjs 原地）、`.tsbuild/`（tests）。构建链：`build:host`（tsc）、`build:cli`（tsc）、`build:tests`（tsc）、`build:client`（tsdown `--config tsdown.config.ts`）；`npm run build` 依次执行这四步。

## 接入形态（自研桥接 + 动态工具 + per-cwd 传参）

**为什么不是官方 mcp-client 行**：行级配置把 `GODOT_MCP_PROJECT_PATH` 固化在 profile 里（memorix 式静态接入），不同会话工作区可能需要不同 Godot 项目——官方行无法按会话切换。因此插件自己管理 server 生命周期与传参：

1. **GodotBridge**（`src/bridge/manager.ts`）：`spawn(process.execPath, [dist])` + newline-delimited JSON-RPC（initialize 握手 30s、tools/call 60s、应答 id 关联、`notifications/tools/list_changed` → onListChanged、退出 → onExit）。按**当前调用会话 cwd** 解析并传参：`env.GODOT_MCP_PROJECT_PATH = projectPath(cwd)`；cwd 变化时重启 server 以切换项目配置。空闲 `idleCloseMs`（10min）自动关闭；工具门面在 server 关闭后**保持在册**（清单已缓存），调用时 `ensureStarted` 自愈。
2. **动态工具（不写死）+ 持久缓存**:无离线占位、无内置清单(`bridge/manifest.ts` 已删除)。
   **工具清单缓存**在 `$DSH_HOME/godot/tools-cache.json`(`src/bridge/tools-cache.ts` 的
   `ToolsCacheStore`,与 PathStore 同款惰性加载/原子写/损坏回退;与 server dist 绑定校验):
   - Godot preset 会话创建(`agent/created`/`agent-preset/selected`)→ `loadFacadeFromCache()`
     ——**只读缓存注册门面,不 spawn server、不连编辑器**(打开 DSH 不一定要做 Godot 操作);
     缓存缺失/与当前 server dist 不匹配 → 忽略待刷新。
   - `syncFacadeFromServer()`(连接成功:工作台 `/api/connect` 或首次工具调用后) →
     `tools/list` → 注册 + **写缓存**;之后不再随调用刷新(`tools/list_changed` → `resyncFacade`
     增量,清单一致时跳过)。
   - 工作台状态条「连接 Godot」按钮 → `POST /godot-workbench/api/connect` → `connectWorkbench()`
     (ensureStarted + sync + 缓存) → 目录缓存失效 → 工具树刷新。
3. **描述本地化**：zhPrompt 开时用 `UPSTREAM_TOOL_ZH`（按名翻译表）覆盖描述；非 zh 用 server 原文。
4. **注入策略（per-agent 表面）**：默认仅 Godot preset 会话注入；非 Godot preset 在 Agent 作用域 `restrict({deny})`。`tools/change` 增量 deny 经 **150ms 合并窗口**（`scheduleDenyRefresh` → `refreshDenyPatches`），只在出现新工具名时调用 restrict（无 diff 守卫会事件回声）。`injectLegacyMode` 开=全局 section + 全面可见（该模式下 `tools/change` 处理短路）。

### 防卡死（2026-09-05/06 同类根因的防御）

- **工作台** `/api/tools` 走 `ToolsCatalogueCache`（5s TTL；`tools/change`、`/api/call` 完成即失效）；**仅当 `discover_tools` 元工具在场（桥接已同步过清单）才调用**它取组目录——离线/未配置时浏览工具目录绝不启动 server、不产生 30-60s MCP 往返。
- **门面注册风暴**：`applyDefs` 清单 diff（无变化跳过）；`tools/change` handler 合并窗口；deny 无 diff 不调用 restrict。
- **预热不阻塞**：`syncFacadeFromServer` 全程 async，不 await 于 apply；Godot preset 会话创建才触发，普通会话（及单元测试环境）不 spawn server。
- 前端 `ToolTree` description 截断（100/60 字符 + title 全文），防上游长描述进 DOM 拖死渲染。

## CLI 语义（v0.4）

- `install`：校验 dist/项目 → 写 `$DSH_HOME/godot/paths.json`（`serverDist` + `{cwd, projectPath}` 条目，`--cwd` 默认当前目录）→ **清理历史官方 mcp-client 受管块**（`removeManagedRow`，防双轨）→ 提示插件页配置表单可按 cwd 增改。`--read-only/--unsafe/--rate-limit/--editor-port/--runtime-port/--timeout-ms` 为历史官方行 env 参数，不再写入并警告。
- `uninstall`：清空 paths.json（`{projects: []}`）+ 清理受管块。
- `status`：报告 paths.json（serverDist 存在性、项目条目有效性）、编辑器注册表体检、bundle 行状态。
- paths.json 读写与 host 侧 `PathStore` 同源形状（不 import host 模块；文件级最小实现，测试不直接覆盖）。

## Godot 工作台 client 半边

- client 作为 client bundle（`lib/client.js`）随插件冷启动挂载；`dsh.client` 声明为 `{ platform:'web', immediately:true, inject:[] }`。
- **fiber 注入 `['slots']`（bundle exports.inject）**：浏览器端 Loader 从 bundle exports 读 `inject` 构建 fiber 服务注入集；client 入口注册 `conversation.view` 插槽需要 `ctx.slots`，因此 bundle 必须导出 `inject = ['slots']`。注意与 `dsh.client.inject`（包名级 factory 先行边，本包无，保持 `[]`）是两回事。
- **零包依赖/零 react 依赖决策**：数据走浏览器原生 `fetch` 同源相对路径；`react` 不打进 bundle，运行时经 DSH 模块表供应（tsdown `deps.neverBundle: ['react']`）；client 不进 tsc include，无 `@types/react`。源码 `.ts` + `React.createElement`（无 JSX/TSX）；`grouping.ts`/`form-schema.ts`/`history.ts` 三个纯函数被 `tsconfig.tests.json`（`noCheck`）纳入供 node --test。
- **`slots.inject` 回调必须返回注册清理句柄**（ui-chat 同款契约）：epoch 变化（HMR/重挂）时先撤销旧注册再重跑回调；缺失则重复 Tab。
- **composer-overlay 模式**：根元素带 `data-conversation-composer-overlay=""`，宿主 `ConversationRoot.module.css` 的 `:has()` 规则切换布局；宿主无该规则时退化为常规布局（功能不受影响）。
- **状态拉取失败可诊断**：`Workbench.ts` 用 `Promise.allSettled` 并行拉 `/status` 与 `/tools`，失败保留原因并清空对应数据，状态条显示错误徽标 + 重试。

## 不可破坏的约束

1. **不写官方 mcp-client 行**：v0.4 自研桥接不需要它；CLI 安装升级必须清理旧受管块（`removeManagedRow`），绝不让官方行与自研门面双轨。
2. **不写死工具清单**：`src/bridge/` 只保留 manager（桥接）与 upstream-zh（翻译表）；不得再引入离线占位/内置工具表（manifest 模式已废弃）。
3. **预热/调用路径不得同步阻塞 apply**：`syncFacadeFromServer`/`ensureStarted` 只在 async 上下文（Promise 回调、工具 execute）中运行；apply 永不 await 桥接。
4. **受管块边界**（历史兼容/清理用）：`# dsh-godot:begin/end` 之外内容只读；卸载只剩注释时写回 `[]`；原子写（tmp+rename）+ 同目录写锁。
5. **常量双侧同步**：host `src/constants.ts` 与 CLI `src/bin/cli/constants.mts` 独立定义，`patch-row.test.mts` 对齐断言守护；改行 id/标记两侧同改。
6. **spawn 安全**：不用 `shell: true`；无法安全转发的参数（`%`、`!`、引号、尾反斜杠+空格）报错而非篡改；`dsh plugin` 的 `--profile` 必须跟在子命令后。
7. **section 注册容错**：重复注册（双行并存）按 duplicate 吞掉并 warn；`systemPrompt` 服务缺失降级 warn 不抛错；teardown 经 `ctx.effect` 可逆。
8. **注入策略不触发事件回声**：deny 补丁只在出现新工具名时调用 `restrict`；`tools/change` 处理必须经合并窗口，禁止在事件处理器内同步全量重装。
9. **不修改 GodotMCP server 仓库**：上游行为按现状接受（不改他人仓库），本仓库文档只记录接入语义。

## 测试策略

- 单元（node:test）：标记块生命周期（新建/幂等/参数重写/用户内容保全/`[]` 恢复/往返解析）、常量对齐、resolve 优先级、插件冒烟（注入分流/serverName 覆盖/重复容错/服务缺失降级/工作台装配/teardown）、注入纯函数（含描述翻译映射）。
- client 纯逻辑（node --test，直接编译进 client bundle）：工具树分组、schema→表单映射、历史上限/摘要截断。
- 组件与 bundle 形状：由 `verify-build.mjs` 断言（client 产物存在、经典脚本格式、注册 `conversation.view`/`id godot`/`order 15`、工作台组件在场、未打包 react）；交互与视觉效果由 GUI 验收覆盖。
- 路由冒烟（真实 Node http + mock tools/webServer）：同源校验、JSON 错误形状、`/call` 取消/超时/错误投影、路径设置读写。
- 运行期验证（不重启 web）：`dsh-godot status` + `dsh --profile <p> --dump-config`（组合校验）；一次性会话（`dsh --profile <p> --patch <行文件> "任务"`）验证桥接进程链（spawn → MCP 握手 → tools=20 → WS 认证）。

## 安全审计加固（2026-09 第二轮，本仓库首审）

Mimosa 深扫 0 findings；人工全源码审计发现并修复（77 tests 全绿 + verify-build/typecheck 通过）：

- **桥接进程代数保护（产品 bug）**：cwd 切换重启时旧进程的 `exit` 事件晚于新进程接替到达，原处理器无条件清 `this.proc` → 新进程注册被误清，`stop()` 从此杀不到它（**进程泄漏**）。exit 处理器改为仅当前注册进程自己退出才清状态/触发 `onExit`；`bridge-manager.test.mts` 真实 spawn 子进程断言重启后 `running` 仍为 true。
- **stdout 单行 4MiB 上限**：失控 server 无换行输出不再无界累积——超限立即失败全部在途请求并终止桥接（`MAX_STDOUT_LINE_BYTES` + `failAllPending`）。
- **工作台路由第三层围栏**：`sec-fetch-site`（cross-site/same-site 拒绝，same-origin/none/缺省放行），对齐 zh_pro `isTrustedApiRequest` 三重围栏标准。
- **500 固定文案**：路由 500 不再透传底层 `error.message`（防绝对路径等细节泄漏），原始错误经 `warn` 回调走 host 日志（`WorkbenchDeps.warn`）。
- **MCP clientInfo 版本**：`constants.VERSION` 运行时读包内 `package.json`（`createRequire`，读不到回退字面量），不再硬编码 `0.2.0`。
- **CLI `writeGodotPaths` 临时文件名**：tmp 带 `pid+时间戳` 后缀且失败清理，与 `patch-row.writeAtomic` 同策略（防并发实例互踩）。
- **`.gitignore` 锚定 `/lib/`、`/bin/`**：原裸 `bin/` 模式误匹配 `src/bin/`，整个 CLI 源码树从未入库（克隆即缺文件）；修正后 `src/bin/` 可见为未跟踪，**提交时务必 `git add src/bin`**。

## 已验证事实（2026-09,本机）

- GodotMCP 服务器 `--list-eager`：19 个常驻(eager)工具 + 1 个元(meta)工具。
- 注册表目录 `%APPDATA%/godot-mcp-toolkit/`,当前为 `entries/<hash>.json`(每条含 `_key` 小写规范路径、`port`/`pid`/`godot_version` 等);旧 `projects.json`(`by_path{}`)仅作兼容回退。键为小写规范路径(win),`normalizeProjectKey` 与其一致。
- 自研桥接 spawn 时环境清洗 `/KEY|PASSWORD|SECRET|TOKEN/i` 与 `DSH_*`；显式 `GODOT_MCP_PROJECT_PATH` 幸存。
- paths.json 全局 serverDist + 按 cwd 项目条目；host 侧 PathStore 惰性加载、原子写、cwd 去重、上限 100。
- `discover_tools` 参数契约（实测 2026-09）：组名/关键词走 `request`（string|string[]），`activate` 是 boolean（默认 true 自动激活，false 仅浏览），`reset` 接受 boolean 或组名数组，`include_schemas` 布尔。既往 agent 误把组名传给 `activate`（expected boolean 报错）；section-text 提示词已写明该签名防复发。
- 按需工具组的工具参数契约：必填参数（如 classdb_query 的 `mode`）以每个工具自己的 schema 为准——不确定参数名/类型/必填项时先用 `include_schemas=true` 查看，凭记忆猜参数会导致 Invalid option。section-text 已加入该引导（中英双语）。

## 路线图（未实现）

- 历史说明:旧设置卡片把 `--read-only`/`--unsafe`/限速/端口固定映射为桥接 env（当前 server 默认/项目设置承担）。
- 工具注解→权限映射（需 DSH mcp-client 支持工具注解桥接,当前版本不支持）。
- npm 发布后 `dsh plugin add deepseek-harness-godot_unified` 的正式通道。
