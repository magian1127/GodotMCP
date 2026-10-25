# Agent and contributor instructions

[中文](AGENTS.md) | English

This directory is the DSH integration inside GodotMCP, not an independent Git repository. Only skills/ is a junction. Bridge/tool facts belong to plugin/godot-mcp-unified/server-dotnet; integration behavior belongs here.

The adapter supplies an entry-configuration CLI, workflow prompt, official-client mount policy/config route, and Godot preset declaration. CLI project input validates a project without binding it. Runtime passes cwd, not a project environment variable; connect/discover manage target/groups per shim. Paths API persists serverDist only. Shared mode shares target/groups too.

## Documentation

- [README](README.en.md): installation, use, uninstall, boundaries.
- [Behavior](docs/behavior.en.md): observable defaults, failures, recovery.
- [Development](docs/development.en.md): module ownership, invariants, tests.
- [Release](docs/release.en.md): release checks and changes.

## Invariants

1. Preserve shared DSH workspace boundaries: do not restart dsh web, create substitute servers, or edit the DSH checkout. Use this CLI or dsh plugin for profile/patch operations.
2. Use placeholders rather than local absolute paths in tracked documentation.
3. Preserve user content outside managed patch blocks. Comment-only remnants become valid [].
4. Do not add official @deepseek-ai runtime modules as dependencies/peerDependencies. Resolve them from the active profile using profile-modules.ts; retain duck-typed interfaces.
5. Use the official MCP client. If unavailable, skip mounting and log a fixed diagnostic; do not implement a replacement.
6. Catch session-creation failures. Warn and skip with failOnStartupError:false; readiness grace is bounded.
7. Bookkeep mounts by session ID, not Agent object reference.
8. Log fixed prose/error.name, not exception messages or configuration.

## Engineering and Git

Edit handwritten sources/documents; lib/, bin/, .tsbuild/ are ignored outputs. Run typecheck, build, test, verify. After deleting/renaming sources, verify output directories stay within this package, recycle them, rebuild, then test. Keep host/CLI constants aligned.

Use --dump-config for composition and isolated host sessions for integration checks; do not disturb running hosts. Workflow prompts remain bilingual with host-native tool naming.

No independent repository/commit boundary exists here. Do not commit/push unless the user explicitly approves. Commit titles start in Chinese; default branch is main.
