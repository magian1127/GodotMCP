# Godot MCP Unified documentation

[中文](README.md) | English

Chinese .md files are the maintained primary documents, with .en.md counterparts. Upstream English .md references keep .zh-CN.md translations. Keep one detailed home for each fact and link to it.

## Entry points

- [Workspace](../README.en.md): scope and layout.
- [Contributors](../AGENTS.md): repository rules and commands.
- [Adapters](../adapters/README.en.md): host entry points.
- [Plugin](../plugin/godot-mcp-unified/README.en.md): installation and use.
- [Daemon](../plugin/godot-mcp-unified/server-dotnet/README.en.md): session surfaces, counts, default targets, timeouts, notifications, current limits.
- [Acceptance project](../test-project/README.en.md): Godot fixtures and tests.
- [Toolkit](../plugin/godot-mcp-unified/addons/godot_mcp_toolkit/README.md): addon UI and engineering.
- [Architecture decisions](adr/): 0001–0008, including keys, tool surfaces, default targets. Decisions do not replace runtime evidence; historical counts/env settings are not current setup instructions.

## Six skills

- [godot-control](../plugin/godot-mcp-unified/skills/godot-control/SKILL.en.md)
- [godot-debug](../plugin/godot-mcp-unified/skills/godot-debug/SKILL.en.md)
- [godot-playtest](../plugin/godot-mcp-unified/skills/godot-playtest/SKILL.en.md)
- [godot-project-setup](../plugin/godot-mcp-unified/skills/godot-project-setup/SKILL.en.md)
- [godot-mcp-extension](../plugin/godot-mcp-unified/skills/godot-mcp-extension/SKILL.en.md)
- [godot-ui](../plugin/godot-mcp-unified/skills/godot-ui/SKILL.en.md)

Start with the workspace/plugin README, then the relevant adapter. Use runtime docs and ADRs for implementation context, and the contributor command table for checks.
