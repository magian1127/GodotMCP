# 仓库脚本（scripts/）

本目录是**仓库级开发/验证工具**：它们校验全仓不变量或组装跨目录回归夹具，只在仓库根有意义，不随插件负载分发。脚本类目录的分工如下：

| 位置 | 归属 | 内容 | 判据 |
| --- | --- | --- | --- |
| `scripts/`（本目录） | 仓库开发/CI 工具 | 文档一致性验证、跨目录回归 harness | 校验对象是**整个仓库**；不进插件负载 |
| `plugin/godot-mcp-unified/scripts/` | 插件负载自带脚本 | 面向最终用户的安装器与负载体检 | 随插件真源分发，被 skills 与用户直接调用 |
| `adapters/` | 客户端接入管理层 | 针对各客户端缓存/注册状态/项目指针的安装与维护脚本 | 操作对象是**客户端私有状态**（junction、用户级配置、项目级 `.vscode/mcp.json`） |

## 本目录脚本

- `verify-doc-localization.mjs` —— 全仓文档一致性验证：中英文档成对、结构同步、本地化边界（上游生成文档保留英文原文 + 中文译本的规则见 [`docs/README.md`](../docs/README.md)）。
- `test-doc-localization.mjs` —— 上一脚本的单元回归：在系统临时目录构造夹具验证验证器自身（正例/反例）。
- `test-mcp-config-discovery.ps1` —— MCP 配置发现与写入边界回归：复制插件 `mcp_json_sync/discovery` 与模板到隔离夹具工程后以 Godot headless 执行，不触碰真实项目。需 `-GodotExecutable`（必填）；`-IncludeUi` 追加工具坞/向导用例。

## 约定

- 新增脚本前先判断归属：若它操作客户端私有状态，放 `adapters/<client>/`；若它随插件分发供用户安装/体检，放 `plugin/godot-mcp-unified/scripts/`；只有仓库级校验与 harness 才进本目录。
- 脚本不得写入本机绝对路径（机器相关值一律参数化，示例用占位符）；临时产物进仓库根 `temp/`。
