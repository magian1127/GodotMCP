# 把 ZCode 的 godot-mcp-unified MCP 注册翻到 daemon 的 HTTP 面(issue 01/18 前置)。
#
# 做四件事(幂等,可重复执行):
#   1. Release 发布 daemon(win-x64 自包含单文件)并同步到边车复制安装位;
#   2. 确保机器级单例 daemon 在跑(端口探活,缺席则以发布物拉起,长空闲阈值);
#   3. 读取稳定 token,把用户级 ZCode 配置(~/.zcode/cli/config.json → mcp.servers)
#      的 godot 条目写为 type=http + Authorization 头——真值只落本机
#      用户配置,不入版本控制;插件 .mcp.json 保持占位符形态(user 级覆盖插件级同名条目);
#      旧 godot-mcp-unified 条目在场时一并移除(键迁移)。
#   4. 打印"重启 ZCode"提示。
#
# 回滚: -Restore 注销用户级 HTTP 注册(回到插件层占位符状态);Node 桥已于 2026-09-14
#       退役(issue 19),整体回退走 git tag node-bridge-final。
#
# 用法(需 PowerShell 7+):
#   pwsh adapters/zcode/install-http-face.ps1
#   pwsh adapters/zcode/install-http-face.ps1 -SkipPublish
#   pwsh adapters/zcode/install-http-face.ps1 -Restore
[CmdletBinding()]
param(
    # 跳过发布(已发布过/只想重写 mcp 注册时用)。
    [switch]$SkipPublish,
    # 注销用户级 HTTP 注册(回滚)。
    [switch]$Restore,
    # 安装器拉起的 daemon 空闲阈值(秒)。默认一天,保证重启 ZCode 期间 daemon 存活;
    # 长期运行由 Godot 边车/宿主管理生命周期。
    [int]$IdleSeconds = 86400
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$pluginDir = Join-Path $repoRoot "plugin\godot-mcp-unified"
$serverDotnet = Join-Path $pluginDir "server-dotnet"
$publishExe = Join-Path $serverDotnet "publish\win-x64\godot-mcp-daemon.exe"
$sidecarBinDir = Join-Path $pluginDir "addons\godot_mcp_toolkit\bin\win-x64"
$registryDir = Join-Path $env:APPDATA "godot-mcp-toolkit"
$tokenFile = Join-Path $registryDir "daemon-token"
$zcodeConfig = Join-Path $env:USERPROFILE ".zcode\cli\config.json"
$serverKey = "godot"
# 旧 server key(2026-10 简化前):迁移时从用户级配置移除,避免新旧两个服务器面并存。
$legacyServerKey = "godot-mcp-unified"
$daemonPort = 6590

function Invoke-DaemonPublish {
    Write-Host "── 发布 daemon(win-x64 自包含单文件)…"
    dotnet publish (Join-Path $serverDotnet "src\godot-mcp-daemon") -c Release -p:PublishProfile=win-x64 --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败: $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $publishExe)) { throw "发布产物缺失: $publishExe" }

    Write-Host "── 同步到边车复制安装位…"
    New-Item -ItemType Directory -Force -Path $sidecarBinDir | Out-Null
    Copy-Item (Join-Path (Split-Path $publishExe) "*") $sidecarBinDir -Force
    Write-Host "   $sidecarBinDir"
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
        Write-Host "── daemon 已在 $daemonPort 监听,跳过拉起。"
        return
    }
    if (-not (Test-Path -LiteralPath $publishExe)) { throw "daemon 可执行文件缺失: $publishExe" }
    Write-Host "── 拉起 daemon(空闲阈值 $IdleSeconds 秒,后台驻留)…"
    # Start-Process(ShellExecute)让子进程获得自己的隐藏控制台:不继承本进程的
    # stdout/stderr 管道 —— 否则安装器退出后无人排水,daemon 的控制台日志写满
    # 管道缓冲会被永久阻塞。环境变量由子进程继承当前 shell。
    $env:GODOT_MCP_DAEMON_PORT = "$daemonPort"
    $env:GODOT_MCP_DAEMON_IDLE_SECONDS = "$IdleSeconds"
    $daemonProcess = Start-Process -FilePath $publishExe -WindowStyle Hidden -PassThru

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

function Set-UserRegistration {
    param([string]$Token)
    $config = Read-ZcodeConfig
    if (-not $config.Contains("mcp")) { $config["mcp"] = [ordered]@{} }
    $mcp = $config["mcp"]
    if (-not ($mcp -is [System.Collections.IDictionary])) { throw "用户配置 mcp 节不是对象: $zcodeConfig" }
    if (-not $mcp.Contains("servers")) { $mcp["servers"] = [ordered]@{} }
    $servers = $mcp["servers"]
    if (-not ($servers -is [System.Collections.IDictionary])) { throw "用户配置 mcp.servers 节不是对象: $zcodeConfig" }

    $current = $servers.Contains($serverKey) ? $servers[$serverKey] : $null
    if ($current -is [System.Collections.IDictionary] -and $current["type"] -eq "http" -and
        $current["url"] -eq "http://127.0.0.1:$daemonPort/" -and
        $current["headers"] -is [System.Collections.IDictionary]) {
        Write-Host "── 用户级注册已是 HTTP 面,刷新 token 后跳过。"
        $current["headers"]["Authorization"] = "Bearer $Token"
    } else {
        $servers[$serverKey] = [ordered]@{
            type = "http"
            url = "http://127.0.0.1:$daemonPort/"
            headers = [ordered]@{ Authorization = "Bearer $Token" }
            timeoutMs = 120000
        }
        Write-Host "── 用户级注册已写入 HTTP 面(插件 .mcp.json 保持占位符,user 级覆盖插件级)。"
    }
    # 键迁移:旧 godot-mcp-unified 条目在场时移除,防止客户端出现重复服务器面。
    if ($servers.Contains($legacyServerKey)) {
        $servers.Remove($legacyServerKey) | Out-Null
        Write-Host "── 已从用户级配置移除旧注册 $legacyServerKey(迁移为 $serverKey)。"
    }
    Write-ZcodeConfig -Config $config
    Write-Host "   $zcodeConfig"
}

function Remove-UserRegistration {
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
        Write-Host "── 已注销用户级 HTTP 注册($($removed -join ', '))(回到插件层占位符状态,重启 ZCode 生效)。"
    } else {
        Write-Host "── 用户级无 godot / godot-mcp-unified 注册,无需注销。"
    }
}

if ($Restore) {
    Remove-UserRegistration
    Write-Host "   Node 桥已于 2026-09-14 退役(issue 19);整体回退用 git tag node-bridge-final,"
    Write-Host "   各 host 还原步骤见 .scratch/godot-mcp-daemon/issues/18-adapters-flipover.md。"
    return
}

if (-not $SkipPublish) {
    Invoke-DaemonPublish
} else {
    Write-Host "── 跳过发布(-SkipPublish)。"
}
Start-DetachedDaemon
$token = Get-DaemonToken
Set-UserRegistration -Token $token

# Codex 的 url 型 server 经环境变量取 Bearer token(用户级持久化;值 = 稳定 token)。
[Environment]::SetEnvironmentVariable("GODOT_MCP_DAEMON_TOKEN", $token, "User")
Write-Host "── 已写入用户环境变量 GODOT_MCP_DAEMON_TOKEN(Codex url 型 server 认证用)。"

Write-Host ""
Write-Host "完成。请重启 ZCode —— 重启后 godot 服务器将经 HTTP(127.0.0.1:$daemonPort) 提供 MCP 服务。"
Write-Host "回滚: pwsh adapters/zcode/install-http-face.ps1 -Restore"
