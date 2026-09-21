// CLI 参数解析与命令分发(install / uninstall / status)。
// v0.4:本插件使用**自研桥接**——不再写官方 mcp-client 受管块;
// install 把路径写入 `$DSH_HOME/godot/paths.json`(host 侧 PathStore 同源配置),
// uninstall 清空该文件,status 体检配置与编辑器注册表。
import { existsSync, mkdirSync, readFileSync, renameSync, unlinkSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { isAbsolute, dirname, join, resolve } from 'node:path'
import { MCP_ROW_ID, PKG, SERVER_NAME_DEFAULT, type InstallArgs } from './constants.mjs'
import { removeManagedRow } from '../patch-row.mjs'
import { bundlesHasPlugin, pidAlive, portListening, readRegistry, normalizeProjectKey } from './probes.mjs'
import { resolveServerDist, validateGodotProject, validateServerDistFile } from '../resolve.mjs'
import { spawnCommand } from '../spawn.mjs'
import { validateNonNegativeInt, validatePort, validateProfileName, validateRowId, validateServerName } from './validate.mjs'

const USAGE = [
  `用法:`,
  `  dsh-godot install --project <Godot 项目绝对路径> [选项]`,
  `      选项:`,
  `        --profile <name>        目标 profile(默认 web;仅用于状态语义)`,
  `        --server-dist <file>    server dist/index.js 绝对路径(优先级最高)`,
  `        --godot-mcp-root <dir>  GodotMCP 工作区根目录(取 plugin/godot-mcp-unified/server/dist/index.js)`,
  `        --cwd <dir>             绑定的会话工作目录(默认当前目录;按 cwd 匹配生效)`,
  `        --server-name <name>    保留参数(工具命名空间,默认 ${SERVER_NAME_DEFAULT})`,
  `        --row-id <id>           保留参数(patch 行 id,默认 ${MCP_ROW_ID})`,
  `        --link <dir>            同时以 link: 把本包装进 profile bundles(开发安装)`,
  `  dsh-godot uninstall [--profile <name>]`,
  `  dsh-godot status [--profile <name>]`,
  ``,
  `v0.4 起 install 不再写官方 mcp-client 行(自研桥接):路径写入 $DSH_HOME/godot/paths.json,`,
  `由 Godot preset 会话创建时自动加载;正式入口是侧栏「插件」页 → 本包页面路径区(DSH 0.1.6+)。`,
  `server dist 解析顺序:--server-dist > $GODOT_MCP_SERVER_DIST > --godot-mcp-root/$GODOT_MCP_ROOT。`,
].join('\n')

function parseInstallArgs(argv: string[]): InstallArgs {
  const args: InstallArgs = {
    profile: 'web',
    project: null,
    serverDist: null,
    godotMcpRoot: null,
    serverName: SERVER_NAME_DEFAULT,
    rowId: MCP_ROW_ID,
    readOnly: false,
    rateLimit: null,
    unsafe: false,
    editorPort: null,
    runtimePort: null,
    timeoutMs: null,
    link: null,
    cwd: null,
  }
  const valueFlags: Record<string, keyof InstallArgs> = {
    '--profile': 'profile',
    '--project': 'project',
    '--server-dist': 'serverDist',
    '--godot-mcp-root': 'godotMcpRoot',
    '--server-name': 'serverName',
    '--row-id': 'rowId',
    '--rate-limit': 'rateLimit',
    '--editor-port': 'editorPort',
    '--runtime-port': 'runtimePort',
    '--timeout-ms': 'timeoutMs',
    '--link': 'link',
    '--cwd': 'cwd',
  }
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i]
    if (arg === '--read-only') {
      args.readOnly = true
      continue
    }
    if (arg === '--unsafe') {
      args.unsafe = true
      continue
    }
    const field = valueFlags[arg]
    const value = argv[i + 1]
    if (field === undefined || value === undefined || value.startsWith('--')) {
      throw new Error(`无法识别或缺少值的参数: ${arg}`)
    }
    i += 1
    switch (field) {
      case 'profile': args.profile = validateProfileName(value); break
      case 'project': args.project = isAbsolute(value) ? value : resolve(process.cwd(), value); break
      case 'serverDist': args.serverDist = value; break
      case 'godotMcpRoot': args.godotMcpRoot = value; break
      case 'serverName': args.serverName = validateServerName(value); break
      case 'rowId': args.rowId = validateRowId(value); break
      case 'rateLimit': args.rateLimit = validateNonNegativeInt(value, '--rate-limit'); break
      case 'editorPort': args.editorPort = validatePort(value, '--editor-port'); break
      case 'runtimePort': args.runtimePort = validatePort(value, '--runtime-port'); break
      case 'timeoutMs': args.timeoutMs = validateNonNegativeInt(value, '--timeout-ms'); break
      case 'link': args.link = isAbsolute(value) ? value : resolve(process.cwd(), value); break
      case 'cwd': args.cwd = isAbsolute(value) ? value : resolve(process.cwd(), value); break
    }
  }
  return args
}

