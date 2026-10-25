# 0004: Daemon lifecycle, singleton, local security

[中文](0004-daemon-lifecycle-and-local-security.md) | English

## Decision

The daemon listens on loopback 6590 by default and uses a machine-wide named mutex plus a state-directory file lock. The addon or shim may probe/bootstrap it. It stays resident unless GODOT_MCP_DAEMON_IDLE_SECONDS is positive. Exit requires no requests/listen streams, no connected Godot instances, and elapsed idle threshold.

A stable Bearer token is generated in the machine registry and survives restarts. Hosts now use stdio shims that read it; do not distribute it through host Authorization configuration or user environment variables. Historical installer/bridge compatibility code remains, so removal of every old path is not claimed.

Missing/invalid keys get only godot_connect without orphan creation; valid listen may create a key. Keys isolate state rather than authorize access. Tokens do not defend against malicious processes with the user’s file access.

## Trade-offs

- Idle exit assumed periodic host requests; idle hosts may send none, so it became opt-in on 2026-09-23.
- A stable daemon token avoids restart-induced configuration failure. Per-project editor tokens are separate, published by the addon and read by the daemon.
- Debug/eager switches serve diagnostics/migration, not authentication. See the [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.en.md) for gaps.

## Session revision

A live listen request prevents idle exit. Dormant key retention and process idleness are separate. Legacy shims now maintain internal listen; other protocol paths depend on their actual subscriptions, so shim process existence alone is not the idle criterion. Grace/TTL only preserve state while the daemon lives; restarting clears memory keys.

Publishing uses a self-contained daemon and native-toolchain AOT shim when available. Runtime docs own state/lock/token and isolated-test details.


## Machine-wide singleton revision

The singleton is no longer scoped by state directory. MachineSingleton uses one named mutex across copies/ports/state and checks older legacy executables first. A second instance exits with code 2. Tests run serially and clean up bootstrapped children; development does not bypass the mutex.
