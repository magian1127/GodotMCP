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
Copy-Item -LiteralPath (Join-Path $PluginRoot 'addons\godot_mcp_toolkit\.mcp.json.template') -Destination $addonRoot
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
function Invoke-IsolatedTest {
    param([string]$Name, [string[]]$TestArguments)
    $stdout = Join-Path $fixtureRoot ($Name + '.stdout.log')
    $stderr = Join-Path $fixtureRoot ($Name + '.stderr.log')
    $arguments = @('--headless', '--path', ('"' + $projectRoot + '"')) + $TestArguments
    $process = Start-Process -FilePath $GodotExecutable -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw '隔离测试超过 30 秒，已停止本次测试进程。'
    }
    if ($Name -ne 'import') { Get-Content -LiteralPath $stdout | Write-Host }
    Get-Content -LiteralPath $stderr | Write-Host
    if ($process.ExitCode -ne 0) { exit $process.ExitCode }
    if (Select-String -LiteralPath $stderr -Pattern '^(SCRIPT ERROR|ERROR):' -Quiet) {
        throw ('隔离测试出现引擎错误：' + $Name)
    }
    if ($Name -eq 'import') { Write-Host 'PASS: 隔离副本的全局脚本类扫描完成。' }
}
Invoke-IsolatedTest 'discovery' @('--script', 'res://tests/mcp_config_discovery_test.gd')
if ($IncludeUi) {
    # 扫描独立副本的全局脚本类；project.godot 中没有启用插件或自动加载。
    Invoke-IsolatedTest 'import' @('--editor', '--import', '--quit')
    Invoke-IsolatedTest 'ui' @('--script', 'res://tests/mcp_config_ui_test.gd')
    Invoke-IsolatedTest 'locales' @('--script', 'res://tests/onboarding_locale_test.gd')
}
