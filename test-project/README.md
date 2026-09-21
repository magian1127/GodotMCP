# 本地功能验收项目

本项目用于验证 Godot MCP Unified 的编辑器、运行时、C#、LSP、调试器和输入工具。共享插件目录采用 Windows Junction：

```text
test-project/addons/godot_mcp_toolkit
  -> plugin/godot-mcp-unified/addons/godot_mcp_toolkit
```

`addons/editor_ui_locale_test` 是本测试项目自己的 UI 测试插件。共享插件的旧副本备份与测试临时产物存放在 `test-project/.godot-mcp-unified-backups/` 等被 git 忽略的位置，避免 Godot 扫描两份插件源码或污染版本管理。

| 文件 | 验证用途 |
| --- | --- |
| `Main.tscn`、`runtime_counter.gd` | 编辑器场景操作和运行时精确帧计数 |
| `Acceptance.csproj`、`tests/McpValCsNode.cs` | C# 全局类、导出属性、信号和节点操作 |
| `Validations/fixtures/env_probe.gd` | LSP 符号、引用与调试器断点 |
| `scripts/test_framework/check_all_scripts.gd` | GDScript 加载校验；第二个独立的断点身份夹具 |
| `test/fixtures/send_text_smoke.tscn` | 单行输入、秘密字段遮蔽、多行输入和提交 |
| `tests/*_test.gd` | 时间控制、语言、主题、本地连接策略及配置发现回归 |

在仓库根目录检查链接并构建 C# 夹具：

```powershell
Get-Item -Force .\test-project\addons\godot_mcp_toolkit |
    Select-Object FullName, LinkType, Target
dotnet build .\test-project\Acceptance.csproj
```

2026-09-08 的验收使用 Godot 4.7.2 stable（.NET/mono 版），实测版本为 `4.7.2.stable.mono.custom_build.5f9f086df`。编辑器需要真实显示环境才能验证截图。为测试进程设置独立端口，并显式绑定本项目：

```powershell
$env:GODOT_MCP_PROJECT_PATH = (Resolve-Path .\test-project).Path
$env:GODOT_MCP_EDITOR_PORT = '6551'
$env:GODOT_MCP_RUNTIME_PORT = '6571'
$env:GODOT_MCP_LSP_PORT = '6015'
& '<Godot 可执行文件>' --editor --path $env:GODOT_MCP_PROJECT_PATH --lsp-port 6015 --dap-port 6016
```

先确认上述端口未被占用。Godot 的 DAP 服务可能使用 6006；把 LSP 也指向该端口会出现 TCP 连接成功但 LSP 初始化一直不返回的情况。固定运行时端口必须同时传给编辑器进程及 MCP 服务进程。

在 `plugin/godot-mcp-unified/server` 目录运行：

```powershell
npm run verify
npm run smoke
npm run flows
npm run eval
npm run probe:screenshot
```

本项目的 Godot 回归脚本可直接以 `godot --headless --path . -s` 方式运行。配置发现测试会创建隔离目录，需通过仓库根目录下的 `scripts/test-mcp-config-discovery.ps1 -IncludeUi -GodotExecutable "<Godot 可执行文件>"` 执行；它会修改测试配置，因此不要绕过脚本中的隔离标记检查。

分发、安全和压力测试从本项目的注册表条目读取令牌，只将其传给测试进程，避免写入命令、日志或版本控制。每轮实时测试完成后检查临时场景、资源、脚本和用户数据的清理情况，并保留原始失败记录。
