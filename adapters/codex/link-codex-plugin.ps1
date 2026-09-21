# 把 Codex 插件缓存中的 Godot MCP Unified 副本替换为指向仓库唯一真源
# (plugin/godot-mcp-unified) 的目录联接(junction)。链接后仓库内的改动无需
# 重新部署，重启 Codex（新开任务）即可加载。
#
# 背景：Codex 在 ~/.codex/plugins/cache/<marketplace>/<plugin>/<version>/ 维护
# 完整工作副本（含 server/node_modules，约 121MB），每次改动都需重新安装才能
# 生效；插件的 .codex-plugin/mcp.json 通过 ${PLUGIN_ROOT} 相对引用 server，
# 因此缓存目录换成联接后，Codex 直接运行仓库里的服务器。
#
# 用法（需 PowerShell 7+）：
#   pwsh adapters/codex/link-codex-plugin.ps1            建立链接（幂等）
#   pwsh adapters/codex/link-codex-plugin.ps1 -Unlink    解除链接（不恢复副本；
#                                                         需要副本模式时从
#                                                         Codex 插件市场重装）
#   -CacheName <name>   指定缓存目录名。默认复用现有最新目录名以保持
#                       Codex 状态兼容；没有现存目录时用 <版本>+codex.link。
#                       插件版本号变更后可用它重建带新版本号前缀的链接。
#
# 注意：Codex 的插件缓存被删/被换不影响 config.toml 的
# [plugins."godot-mcp-unified@personal"] 启用开关。
[CmdletBinding()]
param(
    # 只解除链接，不建立。
    [switch]$Unlink,
    # 缓存目录名（默认：复用现有最新目录名，否则 <版本>+codex.link）。
    [string]$CacheName
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$sourceDir = Join-Path $repoRoot "plugin\godot-mcp-unified"
$codexCachePluginRoot = Join-Path $env:USERPROFILE ".codex\plugins\cache\personal\godot-mcp-unified"

if (-not (Test-Path -LiteralPath (Join-Path $sourceDir ".codex-plugin\plugin.json"))) {
    throw "Plugin manifest missing under $sourceDir"
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

if ($Unlink) {
    $junction = Get-ChildItem -LiteralPath $codexCachePluginRoot -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.LinkType -eq "Junction" }
    if (-not $junction) {
        Write-Host "No junction found under $codexCachePluginRoot"
        exit 0
    }
    foreach ($entry in $junction) { Remove-PluginJunction $entry.FullName }
    Write-Host ""
    Write-Host "Unlinked. Codex now has no local copy; reinstall from the"
    Write-Host "Codex plugin marketplace if you want copy deployment back."
    exit 0
}

# 解析缓存目录名：显式参数 > 现有最新目录 > <版本>+codex.link。
if (-not $CacheName) {
    $existing = Get-ChildItem -LiteralPath $codexCachePluginRoot -Force -Directory -ErrorAction SilentlyContinue
    if ($existing) {
        # 目录名以版本开头（如 1.0.0+codex.<ts>）；按名称排序取最新。
        $CacheName = ($existing | Sort-Object Name -Descending | Select-Object -First 1).Name
        Write-Host "Reusing existing cache dir name: $CacheName"
    } else {
        $version = (Get-Content (Join-Path $sourceDir ".codex-plugin\plugin.json") -Raw |
            ConvertFrom-Json).version
        if (-not $version) { throw "No version in plugin manifest" }
        $CacheName = "$version+codex.link"
        Write-Host "No existing cache dir; using: $CacheName"
    }
}
$cacheTarget = Join-Path $codexCachePluginRoot $CacheName

New-Item -ItemType Directory -Path $codexCachePluginRoot -Force | Out-Null

$existingType = Get-PathLinkType $cacheTarget
if ($existingType -eq "Junction") {
    $target = (Get-Item -LiteralPath $cacheTarget -Force).Target
    if ($target -and ([IO.Path]::GetFullPath($target) -ieq [IO.Path]::GetFullPath($sourceDir))) {
        Write-Host "Junction already up to date: $cacheTarget"
    } else {
        Remove-PluginJunction $cacheTarget
        New-Item -ItemType Junction -Path $cacheTarget -Target $sourceDir | Out-Null
        Write-Host "Re-pointed junction: $cacheTarget -> $sourceDir"
    }
} elseif ($null -ne $existingType) {
    throw "$cacheTarget is another kind of reparse point ($existingType); resolve it manually"
} else {
    if (Test-Path -LiteralPath $cacheTarget) {
        # 复制部署的旧副本——派生物（可从插件市场重装），删除后替换为联接。
        Write-Host "Removing stale copy deployment: $cacheTarget"
        Remove-Item -LiteralPath $cacheTarget -Recurse -Force
    }
    New-Item -ItemType Junction -Path $cacheTarget -Target $sourceDir | Out-Null
    Write-Host "Created junction: $cacheTarget -> $sourceDir"
}

# 校验：联接必须能透出 Codex 加载契约所需的清单与 MCP 配置。
foreach ($required in @(".codex-plugin\plugin.json", ".codex-plugin\mcp.json", "server\dist\index.js")) {
    if (-not (Test-Path -LiteralPath (Join-Path $cacheTarget $required))) {
        throw "Junction created but required file not visible through it: $required"
    }
}

# 清理指向本真源的其他过期版本联接（-CacheName 换名后残留）。
foreach ($sibling in Get-ChildItem -LiteralPath $codexCachePluginRoot -Force) {
    if ($sibling.PSIsContainer -and $sibling.LinkType -eq "Junction" -and
        $sibling.Name -ne $CacheName -and
        $sibling.Target -and ([IO.Path]::GetFullPath($sibling.Target) -ieq [IO.Path]::GetFullPath($sourceDir))) {
        Remove-PluginJunction $sibling.FullName
    }
}

Write-Host ""
Write-Host "Done. Repository edits under plugin/godot-mcp-unified are now live"
Write-Host "for Codex after restarting it (start a new Codex task)."
