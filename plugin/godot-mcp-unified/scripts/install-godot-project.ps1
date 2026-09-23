[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    # 本机 Godot 可执行文件：显式参数 > 仓库根 .env 的 GODOT_EXECUTABLE（见 .env.example）。
    [string]$GodotExecutable,

    [ValidateSet("none", "empty", "default", "2d-platformer", "3d-fps")]
    [string]$Template = "none",

    [switch]$SkipEditorValidation,
    [switch]$NoProjectMcpConfig
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectRoot = [IO.Path]::GetFullPath($ProjectPath)
$projectFile = Join-Path $projectRoot "project.godot"
$addonSource = Join-Path $pluginRoot "addons\godot_mcp_toolkit"
$addonDestination = Join-Path $projectRoot "addons\godot_mcp_toolkit"
$pluginConfigPath = "res://addons/godot_mcp_toolkit/plugin.cfg"

# 服务入口（本机唯一落点：插件根的 server-dotnet/publish/<rid>/，由发布产出）。
# 项目 .mcp.json 的 stdio 条目就指向它——与插件面板写入的形态同源。
function Get-CurrentRid {
    if ($IsWindows) { return "win-x64" }
    if ($IsMacOS) { return $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "osx-arm64" } else { "osx-x64" }) }
    return "linux-x64"
}
$rid = Get-CurrentRid
$shimExe = Join-Path $pluginRoot ("server-dotnet\publish\{0}\godot-mcp-shim{1}" -f $rid, $(if ($IsWindows) { ".exe" } else { "" }))

# 机器相关值回退：显式参数缺省时读仓库根 .env（模板见 .env.example；该文件不入版本管理）。
if (-not $GodotExecutable) {
    $envFile = Join-Path (Split-Path -Parent (Split-Path -Parent $pluginRoot)) ".env"
    if (Test-Path -LiteralPath $envFile) {
        $match = Select-String -LiteralPath $envFile -Pattern '^\s*GODOT_EXECUTABLE\s*=' | Select-Object -First 1
        if ($match) { $GodotExecutable = ($match.Line -split '=', 2)[1].Trim().Trim('"').Trim("'") }
    }
}
if (-not $GodotExecutable) {
    throw "Godot executable not specified. Pass -GodotExecutable or set GODOT_EXECUTABLE in the repository root .env (see .env.example)."
}


function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parent,
        [Parameter(Mandatory = $true)]
        [string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd("\") + "\"
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved path escaped its parent: $childFull"
    }
}


function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}


function Enable-EditorPlugin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$PluginPath
    )

    $content = [IO.File]::ReadAllText($Path)
    if ($content.Contains('"' + $PluginPath + '"')) {
        return $false
    }

    $sectionPattern = '(?ms)^\[editor_plugins\]\s*(.*?)(?=^\[|\z)'
    $sectionMatch = [regex]::Match($content, $sectionPattern)
    if (-not $sectionMatch.Success) {
        $trimmed = $content.TrimEnd()
        $updated = $trimmed + [Environment]::NewLine + [Environment]::NewLine +
            "[editor_plugins]" + [Environment]::NewLine + [Environment]::NewLine +
            'enabled=PackedStringArray("' + $PluginPath + '")' + [Environment]::NewLine
        Write-Utf8NoBom -Path $Path -Content $updated
        return $true
    }

    $section = $sectionMatch.Value
    $enabledPattern = '(?m)^enabled\s*=\s*PackedStringArray\((.*?)\)\s*$'
    $enabledMatch = [regex]::Match($section, $enabledPattern)
    if ($enabledMatch.Success) {
        $existing = $enabledMatch.Groups[1].Value.Trim()
        $replacement = if ($existing.Length -eq 0) {
            'enabled=PackedStringArray("' + $PluginPath + '")'
        } else {
            'enabled=PackedStringArray(' + $existing + ', "' + $PluginPath + '")'
        }
        $updatedSection = $section.Remove($enabledMatch.Index, $enabledMatch.Length).Insert(
            $enabledMatch.Index,
            $replacement
        )
    } else {
        $updatedSection = $section.TrimEnd() + [Environment]::NewLine +
            'enabled=PackedStringArray("' + $PluginPath + '")' + [Environment]::NewLine +
            [Environment]::NewLine
    }

    $updatedContent = $content.Remove($sectionMatch.Index, $sectionMatch.Length).Insert(
        $sectionMatch.Index,
        $updatedSection
    )
    Write-Utf8NoBom -Path $Path -Content $updatedContent
    return $true
}


function Write-ProjectMcpConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $config = [ordered]@{}
    if (Test-Path -LiteralPath $Path) {
        try {
            $config = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable
        } catch {
            throw "Existing MCP config is not valid JSON: $Path"
        }
    }
    if (-not $config.Contains("mcpServers")) {
        $config["mcpServers"] = [ordered]@{}
    }
    # stdio 型：command 即随包 shim（host 直接 spawn，由它自举机器级 daemon），
    # args 为空——shim 的参数全部来自 env。绝不写出指向不存在入口的条目。
    if (-not (Test-Path -LiteralPath $shimExe)) {
        throw ("服务入口缺失：$shimExe`n先发布该平台产物：`n" +
            "  dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=$rid`n" +
            "（或用 -NoProjectMcpConfig 跳过项目配置写入）")
    }
    $config["mcpServers"]["godot"] = [ordered]@{
        type    = "stdio"
        command = $shimExe
        args    = @()
    }
    $json = $config | ConvertTo-Json -Depth 20
    Write-Utf8NoBom -Path $Path -Content ($json + [Environment]::NewLine)
}