// ---- paths.json 读写(与 host 侧 PathStore 同源形状;不引入 host 依赖)。 ----

interface PathFileData { serverDist?: string; projects: Array<{ cwd: string; projectPath: string }> }

function godotPathsFile(): string {
  const home = process.env.DSH_HOME ?? join(homedir(), '.dsh')
  return join(home, 'godot', 'paths.json')
}

function readGodotPaths(): PathFileData {
  try {
    if (!existsSync(godotPathsFile())) return { projects: [] }
    const raw = JSON.parse(readFileSync(godotPathsFile(), 'utf8')) as Record<string, unknown>
    const serverDist = typeof raw.serverDist === 'string' && raw.serverDist.trim() !== '' ? raw.serverDist : undefined
    const projects: Array<{ cwd: string; projectPath: string }> = []
    if (Array.isArray(raw.projects)) {
      for (const item of raw.projects) {
        const rec = item as Record<string, unknown>
        const cwd = typeof rec.cwd === 'string' ? rec.cwd.trim() : ''
        const projectPath = typeof rec.projectPath === 'string' ? rec.projectPath.trim() : ''
        if (cwd !== '' && projectPath !== '') projects.push({ cwd, projectPath })
      }
    }
    return { serverDist, projects }
  } catch {
    return { projects: [] }
  }
}

function writeGodotPaths(data: PathFileData): void {
  const path = godotPathsFile()
  mkdirSync(dirname(path), { recursive: true })
  // 临时文件带 pid+时间戳后缀:并发 CLI 实例互不踩踏(与 patch-row writeAtomic 同策略)。
  const tmp = `${path}.tmp-${process.pid}-${Date.now()}`
  try {
    writeFileSync(tmp, JSON.stringify(data, null, 2), 'utf8')
    renameSync(tmp, path)
  } catch (error: unknown) {
    try {
      unlinkSync(tmp)
    } catch {
      // 临时文件已不存在,忽略。
    }
    throw error
  }
}

async function cmdInstall(argv: string[]): Promise<number> {
  const args = parseInstallArgs(argv)
  if (args.project === null) throw new Error('install 需要 --project <Godot 项目绝对路径>')
  const project = validateGodotProject(args.project)
  if (!project.ok) {
    console.error(`[${PKG}] 项目校验失败: ${project.reason}`)
    return 1
  }
  const dist = resolveServerDist({ serverDist: args.serverDist ?? undefined, godotMcpRoot: args.godotMcpRoot ?? undefined, env: process.env })
  if (!dist.ok) {
    console.error(`[${PKG}] ${dist.reason}`)
    return 1
  }
  const distFile = validateServerDistFile(dist.path)
  if (!distFile.ok) {
    console.error(`[${PKG}] ${distFile.reason}`)
    return 1
  }

  // 可选:开发安装 bundle 行(link: 直连工作区目录)。
  if (args.link !== null) {
    const spec = `link:${args.link.replace(/[\\/]+$/, '')}`
    console.log(`[${PKG}] dsh plugin --profile ${args.profile} add ${spec}`)
    // dsh 的选项解析要求 --profile 跟在 plugin 子命令之后。
    const res = spawnCommand('dsh', ['plugin', '--profile', args.profile, 'add', spec], { stdio: 'inherit' })
    if (res.status !== 0) {
      console.error(`[${PKG}] bundle 安装失败(退出码 ${res.status ?? 'null'});可稍后手动安装 bundle`)
    } else if (!bundlesHasPlugin(args.profile)) {
      console.warn(`[${PKG}] 警告: bundles 未包含本插件(链接可能未解析),提示词 section 不会挂载`)
    }
  }

  // v0.4:自研桥接——路径写入 host 侧 PathStore 同源文件($DSH_HOME/godot/paths.json),
  // 由 Godot preset 会话创建时自动加载;不再写官方 mcp-client 行。
  const cwd = args.cwd ?? process.cwd()
  const current = readGodotPaths()
  const projects = current.projects.filter(entry => entry.cwd !== cwd)
  projects.push({ cwd, projectPath: project.path })
  writeGodotPaths({ serverDist: dist.path, projects })
  // 清理历史官方 mcp-client 受管块(自研桥接不使用;残留会双轨)。
  if (removeManagedRow(args.profile)) {
    console.log(`[${PKG}] 已清理旧的官方 mcp-client 桥接行(自研桥接不再需要)`)
  }
  console.log(`[${PKG}] 路径配置已写入 $DSH_HOME/godot/paths.json:serverDist + cwd=${cwd} → ${project.path}`)
  console.log(`[${PKG}] Godot preset 会话创建时桥接自动加载;侧栏「插件」页 → 本包页面可继续按 cwd 增改`)
  console.log(`[${PKG}] 前提:目标 Godot 编辑器已运行且项目装了 godot_mcp_toolkit addon;否则工具调用会返回可行动错误`)
  if (args.readOnly || args.unsafe || args.rateLimit !== null || args.editorPort !== null || args.runtimePort !== null || args.timeoutMs !== null) {
    console.warn(`[${PKG}] 警告: --read-only/--unsafe/--rate-limit/--editor-port/--runtime-port/--timeout-ms 原为官方行 env 参数,v0.4 自研桥接不再写入;相关能力暂由 server 默认/项目设置提供`)
  }
  return 0
}

