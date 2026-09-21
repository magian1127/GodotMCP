# 维护 DSH 侧到本仓库的两类目录联接(junction)——**可选的本地开发布局**。
# 发布/常规使用无需本脚本：把 DSH 直接指向仓库内的接入层目录
#   <仓库>\adapters\dsh\deepseek-harness-godot_unified
# （或用接入层 CLI：dsh-godot install --link <该目录> --project <Godot 项目>）。
# 仅当你想在一个自选的机器位置维持稳定联接时才使用本脚本：
#   1. 插件工作区位置：<自选位置>\deepseek-harness-godot_unified
#      → <本仓库>\adapters\dsh\deepseek-harness-godot_unified（接入层源码）
#   2. godot preset 技能目录：~\.dsh\.agent-presets\godot\skills\<技能名>
#      → <本仓库>\plugin\godot-mcp-unified\skills\<技能名>
#      （DSH 对话框输入 / 列出的技能来自 preset 的 skills/，链接后改仓库即生效）
# 链接后在本仓库修改接入层源码或技能文件，DSH 侧直接生效（接入层 TS 改动
# 需构建 lib/；服务器改动需重启 DSH 会话；提示词 section 随冷启动挂载）。
#
# 用法（需 PowerShell 7+）：
#   pwsh adapters/dsh/link-dsh-plugin.ps1 -LinkPath <自选位置>\deepseek-harness-godot_unified
#   pwsh adapters/dsh/link-dsh-plugin.ps1 -LinkPath <同上> -Unlink   解除链接（只移除联接本身）
#   -PresetSkillsPath <dir>  preset 技能目录，默认 ~\.dsh\.agent-presets\godot\skills
#
# 边界与安全：接入层是本仓库受控源码树；DSH 运行态（profile 行、bundle、
# $DSH_HOME/godot/paths.json）由接入层自带的 dsh-godot CLI 与 DSH 宿主管理，
# 本脚本不触碰。preset 技能若存在真实目录：与仓库逐字节一致时自动替换为联接
# （无数据丢失）；存在差异时跳过并报告，绝不静默丢弃可能承载工作的内容。
[CmdletBinding()]
param(
    # 只解除链接，不建立。
    [switch]$Unlink,
    # 接入层联接所在位置（本机自选位置，必填——仓库外部路径无法推导）。
    [Parameter(Mandatory = $true)]
    [string]$LinkPath,
    # godot preset 的技能目录。
    [string]$PresetSkillsPath = (Join-Path $env:USERPROFILE '.dsh\.agent-presets\godot\skills')
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$sourceDir = Join-Path $repoRoot "adapters\dsh\deepseek-harness-godot_unified"
$pluginSkillsDir = Join-Path $repoRoot "plugin\godot-mcp-unified\skills"

foreach ($required in @("package.json", "bin\dsh-godot.mjs")) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $required))) {
        throw "DSH access-layer source missing ${required}: $sourceDir"
    }
}
if (-not (Test-Path -LiteralPath $pluginSkillsDir)) {
    throw "Plugin skills directory missing: $pluginSkillsDir"
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

# 两棵树是否逐字节一致（相对路径集合 + 每文件 SHA256）。
function Test-TreeIdentical {
    param([string]$Left, [string]$Right)
    $leftFiles = Get-ChildItem -LiteralPath $Left -Recurse -File |
        ForEach-Object { @{ Rel = [IO.Path]::GetRelativePath($Left, $_.FullName); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } } |
        Sort-Object Rel
    $rightFiles = Get-ChildItem -LiteralPath $Right -Recurse -File |
        ForEach-Object { @{ Rel = [IO.Path]::GetRelativePath($Right, $_.FullName); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } } |
        Sort-Object Rel
    if ($leftFiles.Count -ne $rightFiles.Count) { return $false }
    for ($i = 0; $i -lt $leftFiles.Count; $i++) {
        if ($leftFiles[$i].Rel -ne $rightFiles[$i].Rel -or $leftFiles[$i].Hash -ne $rightFiles[$i].Hash) { return $false }
    }
    return $true
}

# 把 preset 中的一个技能目录替换/维护为指向仓库技能的联接。
function Ensure-SkillJunction {
    param([string]$SkillName)
    $skillSource = Join-Path $pluginSkillsDir $SkillName
    $skillTarget = Join-Path $PresetSkillsPath $SkillName
    $existingType = Get-PathLinkType $skillTarget
    if ($existingType -eq "Junction") {
        $target = (Get-Item -LiteralPath $skillTarget -Force).Target
        if ($target -and ([IO.Path]::GetFullPath($target) -ieq [IO.Path]::GetFullPath($skillSource))) {
            Write-Host "  skill junction up to date: $SkillName"
        } else {
            Remove-PluginJunction $skillTarget
            New-Item -ItemType Junction -Path $skillTarget -Target $skillSource | Out-Null
            Write-Host "  re-pointed skill junction: $SkillName"
        }
        return $true
    }
    if ($null -ne $existingType) {
        Write-Warning "  skipped $SkillName`: $skillTarget is a $($existingType), not a junction"
        return $false
    }
    if (Test-Path -LiteralPath $skillTarget) {
        if (Test-TreeIdentical $skillSource $skillTarget) {
            # 与仓库逐字节一致的副本：安全替换为联接，消除重复部署。
            Remove-Item -LiteralPath $skillTarget -Recurse -Force
            New-Item -ItemType Junction -Path $skillTarget -Target $skillSource | Out-Null
            Write-Host "  replaced identical copy with junction: $SkillName"
            return $true
        }
        Write-Warning "  skipped $SkillName`: preset copy differs from the repository skill; resolve manually"
        return $false
    }
    New-Item -ItemType Junction -Path $skillTarget -Target $skillSource | Out-Null
    Write-Host "  created skill junction: $SkillName"
    return $true
}

if ($Unlink) {
    if ((Get-PathLinkType $LinkPath) -eq "Junction") {
        Remove-PluginJunction $LinkPath
    } else {
        Write-Host "Not a junction, skipped: $LinkPath"
    }
    if (Test-Path -LiteralPath $PresetSkillsPath) {
        foreach ($entry in Get-ChildItem -LiteralPath $PresetSkillsPath -Force -Directory) {
            if ($entry.LinkType -ne "Junction") { continue }
            $target = $entry.Target
            if ($target -and ([IO.Path]::GetFullPath($target) -like "$pluginSkillsDir*")) {
                Remove-PluginJunction $entry.FullName
            }
        }
    }
    Write-Host ""
    Write-Host "Unlinked. The access layer and skills remain in this repository."
    exit 0
}

# ── 1. 接入层联接 ───────────────────────────────────────────────────────────

$linkParent = Split-Path $LinkPath -Parent
if (-not (Test-Path -LiteralPath $linkParent)) {
    throw "Link parent directory missing: $linkParent"
}

$existingType = Get-PathLinkType $LinkPath
if ($existingType -eq "Junction") {
    $target = (Get-Item -LiteralPath $LinkPath -Force).Target
    if ($target -and ([IO.Path]::GetFullPath($target) -ieq [IO.Path]::GetFullPath($sourceDir))) {
        Write-Host "Junction already up to date: $LinkPath"
    } else {
        Remove-PluginJunction $LinkPath
        New-Item -ItemType Junction -Path $LinkPath -Target $sourceDir | Out-Null
        Write-Host "Re-pointed junction: $LinkPath -> $sourceDir"
    }
} elseif ($null -ne $existingType) {
    throw "$LinkPath is another kind of reparse point ($existingType); resolve it manually"
} elseif (Test-Path -LiteralPath $LinkPath) {
    # 真实目录：可能是手工恢复的仓库副本，删除可能丢失工作——交由人工决策。
    throw "$LinkPath is a real directory, not a junction. If it is a restored copy with" +
        " no unmerged work, move it away or delete it manually, then re-run this script."
} else {
    New-Item -ItemType Junction -Path $LinkPath -Target $sourceDir | Out-Null
    Write-Host "Created junction: $LinkPath -> $sourceDir"
}

foreach ($required in @("package.json", "bin\dsh-godot.mjs", "src")) {
    if (-not (Test-Path -LiteralPath (Join-Path $LinkPath $required))) {
        throw "Junction created but required path not visible through it: $required"
    }
}

# ── 2. preset 技能联接（/ 菜单的数据源）───────────────────────────────────

$skillSkipped = 0
if (Test-Path -LiteralPath $PresetSkillsPath) {
    Write-Host ""
    Write-Host "Linking godot preset skills ($PresetSkillsPath)..."
    foreach ($skill in Get-ChildItem -LiteralPath $pluginSkillsDir -Directory) {
        if (-not (Ensure-SkillJunction $skill.Name)) { $skillSkipped++ }
    }
    if ($skillSkipped -gt 0) {
        Write-Warning "$skillSkipped preset skill(s) skipped — see warnings above."
    }
} else {
    Write-Host ""
    Write-Warning "Preset skills directory not found, skills linking skipped: $PresetSkillsPath"
}

# ── 3. 体检（诊断性质）─────────────────────────────────────────────────────

# 接入层 CLI 自检 server/项目路径与 bundle 行；失败不回滚链接——status 依赖
# $DSH_HOME 运行态，环境缺失属独立问题。
Write-Host ""
Write-Host "Running dsh-godot status (diagnostics)..."
& node (Join-Path $sourceDir "bin\dsh-godot.mjs") status
if ($LASTEXITCODE -ne 0) {
    Write-Warning "dsh-godot status exited with $LASTEXITCODE — the junction itself is fine;" +
        " check `$DSH_HOME profile/paths state if the workbench misbehaves."
}

Write-Host ""
Write-Host "Done. Edits under adapters/dsh/deepseek-harness-godot_unified and"
Write-Host "plugin/godot-mcp-unified/skills are live for DSH; adapter/server changes"
Write-Host "need a session restart, and preset skills appear via '/' after a UI refresh."
