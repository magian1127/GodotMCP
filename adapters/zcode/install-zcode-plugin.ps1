# 让 ZCode 以 stdio + 随包 shim 接入 Godot MCP Unified（幂等，可重复执行）。
#
# 做四件事：
#   1. Release 发布服务产物到 server-dotnet/publish/<rid>/——本机唯一落点
#      （godot-mcp-daemon[.exe] + godot-mcp-shim[.exe]，同目录）；
#   2. 校验插件根契约 `.mcp.json` 的 command 指向该 shim：ZCode 读插件根的
#      `.mcp.json`，其中 `${ZCODE_PLUGIN_ROOT}` 由 ZCode 展开为插件安装目录；
#      该契约为 **stdio 型**，由 shim 确保机器级单例 daemon 在跑再把 stdio 转发到
#      daemon 的 HTTP 面——因此不需要 token，也不要求 daemon 先被别处拉起；
#   3. 从用户级 ZCode 配置（~/.zcode/cli/config.json → mcp.servers）**移除** godot
#      条目：user 级同名条目会盖住插件级契约，旧形态（http 型 + Bearer）会让 ZCode
#      继续走 HTTP 面而不是 stdio + shim。其它服务器条目与其它顶层键原样保留
#      （`-Restore` 可把 http 形态写回）。
#   4. 拉起机器级 daemon 做预热（shim 本身也能自举，这一步只是让首个调用即时可用），
#      并刷新用户环境变量 GODOT_MCP_DAEMON_TOKEN（HTTP 型消费者用，例如 DSH 的
#      adapters/dsh/godot-http-bridge.mjs 兜底桥）。
#
# 注意（2026-09-23 实测）：第 4 步预热拉起的 daemon 会**继承本进程的作业对象(Job)**，
# 而 Job 成员身份在创建后无法脱离。若从 IDE 的内置终端运行本脚本（VS Code 集成终端等，
# 它通常是 IDE 那个 Job 的成员），而该 Job 带 KILL_ON_JOB_CLOSE，那么 daemon 会随 IDE
# 关闭一起被杀 —— 其他宿主会突然失去服务。**请从普通终端运行本脚本**，或改由编辑器边车
# （daemon_sidecar.gd，autostart 默认开启）与机器级开机自启拉起 daemon。
# daemon 启动时会做只读自检，命中该情况会在 daemon.log 记一条 Warning，可用它确认。
#
# 用法（需 PowerShell 7+）：
#   pwsh adapters/zcode/install-zcode-plugin.ps1
#   pwsh adapters/zcode/install-zcode-plugin.ps1 -SkipPublish
#   pwsh adapters/zcode/install-zcode-plugin.ps1 -Restore    # 写回用户级 http 注册（回滚）
[CmdletBinding()]
param(
    # 跳过发布（已发布过/只想重写注册时用）。
    [switch]$SkipPublish,
    # 回滚：把用户级 godot 条目写回 http 型（插件级 stdio 契约不动）。
    [switch]$Restore,
    # 预热拉起的 daemon（默认常驻：空闲自退默认关闭，见 ADR-0004 2026-09-23 修订）。
    [int]$IdleSeconds = 0
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$pluginDir = Join-Path $repoRoot "plugin\godot-mcp-unified"
$serverDotnet = Join-Path $pluginDir "server-dotnet"

# 当前平台的 .NET RID（与 addon 侧 paths/platform_rid.gd、发布档同名同义）。
function Get-CurrentRid {
    if ($IsWindows) { return "win-x64" }
    if ($IsMacOS) { return $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "osx-arm64" } else { "osx-x64" }) }
    return "linux-x64"
}
$rid = Get-CurrentRid
$exeSuffix = $(if ($IsWindows) { ".exe" } else { "" })
$publishDir = Join-Path $serverDotnet "publish\$rid"
$daemonExe = Join-Path $publishDir ("godot-mcp-daemon" + $exeSuffix)
$shimExe = Join-Path $publishDir ("godot-mcp-shim" + $exeSuffix)
$pluginContract = Join-Path $pluginDir ".mcp.json"
$registryDir = Join-Path $env:APPDATA "godot-mcp-toolkit"
$tokenFile = Join-Path $registryDir "daemon-token"
$zcodeConfig = Join-Path $env:USERPROFILE ".zcode\cli\config.json"
$serverKey = "godot"
# 旧 server key(2026-10 简化前):迁移时从用户级配置移除,避免新旧两个服务器面并存。
$legacyServerKey = "godot-mcp-unified"
$daemonPort = 6590

