// CLI 顶层常量与共享类型。
export const PKG = 'deepseek-harness-godot_unified'

/** mcp-client 桥接行(CLI 写入 profile 用户 patch 层)的默认 id。 */
export const MCP_ROW_ID = 'godot-mcp'

/** 默认 serverName:工具以 godot_<tool> 形式出现。 */
export const SERVER_NAME_DEFAULT = 'godot'

/** bundle patch 行 id(cordis.patch.yml 的 insert 条目,与 host 侧同步)。 */
export const BUNDLE_ROW_ID = 'dsh-godot'

/** 用户 patch 层受管块标记(与 host/CLI 两侧各自独立;tests 有对齐断言)。 */
export const ROW_BEGIN = '# dsh-godot:begin'
export const ROW_END = '# dsh-godot:end'

/** 旧 Node 桥 server 入口在工作区布局中的固定相对路径(Node 桥 2026-09-14 退役,保留作回滚通道)。 */
export const SERVER_DIST_RELATIVE = 'plugin/godot-mcp-unified/server/dist/index.js'

/** daemon HTTP 自举桥入口的固定相对路径(DSH 翻转后的默认 server 入口)。 */
export const BRIDGE_ENTRY_RELATIVE = 'adapters/dsh/godot-http-bridge.mjs'

/** root 推导候选(bridge 优先,legacy 兼容)。 */
export const SERVER_ENTRY_CANDIDATES = [BRIDGE_ENTRY_RELATIVE, SERVER_DIST_RELATIVE]

export const WINDOWS_COMMAND_ENV = 'DSH_GODOT_COMMAND_JSON'
const WINDOWS_COMMAND_SCRIPT = [
  "$ProgressPreference = 'SilentlyContinue'",
  `$raw = $env:${WINDOWS_COMMAND_ENV}`,
  `Remove-Item Env:${WINDOWS_COMMAND_ENV} -ErrorAction SilentlyContinue`,
  '$payload = ConvertFrom-Json -InputObject $raw',
  '$command = [string]$payload.command',
  '$commandArgs = @($payload.args)',
  '& $command @commandArgs',
  '$succeeded = $?',
  '$exitCode = $LASTEXITCODE',
  'if ($null -ne $exitCode) { exit $exitCode }',
  'if (-not $succeeded) { exit 127 }',
].join('; ')
export const WINDOWS_COMMAND_ENCODED: string = Buffer.from(WINDOWS_COMMAND_SCRIPT, 'utf16le').toString('base64')

/** spawn 选项的最小形状(spawnSync 兼容)。 */
export interface SpawnOptions {
  cwd?: string
  env?: NodeJS.ProcessEnv
  stdio?: 'ignore' | 'inherit' | 'pipe'
}

/** spawnSync 结果的最小形状。 */
export interface SpawnResult {
  status: number | null
  error?: NodeJS.ErrnoException
}

/** install 命令解析结果。 */
export interface InstallArgs {
  profile: string
  project: string | null
  serverDist: string | null
  godotMcpRoot: string | null
  serverName: string
  rowId: string
  readOnly: boolean
  rateLimit: number | null
  unsafe: boolean
  editorPort: number | null
  runtimePort: number | null
  timeoutMs: number | null
  link: string | null
  cwd: string | null
}

/** profile package.json 的最小形状。 */
export interface ProfileManifest {
  dependencies?: Record<string, string>
  dsh?: { profile?: { bundles?: string[] } }
}

/** GodotMCP 工具包注册表条目(projects.json by_path 值的最小子集)。 */
export interface RegistryEntry {
  port?: number
  runtime_port?: number | null
  pid?: number
  godot_version?: string
  token_path?: string
}
