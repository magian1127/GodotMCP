# 开发说明

中文 | [English](development.en.md)

## 归属与模块

本目录是 GodotMCP 仓库的普通目录，共用 Git 历史；只有 skills/ 是指向 `plugin/godot-mcp-unified/skills` 的 junction。本包不实现桥接或 MCP 客户端。工具面事实归上游 daemon，宿主接入事实归本包。

| 文件/目录 | 责任 |
| --- | --- |
| src/index.ts | 按会话/host 挂载、传输选择与端点缓存、提示词和路由装配 |
| src/daemon-endpoint.ts | daemon 端口/状态目录/token 解析、detach 自举、2026 面探测、会话键派生、cwd 头编码 |
| src/mcp-mount.ts | shim/历史 JS 启动方案、stdio 与 streamable-http 两种官方 Config、身份键、errorName |
| src/injection.ts | 设置解析、preset/注入判断、工具名前缀、传输/键作用域枚举 |
| src/section-text.ts | 双语模型工作流，不维护工具表 |
| src/profile-modules.ts | 从当前 profile 解析 schemastery 与官方 mcp-client |
| src/path-store.ts | paths.json 持久化、mtime 重读；项目数据结构保留兼容 |
| src/path-settings-route.ts | serverDist 读写 API，旧 projects 输入被忽略 |
| src/client/ | 插件页配置表单与同源 fetch |
| src/bin/cli/ | install/uninstall/status、路径校验与只读体检 |
| src/bin/patch-row.mts | 历史受管块清理、原子写与写锁 |
| src/tests/ | node:test 回归 |
| cordis.patch.yml | dsh-godot 与 preset-godot 声明 |
| skills/ | 六个技能的真源映射，不复制维护 |

lib/、bin/、.tsbuild/ 是忽略的构建产物。npm run build 依次构建 host、CLI、tests 和 client。tsc 不自动删除旧输出；删除或改名源文件后，确认这三个目标都在本包内，再回收并重建，防止陈旧测试或宿主模块被继续加载。

## 挂载与状态归属

- 官方客户端必须挂在 agent.ctx 才能只对该会话可见。子代理继承 preset 版本而非父 Agent scope，所以各自挂载；默认目标和组在每个会话键上隔离。
- 不能把静态 mcp-client 行放进 preset：preset 树在 profile 加载时急切挂载，无法按会话 cwd 传递环境。默认只在 Godot 会话创建/空白预设切换时挂载。
- injectLegacyMode 刻意在 host 上共享一个连接与提示词，所有会话共享目标和组；与协议 legacy 无关。直连时 host 上只有一条 HTTP 连接。
- 按会话 id 记账，按 entry 身份卸载。重复 agent/created 即使传不同对象也不能重复挂载；syncAgent 按会话 id 串行化（`syncInFlight`），因为"等端点"必须让出，两个并发调用会各挂一次。
- serverDist 优先取 PathStore，再回退设置；projectPath 兼容字段恒为空。cwd 来自 agent.session.header.cwd，原版模式用 process.cwd()。
- **传输选择**：`auto`/`http` 时 `syncAgent` 读端点缓存，未就绪则短等 1.2s（`waitEndpointFor`），仍无则回退 stdio。端点准备（`prepareDaemonEndpoint`）在后台由 `resyncAll → ensureEndpoint` 触发：探端口 → 不在跑则从 serverDist 同目录 detach 拉起 daemon → 读机器级 token → `server/discover` 验 2026-07-28。失败结果缓存 30s，成功后 `endpointNotifiedFor` 只触发一次重挂。
- 挂载身份键统一为 `mountConfigKey`：stdio 分支含入口/参数/env/cwd/超时，http 分支含 url/headers/超时，前缀不同。**切换传输、token 轮换、键作用域变化、改超时都会重挂**（此前"timeout-only 不重挂"的缺口已修）。
- 会话键：`sessionKeyFor` 用 `session:<id>` 或 `project:<规范化 cwd>` 派生 UUID v5 形状（`deriveSessionKey`）。确定性派生保证同一会话/项目重连拿到同一键；a 会话激活 11 个工具后，session 作用域的新会话仍从 1 个工具起步（实测）。
- Config 两种形状：stdio 为 command/args/env:{}、可选 cwd；http 为 url + headers（authorization / x-godot-session / x-godot-cwd / x-godot-shim-version）。两者都 `toolCallTimeoutMs`（默认 240000）与 `failOnStartupError:false`。超时正数取整并截到 900000。
- 会话创建路径全部 try/catch，失败只 warn。就绪最多等 READY_GRACE_MS=10s；连接仍在后台完成，后续原生工具发现需宿主处理。
- 测试注入点：宿主提供 `godotDaemonEndpointProvider` 服务时替换真实探测（返回 undefined = 不可用）。生产没有这个服务。plugin-smoke 默认 `transportMode=stdio`，避免测试依赖本机 daemon/token。