function Invoke-ServerPublish {
    Write-Host "── 发布服务产物（$rid，自包含单文件）…"
    foreach ($project in @("godot-mcp-daemon", "godot-mcp-shim")) {
        dotnet publish (Join-Path $serverDotnet "src\$project") -c Release -p:PublishProfile=$rid --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project 失败: $LASTEXITCODE" }
    }
    if (-not (Test-Path -LiteralPath $shimExe)) { throw "发布产物缺失: $shimExe" }
    if (-not (Test-Path -LiteralPath $daemonExe)) { throw "发布产物缺失: $daemonExe（shim 缺省从自身同目录解析 daemon）" }
    Write-Host "   落点（本机唯一）：$publishDir"
}

# 校验插件根 .mcp.json 是 stdio 型且 command 指向随包 shim——ZCode 会读它，
# 契约与产物不一致时 ZCode 起不来，必须在这里报出来。
function Assert-PluginContract {
    if (-not (Test-Path -LiteralPath $pluginContract)) { throw "插件契约缺失: $pluginContract" }
    $contract = Get-Content -LiteralPath $pluginContract -Raw | ConvertFrom-Json -AsHashtable
    if (-not ($contract -is [System.Collections.IDictionary]) -or -not $contract.Contains("mcpServers")) {
        throw "插件契约缺少 mcpServers: $pluginContract"
    }
    $servers = $contract["mcpServers"]
    if (-not ($servers -is [System.Collections.IDictionary]) -or -not $servers.Contains("godot")) {
        throw "插件契约缺少 mcpServers.godot: $pluginContract"
    }
    $entry = $servers["godot"]
    if ($entry["type"] -ne "stdio") { throw "插件契约不是 stdio 型（type=$($entry['type'])）: $pluginContract" }
    $command = [string]$entry["command"]
    if ($command -notmatch "godot-mcp-shim") {
        throw "插件契约的 command 未指向随包 shim: $command"
    }
    $expectedSuffix = "server-dotnet/publish/$rid/godot-mcp-shim$exeSuffix"
    if ($command.Replace('\', '/') -notlike "*$expectedSuffix") {
        Write-Warning "插件契约的 command（$command）与本机 RID 的落点后缀不一致（期望 *$expectedSuffix）；非本平台产物时请忽略。"
    }
    Write-Host "── 插件契约已校验（stdio + 随包 shim）：$pluginContract"
}

function Test-DaemonPort {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync("127.0.0.1", $daemonPort)
        if (-not $task.Wait(500)) { return $false }
        return $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Start-DetachedDaemon {
    if (Test-DaemonPort) {
        Write-Host "── daemon 已在 $daemonPort 监听，跳过预热拉起。"
        return
    }
    if (-not (Test-Path -LiteralPath $daemonExe)) { throw "daemon 可执行文件缺失: $daemonExe" }
    $idleText = $(if ($IdleSeconds -gt 0) { "空闲 $IdleSeconds 秒后自退" } else { "不自退（常驻）" })
    Write-Host "── 预热拉起 daemon（$idleText，后台驻留）…"
    # Start-Process(ShellExecute)让子进程获得自己的隐藏控制台:不继承本进程的
    # stdout/stderr 管道 —— 否则安装器退出后无人排水,daemon 的控制台日志写满
    # 管道缓冲会被永久阻塞。环境变量由子进程继承当前 shell。
    $env:GODOT_MCP_DAEMON_PORT = "$daemonPort"
    # 0 = 禁用自退（默认）；-IdleSeconds 给正数才启用（见 ADR-0004 2026-09-23 修订）。
    $env:GODOT_MCP_DAEMON_IDLE_SECONDS = "$IdleSeconds"
    $daemonProcess = Start-Process -FilePath $daemonExe -WindowStyle Hidden -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((-not (Test-DaemonPort)) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    if (-not (Test-DaemonPort)) { throw "daemon 未在 20s 内监听 $daemonPort" }
    Write-Host "   daemon 已监听 127.0.0.1:$daemonPort (pid=$($daemonProcess.Id))"
}

function Get-DaemonToken {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $tokenFile) {
            $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
            if ($token.Length -gt 0) { return $token }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "稳定 token 未就绪: $tokenFile"
}

function Read-ZcodeConfig {
    if (Test-Path -LiteralPath $zcodeConfig) {
        return (Get-Content -LiteralPath $zcodeConfig -Raw | ConvertFrom-Json -AsHashtable)
    }
    return [ordered]@{}
}

function Write-ZcodeConfig {
    param([System.Collections.IDictionary]$Config)
    # temp+rename 原子写:ZCode 可能并发读写同一路径。
    $tempPath = "$zcodeConfig.tmp"
    ($Config | ConvertTo-Json -Depth 32) | Set-Content -LiteralPath $tempPath -NoNewline
    Move-Item $tempPath $zcodeConfig -Force
}

# 回滚用：把用户级 godot 条目写回 http 型（需要 daemon 在跑 + 稳定 token）。
function Set-UserRegistration {
    param([string]$Token)
    $config = Read-ZcodeConfig
    if (-not $config.Contains("mcp")) { $config["mcp"] = [ordered]@{} }
    $mcp = $config["mcp"]
    if (-not ($mcp -is [System.Collections.IDictionary])) { throw "用户配置 mcp 节不是对象: $zcodeConfig" }
    if (-not $mcp.Contains("servers")) { $mcp["servers"] = [ordered]@{} }
    $servers = $mcp["servers"]
    if (-not ($servers -is [System.Collections.IDictionary])) { throw "用户配置 mcp.servers 节不是对象: $zcodeConfig" }

    $servers[$serverKey] = [ordered]@{
        type = "http"
        url = "http://127.0.0.1:$daemonPort/"
        headers = [ordered]@{ Authorization = "Bearer $Token" }
        timeoutMs = 120000
    }
    if ($servers.Contains($legacyServerKey)) { $servers.Remove($legacyServerKey) | Out-Null }
    Write-ZcodeConfig -Config $config
    Write-Host "── 已写回用户级 http 注册（回滚态）。"
    Write-Host "   $zcodeConfig"
}

# 迁移：移除用户级 godot / godot-mcp-unified 条目，让插件级 stdio 契约生效。
# 其它服务器条目与其它顶层键原样保留。
function Remove-UserRegistration {
    if (-not (Test-Path -LiteralPath $zcodeConfig)) {
        Write-Host "── 用户级配置不存在，无需迁移：$zcodeConfig"
        return
    }
    $config = Read-ZcodeConfig
    $servers = $config.Contains("mcp") -and $config["mcp"] -is [System.Collections.IDictionary] ? $config["mcp"]["servers"] : $null
    $removed = @()
    if ($servers -is [System.Collections.IDictionary]) {
        foreach ($key in @($serverKey, $legacyServerKey)) {
            if ($servers.Contains($key)) {
                $servers.Remove($key) | Out-Null
                $removed += $key
            }
        }
    }
    if ($removed.Count -gt 0) {
        Write-ZcodeConfig -Config $config
        Write-Host "── 已从用户级配置移除 $($removed -join ', ')（user 级同名条目会盖住插件级契约；其它条目保留）。"
    } else {
        Write-Host "── 用户级无 godot / godot-mcp-unified 条目，无需迁移。"
    }
}

if ($Restore) {
    Start-DetachedDaemon
    $token = Get-DaemonToken
    Set-UserRegistration -Token $token
    Write-Host "   回滚完成：ZCode 将经 HTTP(127.0.0.1:$daemonPort) 读取 MCP 服务；插件级 stdio 契约仍在（user 级优先）。"
    return
}

if (-not $SkipPublish) {
    Invoke-ServerPublish
} else {
    Write-Host "── 跳过发布（-SkipPublish）。"
}

Assert-PluginContract
Remove-UserRegistration
Start-DetachedDaemon
$token = Get-DaemonToken

# HTTP 型消费者经该用户级环境变量取 Bearer token（值 = 机器级稳定 token）：
# DSH 的 adapters/dsh/godot-http-bridge.mjs 默认读它，其它 url 型宿主同源。
# ZCode 与 Codex 现在是 stdio + 随包 shim，由 shim 自行从注册表取 token，不用本变量。
[Environment]::SetEnvironmentVariable("GODOT_MCP_DAEMON_TOKEN", $token, "User")
Write-Host "── 已刷新用户环境变量 GODOT_MCP_DAEMON_TOKEN（HTTP 型消费者用，如 DSH bridge）。"

Write-Host ""
Write-Host "完成。请重启 ZCode —— 重启后 godot 服务器经插件根 .mcp.json 以 stdio 方式接入："
Write-Host "  command = `${ZCODE_PLUGIN_ROOT}/server-dotnet/publish/$rid/godot-mcp-shim$exeSuffix"
Write-Host "  （shim 自举机器级 daemon，无需 token；daemon 已在 127.0.0.1:$daemonPort 预热就绪）"
Write-Host "回滚: pwsh adapters/zcode/install-zcode-plugin.ps1 -Restore"