async function cmdUninstall(argv: string[]): Promise<number> {
  let profile = 'web'
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--profile' && argv[i + 1] !== undefined) {
      profile = validateProfileName(argv[i + 1])
      i += 1
    } else {
      throw new Error(`无法识别的参数: ${argv[i]}`)
    }
  }
  writeGodotPaths({ projects: [] })
  console.log(`[${PKG}] 已清空 $DSH_HOME/godot/paths.json 路径配置(桥接下次调用起不再生效)`)
  const removed = removeManagedRow(profile)
  console.log(removed
    ? `[${PKG}] 已删除历史官方 MCP 桥接行`
    : `[${PKG}] 无历史官方 MCP 桥接行`)
  if (bundlesHasPlugin(profile)) {
    console.log(`[${PKG}] 提示: bundle 行仍在(提示词 section/工作台);移除用 dsh plugin --profile ${profile} remove ${PKG}`)
  }
  return 0
}

async function cmdStatus(argv: string[]): Promise<number> {
  let profile = 'web'
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--profile' && argv[i + 1] !== undefined) {
      profile = validateProfileName(argv[i + 1])
      i += 1
    } else {
      throw new Error(`无法识别的参数: ${argv[i]}`)
    }
  }
  console.log(`[${PKG}] status (profile "${profile}", node ${process.version})`)
  const paths = readGodotPaths()
  if (paths.serverDist === undefined && paths.projects.length === 0) {
    console.log('  路径配置:      未配置(运行 dsh-godot install 或在侧栏「插件」页 → 本包页面填写)')
  } else {
    const distOk = paths.serverDist !== undefined ? validateServerDistFile(paths.serverDist) : null
    console.log(`  server dist:   ${paths.serverDist === undefined ? '(未设置)' : (distOk?.ok ?? false) ? '存在' : '缺失(先构建 GodotMCP server)'} — ${paths.serverDist ?? '-'}`)
    console.log(`  项目条目:      ${paths.projects.length} 条(按 cwd 匹配)`)
    for (const entry of paths.projects) {
      console.log(`    ${entry.cwd} → ${validateGodotProject(entry.projectPath).ok ? '有效' : '无效(缺 project.godot)'} ${entry.projectPath}`)
    }
    if (paths.projects.length > 0) {
      const entry = readRegistry().get(normalizeProjectKey(paths.projects[0]!.projectPath))
      if (entry === undefined) {
        console.log('  编辑器注册表:  无条目(Godot 未开或项目未装 addon;工具调用会返回可行动错误)')
      } else {
        const alive = pidAlive(entry.pid)
        const portOk = entry.port !== undefined ? await portListening(entry.port) : false
        console.log(`  编辑器注册表:  条目存在(godot ${entry.godot_version ?? '?'},port ${entry.port ?? '?'},pid ${entry.pid ?? '?'}${alive === undefined ? '' : alive ? '存活' : '已退出'})`)
        console.log(`  编辑器端口:    ${portOk ? `监听中(${entry.port})` : '未监听(编辑器可能未运行)'}`)
      }
    }
  }
  console.log(`  bundle 行:     ${bundlesHasPlugin(profile) ? '已就绪(提示词 section/工作台随冷启动挂载)' : '未就绪(可选;--link 或 dsh plugin add 安装)'}`)
  return 0
}

export async function main(): Promise<void> {
  const [cmd, ...rest] = process.argv.slice(2)
  try {
    if (cmd === 'install') process.exitCode = await cmdInstall(rest)
    else if (cmd === 'uninstall') process.exitCode = await cmdUninstall(rest)
    else if (cmd === 'status') process.exitCode = await cmdStatus(rest)
    else {
      console.log(`[${PKG}] ${USAGE}`)
      process.exitCode = 2
    }
  } catch (error: unknown) {
    console.error(`[${PKG}] ${error instanceof Error ? error.message : String(error)}`)
    console.error(`[${PKG}] ${USAGE}`)
    process.exitCode = 1
  }
}
