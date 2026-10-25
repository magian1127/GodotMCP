# Local acceptance project

[中文](README.md) | English

test-project validates editor/runtime, C#, LSP, debugger, input. addons/godot_mcp_toolkit is a junction to plugin source; editor_ui_locale_test belongs to this fixture. Backups and Godot caches stay ignored.

| Fixture | Purpose |
| --- | --- |
| Main.tscn, runtime_counter.gd | Scene operations and precise frame counting |
| Acceptance.csproj, tests/McpValCsNode.cs | C# classes, properties, signals |
| Validations/fixtures/env_probe.gd | LSP, references, breakpoints |
| scripts/test_framework/check_all_scripts.gd | Script load checks |
| test/fixtures/send_text_smoke.tscn | Input, secret masking, submit |
| tests/*_test.gd | Time, locale, theme, local connection/config discovery |

## Build and start

From the repository root, using local .env or an explicit executable:

```powershell
Get-Item -Force ./test-project/addons/godot_mcp_toolkit | Select-Object FullName, LinkType, Target
dotnet build ./test-project/Acceptance.csproj
$fixturePath = (Resolve-Path ./test-project).Path
& "<Godot 可执行文件>" --headless --editor --path $fixturePath
```

Validated target: Godot 4.7.2 stable .NET/mono; verify --headless --version. Use a display for screenshots. Do not select targets through GODOT_MCP_PROJECT_PATH: shim supplies cwd and godot_connect(project) selects the session target.

## MCP evidence

1. Build current daemon/shim; old publish/cache binaries are not current-source evidence.
2. Handshake through stdio, then list: only godot_connect initially.
3. Connect this project, list core 9, or 12 with editor_ops. Verify real project identity through list_instances and scene_get_tree.
4. Activate code_intel for checks/LSP, playtest for runtime, scene_edit for authoring.
5. Validate native host refresh separately; a client explicitly requesting tools/list does not prove notifications worked.

Multiple editors use separate directories/WS ports but share one daemon. Run process regressions serially while the production service is stopped; changing port/state does not permit a second daemon. STATE_DIR is also daemon registry input; isolated editors must publish to the matching registry. WS pins configure listeners; actual LSP and registry endpoints must agree. See [multi-instance](../plugin/godot-mcp-unified/addons/godot_mcp_toolkit/docs/multi-instance.md).

## Regression and boundaries

From plugin/godot-mcp-unified/server-dotnet:

```powershell
dotnet test tests/godot-mcp-daemon.tests
dotnet test tests/godot-mcp-daemon.tests --filter "FullyQualifiedName~AcceptanceHarnessTests"
```

Run Godot SceneTree scripts in this project with --headless --path . -s res://tests/<name>.gd. Config discovery uses scripts/test-mcp-config-discovery.ps1 -IncludeUi -GodotExecutable "<Godot executable>"; retain its isolation guards. Retired server/ npm scripts are not current test entries.

Read tokens only into test processes, keeping them out of commands/logs/Git. Clean created fixtures/runtime state, preserve original failure evidence. See [daemon limits](../plugin/godot-mcp-unified/server-dotnet/README.en.md).