profile 模块通过 require.resolve、exports 的 import 入口与 TLA 异步预载；避免在宿主 hooks 尚未加载完整时同步 require(esm)。官方模块不加入 dependencies/peerDependencies。挂载前用官方 Config 自校验，诊断只记录 error.name；不回显配置或异常 message。

## 路径设置与配置页

GET/POST 合并在一个 webServer.register 路由，handler 按方法分支；宿主路由按 path 注册，分开注册会互相覆盖。GET 返回 serverDist 与空项目字段，POST 只持久化 serverDist 并清空旧 projects。PathStore、路由及兼容类型仍在，项目映射是停用而非源文件全部删除。

路由使用回环 peer、Host、Origin、sec-fetch-site 围栏，返回 405/413/400/固定 500；请求体上限 8MiB。装配使用 ctx.inject(['webServer'])，缺席时返回 undefined。teardown 经 ctx.effect 可逆。兜底 serverDist 使用 getter 读取更新后的 current，已不再是挂载时快照。

client 为经典脚本 lib/client.js，源码用 React.createElement，不引入本地 React 运行时。tsdown 不打包 react，宿主模块表供应。fiber exports.inject 是 slots、locale、configForms；dsh.client.inject 的包级依赖是另一层，保持空。配置表单注册回调返回清理句柄，避免 HMR/epoch 变化泄漏注册。草稿统一在保存时 POST；读取失败显示在表单中。

## CLI 与 preset

install 仍验证 --project；写入 serverDist、projects:[]，清理历史 mcp-client 受管块，不写新 mcp-client 行。desktop profile 跳过 bundle/patch 操作。status 可能展示旧 projects 内容，这是兼容诊断，不表示 host 仍按它路由。

受管块之外的 patch 内容只读。只剩注释时写回合法 []，写入采用同目录写锁与 tmp+rename。host/CLI 常量分开定义，由对齐测试守护。spawn 不用 shell:true；无法安全转发的特殊参数报错，profile 参数位置按已安装 DSH 命令约定处理。

preset 由 bundle 的 @deepseek-ai/dsh-agent-preset 声明；旧 .agent-presets 不读取。customSkillDirs 从包 realpath 解析。DSH 0.2.1 适配保留以下约束：

- engines.dsh 独立决定兼容范围，匹配包含预发布；下界 0.2.0-rc.2 是正向样本。
- persona 不声明 suffix；cwd 来自 working-directory 上下文，model 变量保留。
- time-context 与 tool-schedule 两行在场。
- subagent 不使用 backgroundMode/enableRunInBackground；现在以 activation 与父级完成通知交付。
- agent-instructions 不再有 dshHome 覆盖，skill-filesystem 的自有目录设置仍存在。
- 本包不调用 ctx.skills；技能由 filesystem provider 读取，不引入新 provider。

桌面开发 link 应在应用未运行时执行；profile 的依赖与 bundles 指向本包后按其包管理流程物化。长跑 DSH web 的构建热更新按专门工作流处理，不擅自重启或起替代服务。

## 官方客户端契约与验证

官方 mcp-client 决定协议协商、发现、通知、图片与重连；adapter 不保证它一定因 ttlMs=0 重拉。它用 `versionNegotiation: {mode:'auto'}` 先探 `server/discover`：daemon 返回 2026-07-28 就进现代面（每请求带协议元数据、`subscriptions/listen` 长流拿 tools/list_changed）；旧服务端回退 initialize。**不能用 initialize 探测 daemon 的 2026 支持**——2026 不走握手，会得到误导性的版本拒绝；必须发 `server/discover`（带 `MCP-Protocol-Version` 与 `Mcp-Method` 头）。daemon 的 2026 stateless 面不支持 GET SSE（405），所以直连时通知只能靠订阅长流。

其它已依赖契约：同 scope serverName 唯一，不同 Agent scope 可同名；工具名 mcp__<serverName>__<rawName>；子进程父环境清洗凭据样式变量和 DSH_*；prompts 不支持，资源由宿主另一插件负责。保留当前宿主的真实契约，升级时核对源码与 schema，不根据旧文档模拟实现。

所有改动运行 npm run typecheck、npm run build、npm test、npm run verify。测试覆盖受管块、配置形状（stdio/http 两种）、默认/共享挂载、端点解析与自举、会话键派生、路径 API、preset 声明等；`daemon-endpoint.test.mts` 全部用注入依赖，不碰真实 daemon/token。组合用 --dump-config，真实宿主调用与视觉附件另行验收；不把 mock 通过写成 GUI 已验证。历史变化见 [release.md](release.md)。
