# 代理控制层（adapters）

英文版：[README.en.md](README.en.md)

本目录统一收纳所有"代理客户端 → Godot MCP Unified"的接入管理层。维护原则：**文件不重复、真源明确**——每个客户端都直接消费仓库唯一真源 `plugin/godot-mcp-unified`，不在客户端目录里维护插件副本。

## 通道总览

| 通道 | 接入机制 | 客户端侧副本 | 改动后需要 | 管理入口 |
| --- | --- | --- | --- | --- |
| ZCode | 缓存与市场镜像 = 指向真源的目录联接(junction)；MCP 条目取**插件根 `.mcp.json`**（stdio 型 + 随包 shim，`${ZCODE_PLUGIN_ROOT}` 解析） | 无 | 重启 ZCode | [`zcode/link-zcode-plugin.ps1`](zcode/link-zcode-plugin.ps1)（链接）；[`zcode/sync-zcode-plugin.ps1`](zcode/sync-zcode-plugin.ps1)（复制，链接态拒绝）；[`zcode/install-zcode-plugin.ps1`](zcode/install-zcode-plugin.ps1)（发布产物 + 校验契约 + 迁移旧 user 级 http 注册） |
| Codex | 插件缓存 = 指向真源的目录联接(junction)，`mcp.json` 以 `${PLUGIN_ROOT}` 直达仓库内唯一的服务入口(随包 shim，由其自举 daemon) | 无 | 重启 Codex（新建任务） | [`codex/link-codex-plugin.ps1`](codex/link-codex-plugin.ps1) |
| DSH | 接入层按 `paths.json` 的 `serverDist` 直接 spawn 服务入口(默认随包 shim，native 直连；`.mjs` 入口才经 node)；godot preset 技能 = 指向真源技能目录的联接 | 无（接入层本体在 `dsh/`） | 重启 DSH 会话 | [`dsh/link-dsh-plugin.ps1`](dsh/link-dsh-plugin.ps1)（维护接入层与 preset 技能联接）；`dsh/deepseek-harness-godot_unified/bin/dsh-godot.mjs status` 体检 |
| VS Code | 项目级 `.vscode/mcp.json` 配 stdio 型本地服务器，`command` 指向随包 shim（由其自举 daemon） | 无（指针式配置） | 重载窗口 | [`vscode/install-vscode-mcp.ps1`](vscode/install-vscode-mcp.ps1)；指南见 [`vscode/README.md`](vscode/README.md) |

## 目录结构

- `zcode/` —— ZCode 链接/复制部署与服务发布：`link-zcode-plugin.ps1`、`sync-zcode-plugin.ps1`、`register-zcode-plugin.py`、`install-zcode-plugin.ps1`（发布 daemon+shim 到 `server-dotnet/publish/<rid>/`、校验插件根 `.mcp.json` 的 stdio 契约、迁移掉会盖住契约的用户级 http 注册）。
- `codex/` —— Codex 链接部署：`link-codex-plugin.ps1`。
- `vscode/` —— VS Code 项目级接入：`install-vscode-mcp.ps1`（生成 `.vscode/mcp.json`）、`mcp.json.example`（手动模板）与 `README.md`（接入指南）。
- `dsh/` —— DSH 接入层与联接脚本；接入层是本仓库普通目录，共用 Git 历史，只有其 skills/ 是指向插件技能真源的 junction。

## 为什么清单槽位不搬进本目录

`.zcode-plugin/` 与 `.codex-plugin/` 仍留在 `plugin/godot-mcp-unified/` 内：它们是插件包自身的加载契约——ZCode 按 `<安装根>/.zcode-plugin/plugin.json` 解析 installPath，并按**插件根 `.mcp.json`**（`${ZCODE_PLUGIN_ROOT}` / `${ZCODE_PROJECT_DIR}` 变量）读 MCP 条目；Codex 按 `${PLUGIN_ROOT}` 相对引用服务入口。它们属于插件负载而非管理层，搬动会破坏客户端的安装路径契约。VS Code 无加载契约（消费的是 Godot 项目级 `.vscode/mcp.json`），其适配器因此整体收纳在本目录 `vscode/`。

## 服务产物的唯一落点

所有客户端都指向**仓库内唯一的一份**服务产物，不在各自目录里维护副本：

- 落点：`plugin/godot-mcp-unified/server-dotnet/publish/<rid>/`（`godot-mcp-daemon[.exe]` + `godot-mcp-shim[.exe]`，均不依赖 .NET runtime；daemon 为自包含单文件、shim 在本机发布时为 NativeAOT，见 [server-dotnet 发布](../plugin/godot-mcp-unified/server-dotnet/README.md#构建验证与发布)）。本地开发下每个 Godot 工程把 addon 目录链接到真源，因此**一次发布即服务所有工程**，无需任何搬运。
- 生产：`dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=<rid>`（daemon 同理）。
- 随包分发（把插件整份复制给别的机器）才需要把产物同步进 addon 的 `bin/<rid>/`：加 `-p:CopyPublishToAddonBin=true` 显式打开（见 `server-dotnet/Directory.Build.targets`）。

## 版本号变更

插件版本提升后：ZCode 重跑 `zcode/link-zcode-plugin.ps1`（缓存目录按版本命名）；Codex 重跑 `codex/link-codex-plugin.ps1 -CacheName <新版本>+codex.link`（或沿用旧缓存目录名——名字只是缓存键，Codex 读取的是联接内的当前清单）；DSH 无需处理（它只认 `paths.json` 指向的 server 文件）。

## 排障

- 链接是否生效：`Get-Item <客户端缓存目录> | Select-Object LinkType, Target` 应显示 `Junction` 与真源路径。
- 所有宿主使用 stdio + 随包 shim；loopback HTTP 是 shim 后端。升级后删除旧的 HTTP 直连条目；token 由 shim 从机器级注册表读取。
- 客户端读的是哪个文件：Codex → `.codex-plugin/mcp.json`；ZCode → 插件根 `.mcp.json`；VS Code → 工程 `.vscode/mcp.json`；DSH → `$DSH_HOME/godot/paths.json` 的 `serverDist`。四者都指向同一份 `server-dotnet/publish/<rid>/godot-mcp-shim[.exe]`。
- ZCode 复制部署在链接态会被 `sync-zcode-plugin.ps1` 主动拒绝——先 `-Unlink` 再同步，防止 robocopy `/MIR` 穿透联接改写真源。
- DSH 技能由 Godot preset 的 dsh-skill-filesystem 行通过包内 skills/ 提供，customSkillDirs 从包 realpath 解析；旧 .agent-presets 目录已废弃。

## 会话连接与验收

先调用 `godot_connect`，再按本会话激活组。配置已启用、MCP 握手成功、真实编辑器调用、当前聊天工具列表更新是不同证据。宿主的重新加载入口与生效时机以实际版本为准；通知与组恢复的当前限制见 [daemon README](../plugin/godot-mcp-unified/server-dotnet/README.md)。二进制变化需要重新发布并重连进程，磁盘联接不等于运行中 daemon 已更新。