if (-not (Test-Path -LiteralPath $addonSource -PathType Container)) {
    throw "Bundled addon is missing: $addonSource"
}
if (-not (Test-Path -LiteralPath $GodotExecutable -PathType Leaf)) {
    throw "Godot executable not found: $GodotExecutable"
}

$godotConsole = $GodotExecutable
$consoleCandidate = [IO.Path]::Combine(
    [IO.Path]::GetDirectoryName($GodotExecutable),
    [IO.Path]::GetFileNameWithoutExtension($GodotExecutable) + "_console.exe"
)
if (Test-Path -LiteralPath $consoleCandidate -PathType Leaf) {
    $godotConsole = $consoleCandidate
}

$versionOutput = (& $godotConsole --headless --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Godot version probe failed: $versionOutput"
}
if ($versionOutput -notmatch '^4\.(\d+)') {
    throw "Godot 4.x is required; detected: $versionOutput"
}
Write-Host "[OK] Godot: $versionOutput"

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    if ($Template -eq "none") {
        throw "project.godot not found. Pass -Template to create a new project: $projectRoot"
    }
    $templateRoot = Join-Path $pluginRoot ("templates\" + $Template)
    if (-not (Test-Path -LiteralPath $templateRoot -PathType Container)) {
        throw "Bundled template not found: $templateRoot"
    }
    if (Test-Path -LiteralPath $projectRoot) {
        $existingEntries = @(Get-ChildItem -Force -LiteralPath $projectRoot)
        if ($existingEntries.Count -gt 0) {
            throw "Refusing to create a template in a non-empty directory: $projectRoot"
        }
    } else {
        New-Item -ItemType Directory -Force -Path $projectRoot | Out-Null
    }
    Copy-Item -Recurse -Force -Path (Join-Path $templateRoot "*") -Destination $projectRoot
    $projectContent = [IO.File]::ReadAllText($projectFile)
    $featureVersion = ($versionOutput -split '\.')[0..1] -join "."
    $projectContent = $projectContent.Replace("__GODOT_VERSION__", $featureVersion)
    $projectName = Split-Path -Leaf $projectRoot
    $projectContent = [regex]::Replace(
        $projectContent,
        'config/name="[^"]*"',
        'config/name="' + $projectName + '"',
        1
    )
    Write-Utf8NoBom -Path $projectFile -Content $projectContent
    Write-Host "[OK] Created project from template '$Template': $projectRoot"
}

Assert-ChildPath -Parent $projectRoot -Child $addonDestination

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $projectRoot (".godot-mcp-unified-backups\" + $timestamp)
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
Copy-Item -Force -LiteralPath $projectFile -Destination (Join-Path $backupRoot "project.godot")

if (Test-Path -LiteralPath $addonDestination -PathType Container) {
    $addonBackup = Join-Path $backupRoot "godot_mcp_toolkit"
    Copy-Item -Recurse -Force -LiteralPath $addonDestination -Destination $addonBackup
    Write-Host "[OK] Backed up existing addon: $addonBackup"
}

New-Item -ItemType Directory -Force -Path $addonDestination | Out-Null
& robocopy $addonSource $addonDestination /MIR /XD ".godot" /NFL /NDL /NJH /NJS /NP | Out-Null
$copyExit = $LASTEXITCODE
if ($copyExit -gt 7) {
    throw "Addon copy failed with robocopy exit code $copyExit"
}
Write-Host "[OK] Installed addon: $addonDestination"

$enabledNow = Enable-EditorPlugin -Path $projectFile -PluginPath $pluginConfigPath
if ($enabledNow) {
    Write-Host "[OK] Enabled Godot MCP Unified in project.godot"
} else {
    Write-Host "[OK] Godot MCP Unified was already enabled"
}

if (-not $NoProjectMcpConfig) {
    $mcpConfigPath = Join-Path $projectRoot ".mcp.json"
    if (Test-Path -LiteralPath $mcpConfigPath) {
        Copy-Item -Force -LiteralPath $mcpConfigPath -Destination (Join-Path $backupRoot ".mcp.json")
    }
    Write-ProjectMcpConfig -Path $mcpConfigPath
    Write-Host "[OK] Registered project MCP config: $mcpConfigPath"
}

Write-Host "[OK] Project MCP config points at the bundled shim (stdio); the shim starts the machine-level daemon (127.0.0.1:6590) on demand, so clients need no token and no pre-started daemon."

if (-not $SkipEditorValidation) {
    $editorOutput = (& $godotConsole --headless --path $projectRoot --editor --quit 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw ("Headless editor validation failed." + [Environment]::NewLine + $editorOutput)
    }
    if ($editorOutput -notmatch '\[MCPServer\] listening on 127\.0\.0\.1:') {
        throw ("Plugin loaded without a listening confirmation." + [Environment]::NewLine + $editorOutput)
    }
    Write-Host "[OK] Headless editor loaded the plugin and opened its authenticated loopback server"
}

Write-Host ""
Write-Host "Godot MCP Unified project deployment complete."
Write-Host "Project: $projectRoot"
Write-Host "Godot:   $GodotExecutable"
Write-Host "Backup:  $backupRoot"
