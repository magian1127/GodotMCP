# 0007: Per-key tool surfaces

[中文](0007-tool-face-per-session.md) | English

## Decision

Before connection expose only godot_connect; afterward expose core 9 union this key’s active groups. There are 17 built-in groups; the [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.en.md) and GroupCatalogue own counts/membership, with extensions additional. Overlapping group members require recomputing a union.

Each request gets its collection. Register the 34 C# objects normally, filter by visibility, and reuse lazy objects across requests. Calls/lists share visibility; CallToolHandler returns explanatory isError on misses. Do not rewrite shared descriptions/schemas per session; uniform idempotent protocol-schema replacement is allowed.

## Alternatives

- Wrap all resident implementations as deferred eager: loses LSP, build waits, and composite routing.
- Filter only list: hidden calls can still hit registered objects.
- Reimplement visibility in call filters: duplicates the decision.
- Remove low-frequency capabilities: use lazy groups instead.

EagerRegisteredToolNames now has 25 names for avoiding shadow implementations, not permanent visibility. Resident schema lookup still has a gap.

## Cache, switches, delivery status

List the entry first and remaining names in protocol-table order. ttlMs=0/cacheScope=private express freshness/privacy, not proven host refresh.

The designed debug face is stateless/all-tools for missing headers; eager restores global groups while retaining version/unsafe gates. Current debug has 34 tools and incorrect invalid-header/priority handling; eager can remain per-key. They have not passed the design matrix. Notification pumping/re-subscription, group replacement, and incremental replay also have reproduced gaps. Runtime limits belong in the daemon README; design tables are not current-behavior evidence.
