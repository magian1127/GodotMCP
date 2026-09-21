[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
$functionalExtensions = @(
    ".cfg", ".gd", ".js", ".json", ".mjs", ".ps1", ".py", ".ts", ".yaml", ".yml"
)
$authorizationNames = @(
    "LICENSE", "LICENSE.zh-CN.md", "ATTRIBUTIONS.md", "ATTRIBUTIONS.zh-CN.md"
)

# NodeToolTable.json 是冻结的 parity 基准语料(源自 Node 工具定义导出),其
# $schema 标识 URL 是格式标识而非产品路由,与 package-lock.json 同理豁免。
$allFiles = Get-ChildItem -LiteralPath $pluginRoot -Recurse -File | Where-Object {
    $_.FullName -ne $selfPath -and
    $_.FullName -notmatch "\\node_modules\\|\\dist\\|\\.mimosa\\|\\server-dotnet\\(.*\\)?(obj|bin|publish)\\" -and
    $_.Name -notin @("package-lock.json", "NodeToolTable.json")
}
$productFiles = $allFiles | Where-Object { $_.Name -notin $authorizationNames }
$functionalFiles = $productFiles | Where-Object { $_.Extension -in $functionalExtensions }
$productTextFiles = $productFiles | Where-Object {
    $_.Extension -in ($functionalExtensions + @(".md", ".txt"))
}

$failures = [Collections.Generic.List[string]]::new()
$upstreamVendor = "npgame" + "dev"
$upstreamRoutePattern = "(?i)@?$upstreamVendor|github\.com|gist\.github"

function Add-Matches {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IEnumerable]$Files,
        [Parameter(Mandatory = $true)]
        [string]$Pattern,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )
    foreach ($file in $Files) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match $Pattern) {
            $relative = $file.FullName.Substring($pluginRoot.Length + 1)
            $failures.Add("${Label}: $relative")
        }
    }
}

# 可执行/配置类文件一律不允许出现远程 URL。包锁定文件(package-lock.json)除外：
# 其依赖完整性记录包含注册表(registry)源 URL，但不属于产品运行时路由。
# W3C 命名空间标识符（如 SVG 夹具的 xmlns="http://www.w3.org/2000/svg"）是
# 格式标识而非远程路由，予以豁免。回环地址(daemon HTTP 面 http://127.0.0.1:6590/)
# 是本机服务而非远程路由，同样豁免。
$loopbackExempt = "(?!www\.w3\.org/|127\.0\.0\.1|localhost[:/])"
$externalUrlPattern = "(?i)https?://$loopbackExempt"
Add-Matches $functionalFiles $externalUrlPattern "external URL in functional file"
Add-Matches $functionalFiles $upstreamRoutePattern "upstream route in functional file"
Add-Matches $productTextFiles $externalUrlPattern "external URL outside authorization files"

# 产品文案/资源中，上游(upstream)信息只允许出现在授权声明文件内。
Add-Matches $productTextFiles $upstreamRoutePattern "upstream attribution outside authorization files"

$addonRoot = Join-Path $pluginRoot "addons\godot_mcp_toolkit"
$templatePath = Join-Path $addonRoot ".mcp.json.template"
$templateText = [IO.File]::ReadAllText($templatePath)
if ($templateText -match "(?i)\bnpx\b|@$upstreamVendor") {
    $failures.Add("project MCP template contains a remote package launcher")
}
$template = $templateText | ConvertFrom-Json -AsHashtable
$templateEntry = $template.mcpServers["godot"]
if ($templateEntry.type -ne "http" -or $templateEntry.url -notmatch "^http://127\.0\.0\.1:\d+/$") {
    $failures.Add("project MCP template does not point at the local daemon HTTP face")
}

$catalogPath = Join-Path $addonRoot "extensions\catalog.json"
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    $failures.Add("bundled local extension catalog is missing")
}
$uiFiles = Get-ChildItem -LiteralPath (Join-Path $addonRoot "ui") -Recurse -File -Filter "*.gd"
Add-Matches $uiFiles "(?i)HTTPRequest|CATALOG_URL|repo_url|https?://$loopbackExempt" "remote-capable Godot UI"

if ($failures.Count -gt 0) {
    $failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Local-only policy failed with $($failures.Count) finding(s)."
}

Write-Host "PASS: local-only policy — no external product routes; upstream attribution is isolated"
