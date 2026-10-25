# 0002: Communication faces — stdio shim for hosts, WebSocket for Godot

[中文](0002-daemon-listening-faces.md) | English

## Current decision

All hosts enter through a stdio shim; loopback Streamable HTTP is the shim backend. The shim bootstraps, reads the stable token, and supplies a session key. The addon remains a WebSocket server and the daemon discovers/connects through the machine registry. Editor default scan: 6550–6560; runtime: 6570–6585.

ModelContextProtocol 2.2.0 supports the stateless core and legacy handshake compatibility. Host/shim negotiation differs from the listen envelope, currently 2026-07-28. See the [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.en.md) for implemented forwarding and incomplete notification adaptation.

## Alternatives

- Reverse Godot’s connection direction: rejected because it rewrites transport/authentication and forces contract upgrades.
- Direct host HTTP: saves a process but loses reliable local bootstrap; missing session headers restrict the face to the entry.
- A shim for every host: accepted, trading a small per-session process for consistent startup, keys, and credential reading.

## Revision

The original HTTP choice for Codex/ZCode/VS Code was superseded on 2026-09-23 by [0005](0005-host-entry-stdio-shim.en.md). Remove old direct-HTTP entries. Addon WS direction and public commands stay the same.
