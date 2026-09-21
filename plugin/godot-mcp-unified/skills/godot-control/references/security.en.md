Language: English | [中文](security.md)

# Security boundary

The bridge controls a local editor with the same filesystem authority as that editor.

- Keep network listeners on 127.0.0.1 and retain per-session token authentication.
- Keep file operations inside res:// or the explicitly whitelisted user:// paths. Treat absolute paths, traversal, UNC paths, and addon self-modification as refusals.
- Use read-only inspection before mutation. Do not disable the server-side path guard, audit log, response caps, or untrusted-content envelopes.
- Treat execute_code, arbitrary node method calls, recursive deletion, external URL access, and project/plugin configuration as high-risk. execute_code and node_call_method appear in the unsafe group only when the server starts with GODOT_MCP_UNSAFE=1; enable it only for the currently authorized operation. Use a typed alternative when one exists and keep the affected scope explicit.
- Preserve the running project's own pause state when using time control, and thaw in cleanup.
- Do not expose the runtime autoload in exported games. The addon strips itself only when enabled during export; verify export settings for production builds.
