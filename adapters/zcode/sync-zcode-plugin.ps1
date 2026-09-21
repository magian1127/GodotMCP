# 从唯一插件源(plugin/godot-mcp-unified)把 Godot MCP Unified 插件安装/更新到
# 当前 ZCode。仓库根目录即本地插件市场(marketplace)
# （marketplace.json -> ./plugin/godot-mcp-unified）。
#
# ZCode 在 ~/.zcode/cli/plugins 下维护自己的工作副本（市场镜像(mirror) +
# 按版本缓存(cache)）；本脚本从唯一真源刷新这两处，因此仓库内不再维护
# 市场快照(snapshot)。
#
# 可重复执行；完成后需重启 ZCode 才能加载更新。
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$sourceDir = Join-Path $repoRoot "plugin\godot-mcp-unified"
$marketplaceJson = Join-Path $repoRoot "marketplace.json"
$zcodePlugins = Join-Path $env:USERPROFILE ".zcode\cli\plugins"

foreach ($dir in @($sourceDir, $zcodePlugins)) {
    if (-not (Test-Path $dir)) {
        throw "Required directory missing: $dir"
    }
}
if (-not (Test-Path $marketplaceJson)) {
    throw "Required marketplace manifest missing: $marketplaceJson"
}

$manifestPath = Join-Path $sourceDir ".zcode-plugin\plugin.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = $manifest.version
if (-not $version) { throw "No version in $manifestPath" }
$cacheTarget = Join-Path $zcodePlugins "cache\godot-mcp-local\godot-mcp-unified\$version"

# 链接模式守卫：目标是目录联接(junction)时绝不能 /MIR 镜像——
# robocopy 会穿透联接直接改写仓库唯一真源（还会按排除规则误删仓库文件）。
foreach ($linkedPath in @(
        $cacheTarget,
        (Join-Path $zcodePlugins "marketplaces\godot-mcp-local\plugin\godot-mcp-unified")
    )) {
    if ((Test-Path -LiteralPath $linkedPath) -and
        (Get-Item -LiteralPath $linkedPath -Force).LinkType) {
        throw "$linkedPath 处于目录联接(junction)链接部署。请先运行 adapters\zcode\link-zcode-plugin.ps1 -Unlink 再复制部署。"
    }
}

function Invoke-Robocopy {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$ExcludeDirs = @(),
        [string[]]$ExcludeFiles = @(),
        [switch]$Mirror
    )
    # /XD 条目使用裸目录名，因此可匹配任意深度。
    $robocopyArgs = @($Source, $Destination, "/E", "/NFL", "/NDL", "/NJH", "/NP")
    if ($Mirror) { $robocopyArgs += "/MIR" }
    foreach ($d in $ExcludeDirs) { $robocopyArgs += @("/XD", $d) }
    foreach ($f in $ExcludeFiles) { $robocopyArgs += @("/XF", $f) }
    & robocopy @robocopyArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination" }
}

Write-Host "1/4 Refreshing ZCode marketplace mirror from the repository root..."
# ZCode 相对镜像根目录解析 marketplace.json 与插件源；只镜像市场定义所需的
# 内容（清单 + 插件目录树）。
$marketplaceMirror = Join-Path $zcodePlugins "marketplaces\godot-mcp-local"
New-Item -ItemType Directory -Path $marketplaceMirror -Force | Out-Null
Invoke-Robocopy $sourceDir (Join-Path $marketplaceMirror "plugin\godot-mcp-unified") `
    -ExcludeDirs @("node_modules", ".mimosa", ".git") `
    -Mirror
Copy-Item $marketplaceJson $marketplaceMirror -Force

Write-Host "2/4 Syncing plugin content into the ZCode cache ($version)..."
# 缓存目录才是 ZCode 实际运行的插件：保留全部客户端适配层(含 .codex-plugin)，
# 剔除笨重的本地状态；生产依赖随后单独安装。
Invoke-Robocopy $sourceDir $cacheTarget `
    -ExcludeDirs @("node_modules", ".mimosa") `
    -ExcludeFiles @(
        (Join-Path $sourceDir "addons\godot_mcp_toolkit\icon.svg.import")
    ) `
    -Mirror

# Node 桥已随插件 1.1.0 退役:MCP 服务由机器级 daemon 经 HTTP 提供,
# 缓存内不再有 server 目录与 npm 依赖步骤。
Write-Host "3/4 No npm dependencies to install (daemon HTTP face, Node bridge retired)."

Write-Host "4/4 Re-registering plugin state..."
& python (Join-Path $repoRoot "adapters\zcode\register-zcode-plugin.py")
if ($LASTEXITCODE -ne 0) { throw "register-zcode-plugin.py failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "Done. Restart ZCode so the refreshed plugin is picked up."
Write-Host "Verify in a new session: the 6 godot-* skills and the godot-mcp-unified MCP server (83 built-in tools; 20 at startup)."
