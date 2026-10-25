# 0001: Singleton C# daemon

[中文](0001-singleton-csharp-daemon.md) | English

The machine-level C# daemon replaces per-session Node bridges and multiplexes hosts and Godot instances. Godot retains mutation serialization and scene leases. The historical 83-tool/31-group baseline describes the original migration, not today’s catalog. Current state is owned by [runtime documentation](../../plugin/godot-mcp-unified/server-dotnet/README.en.md) and decisions 0006–0008.

Rejected alternatives include a separate bridge per host/session and redesigning Godot-side concurrency. The daemon centralizes transport/discovery while preserving the addon contract. See the Chinese decision for the original rationale and migration history.
