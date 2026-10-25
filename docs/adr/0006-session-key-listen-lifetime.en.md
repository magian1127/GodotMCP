# 0006: Session keys and listen lifetime

[中文](0006-session-key-listen-lifetime.md) | English

## Decision

Stateless MCP does not own application group/default-target state. Each shim generates a UUID and sends x-godot-session on every request. The daemon stores connected flag, groups, target per key. Keys are neither project paths nor authorization boundaries.

Active means at least one stream. Last-stream close enters Dormant; completing a streamless ordinary request enters IdleNoStream. Valid connect/listen may create keys; creation plus stream registration is atomic. Streams and in-flight requests prevent recycling.

## Current policy

Source adds Connected and ConnectedTtl: unconnected Dormant grace 30s, unconnected IdleNoStream TTL 10min, connected streamless TTL 24h, capacity 1024. Connected keys are not capacity-evicted. Three duration values allow test environment overrides; SessionKeyPolicy is authoritative.

This revises the original universal 30s/10min, environment-free policy. The original legacy implementation lacked listen (internal legacy listen is now implemented); a short TTL would recycle useful connections. Connected does not mean permanently Active; ConnectedTtl still reclaims it.

## Trade-offs/invariants

- Do not depend on protocol session IDs or bind keys to projects.
- Close-only cleanup leaks streamless keys; use an independent reaper.
- Use monotonic time.
- Ordinary requests include ping; do not restrict renewal to a tool-method whitelist.
- Listen close is not ordinary request completion and must preserve grace.
- Registry owns streams, in-flight count, capacity; exhaustion returns SESSION_LIMIT without displacing live keys.

Notifications fan out to all same-key streams with each stream’s ID. Legacy internal pumping/reconnection are implemented; modern host re-subscription remains incomplete, and last-frame replay is not full state restoration. See [runtime limits and diagnostics](../../plugin/godot-mcp-unified/server-dotnet/README.en.md).
