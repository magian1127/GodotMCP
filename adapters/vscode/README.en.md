# VS Code adapter

[中文](README.md) | English

Configure a stdio server in workspace `.vscode/mcp.json` under servers. Point command to the bundled shim; it bootstraps the daemon and reads credentials.

## Install and use

Publish both binaries under `<repo>/plugin/godot-mcp-unified/server-dotnet/publish/<rid>/` and install the addon:

```powershell
& "<仓库>/plugin/godot-mcp-unified/scripts/install-godot-project.ps1" -ProjectPath "<Godot 项目>" -GodotExecutable "<Godot 可执行文件>"
& "<仓库>/adapters/vscode/install-vscode-mcp.ps1" -ProjectPath "<Godot 项目>"
```

The installer backs up existing configuration and preserves other servers. Open the project and follow the installed version’s MCP trust/start flow, reloading the window when needed.

Initially only godot_connect exists. Call godot_connect(project="<absolute Godot project path>") for nine core tools, then activate groups with discover_tools(request=[group names]). Use actual host names and latest schemas.

## Scope and evidence

Configuration is workspace-local; daemon processes are shared per machine state directory. Agent Host may instead read workspace .mcp.json or user ~/.copilot/mcp-config.json; verify the selected host’s entry. Manual template: [mcp.json.example](mcp.json.example).

Remove direct-HTTP entries after upgrading. Server startup or a programmatic probe does not prove native discovery refreshed in the current chat. Use the installed host’s actual reconnect action. See [daemon limits](../../plugin/godot-mcp-unified/server-dotnet/README.en.md); automatic real-host expansion still needs acceptance.
