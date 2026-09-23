# 写入项目级 .vscode/mcp.json：把 Godot MCP Unified 配成 stdio 型本地服务器，
# command 指向仓库内唯一的服务入口（随包 shim：
# plugin/godot-mcp-unified/server-dotnet/publish/<rid>/godot-mcp-shim[.exe]）。
# host 直接 spawn 该 shim，由 shim 确保机器级单例 daemon 在跑（同目录的
# godot-mcp-daemon[.exe]）再把 stdio 转发到 daemon 的 HTTP 面——因此不需要
# Bearer token，也不要求 daemon 先被别处拉起。
#
# 键名与形态按 VS Code 官方 schema：顶层 `servers`（不是 `mcpServers`），
# 本地服务器用 `type: "stdio"` + `command`/`args`。
# 可重复执行：已有的 .vscode/mcp.json 会先备份为 .bak，并**只替换 `servers.godot`
# 条目**（同文件里的其它 MCP 服务器与其它顶层键原样保留）。
# 归属：本脚本属客户端接入层(adapters)；Godot 项目内的 toolkit addon 用
# plugin\godot-mcp-unified\scripts\install-godot-project.ps1 安装。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$pluginDir = Join-Path $repoRoot "plugin\godot-mcp-unified"

# 当前平台的 .NET RID（与 addon 侧 paths/platform_rid.gd、发布档同名同义）。
function Get-CurrentRid {
    if ($IsWindows) { return "win-x64" }
    if ($IsMacOS) { return $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "osx-arm64" } else { "osx-x64" }) }
    return "linux-x64"
}
$rid = Get-CurrentRid
$shimPath = Join-Path $pluginDir ("server-dotnet\publish\{0}\godot-mcp-shim{1}" -f $rid, $(if ($IsWindows) { ".exe" } else { "" }))

$projectRoot = [IO.Path]::GetFullPath($ProjectPath)
$vscodeDir = Join-Path $projectRoot ".vscode"
$mcpJsonPath = Join-Path $vscodeDir "mcp.json"


function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}


if (-not (Test-Path $projectRoot)) {
    throw "Project directory does not exist: $projectRoot"
}

# 服务入口必须先在位：它由 daemon 侧发布产出。缺了 host 起不来；但**不需要**先启动
# daemon——shim 会自举（这也是 stdio 形态相对 url 形态的主要差别）。
if (-not (Test-Path -LiteralPath $shimPath)) {
    throw ("服务入口缺失：$shimPath`n先发布该平台产物：`n" +
        "  dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=$rid")
}

if (-not (Test-Path (Join-Path $projectRoot "project.godot"))) {
    Write-Warning "No project.godot in '$projectRoot'. Make sure the Godot project exists and the toolkit addon is installed (plugin\godot-mcp-unified\scripts\install-godot-project.ps1)."
}

if (-not (Test-Path $vscodeDir)) {
    New-Item -ItemType Directory -Path $vscodeDir -Force | Out-Null
}

$mcpJsonHadFile = Test-Path $mcpJsonPath
if ($mcpJsonHadFile) {
    Copy-Item $mcpJsonPath ($mcpJsonPath + ".bak") -Force
    Write-Host "Backed up existing $mcpJsonPath to $mcpJsonPath.bak"
    Write-Host "Existing servers are preserved; only the 'godot' entry is replaced."
}

# stdio 型条目：command 为 shim 的绝对路径（shim 位于仓库而非工程内，故不能写
# ${workspaceFolder} 相对形式）；args 为空——shim 的参数全部来自 env。
$entry = [ordered]@{
    type    = "stdio"
    command = $shimPath
    args    = @()
}

# 合并写：只替换 `servers.godot`，保留同一文件里的其它服务器条目（例如别的 MCP 服务器）
# 与其它顶层键。文件损坏时退回整份重写（已备份为 .bak，可手工恢复）。
$config = [ordered]@{}
if ($mcpJsonHadFile) {
    try {
        $parsed = Get-Content -LiteralPath ($mcpJsonPath + ".bak") -Raw | ConvertFrom-Json -AsHashtable
        if ($parsed -is [System.Collections.IDictionary]) { $config = $parsed }
    } catch {
        Write-Warning "现有 mcp.json 无法解析，将整份重写（备份在 $mcpJsonPath.bak）"
    }
}
if (-not $config.Contains("servers") -or -not ($config["servers"] -is [System.Collections.IDictionary])) {
    $config["servers"] = [ordered]@{}
}
$config["servers"]["godot"] = $entry

$json = $config | ConvertTo-Json -Depth 8
Write-Utf8NoBom -Path $mcpJsonPath -Content ($json + "`n")

Write-Host ""
Write-Host "Wrote $mcpJsonPath"
Write-Host "Server entry (stdio): $shimPath"
Write-Host "  由该 shim 自举机器级 daemon（127.0.0.1:6590），无需 Bearer token。"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Open '$projectRoot' in VS Code and trust the workspace."
Write-Host "  2. Reload the window (Ctrl+Shift+P -> Developer: Reload Window)."
Write-Host "  3. The godot server (20 startup tools) appears in the MCP panel."
