# AGENTS.md

> 本文件只定义仓库级协作规则。用户说明见 [`README.md`](README.md)，详细事实按主题收录于 [`docs/`](docs/)。
> 代码注释和文档以中文为主；上游术语、API、命令和标识符保留原文。

## 项目定位

本仓库是 GodotMCP 工作区（Godot 4 MCP 桥接工具集）与 DSH 的**接入层**：它不实现 Godot 桥接逻辑（那是 GodotMCP server 与编辑器 addon 的职责），**也不实现 MCP 客户端**——v0.5 起连接、握手、工具发现、重连与图片投影全部交给宿主自带的 `@deepseek-ai/dsh-mcp-client`，本包只决定"哪些会话需要它、它连哪个 server"。本包提供四样东西：

1. **路径配置 CLI**（`dsh-godot`）：校验 server 入口/Godot 项目，把路径写入 `$DSH_HOME/godot/paths.json`（host 侧 PathStore 同源；不写 profile 的 mcp-client 行），并做体检（status）。
2. **Godot 工作流提示词 section**（host 插件，行 id `dsh-godot`）：教模型按需扩面、理解编辑器 FIFO 串行语义与错误恢复。蒸馏自 GodotMCP 的 godot-control / godot-playtest 技能。
3. **按会话挂载策略 + 路径设置路由**（host/client，行 id `dsh-godot`）：默认只有 Godot preset 会话在 `agent/created` 时把官方 mcp-client 挂进该 Agent 作用域（工具名 `mcp__godot__*`），其余会话根本没有这些工具；项目路径按会话 cwd 解析并作为 `GODOT_MCP_PROJECT_PATH` 传入。`/godot-config/api/path-settings` 为插件页配置表单提供 host 侧 `PathStore` 的整份读写（详见 `docs/behavior.md` 与 `docs/development.md`）。通用工具调试界面不在本包——已迁至 `deepseek-harness-project-mcp` 的「工具调试」Tab。
4. **Godot agent preset 声明**（bundle patch 行 id `preset-godot`）：DSH 0.1.6+ 起 preset 只能由 bundle patch 的 `@deepseek-ai/dsh-agent-preset` 声明行注册（`$DSH_HOME/.agent-presets/<id>/` 目录已废弃）；该行 plugins 是 shipped `standard` 的完整副本 + 本包技能目录（`skills/` 为指向上游 GodotMCP 的 junction，`customSkillDirs` 从本包 realpath 解析）。

上游事实（工具目录、分组、env 变量、注册表）以 GodotMCP 仓库 `plugin/godot-mcp-unified/server/` 的源码与文档为准；本仓库文档只写接入语义，不复制上游目录。

## 文档索引

| 文档 | 唯一职责 |
| --- | --- |
| [`README.md`](README.md) / [`README.en.md`](README.en.md) | 面向用户：功能、安装、卸载、验证与边界 |
| [`docs/behavior.md`](docs/behavior.md) | 用户可见行为、默认值、失败模式与错误码 |
| [`docs/development.md`](docs/development.md) | 仓库结构、不可破坏约束、测试策略与开发经验 |
| [`docs/release.md`](docs/release.md) | 发布前验证、版本记录与 npm 发布 |

## 铁律（最高优先级）

1. 遵守所在 DSH 插件工作区共享边界：不重启 `dsh web`、不启动替代服务器、不修改 DSH checkout、profile 与 patch 行只通过本 CLI 或 `dsh plugin` 读写。
2. **不得在文档中写入本机绝对路径**；示例用 `<GodotMCP 工作区>` / `<Godot 项目>` 等占位符。
3. patch 受管块（`# dsh-godot:begin/end`）之外的任何用户 patch 内容**绝不重写**；卸载后只剩注释时写回合法 `[]`。
4. **不得把 `@deepseek-ai/*` 写进 `dependencies` / `peerDependencies`**：DSH 运行时模块（`schemastery`、`dsh-mcp-client`）一律经 `src/profile-modules.ts` 从当前 profile 解析，类型面用 `src/types.ts` 的鸭子类型。
5. **不得自建 MCP 客户端**：官方客户端拿不到时降级为"不挂载 + 记错误日志"，绝不退回自研连接（v0.4 的 `src/bridge/` 已删除）。
6. 会话创建路径上**任何函数都不抛异常**（会打断会话创建）：挂载失败一律 warn + 跳过，`failOnStartupError: false`；`agent/created` 只返回一个有上限的就绪宽限 promise。
7. 挂载记账按**会话 id**（不是 agent 对象引用）：同一 sessionId 可能被 `agent/created` 报告两次且传不同对象，按引用记账会重复挂载并多起一个 shim。
8. 诊断只记固定文案与 `error.name`，不记 `message(error)`、不记 config——schemastery 的 union 校验错误会回显整个 config。

## 工程纪律

- 只改手写源码（`src/`）；`lib/`、`bin/`、`.tsbuild/` 是构建产物。
- 任何改动后执行 `npm run typecheck`、`npm run build`、`npm test`、`npm run verify`；安装层验证用 `dsh-godot status` 与 `dsh --profile <p> --dump-config`。
- **构建目录没有自动清理**（tsc 不删 `outDir`）：重命名/删除源文件后，`.tsbuild/` 与 `lib/` 里会留下陈旧产物并被 `node --test ".tsbuild/tests/*.test.mjs"` 继续执行。删除或移动源文件后必须重建这两个目录（回收后用 `npm run build` 重新生成），再跑测试。
- host 侧常量（`src/constants.ts`）与 CLI 侧常量（`src/bin/cli/constants.mts`）各自独立定义，`src/tests/patch-row.test.mts` 的对齐断言守护同步；改行 id/标记必须两侧同改并过测试。
- 组合层验证不动运行中进程：`--dump-config` 校验组合；一次性会话（`dsh --profile <p> --patch <行文件> "任务"`）验证 MCP 挂载链路。
- section 文本双语（英文默认，`zhPrompt` 设置切换中文）；工具名由官方约定 `mcp__<serverName>__<rawName>` 决定，`serverName` 由行 config 覆盖。

## Git 纪律

本目录是指向 GodotMCP 仓库 `adapters/dsh/deepseek-harness-godot_unified` 的 Windows
Junction：git 命令由此解析到 GodotMCP 仓库根，源码与历史统一由该仓库管理，无独立
远程。不主动 `git commit` / `git push`；用户明确批准后提交标题以中文开头。默认分支
`main`。
