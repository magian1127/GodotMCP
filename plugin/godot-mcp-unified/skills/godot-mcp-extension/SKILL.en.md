---
name: godot-mcp-extension
description: Add or update project-specific Godot MCP tools using the Godot MCP Unified extension API. Use when the user asks for a new MCP tool, a custom project operation, an extension pack, or behavior the built-in 83-tool surface cannot express cleanly.
---

Language: English | [中文](SKILL.md)

# Godot MCP Extension

Before editing, read ../../addons/godot_mcp_toolkit/docs/extending.md, resolved relative to this skill.

Prefer a project extension over changing the toolkit core:

1. Confirm no built-in or on-demand tool already provides the operation.
2. Put GDScript extensions under the target project's own addons/<extension_name>/, outside addons/godot_mcp_toolkit/, so toolkit updates cannot erase them.
3. Use @tool, a unique class_name extending MCPToolkitExtension, and register through MCPToolkitExtensionOptions.
4. Declare every accepted parameter in the JSON schema. Mark read-only/idempotent/destructive behavior truthfully; unannotated extensions are treated as mutating.
5. Guard every LLM-supplied res:// or user:// path, wrap external/project content as untrusted, paginate bounded reads, and cap long operations.
6. Use UndoRedo or MCPToolkitSafeSceneOps for editor mutations and scene saves. Keep runtime-shipped dependencies free of editor-only types.
7. Add direct success, boundary, invalid-input, and security tests. Refresh extensions, inspect the published schema, invoke the tool through MCP, and verify its observable effect.

Modify the core protocol only when an extension cannot reach the required editor/runtime lifecycle, and then update both server and addon contracts together.
