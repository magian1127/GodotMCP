# VS Code 适配

中文 | [English](README.en.md)

使用工作区 `.vscode/mcp.json` 配置 stdio 服务器，顶层键为 servers；command 指向随包 shim，shim 自举 daemon 并读取 token，宿主不配置凭据。

## 安装与使用

先将 daemon 与 shim 发布到 `<仓库>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/`，并安装目标项目 addon：

```powershell
& "<仓库>/plugin/godot-mcp-unified/scripts/install-godot-project.ps1" -ProjectPath "<Godot 项目>" -GodotExecutable "<Godot 可执行文件>"
& "<仓库>/adapters/vscode/install-vscode-mcp.ps1" -ProjectPath "<Godot 项目>"
```

安装器会备份已有配置并保留其它服务器。打开该项目，按当前 VS Code 的 MCP 信任/启动流程启用 godot；需要时重载窗口。

初始只有 godot_connect。调用 godot_connect(project="<Godot 项目绝对路径>") 后应展开 9 个核心工具，再用 discover_tools(request=[组名]) 激活更多能力。以客户端实际显示名称和最新 schema 为准。

## 配置范围与验证

配置只影响本工作区；后台 daemon 按机器级状态目录复用。当前 Agent Host 也可能读取工作区 .mcp.json 或用户 ~/.copilot/mcp-config.json，核对所用宿主的实际入口。手动配置模板为 [mcp.json.example](mcp.json.example)。

升级后删除旧 HTTP 直连条目。仅“服务器已启动”或程序探针成功不证明当前聊天目录已刷新；使用实际版本提供的重连动作。当前通知与重放缺口见 [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.md)，真实宿主自动扩面仍需验收。
