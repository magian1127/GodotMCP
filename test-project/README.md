# 本地功能验收项目

中文 | [English](README.en.md)

test-project 承载编辑器、运行时、C#、LSP、调试器和输入验证。addons/godot_mcp_toolkit 是指向插件真源的 junction；addons/editor_ui_locale_test 属于本项目。备份与 Godot 构建缓存均在被忽略目录。

| 夹具 | 用途 |
| --- | --- |
| Main.tscn、runtime_counter.gd | 场景操作与精确帧计数 |
| Acceptance.csproj、tests/McpValCsNode.cs | C# 全局类、属性与信号 |
| Validations/fixtures/env_probe.gd | LSP、引用、断点 |
| scripts/test_framework/check_all_scripts.gd | 全脚本加载 |
| test/fixtures/send_text_smoke.tscn | 输入、秘密字段遮蔽、提交 |
| tests/*_test.gd | 时间、语言、主题、本地连接与配置发现 |

## macOS / Linux 链接

这两个平台用目录符号链接代替 Windows junction。只在链接位置尚不存在时创建；带尾斜杠的目录忽略规则可能不匹配符号链接，按需添加本地排除：

```bash
ln -s ../../plugin/godot-mcp-unified/addons/godot_mcp_toolkit test-project/addons/godot_mcp_toolkit
echo 'test-project/addons/godot_mcp_toolkit' >> .git/info/exclude
```

## 构建与启动

从仓库根运行，Godot 路径使用本机 .env 或显式参数：

```powershell
Get-Item -Force ./test-project/addons/godot_mcp_toolkit | Select-Object FullName, LinkType, Target
dotnet build ./test-project/Acceptance.csproj
$fixturePath = (Resolve-Path ./test-project).Path
& "<Godot 可执行文件>" --headless --editor --path $fixturePath
```

验证目标是 Godot 4.7.2 stable .NET/mono，实际版本用 --headless --version 核对。截图等视觉检查另开有显示环境的编辑器。不再用 GODOT_MCP_PROJECT_PATH 选择会话目标；shim 传 cwd，模型调用 godot_connect(project) 绑定。

## MCP 验证链

1. 构建当前 daemon/shim；不要把旧 publish 或宿主缓存当作当前源码。
2. 用 stdio shim 握手并取列表，初始应只有 godot_connect。
3. 连接本项目，再取列表应是 core 9；带 editor_ops 则为 12。用 list_instances 与 scene_get_tree 确认真实项目身份。
4. 激活 code_intel 后做脚本/LSP检查，playtest 后做运行时检查，scene_edit 后做场景变更。
5. 单独验证宿主原生目录自动刷新；程序客户端的主动 tools/list 不证明宿主收到通知。

多编辑器可使用不同项目目录和 WS 端口，但共用同一个 daemon；进程回归在正式服务停止后串行执行，不以不同端口/状态目录绕过单例。当前 daemon 的 STATE_DIR 也是注册表消费目录；隔离 daemon 后，编辑器须向同一注册表发布。WS 端口固定配置监听进程；LSP 实际监听必须与注册表发布一致。见 [多实例指南](../plugin/godot-mcp-unified/addons/godot_mcp_toolkit/docs/multi-instance.zh-CN.md)。

## 回归命令与边界

在 plugin/godot-mcp-unified/server-dotnet 运行：

```powershell
dotnet test tests/godot-mcp-daemon.tests
dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"
```

Godot SceneTree 测试在本项目中用 --headless --path . -s res://tests/<name>.gd 运行。配置发现回归由仓库 scripts/test-mcp-config-discovery.ps1 -IncludeUi -GodotExecutable "<Godot 可执行文件>" 驱动，保留其隔离标记检查。已退役 server/ 下的 npm verify/smoke/flows/eval 不是当前测试入口。

token 只从注册表读取给测试进程，不写命令、日志或版本控制。清理本次创建的场景、资源、脚本和运行时状态，保留原始失败证据。已知工具面/通知限制见 [daemon README](../plugin/godot-mcp-unified/server-dotnet/README.md)。
