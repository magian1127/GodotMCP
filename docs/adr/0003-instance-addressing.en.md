# 0003: Instance addressing

[中文](0003-instance-addressing.md) | English

Targeted tools accept canonical project paths or 12-character short IDs. Runtime channels follow their editor instance rather than adding runtime handles. Opening two editors on the same project path remains unsupported.

[0008](0008-per-key-default-target.en.md) inserts key default before the unique-instance fallback; explicit instance remains highest priority. Global read tools are exceptions to targeted-tool parameters. Versions/extensions expose a union, while unsupported target calls fail explicitly. There is no cross-instance batch syntax.

The original rejection of protocol-session use_project did not cover the later application-owned key. Current in-flight pinning and group-replacement limits are documented in [runtime documentation](../../plugin/godot-mcp-unified/server-dotnet/README.en.md).
