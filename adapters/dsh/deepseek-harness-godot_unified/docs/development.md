# 开发说明

中文 | [English](development.en.md)

## 归属与模块

本目录是 GodotMCP 仓库的普通目录，共用 Git 历史；只有 skills/ 是指向 `plugin/godot-mcp-unified/skills` 的 junction。本包不实现桥接或 MCP 客户端。工具面事实归上游 daemon，宿主接入事实归本包。

| 文件/目录 | 责任 |
| --- | --- |
| src/index.ts | 按会话/host 挂载、提示词和路由装配 |
| src/mcp-mount.ts | shim/历史 JS 启动方案、官方 Config、共享模式身份键、errorName |
| src/injection.ts | 设置解析、preset/注入判断、工具名前缀 |
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

- 官方客户端必须挂在 agent.ctx 才能只对该会话可见。子代理继承 preset 版本而非父 Agent scope，所以各自挂载 shim；默认目标和组在每个 shim 的键上隔离。
- 不能把静态 mcp-client 行放进 preset：preset 树在 profile 加载时急切挂载，无法按会话 cwd 传递环境。默认只在 Godot 会话创建/空白预设切换时挂载。
- injectLegacyMode 刻意在 host 上共享一个连接与提示词，所有会话共享目标和组；与协议 legacy 无关。
- 按会话 id 记账，按 entry 身份卸载。重复 agent/created 即使传不同对象也不能重复挂载。
- serverDist 优先取 PathStore，再回退设置；projectPath 兼容字段恒为空。cwd 来自 agent.session.header.cwd，原版模式用 process.cwd()。
- 当前按会话 configKey 是 dist、空 project、cwd；共享模式 mountConfigKey 还含命令、args、env、serverName、timeout。**timeout-only 不重挂既有按会话连接**是当前缺口，不能写成已支持。
- Config 是 stdio、command/args、env:{}、可选 cwd、toolCallTimeoutMs（默认 240000）、failOnStartupError:false。超时正数取整并截到 900000。
- 会话创建路径全部 try/catch，失败只 warn。就绪最多等 READY_GRACE_MS=10s；连接仍在后台完成，后续原生工具发现需宿主处理。

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

官方 mcp-client 决定协议协商、发现、通知、图片与重连；adapter 不保证它一定因 ttlMs=0 重拉。现代协议可主动订阅，legacy 依赖普通通知。当前 shim 的通知/重放限制见 [daemon README](../../../../plugin/godot-mcp-unified/server-dotnet/README.md)。

其它已依赖契约：同 scope serverName 唯一，不同 Agent scope 可同名；工具名 mcp__<serverName>__<rawName>；子进程父环境清洗凭据样式变量和 DSH_*；prompts 不支持，资源由宿主另一插件负责。保留当前宿主的真实契约，升级时核对源码与 schema，不根据旧文档模拟实现。

所有改动运行 npm run typecheck、npm run build、npm test、npm run verify。测试覆盖受管块、配置形状、默认/共享挂载、路径 API、preset 声明等。组合用 --dump-config，真实宿主调用与视觉附件另行验收；不把 mock 通过写成 GUI 已验证。历史变化见 [release.md](release.md)。
