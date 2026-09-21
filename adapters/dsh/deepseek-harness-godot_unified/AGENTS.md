# AGENTS.md

> 本文件只定义仓库级协作规则。用户说明见 [`README.md`](README.md)，详细事实按主题收录于 [`docs/`](docs/)。
> 代码注释和文档以中文为主；上游术语、API、命令和标识符保留原文。

## 项目定位

本仓库是 GodotMCP 工作区（Godot 4 MCP 桥接工具集）与 DSH 的**接入层**：它不实现 Godot 桥接逻辑（那是 GodotMCP server 与编辑器 addon 的职责），而是提供**自研桥接 + 动态工具**接入——插件自己 spawn GodotMCP server 并经内嵌极简 MCP client 桥接，工具清单由 server 的 `tools/list` 动态提供（不写死工具），桥接按调用会话 cwd 解析并传参项目路径。本包提供三样东西：

1. **路径配置 CLI**（`dsh-godot`）：校验 server dist/Godot 项目，把路径写入 `$DSH_HOME/godot/paths.json`（host 侧 PathStore 同源；不再写官方 mcp-client 行），并做体检（status）。
2. **Godot 工作流提示词 section**（host 插件，行 id `dsh-godot`）：教模型按需扩面、理解编辑器 FIFO 串行语义与错误恢复。蒸馏自 GodotMCP 的 godot-control / godot-playtest 技能。
3. **注入策略 + Godot 工作台**（host/client,行 id `dsh-godot`）：默认仅 Godot preset 会话注入 `godot_*` 工具（其余会话 Agent 作用域 deny）；工作台是旁路调试面（浏览/手动调用/状态），带目录缓存与在线判定防卡死（详见 `docs/behavior.md` 与 `docs/development.md`）。

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

## 工程纪律

- 只改手写源码（`src/`）；`lib/`、`bin/`、`.tsbuild/` 是构建产物。
- 任何改动后执行 `npm run typecheck`、`npm run build`、`npm test`、`npm run verify`；安装层验证用 `dsh-godot status` 与 `dsh --profile <p> --dump-config`。
- host 侧常量（`src/constants.ts`）与 CLI 侧常量（`src/bin/cli/constants.mts`）各自独立定义，`src/tests/patch-row.test.mts` 的对齐断言守护同步；改行 id/标记必须两侧同改并过测试。
- 组合层验证不动运行中进程：`--dump-config` 校验组合；一次性会话（`dsh --profile <p> --patch <行文件> "任务"`）验证桥接进程链。
- section 文本双语（英文默认，`zhPrompt` 设置切换中文）；注入策略默认仅 Godot preset（插件页 → 本包页面配置表单，DSH 0.1.6+）。

## Git 纪律

本目录是指向 GodotMCP 仓库 `adapters/dsh/deepseek-harness-godot_unified` 的 Windows
Junction：git 命令由此解析到 GodotMCP 仓库根，源码与历史统一由该仓库管理，无独立
远程。不主动 `git commit` / `git push`；用户明确批准后提交标题以中文开头。默认分支
`main`。
