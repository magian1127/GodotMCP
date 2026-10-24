// DSH 运行时模块的 profile 解析加载(对齐 deepseek-harness-hashline /
// deepseek-harness-zh_pro 的 loadSchemastery 模式):从当前 profile 的
// 模块解析上下文加载 DSH 提供的运行时模块,避免本包显式依赖,同时与
// 主进程拿到同一模块实例。
import { createRequire } from 'node:module'
import { readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

interface ModuleState {
  cache: unknown
  failed: boolean
}

const schemasteryState: ModuleState = { cache: undefined, failed: false }
const mcpClientState: ModuleState = { cache: undefined, failed: false }

/** 桌面版 Host(Electron RunAsNode)argv 不带 --profile,profile 固定为 desktop
 * (apps/desktop/src/paths.ts);此时不能落回 web 默认,否则 createRequire 会
 * 锚到 web profile,拿不到与桌面运行时(asar 内)同实例的 DSH 模块。 */
export function profileNameFrom(argv: readonly string[], electronVersion: string | undefined): string {
  const flag = argv.indexOf('--profile')
  if (flag !== -1 && flag + 1 < argv.length && !argv[flag + 1].startsWith('-')) {
    return argv[flag + 1]
  }
  if (electronVersion !== undefined) return 'desktop'
  return 'web'
}

export function profileName(): string {
  return profileNameFrom(process.argv, process.versions.electron)
}

export function profileDir(): string {
  const home = process.env.DSH_HOME || join(process.env.USERPROFILE || process.env.HOME || '.', '.dsh')
  return join(home, 'profiles', profileName())
}

// DSH 0.1.7-rc 的组合批次经模块 hooks 管线并行动态 import 官方插件的 ESM；
// 同步 require(esm)（包括目标包 CJS 入口内部的 require）会撞 Node 的
// 「not yet fully loaded」：管线被当前同步栈阻塞，重试永远等不到加载完成。
// 因此用 require.resolve 系只做解析（不求值、无竞态），并优先取 exports 的
// import 条目做异步 import：整条依赖链都排进同一条管线串行交付，天然无竞态；
// 顶层 await 保证调用方模块求值前就绪。
function profileEntryPath(requireFromProfile: NodeRequire, name: string): string {
  const requireEntry: string = requireFromProfile.resolve(name)
  try {
    const manifestPath: string = requireFromProfile.resolve(`${name}/package.json`)
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8')) as {
      exports?: Record<string, unknown>
      main?: unknown
    }
    const selfExport: unknown = manifest?.exports?.['.']
    const entry = typeof selfExport === 'string'
      ? selfExport
      : typeof selfExport === 'object' && selfExport !== null
        ? (selfExport as { import?: unknown; default?: unknown }).import ?? (selfExport as { default?: unknown }).default
        : undefined
    if (typeof entry === 'string') return resolve(dirname(manifestPath), entry)
    if (typeof manifest?.main === 'string') return resolve(dirname(manifestPath), manifest.main)
  } catch {
    // exports 不可读时退回 require 条目。
  }
  return requireEntry
}

async function importFromProfile(name: string, state: ModuleState): Promise<void> {
  try {
    const requireFromProfile = createRequire(join(profileDir(), 'package.json'))
    const entry = profileEntryPath(requireFromProfile, name)
    const mod = (await import(pathToFileURL(entry).href)) as { default?: unknown } | null | undefined
    state.cache = mod !== null && mod !== undefined && mod.default !== undefined ? mod.default : mod
  } catch {
    state.cache = undefined
  }
}

// 预载失败保持 cache undefined，由 loadFromProfile 的同步兜底兜住。
// DSH 0.2.1-rc.2 实测：`@deepseek-ai/dsh-mcp-client` 没有 default 导出，聚合面就是
// 整个命名空间（name/inject/Config/apply）——`ctx.plugin()` 需要的正是这个对象。
await importFromProfile('@deepseek-ai/schemastery', schemasteryState)
await importFromProfile('@deepseek-ai/dsh-mcp-client', mcpClientState)

function loadFromProfile(name: string, state: ModuleState): unknown {
  if (state.cache !== undefined) return state.cache
  if (state.failed) return null
  // 同步兜底：仅预载失败后（如脱离 profile 的测试/CLI 环境）首次调用时尝试；
  // 引擎环境再次拒绝即永久标记，避免每次调用都抛错。
  try {
    const mod = createRequire(join(profileDir(), 'package.json'))(name)
    state.cache = mod !== null && mod !== undefined && mod.default !== undefined ? mod.default : mod
  } catch {
    state.failed = true
    return null
  }
  return state.cache
}

export function loadSchemastery(): unknown {
  return loadFromProfile('@deepseek-ai/schemastery', schemasteryState)
}

/**
 * 从当前 profile 解析宿主自带的 MCP 客户端插件（`@deepseek-ai/dsh-mcp-client`）。
 *
 * 返回的是**插件本体**（模块命名空间：name/inject/Config/apply），供
 * `agent.ctx.plugin()` / `ctx.plugin()` 挂载；不是 Config 值。拿不到时返回 null，
 * 调用方降级为"不挂载 + 记一条错误日志"——宁可没有工具，也不自建一份连接。
 */
export function loadMcpClient(): unknown {
  return loadFromProfile('@deepseek-ai/dsh-mcp-client', mcpClientState)
}
