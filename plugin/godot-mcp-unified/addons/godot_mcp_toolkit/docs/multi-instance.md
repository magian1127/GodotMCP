# Multiple instances and MCP sessions

## Connect this session

Hosts use the stdio shim, which supplies a session UUID and cwd. The daemon discovers editors through the machine registry. Call godot_connect first: explicit project/instance wins; omission tries this key’s old target, project.godot directly in cwd, then the only active instance. It does not scan recursively.

```text
godot_connect(project="<absolute Godot project path>", groups=["scene_edit"])
list_instances()
scene_get_tree()
discover_tools(request=["code_intel", "editor_ops"], include_schemas=true)
```

Before connecting only the entry exists; afterward nine core tools plus active groups from 17 built-in groups. Use host names: DSH adds mcp__godot__; only the entry has the native godot_ prefix.

Ordinary calls resolve explicit instance, key default, then the only active instance. Keys isolate groups/targets, not permissions. Sessions sharing one shim share target/groups.

## Supported patterns

| Pattern | Behavior |
| --- | --- |
| Separate directories/worktrees | Supported; separate registry key/token/endpoints |
| One editor, multiple game processes | Partial; only the latest registered runtime is discoverable, and debugging tracks the latest game |
| Two editors, same directory | Unsupported; registry keys/token paths and Godot metadata collide |

Use separate worktrees for multiplayer peers. Editors scan 6550–6560, runtime listeners 6570–6585, publishing actual ports. The daemon reads registry endpoints, not GODOT_MCP_PROJECT_PATH.

Copies with the same application/config/name may share file logs. Prefer log_read source="buffer" on Godot 4.5+; older versions may fall back to files. Different application names separate user-data/log directories.

## Ports and LSP

Editor/runtime port variables configure the listening process. The C# daemon discovers published endpoints instead of reading the retired Node server’s port/project overrides. Occupied/invalid pins fail rather than silently scanning; see [advanced configuration](advanced_configuration.md).

LSP is independent of WS, normally 6005; DAP normally uses 6006, so avoid it. Simultaneous editors need different actual LSP listeners and matching published EditorSettings network/language_server/remote_host and remote_port.

The addon currently publishes EditorSettings, not an already-consumed --lsp-port override. The daemon consumes registry fields. A CLI override alone can therefore leave a wrong registered port. Historical GODOT_MCP_LSP_PORT/HOST instructions applied to the retired Node bridge; current C# hints may still mention them but do not read them. Verify actual/published endpoints match; if that cannot be established, validate one editor’s LSP at a time.

Godot 4.5+ provides workspace-root verification for cross-project LSP safety. Older conflict detection is reduced; TCP connectivity is not project identity.

## Recovery and current limits

- Confirm host refresh after connect/group activation. TTL=0 alone is not evidence. Legacy internal notifications and reconnection are implemented; modern host-listen restoration after restart remains incomplete.
- Recheck target/groups after restart: last connect/discover frames do not preserve arbitrary incremental history. A new shim has a new UUID and empty state.
- Unload with discover_tools(reset=[...]) and verify the list. A browse can currently revive old buckets after connect replaces groups.
- include_schemas=true can return only names for resident group tools; use tools/list after activation for full schemas.
- Pass instance for interleaved projects. Sequence same-key target switches and composite calls; the target is not pinned for a whole call yet.
- Missing-header errors require correcting stdio/old HTTP configuration. TARGET_NOT_RUNNING requires opening the reported editor.
- Respect unsafe/read-only gates; inspect state before resubmitting timed-out mutations.

The plugin server-dotnet README owns full runtime limits. [Companion skills](companion-skills.md) cover authoring workflows.
