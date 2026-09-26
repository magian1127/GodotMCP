# 把 Claude 桌面端(My Uploads 上传安装)的 godot-mcp-unified 插件副本中的
# skills/ 与 addons/ 替换为指向仓库唯一真源(plugin/godot-mcp-unified)的
# 目录联接(junction)。链接后仓库内的改动无需重新上传安装，重启 Claude
# 桌面端(新开会话)即可加载。
#
# 背景：桌面端把上传的 .plugin 包解压到
#   %APPDATA%\Claude\local-agent-mode-sessions\<账户>\<组织>\rpm\plugin_<id>\
# 并在同级 manifest.json 里只登记元数据(name/id/时间)，不校验内容哈希，
# 因此把子目录换成联接安全。包内 .mcp.json 在打包时已写死仓库内 shim 的
# 绝对路径(server-dotnet/publish/win-x64/godot-mcp-shim.exe)，MCP 服务面
# 本就直连真源；本脚本补齐技能与 addon 两块复制体。
#
# 用法（需 PowerShell 7+）：
#   pwsh adapters/claude/link-claude-plugin.ps1           建立链接（幂等，可重复执行）
#   pwsh adapters/claude/link-claude-plugin.ps1 -Unlink   解除链接（不恢复副本；
#                                                         需要副本模式时在桌面端
#                                                         重新上传安装包）
#
# 注意：在桌面端重新上传/更新该插件会重建整个 plugin_<id> 目录（联接被覆盖），
# 届时重跑本脚本即可；上传用的安装包快照见 adapters/claude/godot-mcp-unified.plugin。
[CmdletBinding()]
param(
    # 只解除链接，不建立。
    [switch]$Unlink
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$sourceDir = Join-Path $repoRoot "plugin\godot-mcp-unified"
$sourceSkills = Join-Path $sourceDir "skills"
$sourceAddons = Join-Path $sourceDir "addons"
$sessionsRoot = Join-Path $env:APPDATA "Claude\local-agent-mode-sessions"

foreach ($path in @($sourceSkills, $sourceAddons, (Join-Path $sourceDir ".claude-plugin\plugin.json"), $sessionsRoot)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path missing: $path"
    }
}

# 在所有账户/组织会话目录的 rpm 清单里查找 name=godot-mcp-unified 的已装插件，
# 返回插件目录绝对路径列表（通常只有一个）。
function Find-InstalledPluginDirs {
    $found = @()
    foreach ($manifest in Get-ChildItem -LiteralPath $sessionsRoot -Recurse -Filter manifest.json -File -ErrorAction SilentlyContinue) {
        if ($manifest.Directory.Name -ne "rpm") { continue }
        $entries = (Get-Content $manifest.FullName -Raw | ConvertFrom-Json).plugins
        foreach ($entry in @($entries)) {
            if ($entry.name -eq "godot-mcp-unified") {
                $found += Join-Path $manifest.Directory.FullName $entry.id
            }
        }
    }
    return $found
}

function Get-PathLinkType {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Item -LiteralPath $Path -Force).LinkType
}

# 只移除联接(link)本身，绝不递归进目标——目标可能是仓库唯一真源。
function Remove-PluginJunction {
    param([string]$Path)
    if ((Get-PathLinkType $Path) -ne "Junction") {
        throw "Refusing to remove non-junction path: $Path"
    }
    cmd /c rmdir "$Path"
    if (Test-Path -LiteralPath $Path) { throw "Failed to remove junction: $Path" }
    Write-Host "Removed junction: $Path"
}

# 把 $Path 变成指向 $Target 的联接：已是且指向真源则跳过；指向别处的联接重建；
# 复制部署的旧目录是派生物（可从上传的安装包重装），删除后替换。
function Install-PluginJunction {
    param([string]$Path, [string]$Target)
    $existingType = Get-PathLinkType $Path
    if ($existingType -eq "Junction") {
        $current = (Get-Item -LiteralPath $Path -Force).Target
        if ($current -and ([IO.Path]::GetFullPath($current) -ieq [IO.Path]::GetFullPath($Target))) {
            Write-Host "Junction already up to date: $Path"
            return
        }
        Remove-PluginJunction $Path
    } elseif ($null -ne $existingType) {
        throw "$Path is another kind of reparse point ($existingType); resolve it manually"
    } elseif (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
        Write-Host "Replaced copy deployment with junction: $Path"
    }
    New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
    Write-Host "Created junction: $Path -> $Target"
}

$pluginDirs = Find-InstalledPluginDirs
if (-not $pluginDirs) {
    throw "No godot-mcp-unified install found under $sessionsRoot (install the .plugin package in the Claude desktop app first)"
}

foreach ($pluginDir in $pluginDirs) {
    Write-Host "Plugin dir: $pluginDir"
    if ($Unlink) {
        foreach ($name in @("skills", "addons")) {
            $path = Join-Path $pluginDir $name
            if ((Get-PathLinkType $path) -eq "Junction") {
                Remove-PluginJunction $path
            } else {
                Write-Host "Not a junction, skipped: $path"
            }
        }
        continue
    }

    # 清单与 MCP 配置保持包内复制体（.mcp.json 的 shim 绝对路径已直连仓库），
    # 只链接会随仓库演进的大目录。
    Install-PluginJunction (Join-Path $pluginDir "skills") $sourceSkills
    Install-PluginJunction (Join-Path $pluginDir "addons") $sourceAddons

    # 校验：联接必须能透出桌面端加载契约所需的技能与 addon 标识文件。
    foreach ($required in @("skills\godot-control\SKILL.md", "addons\godot_mcp_toolkit\README.md",
                            ".claude-plugin\plugin.json", ".mcp.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $pluginDir $required))) {
            throw "Junction created but required file not visible through it: $required"
        }
    }
}

Write-Host ""
if ($Unlink) {
    Write-Host "Unlinked. Re-upload adapters/claude/godot-mcp-unified.plugin in the"
    Write-Host "Claude desktop app if you want copy deployment back."
} else {
    Write-Host "Done. Repository edits under plugin/godot-mcp-unified are now live"
    Write-Host "for the Claude desktop app after restarting it (start a new chat)."
}
