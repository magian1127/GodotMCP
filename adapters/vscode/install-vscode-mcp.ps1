# 写入项目级 .vscode/mcp.json，把 Godot MCP Unified 的控制面指向机器级单例
# godot-mcp-daemon 的 loopback HTTP 面。VS Code 从项目的 .vscode/mcp.json 加载
# 上下文协议(MCP)服务器；不存在插件市场(marketplace)或插件打包步骤。
# 可重复执行：已有的 .vscode/mcp.json 会先备份再覆盖。
# 归属：本脚本属客户端接入层(adapters)，自包含——token 从机器级注册表读取，
# 不依赖插件根路径；Godot 项目内的 toolkit addon 用
# plugin\godot-mcp-unified\scripts\install-godot-project.ps1 安装。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [switch]$AutoSetup
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath($ProjectPath)
$vscodeDir = Join-Path $projectRoot ".vscode"
$mcpJsonPath = Join-Path $vscodeDir "mcp.json"
$registryDir = Join-Path $env:APPDATA "godot-mcp-toolkit"
$tokenFile = Join-Path $registryDir "daemon-token"
$daemonPort = 6590


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

# Bearer token：godot-mcp-daemon 首次运行时生成于机器级注册表目录。
if (-not (Test-Path $tokenFile)) {
    throw "daemon token missing: $tokenFile . Start godot-mcp-daemon once (or run adapters\zcode\install-http-face.ps1) so it can generate the token."
}
$daemonToken = (Get-Content -LiteralPath $tokenFile -Raw).Trim()

if (-not (Test-Path (Join-Path $projectRoot "project.godot"))) {
    Write-Warning "No project.godot in '$projectRoot'. Make sure the Godot project exists and the toolkit addon is installed (plugin\godot-mcp-unified\scripts\install-godot-project.ps1)."
}

if (-not (Test-Path $vscodeDir)) {
    New-Item -ItemType Directory -Path $vscodeDir -Force | Out-Null
}

if (Test-Path $mcpJsonPath) {
    Copy-Item $mcpJsonPath ($mcpJsonPath + ".bak") -Force
    Write-Host "Backed up existing $mcpJsonPath to $mcpJsonPath.bak"
}

# http 型指向机器级单例 daemon 的 loopback HTTP 面（token = 机器级稳定 token）。
$mcpConfig = @{
    mcpServers = @{
        "godot" = @{
            type      = "http"
            url       = "http://127.0.0.1:$daemonPort/"
            headers   = @{
                Authorization = "Bearer $daemonToken"
            }
            timeoutMs = 120000
        }
    }
}
$json = $mcpConfig | ConvertTo-Json -Depth 6
Write-Utf8NoBom -Path $mcpJsonPath -Content ($json + "`n")

Write-Host ""
Write-Host "Wrote $mcpJsonPath"
Write-Host "Daemon face: http://127.0.0.1:$daemonPort/"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Open '$projectRoot' in VS Code and trust the workspace."
Write-Host "  2. Reload the window (Ctrl+Shift+P -> Developer: Reload Window)."
Write-Host "  3. The godot server (20 startup tools) appears in the MCP panel."
