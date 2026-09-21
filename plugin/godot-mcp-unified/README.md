# Godot MCP Unified

英文版：[README.en.md](README.en.md)

Godot MCP Unified 是一个本地插件（plugin），用于控制 Godot 4 editor project 和调试游戏 session。能力面包含 83 个内置 tools、159 个 operations、31 个按需加载组、MCP resources，以及五个 agent skills；默认只暴露 19 个业务 tools 和 `discover_tools`。

控制面为机器级单例 **godot-mcp-daemon**（.NET 自包含单文件，本仓库 `server-dotnet/` 构建）：对各 host 暴露 loopback Streamable HTTP（默认 `127.0.0.1:6590`，Bearer 认证），按需自动拉起与自愈（Godot 编辑器的 addon 边车 / stdio host 的 godot-mcp-shim 均可拉起）。MCP 工具的自然语言说明默认使用中文；中文工具参考由 daemon 按需工具组与 `discover_tools` 提供。更新插件后，Codex 需要在新任务中加载，ZCode 需要重启以保证各窗口读到更新后的说明。

| 客户端 | 适配器 | 加载方式 |
| --- | --- | --- |
| Codex | `.codex-plugin/`（plugin.json + mcp.json，url 型） | 个人/仓库插件市场，或 `codex plugins add` 指向本目录 |
| ZCode | `.zcode-plugin/plugin.json` + 根 `.mcp.json`（http 型） | 本地插件市场，市场根为仓库根，入口见根 `marketplace.json` |
| VS Code | `adapters/vscode/`（仓库接入层：README + mcp.json 示例 + 安装脚本） | 项目级 `.vscode/mcp.json`，安装脚本负责写入 |
| DSH（stdio-only） | `adapters/dsh/godot-http-bridge.mjs`（node stdio ↔ daemon HTTP） | 把 DSH 设置的 godotServerDist 指向该文件 |

> 为某个新客户端做适配时，只需要在插件根目录新增一个适配子目录/文件，**不要**复制整份插件内容。

验证过的 Godot 目标：Godot 4.7.2 stable（.NET/mono 版），官方构建报告版本 `4.7.2.stable.mono.official.ed1daf0bf`。

## 包含内容

- Editor authoring：scenes、nodes、properties、signals、scripts、resources、project settings、input maps、autoloads、2D/3D helpers、animation、audio、particles、navigation、TileSet/TileMap、placeholders 和 assets。
- Language/debug：GDScript parse checks、Godot LSP diagnostics/navigation、debugger state 和 breakpoints、editor/game logs、crash context。
- Runtime/playtest：live node state、script variables、input sequences、screenshots、animation control、property changes、arbitrary-expression escape hatch，以及有上限的 freeze/step/step-until time control。
- Security：仅 loopback 的 sockets、轮换 session tokens、project path guards、read-only mode、tool annotations、audit logs、response caps，以及 untrusted-content envelopes。
- Skills：`godot-control`、`godot-debug`、`godot-playtest`、`godot-project-setup` 和 `godot-mcp-extension`。

## 客户端安装

### Codex

Codex 从 `.codex-plugin/plugin.json` 读取插件（skills 与 MCP server 配置见 `.codex-plugin/mcp.json`，url 型指向 daemon HTTP 面）。把本目录注册为个人/仓库市场插件，或运行 `codex plugins add <本目录的绝对路径>` 后启用 `godot-mcp-unified`；并以用户环境变量 `GODOT_MCP_DAEMON_TOKEN` 提供 Bearer token（安装器会写入）。

### ZCode

ZCode 通过本地插件市场安装，市场根为仓库根（`marketplace.json` 位于仓库根），插件源指向本目录。ZCode 读取 `.zcode-plugin/plugin.json` 与根 `.mcp.json`（http 型 + Authorization 头，token 同上）。

安装/更新：仓库根运行 `& ".\adapters\zcode\install-http-face.ps1"`（发布 daemon、拉起、翻转注册，幂等）或 `& ".\adapters\zcode\link-zcode-plugin.ps1"`（插件缓存链接部署），然后重启 ZCode。

### VS Code

VS Code 没有市场/插件包概念，它消费项目级 `.vscode/mcp.json`。运行：

    & "<repo>\adapters\vscode\install-vscode-mcp.ps1" -ProjectPath "D:\Games\MyProject"

安装脚本会在目标项目写入 `.vscode/mcp.json`（http 型指向 daemon HTTP 面 + Bearer token），在 VS Code 中打开该项目即可使用 `godot` 服务器（`mcp__godot__*`）。完整说明见 [`../../adapters/vscode/README.md`](../../adapters/vscode/README.md)。

## 安装到 Godot project

运行：

    & "<plugin-root>\scripts\install-godot-project.ps1" -ProjectPath "D:\Games\MyProject" -GodotExecutable "<Godot 可执行文件>"

安装程序会：

1. 验证 Godot 4；
2. 备份已有的 addon 和 `project.godot`；
3. 安装 `addons/godot_mcp_toolkit` 并启用它（边车将按需自动拉起 daemon）；
4. 写入项目级 `.vscode/mcp.json`（http 型）并保留其他 servers；
5. 启动无头 editor，并要求确认已通过身份验证的 loopback-listener。

使用 `-Template empty`、`default`、`2d-platformer` 或 `3d-fps`，可以在空的目标目录中创建新 project。

## 验证

自动化验收（回归入口）：

    dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"

全量：`dotnet test tests/godot-mcp-daemon.tests`。

## 架构

各 host（ZCode/Codex 走 http 配置，DSH 走 stdio 自举桥）连接机器级单例 **godot-mcp-daemon**；daemon 在 toolkit 的 machine registry 中发现匹配的 project，通过 localhost WebSocket 向 editor 完成身份验证；playtest 运行期间另开经身份验证的 runtime channel。plugin 的 export hook 启用后，导出游戏会排除该 addon。

这是本仓库维护的本地整合实现，不包含第三方运行时依赖；上游材料的许可证与归属记录见插件内的 `LICENSE` 与 `addons/godot_mcp_toolkit/ATTRIBUTIONS.md`。
