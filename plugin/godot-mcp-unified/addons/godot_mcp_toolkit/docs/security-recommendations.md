# Security recommendations

## Boundaries in the current implementation

Hosts use stdio shims; loopback HTTP is the authenticated backend. The shim reads the stable daemon token, and the daemon reads editor tokens. Keep credentials in the machine registry. Session keys isolate target/group state, not permissions; default groups and hidden tools are not a substitute for authorization.

Bridge listeners are local. Whether tool input/output is sent to a model service depends on the host’s data policy.

## Tool capabilities

| Tools | Capability |
| --- | --- |
| execute_code, node_call_method | Arbitrary code/method execution; unsafe daemon opt-in required |
| project_delete | Project files/scenes/folders deletion; inspect dry_run plans |
| save_write, save_delete | Persistent user:// writes/deletion |
| user_data_read | User-data files and directory reads |
| Other authoring tools | Scene/resource/settings mutations, with annotations describing their behavior |

readOnlyHint, destructiveHint, and idempotentHint describe operations for host policy; they are not authentication. Some mutations support Undo, but verify the actual result rather than assuming rollback.

## Read-only configuration

The current C# daemon **does not implement the retired Node bridge’s GODOT_MCP_READ_ONLY environment filter**. The addon UI still has compatibility configuration/status for that key; this is not evidence that mutations are blocked. Do not present the dock toggle or that env value as an enforced read-only boundary.

Use the host’s actual tool allowlist/deny/approval policy. In Codex, a standalone MCP server can be configured in a trusted project’s .codex/config.toml or user config:

```toml
[mcp_servers.godot]
command = "<plugin root>/server-dotnet/publish/<rid>/godot-mcp-shim.exe"
disabled_tools = ["execute_code", "node_call_method"]
```

This example blocks only those two tools, not all writes. Use an explicit enabled_tools allowlist for a restricted workflow, remembering the godot_connect entry and the desired read tools. Installed plugin servers use plugin-specific MCP policy rather than replacing their transport command. See the installed Codex MCP configuration reference; use codex mcp --help for CLI commands.

Other hosts have different configuration and naming. Use the installed version’s filtering/permission controls and verify actual tool visibility/call rejection; an Agents SDK Python tool_filter is not Codex desktop configuration.

## Operational guidance

- Connect first and activate only task groups. Missing tools can mean unconnected state, unloaded groups, version gates, or stale discovery.
- Keep unsafe disabled unless a currently authorized operation requires it.
- Retain path guards, audit logs, response caps, and untrusted-content wrappers. These are server-side checks independent of host discovery.
- Inspect state after timeout/transport loss before resubmitting mutations.
- Scope filesystem/model access using host permissions; an MCP tool restriction does not remove native filesystem access.
- See [session recovery](multi-instance.md) for current notification/group limits.
