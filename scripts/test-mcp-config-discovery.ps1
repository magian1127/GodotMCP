[CmdletBinding()]
param(
    # 本机 Godot 可执行文件：显式参数 > 仓库根 .env 的 GODOT_EXECUTABLE（见 .env.example）。
    [string]$GodotExecutable,
    [string]$PluginRoot,
    [switch]$IncludeUi
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

# 机器相关值回退：显式参数缺省时读仓库根 .env（模板见 .env.example；该文件不入版本管理）。
if (-not $GodotExecutable) {
    $envFile = Join-Path $repoRoot ".env"
    if (Test-Path -LiteralPath $envFile) {
        $match = Select-String -LiteralPath $envFile -Pattern '^\s*GODOT_EXECUTABLE\s*=' | Select-Object -First 1
        if ($match) { $GodotExecutable = ($match.Line -split '=', 2)[1].Trim().Trim('"').Trim("'") }
    }
}
if (-not $GodotExecutable) {
    throw "Godot executable not specified. Pass -GodotExecutable or set GODOT_EXECUTABLE in the repository root .env (see .env.example)."
}
if (-not $PluginRoot) {
    $PluginRoot = Join-Path $repoRoot 'plugin\godot-mcp-unified'
}
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('godot-mcp-config-' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $fixtureRoot 'workspace\game'
$addonRoot = Join-Path $projectRoot 'addons\godot_mcp_toolkit'
New-Item -ItemType Directory -Path (Join-Path $addonRoot 'ui') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'tests') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PluginRoot 'addons\godot_mcp_toolkit\ui\mcp_json_sync.gd') -Destination (Join-Path $addonRoot 'ui\mcp_json_sync.gd')
$discovery = Join-Path $PluginRoot 'addons\godot_mcp_toolkit\ui\mcp_json_discovery.gd'
if (Test-Path -LiteralPath $discovery) {
    Copy-Item -LiteralPath $discovery -Destination (Join-Path $addonRoot 'ui\mcp_json_discovery.gd')
}
# mcp_json_sync.gd 的 shim 路径解析依赖它（路径工具与 RID 单一出处）。
$platformRid = Join-Path $PluginRoot 'addons\godot_mcp_toolkit\paths\platform_rid.gd'
if (Test-Path -LiteralPath $platformRid) {
    New-Item -ItemType Directory -Path (Join-Path $addonRoot 'paths') -Force | Out-Null
    Copy-Item -LiteralPath $platformRid -Destination (Join-Path $addonRoot 'paths\platform_rid.gd')
}
Copy-Item -LiteralPath (Join-Path $PluginRoot 'addons\godot_mcp_toolkit\.mcp.json.template') -Destination $addonRoot
# 写入前置是"随附 shim 存在"（bin/<rid>/godot-mcp-shim[.exe]）。夹具只随附最小 addon，
# 因此这一步交由测试自己按平台落占位文件——插件只做存在性判断，真实 shim 有数十 MB，
# 不值得每次拷贝；缺失分支由测试自行删除该文件来覆盖。
Copy-Item -LiteralPath (Join-Path $repoRoot 'test-project\tests\mcp_config_discovery_test.gd') -Destination (Join-Path $projectRoot 'tests')
if ($IncludeUi) {
    Copy-Item -Path (Join-Path $PluginRoot 'addons\godot_mcp_toolkit\*') -Destination $addonRoot -Exclude '.mimosa' -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'test-project\tests\mcp_config_ui_test.gd') -Destination (Join-Path $projectRoot 'tests')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'test-project\tests\onboarding_locale_test.gd') -Destination (Join-Path $projectRoot 'tests')
}
[IO.File]::WriteAllText((Join-Path $projectRoot 'project.godot'), "config_version=5`n[application]`nconfig/name=`"MCP 配置隔离回归`"`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $projectRoot '.mcp-config-test'), 'isolated', [Text.UTF8Encoding]::new($false))
Write-Output ('隔离目录：' + $fixtureRoot)
# 不启用任何编辑器插件或自动加载，不打开已有工程，也不注册 MCP 端口。
# 因环境问题被容忍的步骤（见 Invoke-IsolatedTest）——末尾统一告警，避免"看着全绿、其实没跑"。
$toleratedFailures = @()
function Invoke-IsolatedTest {
    param(
        [string]$Name,
        [string[]]$TestArguments,
        # 编辑器模式（--import）走本机 .NET/mono 工具链：引擎级 ERROR（mono 编辑器找不到匹配的
        # .NET SDK、user:// 目录、feature_profiles 等）与非零退出都与被测代码无关，只告警不中断。
        # GDScript 的 SCRIPT ERROR 不受此开关影响——任何模式下都是真失败。
        [switch]$EditorMode,
        # 预热 pass：只让 Godot 建立全局类缓存，不回显输出、不做任何断言。
        [switch]$Warmup,
        # 单步超时。编辑器模式的步骤（首次 --import 要建全局类缓存）明显更慢，调用方放宽。
        [int]$TimeoutMs = 30000
    )
    $stdout = Join-Path $fixtureRoot ($Name + '.stdout.log')
    $stderr = Join-Path $fixtureRoot ($Name + '.stderr.log')
    $arguments = @('--headless', '--path', ('"' + $projectRoot + '"')) + $TestArguments
    $process = Start-Process -FilePath $GodotExecutable -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit($TimeoutMs)) {
        $process.Kill()
        throw ('隔离测试超过 ' + [int]($TimeoutMs / 1000) + ' 秒，已停止本次测试进程：' + $Name)
    }
    if (-not $Warmup) {
        if ($Name -notlike 'import*') { Get-Content -LiteralPath $stdout | Write-Host }
        Get-Content -LiteralPath $stderr | Write-Host
    }
    if ($Warmup) { return }
    # GDScript 解析/编译错误先判——它与工具链无关，任何步骤上都是真失败。
    if (Select-String -LiteralPath $stderr -Pattern '^SCRIPT ERROR:' -Quiet) {
        throw ('隔离测试出现脚本错误：' + $Name)
    }
    if ($process.ExitCode -ne 0) {
        if (-not $EditorMode) { exit $process.ExitCode }
        $script:toleratedFailures += "$Name(退出码 $($process.ExitCode))"
        Write-Warning ("$Name 未通过，按环境问题容忍并继续（stderr: $stderr）")
        return
    }
    $engineErrors = @(Select-String -LiteralPath $stderr -Pattern '^ERROR:').Count
    if ($engineErrors -gt 0) {
        if (-not $EditorMode) { throw ('隔离测试出现引擎错误：' + $Name) }
        Write-Warning ("$Name：$engineErrors 条引擎级 ERROR 来自本机工具链（非被测代码），已忽略。")
    }
    if ($Name -eq 'import') {
        Write-Host 'PASS: 隔离副本的全局脚本类扫描完成。'
    } else {
        Write-Host ("PASS: " + $Name)
    }
}
# 夹具预热：Godot 的全局脚本类缓存（.godot/global_script_class_cache.cfg）**只在编辑器模式
# （--import）下建立**，--script 模式跑多少次都不会建。缓存缺席时，任何跨文件引用 class_name
# 的脚本都会报一片 SCRIPT ERROR —— 那是夹具首次运行的假象，不是代码问题（实测：同一夹具在
# import 之前跑 UI 用例得 23 条 SCRIPT ERROR，import 之后为 0）。所以先预热一次；不回显输出、
# 不做断言，失败也不致命（后续步骤会照常暴露真实问题）。
Invoke-IsolatedTest 'fixture-warmup' @('--editor', '--import', '--quit') -EditorMode -Warmup -TimeoutMs 120000

Invoke-IsolatedTest 'discovery' @('--script', 'res://tests/mcp_config_discovery_test.gd')
if ($IncludeUi) {
    # 功能用例：--script 模式，不依赖编辑器工具链。
    Invoke-IsolatedTest 'ui' @('--script', 'res://tests/mcp_config_ui_test.gd')
    Invoke-IsolatedTest 'locales' @('--script', 'res://tests/onboarding_locale_test.gd')
    # 全局脚本类扫描（编辑器模式）。断言只看 SCRIPT ERROR；引擎级 ERROR 来自本机 mono 工具链
    # （找不到匹配的 .NET SDK、user:// 目录等），只告警不判失败。
    Invoke-IsolatedTest 'import' @('--editor', '--import', '--quit') -EditorMode -TimeoutMs 120000
}
if ($toleratedFailures.Count -gt 0) {
    Write-Warning ('以下步骤因环境问题被容忍，未真正完成校验：' + ($toleratedFailures -join '、') + '。其余步骤均通过。')
}
