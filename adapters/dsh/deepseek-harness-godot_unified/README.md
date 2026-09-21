# deepseek-harness-godot_unified

[English](README.en.md) | 中文

把 [GodotMCP](https://github.com/)（Godot 4 编辑器与游戏的 MCP 控制工具集）接入 DSH：模型在会话里直接读写场景、节点、脚本与资源，跑确定性游玩测试，截图验证视觉结果。

## 它做什么

- **自研桥接 + 动态工具**：插件自己 spawn GodotMCP 桥接 server（stdio）并经内嵌极简 MCP client 桥接——工具以 `godot_<tool>` 稳定命名，清单**来自 server 的 `tools/list`**（真实 schema/描述，随组激活 `discover_tools` 动态增删）：启动面 19 个常驻工具 + 1 个元工具，另有 31 组 64 个按需工具组，上下文成本受控（启动面约 8.8K tokens）。插件内**不写死任何工具**。
- **按会话 cwd 传参**：桥接按**当前调用会话的工作目录**解析项目路径（`$DSH_HOME/godot/paths.json` 按 cwd 批量条目，无匹配回退全局设置）并传给 server——不同会话工作区可以是不同 Godot 项目，绝不绑死目录。
- **工作流提示词 section**（可选，随 bundle 行挂载）：教模型按需扩面、批量编辑、理解编辑器 FIFO 串行语义、按错误码恢复、用截图验证。
- **管理 CLI `dsh-godot`**：install / uninstall / status——校验 server 构建产物与 Godot 项目、写入/清理路径配置、体检编辑器注册表与端口。
- **Godot 工作台**：DSH Web 会话顶部第三个 Tab「Godot」，旁路浏览全部工具、按需组激活、手动调用与状态条（见下）。

## 前提

1. DSH（CLI 自带 `@deepseek-ai/dsh-mcp-client`，无需另装）。
2. GodotMCP 工作区就绪：`<GodotMCP>/adapters/dsh/godot-http-bridge.mjs` 在位（仓库自带，随桥接自举拉起机器级 daemon——daemon 由 GodotMCP 的 `adapters/zcode/install-http-face.ps1` 发布，或编辑器边车自动拉起；Node 桥已随插件 1.1.0 退役）。
3. 目标 Godot 项目已安装 `godot_mcp_toolkit` addon（用 GodotMCP 的 `install-godot-project.ps1`），且**编辑器正在运行**（无头亦可：`godot --headless --editor --path <项目>`）。server 不启动编辑器；编辑器不在时工具调用返回可行动错误。

## 环境要求

- DeepSeek Harness ≥ `0.1.5-rc.1`；完整 UI 使用 `web`，Godot preset 会话使用同一 `web` profile
- Node.js `^22.19.0 || >=24.0.0`

## 安装

```sh
# 从本仓库（开发安装,含 bundle 行 + 路径配置）:
node bin/dsh-godot.mjs install --profile web \
  --project <Godot 项目绝对路径> \
  --cwd <会话工作目录> \
  --godot-mcp-root <GodotMCP 工作区根目录> \
  --link <本仓库目录>

# 或不装 bundle(只要桥接能力,不要提示词 section/工作台):
node bin/dsh-godot.mjs install --profile web \
  --project <Godot 项目绝对路径> \
  --server-dist <GodotMCP>/adapters/dsh/godot-http-bridge.mjs
```

server dist 解析顺序：`--server-dist` > `$GODOT_MCP_SERVER_DIST` > `--godot-mcp-root`/`$GODOT_MCP_ROOT`。

v0.4 起 install **不再写官方 mcp-client 行**（自研桥接）：把 `serverDist` 与 `{cwd, projectPath}` 条目写入 `$DSH_HOME/godot/paths.json`（并清理历史官方桥接行）。路径生效无需重启——在 **Godot preset 会话**（`~/.dsh/.agent-presets/godot/`）中首次创建时桥接自动加载；也可在侧栏「插件」页 → deepseek-harness-godot_unified 页面按 cwd 增改路径（保存即时生效，DSH 0.1.6+）。不同会话工作区可以配置不同 Godot 项目。

### 常用选项

| 选项 | 语义 |
| --- | --- |
| `--cwd <dir>` | 条目绑定的会话工作目录（默认当前目录；按 cwd 匹配生效） |
| `--server-name <name>` | 工具命名空间（默认 `godot`，即 `godot_*`；保留参数） |
| `--read-only` / `--unsafe` / `--rate-limit` / `--editor-port` / `--runtime-port` / `--timeout-ms` | 原官方行 env 参数，v0.4 不再写入（警告）；相关能力由 server 默认/项目设置提供 |

## 验证

```sh
node bin/dsh-godot.mjs status --profile web   # 路径配置/server/项目/注册表/端口体检
dsh --profile web --dump-config | grep godot  # 组合校验(不启动)
```

GUI 里：用 Godot preset 新建会话 → 模型即可看到 `godot_*` 工具；也可在「Godot」工作台 Tab 浏览/手动调用。前提是目标项目的 Godot 编辑器在运行。

## 卸载

```sh
node bin/dsh-godot.mjs uninstall --profile web            # 清空路径配置(并清理历史官方桥接行)
dsh plugin --profile web remove deepseek-harness-godot_unified # 移除 bundle(提示词 section/工作台)
```

## 数据与边界

- 桥接 server 只连本机回环（编辑器/运行时 WebSocket，token 鉴权）；自研桥接 spawn 时清洗凭据样式变量与 `DSH_*`，显式传入的 `GODOT_MCP_*` 覆盖值不受影响。
- server 路径与项目路径写在 **`$DSH_HOME/godot/paths.json`**（机器本地配置，不随包发布、不入 git）；历史官方 mcp-client 受管块由 CLI 清理。
- 已知限制：MCP resources（如 `godot://roots`）不被自研桥接透传；上游工具的只读/破坏性注解不映射为 DSH 权限规则（工作台采取保守确认策略）。

## Godot 工作台（Godot Workbench）

DSH Web 会话界面的顶部新增第三个 Tab「Godot」，作为本插件与整个 GodotMCP server 的预览与调试点。它**不经过对话、不写回会话**，全程走同源 `/godot-workbench/api/*` 路由；client 半边随插件冷启动挂载（首次安装需重启一次 `dsh web`），此后 `lib/client.js` 的改动由 DSH client HMR 在页面内自动换血，无需刷新页面。

能力：

- **全部工具浏览**：列出当前可调用的全部工具，`godot_*`（Godot）置顶展开；GodotMCP 的 31 个按需工具组单独列出（灰显 + 描述 + 「激活」按钮），其余按名称前缀聚拢为「内置工具」/「其他插件」。
- **手动调用**：选中工具后按 JSON Schema 生成表单（或切「原始 JSON」整段编辑），点「执行」直接调用并查看结果——文本 / JSON / 截图图片内联 / 错误码 + 可行动建议；进行中可「取消」。
- **结果区四个子页**：结果、历史（最近 50 条，localStorage）、技能（GodotMCP 5 个 SKILL.md + 本包提示词 section）、提示词面（当前实际生效的 systemPrompt sections，验证注入效果）。
- **状态条**：桥接工具数 / 编辑器注册表（项目 · 端口 · Godot 版本 · pid）/ `READ_ONLY` 标记 / server dist 路径与存在性 / 安装指引；拉取失败时显示错误原因与「重试」按钮（MCP/编辑器就绪后一键重新探测），不再无限「加载中」。
- **视图模式**：与「轨迹」Tab 同为 composer-overlay 视图——工作台占满视口、自带滚动器；底部输入框以浮动条形式为内容让位（根元素 `data-conversation-composer-overlay`，走 DSH `ConversationRoot` 的 `:has()` 契约），不呈现对话式粘底输入框。

### 工具注入策略与动态工具（插件页配置表单）

**自研桥接 + 动态工具**：插件自己 spawn GodotMCP server（stdio）并经内嵌极简 MCP client 桥接——工具清单**来自 server 的 `tools/list`**（真实 schema/描述，随组激活/扩展动态增删），插件内**不写死任何工具**；server 默认不启动，Godot preset 会话创建时自动预热一次清单（异步，不阻塞），空闲 10 分钟自动关闭，调用时按需自愈。桥接按**调用会话的工作目录（cwd）**解析并传参项目路径——不同会话可以做不同 Godot 项目，绝不绑死目录。插件页配置表单提供：

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| 注入原版模式 | 关 | 开=所有会话全局注入工具与提示词（即时生效）；关=**仅 Godot preset 会话注入**，其余会话在 Agent 作用域隐藏这些工具（schema 面不出现） |
| 注入工作流提示词 | 开 | 工作流提示词 section 开关（仅对注入工具的会话生效） |
| 提示词中文化 | 关 | 注入的工作流提示词与桥接工具说明使用中文（默认英文）；工具名保持英文 |

路径（server dist、项目路径）支持两种来源：插件页配置表单的「Godot 路径」区（含**按工作目录的项目路径批量条目**，切换会话按 cwd 匹配）与 `$DSH_HOME/godot/paths.json` 持久存储；无匹配时回退全局值。编辑器离场时桥接在几次退避重试后静默待命，下一次调用按需重连自愈。

安全与边界：

- 路由仅接受**同源 + 回环**（校验 `Origin` 与 Host 一致或无 Origin）；GET 只读、POST 带 JSON 体；错误以 `{error:{code,message}}` 返回。
- 工具注解（readOnly/destructive）经 mcp-client 不桥接而不可得，工作台采取**保守确认策略**：每次都弹确认（工具名 + 参数摘要），可勾选「本会话跳过确认」（仅内存记忆、不落盘），但工具名含 `unsafe` 的**永远需要确认**。
- 若其它插件为某工具注册了 pre-execute `'ask'` 策略，工作台调用（无 agent 上下文）会被**拒绝**——错误原样呈现，设计如此、不做绕过。
- 历史仅存浏览器 localStorage（上限 50 条、每条摘要 8KB 截断）；不向对话会话写入任何内容。

已知限制：

1. 截图 base64 内联受单响应体积限制（单图超 4MB 截断为截断标记）。
2. 工具注解不可得 → 确认策略保守（每次确认或会话级豁免；`unsafe` 永远确认）。
3. pre-execute `'ask'` 策略工具在工作台调用会被拒（设计如此，不绕过）。
4. server stderr 对工作台不可见（mcp-client 不暴露）。
5. Tab 依赖 web profile 的 `ui-conversation` 契约（`conversation.view` list slot）——DSH 升级若改契约需适配。

MIT License.
