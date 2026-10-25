# 0005: Host entries use the stdio shim

[中文](0005-host-entry-stdio-shim.md) | English

Host entries use the bundled stdio shim. It can bootstrap the machine-level daemon, read the stable token, and provide session keys; loopback HTTP remains its backend. This supersedes the earlier direct-HTTP choice in [0002](0002-daemon-listening-faces.en.md).

The cost is one lightweight process per connection, accepted for consistent startup across hosts. A machine daemon is still shared. Keep host load contracts at the plugin root; adapter scripts manage installations rather than copying source.

Remove old direct-HTTP registrations when upgrading. A running daemon is not automatically replaced by a new shim binary. Native tool-list refresh and restart recovery still need acceptance; see [runtime documentation](../../plugin/godot-mcp-unified/server-dotnet/README.en.md).
