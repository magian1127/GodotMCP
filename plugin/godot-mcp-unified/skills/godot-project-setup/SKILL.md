---
name: godot-project-setup
description: 为 Godot 4 项目安装、更新或重新连接 Godot MCP Unified；验证 Godot 可执行文件，复制并启用编辑器插件，构建本地 MCP 桥接，或根据随附模板创建项目。适用于设置、部署、工具缺失、插件缺失、连接/认证/端口失败或新建 Godot 项目。
---

语言：中文 | [英文版](SKILL.en.md)

# Godot 项目设置

使用相对于本技能目录解析的随附安装程序 `../../scripts/install-godot-project.ps1`。

1. 解析确切的目标目录，并确认其中包含 `project.godot`，除非用户要求根据随附模板新建项目。
2. 优先使用明确指定的 Godot 可执行文件。本软件包验证过的目标是 Godot 4.7.2 stable（.NET/mono 版）；应验证 `--headless --version`，不要相信文件名。本机路径写在仓库根 `.env` 的 `GODOT_EXECUTABLE`（模板见 `.env.example`），不要写进文档。
3. 使用 `-ProjectPath`、`-GodotExecutable` 和可选的 `-Template` 运行安装程序。它会构建桥接，在替换前备份现有插件，复制 `addons/godot_mcp_toolkit`，启用插件入口，并执行无头编辑器加载。
4. 确认编辑器输出包含 `[MCPServer] listening`，机器注册表指向同一个规范项目路径，且桥接可以完成认证。仅有端口不能证明身份。
5. 运行一个只读 MCP 探针，例如 `scene_get_tree` 或 `project_get_settings`。

不要安装到发现的每一个 Godot 项目中。只操作用户纳入范围的项目或项目集合。没有安装程序生成的带时间戳备份时，不要覆盖现有插件。
