# AGENTS.md —— 代理与贡献者须知

本文件面向在本仓库工作的 AI 代理与贡献者：仓库布局细节、构建与测试命令、文档与脚本归属规则、发布前检查清单。项目介绍见 [README.md](README.md)；文档导航见 [docs/README.md](docs/README.md)。

## 仓库布局（细节）

- `plugin/godot-mcp-unified/` —— 唯一插件真源：
  - `addons/godot_mcp_toolkit/` —— 编辑器/运行时 addon（GDScript：`transport/`、`commands/`、`ui/`、`extensions/`、`registry/`、`docs/`、`CompanionSkills/`）；
  - `server-dotnet/` —— C# 单例 daemon（`godot-mcp-daemon`）与 stdio shim（`godot-mcp-shim`）及测试；
  - `skills/` —— 六个配套技能（godot-control/debug/extension/playtest/project-setup/ui）；
  - `templates/` —— 项目模板（empty/default/2d-platformer/3d-fps）；
  - `.codex-plugin/`、`.zcode-plugin/` —— 客户端加载契约目录，必须留在插件根（客户端按固定相对路径读取）；
  - `scripts/` —— 面向用户的安装器与负载体检（见下"脚本归属"）。
- `adapters/` —— 各客户端接入管理层；其中 `dsh/deepseek-harness-godot_unified` 是独立 git 仓库（Windows junction 指入本仓库），有自己的 AGENTS.md 与铁律，构建产物（`lib/`、`bin/`、`.tsbuild/`）不入库。
- `test-project/` —— 验收项目：`tests/*.gd` 为 Godot 回归脚本（SceneTree 脚本，headless 运行）；`addons/` 为指向真源的 junction。
- `docs/` —— 维护文档、ADR、客户端安装指南；`docs/agents/` 定义 issue tracker / triage / domain docs 约定。
- `scripts/` —— 仓库级开发/验证工具。
- `.env.example` / `.env` —— 机器相关路径模板与本机值；`.env` 被 git 忽略。

构建产物（`lib/`、`bin/`、`.tsbuild/`、`obj/`、`.godot/`、`.vs/` 等）一律 git 忽略，只改手写源码。

## 常用命令

| 目的 | 命令 | 位置 |
| --- | --- | --- |
| daemon 单元测试（全量） | `dotnet test tests/godot-mcp-daemon.tests` | `plugin/godot-mcp-unified/server-dotnet/` |
| 配置发现/写入边界回归 | `pwsh scripts/test-mcp-config-discovery.ps1 [-IncludeUi]` | 仓库根 |
| GDScript 回归（本地连接策略等） | `godot --headless --path . -s res://tests/<name>.gd` | `test-project/` |
| addon 全脚本加载校验 | `check_all_scripts.gd`（见 `test-project/scripts/test_framework/`） | `test-project/` |
| 文档一致性验证 / 自测 | `node scripts/verify-doc-localization.mjs`；`node scripts/test-doc-localization.mjs` | 仓库根 |
| 插件负载体检（本地安全门禁） | `pwsh plugin/godot-mcp-unified/scripts/verify-local-only.ps1` | 插件根 |
| DSH 接入层 typecheck/build/test/verify | `npm run typecheck` / `build` / `test` / `verify` | `adapters/dsh/deepseek-harness-godot_unified/` |

需要 Godot 可执行文件的脚本一律支持仓库根 `.env` 的 `GODOT_EXECUTABLE` 回退（见 `.env.example`）。

## 文档与脚本归属规则

- **三层脚本目录判据**见 [`scripts/README.md`](scripts/README.md)：仓库级工具进根 `scripts/`；随插件分发的安装器进 `plugin/.../scripts/`；操作客户端私有状态的进 `adapters/<client>/`。
- **文档单一出处 + 索引**：同一事实只在一处维护，其他位置链接过去；中英成对（`.md` 主文档 + `.en.md` 对照；上游生成文档为英文原文 + `.zh-CN.md` 译本）。
- **禁止本机绝对路径**：文档、脚本、示例一律使用占位符（`<仓库>`、`<Godot 项目>`、`<Godot 可执行文件>`）；机器相关值进 `.env`（凭据绝不入库，token 只存机器级注册表）。

## 发布前检查清单

1. 全仓扫描本机绝对路径（`C:\Users`、`C:\Soft`、`D:\Projects` 等）为零（git 忽略的构建产物除外）。
2. `git check-ignore .env` 生效；`.env.example` 只含占位符。
3. 上述命令表中的测试与体检全绿。
4. 文档无死链：被移动/删除文档的入链全部清理（可用全文搜索文档名确认）。

## Git 纪律

- 不主动 `git commit` / `git push`；用户明确批准后执行，提交标题以中文开头。
- 默认分支 `main`。

## Agent skills

### Issue tracker

Issues 以 local markdown 方式跟踪：spec 与 implementation issues 存放于本仓库 `.scratch/<feature-slug>/` 下。**`.scratch/` 为本地工作记录，已被 git 忽略、不入版本管理与发布物**。See `docs/agents/issue-tracker.md`.

### Triage labels

保留五个默认 triage labels（label string 与 role name 相同：`needs-triage`、`needs-info`、`ready-for-agent`、`ready-for-human`、`wontfix`）。See `docs/agents/triage-labels.md`.

### Domain docs

Single-context 布局：repo 根目录一个 `CONTEXT.md` + `docs/adr/`（由 `/domain-modeling` 懒创建，缺失时静默继续）。See `docs/agents/domain.md`.
