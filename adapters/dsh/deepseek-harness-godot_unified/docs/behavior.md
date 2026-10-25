# 用户可见行为

中文 | [English](behavior.en.md)

## 环境与挂载

DSH 范围为 `>=0.2.0-rc.2 <0.3.0-0`，加载器用 engines.dsh 判定并包含预发布版本；0.2.0-rc.2 本身受支持。Node 要求见 package.json。

默认按 Agent 作用域挂载官方 mcp-client：Godot preset 在 agent/created 或空白会话切预设时挂载；切走预设、销毁会话或卸载插件时回收。首轮之后 DSH 不允许换预设。其它会话没有连接和工具。子代理继承 preset，各有 shim。

injectLegacyMode=true 在 host 上挂一条共享连接，所有会话共享键、组和默认目标；此时不再按会话挂载。这个设置叫原版模式，与 MCP 协议版本的 legacy 协商不是同一概念。

failOnStartupError=false；会话创建只等待最多 10 秒就绪，失败只 warn。官方客户端从 500ms 起指数退避，上限 30 秒，连续 10 次失败后移除服务器工具；连接稳定超过退避上限后重置重试预算。恢复入口包括新建会话、改变实际 server 入口触发重挂、在空白会话切预设。改提示词开关不会重挂。

## 项目与工具面

1. serverDist 优先取 `$DSH_HOME/godot/paths.json`，再回退 godotServerDist；为空不挂载，模型得到配置提示。
2. shim cwd 是会话 cwd。适配器不再注入 GODOT_MCP_PROJECT_PATH，也不从旧 projects 映射选择目标。
3. 首次只有 `mcp__godot__godot_connect`。连接参数 project/instance 显式选目标；省略时用本键默认目标、cwd 本层 project.godot、唯一活跃实例。daemon 不启动编辑器。
4. 连接后是 9 个核心工具 + 本键激活组。用 request 传组名或关键词，activate 是布尔，include_schemas 单独使用不会激活。工具名和参数由 server 的最新 tools/list 提供。
5. 原始入口名为 godot_connect，其它如 scene_get_tree 不带统一 godot_ 前缀；宿主加 mcp__<serverName>__，serverName 默认 godot。

当前 shim 已为 legacy 建立内部通知流并在断线后重连；现代宿主自有 listen 的重放仍未实现；多次增量组变更也不能仅凭最后一帧完整恢复。TTL=0 不保证宿主重拉。若列表未更新，核对通知与宿主发现能力，按当前宿主提供的重连方式恢复并重新声明所需组。详细限制以 [daemon README](../../../../plugin/godot-mcp-unified/server-dotnet/README.md) 为准。

include_schemas=true 对部分常驻组工具只返回名称；读取激活后的 tools/list 取得完整 schema。使用 discover reset 停用现组并核对结果；多项目交错操作显式传 instance。

## 设置与超时

| 设置 | 默认 | 实际作用 |
| --- | --- | --- |
| injectLegacyMode | false | host 共享连接 / 默认按 Godot 会话挂载 |
| promptGuidance | true | 工作流 section 注入 |
| zhPrompt | false | section 用中文；不翻译工具描述与错误 |
| godotServerDist | 空 | 路径存储无 serverDist 时的兜底 |
| toolCallTimeoutMs | 240000ms | 正数或数字字符串；取整，上限 900000，无效回默认 |

当前按会话 configKey 仅比较入口、兼容空项目字段、cwd；**单改超时不会重挂旧连接**，新会话才读取新值。host 共享模式使用包含 timeout 的 mountConfigKey，会重挂。页面能保存某值不等于既有连接已经应用它。

默认预算：宿主 240 秒 > shim tools/call 210 秒 > daemon 长工具单次 wire 180 秒；普通 daemon 工具 30 秒。组合工具按段预算，不保证整体 180 秒完成。超时/传输不确定时先查询状态，不直接重提变更。

server instructions 会进入宿主提示词，当前说明先连接再展开。prompts 模板不支持。截图在宿主图片能力可用时投影成附件，无法投影时显示诊断；无头模式不能作为视觉验收。

## CLI 与路径 API

CLI install 仍需 --project 校验存在 project.godot；只持久化 serverDist 与空 projects，不再按 cwd 写入项目绑定。--cwd 是兼容输入，不参与运行时目标路由。--link 可选安装 bundle；desktop profile 跳过 bundle/patch 操作。历史静态 mcp-client 受管块会清理，块外用户内容保持。

路径设置路由仍是 `GET/POST /godot-config/api/path-settings`，PathStore 文件仍保留：

- GET 返回 serverDist、projects:[]、currentCwd:null、currentProjectPath:null。
- POST 只持久化 serverDist，忽略 projects；这是兼容形状，项目映射已停用。
- peer 回环、Host、Origin、sec-fetch-site 同源校验；非 GET/POST 为 405，超过 8MiB 为 413，非法 JSON 为 400。
- 配置页统一保存草稿后才 POST。PathStore 按 mtime 重读外部写入；路由的 serverDist 兜底通过 getter 读取最新设置。
- serverDist 是 DSH home 级全局值；默认目标与组是 shim 会话级状态，二者不可混为一谈。

## 失败与恢复

| 症状 | 检查与恢复 |
| --- | --- |
| 没有任何 Godot 工具且提示未配置 | 填写可执行 shim 入口，确认 Godot preset 挂载，必要时新建会话 |
| 只有 godot_connect | 正常的未连接面，调用入口并核对返回目标 |
| 连接/激活后仍只有旧工具列表 | 检查宿主通知与目录更新，使用其实际重连动作；不要把 TTL 当自动重拉证明 |
| TARGET_NOT_RUNNING / NO_INSTANCE | 打开所报项目的 Godot 编辑器，重试只读查询 |
| SESSION_HEADER_MISSING 等头错误 | 核对 stdio shim 和旧 HTTP 配置；重复 connect 无法修复传输头 |
| 长期断线后工具移除 | 修正入口/连接原因后重建会话；提示词开关不会重启连接 |
| 超时设置保存但旧值仍生效 | 当前按会话重挂缺口；新建会话 |
| 目录里无场景写工具 | 检查 scene_edit 是否激活，再看版本/只读限制 |
| 模式选择器没有 Godot | 核对 bundle patch 的 preset-godot 声明；旧 .agent-presets 目录不再读取 |
| 同名 serverName 冲突/无法加载官方 client | 核对当前 profile 官方模块和挂载作用域；只记录错误类型，不回显配置 |

## 数据边界

本包写 DSH home 的 paths.json，并通过 CLI 清理历史 patch 受管块；不读写 Godot 场景源码，也不处理凭据。官方客户端清洗父进程凭据样式变量和 DSH_*；本插件的显式 env 当前为空。编辑器 token 由 daemon 读注册表，shim 读 daemon token。

旧 tools-cache.json 不再读取；通用工具调试界面已迁到 deepseek-harness-project-mcp，本包没有 conversation.view 或 /godot-workbench API。资源来自宿主 dsh-mcp-resources，工具注解不映射成 DSH 权限。
