# deepseek-harness-godot_unified

中文 | [English](README.en.md)

把 Godot MCP Unified 接入 DSH。接入层决定哪些会话挂载哪个服务器；连接、协议协商、工具发现、通知和图片投影交给宿主的 `@deepseek-ai/dsh-mcp-client`，本包不自建 MCP 客户端。

## 当前行为

- 默认只有 Godot preset 会话挂载。每个 Agent 作用域持有一个 stdio shim，会话键、默认目标与激活组各自独立；子代理也各有连接。工具命名为 `mcp__<serverName>__<原生名>`，默认入口 `mcp__godot__godot_connect`。
- 首次只有 godot_connect；调用后提供 9 个核心工具和本会话激活的组。目录共 17 个内置组；工具/schema 来自 server，插件不缓存或硬编码工具表。
- 插件只传会话 cwd，**不再注入项目路径环境变量**。目标由 godot_connect(project/instance) 选择，省略时 daemon 使用本键默认目标、cwd 本层 project.godot、唯一活跃实例。
- paths.json 仅使用全局 serverDist。旧 projects 条目和 godotProjectPath 设置不参与路由；兼容的数据类与 API 结构仍保留。CLI install 仍要求 --project 作项目有效性校验，不会把它持久绑定给会话。
- 挂载在会话创建时发生，不等于已连接到项目。空白会话可切预设；首轮之后 DSH 拒绝换预设。创建最多等 10 秒工具就绪，失败只 warn，failOnStartupError=false。
- 原版模式 injectLegacyMode 使用 **host 上一个共享连接**：所有会话共享它的键、目标和组，不能解释成每会话隔离。
- 宿主默认工具超时 **240000ms**，设置名 toolCallTimeoutMs；shim 默认 210 秒，daemon 长工具单次 wire 默认 180 秒。现有按会话挂载单改超时不会重挂，需新建会话读取新值；提示词开关只改文本。

通知与完整恢复取决于双方协议实现。当前 shim 已支持 legacy 内部通知及重连；现代宿主 listen 的重放仍未完成；TTL=0 不能证明每轮自动重拉。详细行为、恢复和已知限制见 [behavior.md](docs/behavior.md) 与 [daemon README](../../../plugin/godot-mcp-unified/server-dotnet/README.md)。

## 安装与升级

要求 DSH `>=0.2.0-rc.2 <0.3.0-0`、Node.js `^22.19.0 || >=24.0.0`，以及已安装 toolkit addon 的 Godot 项目。发布匹配的 daemon 与 shim 到同一目录，再将 serverDist 指向 shim：

```sh
node bin/dsh-godot.mjs install --profile web   --project "<Godot 项目>"   --godot-mcp-root "<GodotMCP 工作区>"   --link "<接入层目录>"

node bin/dsh-godot.mjs install --profile web   --project "<Godot 项目>"   --server-dist "<GodotMCP 工作区>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim.exe"
```

入口解析顺序：--server-dist > GODOT_MCP_SERVER_DIST > --godot-mcp-root / GODOT_MCP_ROOT。这些仅为 CLI 安装解析；host 使用 paths.json 的 serverDist，再回退 godotServerDist 设置。

升级后删除旧 HTTP 直连条目和手写静态 mcp-client 行。install 会清理历史受管块，保留块外用户配置，并写入 serverDist 与空 projects。历史 godot-http-bridge 脚本不作为本期推荐的入口。--read-only/--unsafe/--rate-limit/端口/--timeout-ms 是不再写入的旧行参数；新宿主超时在插件配置页设置。

桌面 DSH 使用 --profile desktop 时，CLI 跳过 bundle 安装与 patch 行操作，路径文件照常更新。bundle 通过桌面应用的插件管理加载；手工 link 开发安装应在应用未运行时进行，步骤与边界见 [开发说明](docs/development.md)。

## 使用

1. 在插件页填写随包 shim 的 server 入口并保存；server 为空时不挂载，只提供配置提示。
2. 使用 Godot preset 创建会话，打开目标 Godot 编辑器。
3. 调用 godot_connect(project="<Godot 项目绝对路径>")；确认目录已展开，再用 list_instances、scene_get_tree 读取真实实例。
4. 用 discover_tools(request=[组名], include_schemas=true) 激活当前阶段能力，用 reset=[...] 释放。schema 只返回名称时检查激活后的 tools/list，不猜参数。

项目目录下已有 .mcp.json 不等于 DSH 插件已挂载；程序客户端成功也不等于当前模型原生工具列表已更新。使用当前宿主实际提供的重连方式，必要时新建会话。

六个 Godot 技能由 preset 的 dsh-skill-filesystem 行从包内 skills/ 加载；开发目录 skills/ 是指向插件真源的 junction。旧 ~/.dsh/.agent-presets/godot/skills/ 不再使用。改技能源文件无需构建，但已加载会话需确认重新读取；二进制变更需要发布并重连。

## 设置与本地数据

| 设置 | 默认 | 作用 |
| --- | --- | --- |
| injectLegacyMode | false | host 共享连接，关闭时按 Godot 会话挂载 |
| promptGuidance | true | 是否注入工作流提示词 |
| zhPrompt | false | 工作流提示词语言，不改变 server 描述 |
| godotServerDist | 空 | paths.json 无有效 serverDist 时的入口兜底 |
| toolCallTimeoutMs | 240000 | 毫秒；正数/数字字符串，解析时上限 900000；无效值回默认 |

路径设置 GET/POST /godot-config/api/path-settings 只持久化 serverDist；GET projects 恒为空，POST 忽略 projects。路由仅本机回环和同源可用，405/413/400 对应方法、大小与 JSON 失败。旧项目字段保留兼容形状，不代表仍有项目映射。

CLI 与页面写入 `$DSH_HOME/godot/paths.json`，它是机器本地配置。host 按 mtime 重读；单改磁盘文件不意味着既有挂载已经重建。凭据由 daemon/shim 管理，本包不处理 token。

## 验证与卸载

```sh
node bin/dsh-godot.mjs status --profile web
dsh --profile web --dump-config
npm run typecheck
npm run build
npm test
npm run verify
node bin/dsh-godot.mjs uninstall --profile web
dsh plugin --profile web remove deepseek-harness-godot_unified
```

status 是安装与注册表诊断，不能代替真实 MCP 调用。通用工具调试界面已迁至 deepseek-harness-project-mcp；本包不提供会话 Tab。prompts 模板不支持，资源由宿主 dsh-mcp-resources 提供，工具注解未映射成 DSH 权限。

维护约束见 [AGENTS.md](AGENTS.md)，验证与历史见 [release.md](docs/release.md)。MIT License。
