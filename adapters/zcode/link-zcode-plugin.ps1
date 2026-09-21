# 把 ZCode 插件缓存与市场镜像替换为指向仓库唯一真源(plugin/godot-mcp-unified)
# 的目录联接(junction)。链接后仓库内的改动无需重新部署，重启 ZCode 即可加载；
# 仓库根目录即本地插件市场(marketplace)（marketplace.json -> ./plugin/godot-mcp-unified）。
#
# 用法（需 PowerShell 7+）：
#   pwsh adapters/zcode/link-zcode-plugin.ps1           建立链接（幂等，可重复执行）
#   pwsh adapters/zcode/link-zcode-plugin.ps1 -Unlink   解除链接；之后可运行
#                                                      sync-zcode-plugin.ps1 恢复复制部署
#
# 版本号变更后需重跑本脚本：ZCode 的缓存目录按清单版本命名，
# register-zcode-plugin.py 会把 installPath 指向新版本目录。
[CmdletBinding()]
param(
    # 只解除链接，不建立。
    [switch]$Unlink
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$sourceDir = Join-Path $repoRoot "plugin\godot-mcp-unified"
$marketplaceJson = Join-Path $repoRoot "marketplace.json"
$zcodePlugins = Join-Path $env:USERPROFILE ".zcode\cli\plugins"

foreach ($path in @($sourceDir, $zcodePlugins, $marketplaceJson)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path missing: $path"
    }
}

$manifestPath = Join-Path $sourceDir ".zcode-plugin\plugin.json"
$version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
if (-not $version) { throw "No version in $manifestPath" }

$pluginCacheRoot = Join-Path $zcodePlugins "cache\godot-mcp-local\godot-mcp-unified"
$cacheTarget = Join-Path $pluginCacheRoot $version
$mirrorPluginDir = Join-Path $zcodePlugins "marketplaces\godot-mcp-local\plugin\godot-mcp-unified"

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

function Install-PluginJunction {
    param([string]$Path)
    $existingType = Get-PathLinkType $Path
    if ($existingType -eq "Junction") {
        # 已是联接：指向真源则保留，否则重建。
        $target = (Get-Item -LiteralPath $Path -Force).Target
        if ($target -and ([IO.Path]::GetFullPath($target) -ieq [IO.Path]::GetFullPath($sourceDir))) {
            Write-Host "Junction already up to date: $Path"
            return
        }
        Remove-PluginJunction $Path
    } elseif ($null -ne $existingType) {
        throw "$Path is another kind of reparse point ($existingType); resolve it manually"
    } elseif (Test-Path -LiteralPath $Path) {
        # 复制部署的旧副本——派生物，可安全删除后替换为联接。
        Remove-Item -LiteralPath $Path -Recurse -Force
        Write-Host "Replaced copy deployment with junction: $Path"
    }
    New-Item -ItemType Junction -Path $Path -Target $sourceDir | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $Path ".zcode-plugin\plugin.json"))) {
        throw "Junction created but plugin manifest not visible through it: $Path"
    }
    Write-Host "Created junction: $Path -> $sourceDir"
}

if ($Unlink) {
    foreach ($path in @($cacheTarget, $mirrorPluginDir)) {
        if ((Get-PathLinkType $path) -eq "Junction") {
            Remove-PluginJunction $path
        } else {
            Write-Host "Not a junction, skipped: $path"
        }
    }
    Write-Host ""
    Write-Host "Unlinked. Run adapters/zcode/sync-zcode-plugin.ps1 to restore copy deployment."
    exit 0
}

# 1/3 链接按版本命名的运行缓存（ZCode 实际加载的插件）。
New-Item -ItemType Directory -Path $pluginCacheRoot -Force | Out-Null
Install-PluginJunction $cacheTarget

# 清理指向真源的过期版本联接（版本号变更后残留）。
foreach ($sibling in Get-ChildItem -LiteralPath $pluginCacheRoot -Force) {
    if ($sibling.PSIsContainer -and $sibling.LinkType -eq "Junction" -and
        $sibling.Name -ne $version -and
        $sibling.Target -and ([IO.Path]::GetFullPath($sibling.Target) -ieq [IO.Path]::GetFullPath($sourceDir))) {
        Remove-PluginJunction $sibling.FullName
    }
}

# 2/3 链接市场镜像中的插件目录并刷新市场清单。
$mirrorPluginParent = Split-Path $mirrorPluginDir -Parent
New-Item -ItemType Directory -Path $mirrorPluginParent -Force | Out-Null
Install-PluginJunction $mirrorPluginDir
Copy-Item $marketplaceJson (Split-Path $mirrorPluginParent -Parent) -Force

# 3/3 刷新插件注册状态（installPath 指向缓存中的版本目录）。
& python (Join-Path $repoRoot "adapters\zcode\register-zcode-plugin.py")
if ($LASTEXITCODE -ne 0) { throw "register-zcode-plugin.py failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "Done. Repository edits under plugin/godot-mcp-unified are now live"
Write-Host "for ZCode after a restart; re-run this script after a version bump."
Write-Host "Verify in a new session: the 6 godot-* skills and the godot-mcp-unified MCP server (83 built-in tools; 20 at startup)."
