# deepseek-harness-godot_unified

[English](README.en.md) | 中文

把 [GodotMCP](https://github.com/magian1127/GodotMCP)（Godot 4 编辑器与游戏的 MCP 控制工具集）接入 DSH：模型在会话里直接读写场景、节点、脚本与资源，跑确定性游玩测试，截图验证视觉结果。

## 它做什么

- **在 Godot 会话里挂载宿主官方 MCP 客户端**：v0.5 起本包**不实现 MCP 客户端**。加入 Godot preset 的会话创建时，把 DSH 自带的 `@deepseek-ai/dsh-mcp-client` 挂进**该会话的 Agent 作用域**，连接、握手、工具发现、`list_changed` 增量、断线重连与图片投影全部由它负责。工具名是官方固定约定 `mcp__godot__<原生工具名>`（`serverName` 可由插件行 config 覆盖，改它会改掉模型看到的工具名）。
- **工具清单仍由 server 动态提供**：清单来自 GodotMCP server 的 `tools/list`（真实 schema 与描述），插件内**不写死任何工具**。常驻面 19 个工具 + 1 个元工具 `discover_tools`，另有 31 组按需工具组；`discover_tools` 激活一组后 server 发 `list_changed`，官方客户端自动把这批工具加入本会话。
- **只有 Godot 会话有工具**：非 Godot 会话**根本不挂载**——没有连接、没有工具，不是"注册后隐藏"。
- **按会话 cwd 传项目路径**：挂载时按**该会话的工作目录**解析 Godot 项目路径（`$DSH_HOME/godot/paths.json` 按 cwd 批量条目，无匹配回退全局设置）并作为 `GODOT_MCP_PROJECT_PATH` 传给 server；子进程 cwd 也设为会话 cwd——不同会话工作区可以是不同 Godot 项目，绝不绑死目录。
- **进入会话即连接**：进入 Godot 会话就建立连接（不再"首次调用才拉起"，也没有空闲自动关闭）。编辑器没开不会让会话创建失败，断线由官方客户端自动重连（500ms 起、每次翻倍、上限 30s）；截图等图片结果现在是**真正的图片附件**。
- **工作流提示词 section**（可选，随 bundle 行挂载）：教模型按需扩面、批量编辑、理解编辑器 FIFO 串行语义、按错误码恢复、用截图验证。
- **管理 CLI `dsh-godot`**：install / uninstall / status——校验 server 构建产物与 Godot 项目、写入/清理路径配置、体检编辑器注册表与端口。
- **插件页配置表单**：注入三开关 + 「相关路径」区（server 入口全局单值 + 按工作目录的项目路径批量条目），保存即写 host 侧 `paths.json`。

> 通用工具调试界面（工具树 / 手动调用 / 结果与历史 / 状态条）与 Godot 无关，已迁至 `deepseek-harness-project-mcp` 的「工具调试」Tab；本包不再提供会话顶部 Tab。

## 前提

1. DSH（CLI 自带 `@deepseek-ai/dsh-mcp-client`——本插件挂载的就是它，无需另装）。
2. GodotMCP 工作区就绪：随包 shim 在位——`<GodotMCP 工作区>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim[.exe]`（由仓库发布产出：`dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=<rid>`）。本插件把这个入口作为 stdio server 交给宿主官方客户端直接 spawn；shim 负责确保机器级单例 daemon 在跑（同目录的 `godot-mcp-daemon[.exe]`）再把 stdio 转发到 daemon 的 HTTP 面，因此不要求 daemon 先被别处拉起。仅 stdio 兜底/回滚时才用 `<GodotMCP 工作区>/adapters/dsh/godot-http-bridge.mjs`（Node 脚本，经 node 执行；Node 桥已随插件 1.1.0 退役）。
3. 目标 Godot 项目已安装 `godot_mcp_toolkit` addon（用 GodotMCP 的 `install-godot-project.ps1`），且**编辑器正在运行**（无头亦可：`godot --headless --editor --path <Godot 项目>`）。server 不启动编辑器；编辑器不在时工具调用返回可行动错误，但连接与会话创建都不受影响。

## 环境要求

- DeepSeek Harness `>=0.2.0-rc.2 <0.3.0-0`（`package.json` 的 `engines.dsh` 声明，0.2.1 起由 DSH 加载器据此判定；0.1.x 与 0.3.x 会被拒绝加载）；完整 UI 使用 `web`，Godot preset 会话使用同一 `web` profile
- Node.js `^22.19.0 || >=24.0.0`

## 安装

```sh
# 从本仓库(开发安装,含 bundle 行 + 路径配置):
node bin/dsh-godot.mjs install --profile web \
  --project <Godot 项目> \
  --cwd <会话工作目录> \
  --godot-mcp-root <GodotMCP 工作区> \
  --link <本仓库>

# 或不装 bundle(只要挂载能力,不要提示词 section/配置表单):
node bin/dsh-godot.mjs install --profile web \
  --project <Godot 项目> \
  --server-dist <GodotMCP 工作区>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim.exe
```

server dist 解析顺序：`--server-dist` > `$GODOT_MCP_SERVER_DIST` > `--godot-mcp-root`/`$GODOT_MCP_ROOT`。

**桌面版 DSH（Electron 应用）**：本插件兼容桌面版（桌面 Host 端口 19387）。路径配置 `$DSH_HOME/godot/paths.json` 是机器级共享——`install` 在桌面场景照常写入（`--profile desktop` 时仅跳过 bundle 安装）；官方客户端 spawn 的 shim 是原生 exe，不依赖 PATH 上的 node。bundle（提示词 section + 插件页配置表单）的 link 开发安装（桌面应用**未运行**时执行）——编辑 `~/.dsh/profiles/desktop/package.json`：

```jsonc
"dependencies": { "deepseek-harness-godot_unified": "link:<本仓库>" },
"dsh": { "profile": { "bundles": [ /* 官方 bundle 保持原样，末尾追加 */ "deepseek-harness-godot_unified" ] } }
```

在 profile 目录内执行 `pnpm install` 物化链接，启动桌面应用即挂载；改源码后 `npm run build` 并重启桌面应用；卸载移除两处条目后重新 `pnpm install`。

v0.5 起 install **不写 profile 的 mcp-client 行**：把 `serverDist` 与 `{cwd, projectPath}` 条目写入 `$DSH_HOME/godot/paths.json`（并清理历史静态 mcp-client 受管块，防双轨）。路径生效无需重启——进入 **Godot preset 会话**（`Godot` 模式，由本包 bundle patch 的 `preset-godot` 行声明，无需手工安装 preset 目录）时，插件按该会话的 cwd 解析路径、把宿主官方 MCP 客户端挂到该会话；也可在侧栏「插件」页 → deepseek-harness-godot_unified 页面按 cwd 增改路径（保存即时生效，DSH 0.1.6+）。host 按文件 mtime 重读 `paths.json`，所以 CLI 在另一个进程里改写的路径同样会在下一次解析时生效。不同会话工作区可以配置不同 Godot 项目。

### 常用选项

| 选项 | 语义 |
| --- | --- |
| `--cwd <dir>` | 条目绑定的会话工作目录（默认当前目录；按会话 cwd 匹配生效） |
| `--server-name <name>` | 保留参数：v0.5 不再写 profile 行，工具名由插件行 config 的 `serverName` 决定（默认 `godot`，即 `mcp__godot__*`） |
| `--read-only` / `--unsafe` / `--rate-limit` / `--editor-port` / `--runtime-port` / `--timeout-ms` | 原官方行 env 参数，v0.5 不再写入（警告）；相关能力由 server 默认/项目设置提供 |

## 验证

```sh
node bin/dsh-godot.mjs status --profile web   # 路径配置/server/项目/注册表/端口体检
dsh --profile web --dump-config | grep godot  # 组合校验(不启动)
```

GUI 里：用 Godot preset 新建会话 → 模型即可看到 `mcp__godot__*` 工具（进入会话即建立连接，不需要先手动调用一次）。前提是目标项目的 Godot 编辑器在运行。若 server 入口为空，该会话里没有工具、只有一段"如何配置"的提示词。需要手动调试工具调用时，用 `deepseek-harness-project-mcp` 的「工具调试」Tab。

**技能目录**：本包的 6 个 Godot 工作流技能（godot-control / godot-debug / godot-mcp-extension / godot-playtest / godot-project-setup / godot-ui）随 Godot preset 提供，来源是包内 `skills/`——它是指向上游 GodotMCP `plugin/godot-mcp-unified/skills` 的 junction，**改上游即时生效**（无需重建本包）。历史 `~/.dsh/.agent-presets/godot/skills/` 目录已废弃（DSH 0.1.6+ 不再读取该目录）。

**DSH 0.2.1 起的两处行为变化**（本包已适配，无需用户操作）：

- **`{{cwd}}` 提示词变量已移除**：persona 行不再声明 `suffix`，工作目录改由 DSH 的 working-directory 服务以用户上下文供给。`{{model}}` 仍是注册变量。
- **委派不再分前台/后台**：`subagent` / `subagent_fork` 每次委派都返回 activation，最终答案经父级完成通知交付；`job_output` / `job_kill` 不再用于子代理结果。本包 preset 已移除 `backgroundMode`。

## 卸载

```sh
node bin/dsh-godot.mjs uninstall --profile web            # 清空路径配置(并清理历史静态 mcp-client 行)
dsh plugin --profile web remove deepseek-harness-godot_unified # 移除 bundle(提示词 section/配置表单)
```

## 数据与边界

- GodotMCP server 只连本机回环（编辑器/运行时 WebSocket，token 鉴权）；stdio 子进程的环境由宿主官方 MCP 客户端清洗（凭据样式变量与 `DSH_*` 被剔除），本插件显式传入的 `GODOT_MCP_PROJECT_PATH` 不受影响。
- server 入口与项目路径写在 **`$DSH_HOME/godot/paths.json`**（机器本地配置，不随包发布、不入 git）；历史静态 mcp-client 受管块由 CLI 清理。
- 默认（按会话挂载）下，每个 Godot 会话（含继承 preset 的子代理）各持有一条轻量 stdio shim 连接；机器级 daemon 仍是单例，由 shim 复用。原版模式则只有 host 上的一条共享连接。
- 已随 v0.5 移除：工具清单缓存 `$DSH_HOME/godot/tools-cache.json`（**不再读取**，旧文件残留在盘上无害、也不会被自动删除）、上游中文描述翻译表、deny/restrict 注入补丁。
- 已知限制：MCP 提示词模板（prompts）官方客户端不支持；资源（如 `godot://roots`）由 profile 的 `dsh-mcp-resources` 行提供，本包不自行转发；上游工具的只读/破坏性注解不映射为 DSH 权限规则。

## 工具注入策略与路径（插件页配置表单）

**按会话挂载宿主官方 MCP 客户端**：插件只决定"哪些会话需要它、它连哪个 server"。会话创建（`agent/created`）且 preset 是 `godot` 时，把官方客户端挂进该会话的 Agent 作用域；**空白会话（首轮之前）**切到 Godot preset 同样挂载，切走 / 会话销毁 / 插件卸载时卸载（首轮之后 DSH 不允许换预设）。挂载不 await 连接（失败只记日志，绝不打断会话创建）：会话创建最多等 10 秒让工具就绪，超时则工具在后台就位、下一轮可见。插件页配置表单提供：

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| 原版模式（全局挂载） | 关 | 开=在 host 上挂**一个共享连接** + 全局注入工作流提示词，所有会话都能看到 `mcp__godot__*` 工具（项目路径取全局设置、不随会话 cwd 切换）；关=**仅 Godot preset 会话挂载**，其余会话**根本没有**这些工具（不是隐藏） |
| 注入工作流提示词 | 开 | 工作流提示词 section 开关（仅对挂了 Godot MCP 的会话生效：Godot preset 的会话级 section，或原版模式的全局 section） |
| 提示词中文化 | 关 | 只把**工作流提示词**切成中文（默认英文）；工具描述与错误消息来自上游 server，保持原文。工具名保持英文 |

「相关路径」区（server 入口全局单值 + **按工作目录的项目路径批量条目**）经同源 `GET/POST /godot-config/api/path-settings` 读写 `$DSH_HOME/godot/paths.json`；会话创建时按其 cwd 匹配项目路径（全表只有一条时用它兜底），无匹配回退全局值。server 入口为空时不挂载，Godot 会话会得到一段"如何配置"的提示词（模型据此不臆造工具调用并引导你去填路径），其他会话没有任何提示。

安全与边界：

- 路径设置路由仅接受**本机回环 + 同源**（`Origin`/`Host`/`sec-fetch-site` 三重校验）；方法不是 GET/POST 时 405，请求体上限 8MiB（413），非法 JSON 400；错误以 `{error:{code,message}}` 返回。

MIT License.
