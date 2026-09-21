// 工具清单缓存(持久,跨会话/跨重启):连接成功后从 GodotMCP server 获取的
// `tools/list` 快照,供「打开 DSH 但不需要 Godot」时离线提供工具 schema——
// 模型先看到工具(来自缓存),真正执行时才拉起桥接连接编辑器(零闲置开销)。
//
// 设计:
// - 载体:一个 JSON 文件 `<DSH_HOME>/godot/tools-cache.json`,形状
//   `{ fetchedAt, serverDist?, projectPath?, tools: Array<{ name, description?, inputSchema? }> }`。
//   与生效路径绑定:加载方比对当前解析到的 serverDist/projectPath,不一致视为陈旧
//   (连接成功后刷新)。
// - 惰性加载 + 内存缓存;写盘为临时文件 + rename 的原子替换,失败静默。
// - 归一化:仅保留合法条目(name 非空字符串),上限 500(远超 19+31 组工具面,防脏数据膨胀)。
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'

/** 一条被缓存的 Godot 工具定义(server `tools/list` 快照。inputSchema 为 JSON 对象)。 */
export interface CachedTool {
  name: string
  description?: string
  inputSchema?: unknown
}

/** 工具清单缓存文件形状。 */
export interface ToolsCacheFile {
  fetchedAt: string
  /** 生成该快照时的 server dist(与生效路径比对用)。 */
  serverDist?: string
  /** 生成该快照时的项目路径(工具面可能按项目/版本门控不同)。 */
  projectPath?: string
  tools: CachedTool[]
}

const MAX_TOOLS = 500

/** 解析 DSH home:优先 `$DSH_HOME`,否则 `<home>/.dsh`。 */
export function dshHome(): string {
  const env = process.env.DSH_HOME
  return typeof env === 'string' && env.trim() !== '' ? env.trim() : join(homedir(), '.dsh')
}

/** 默认缓存文件路径(可由构造参数覆盖,测试注入临时目录)。 */
export const DEFAULT_TOOLS_CACHE_FILE = join(dshHome(), 'godot', 'tools-cache.json')

/** 归一化缓存文件:结构非法/字段缺失时安全回退(空缓存)。 */
export function normalizeToolsCache(raw: unknown): ToolsCacheFile | undefined {
  const source = typeof raw === 'object' && raw !== null ? raw as Record<string, unknown> : null
  if (source === null || !Array.isArray(source.tools)) return undefined
  const tools: CachedTool[] = []
  for (const item of source.tools) {
    if (tools.length >= MAX_TOOLS) break
    if (item === null || typeof item !== 'object') continue
    const rec = item as Record<string, unknown>
    const name = typeof rec.name === 'string' && rec.name.trim() !== '' ? rec.name : ''
    if (name === '') continue
    tools.push({
      name,
      ...typeof rec.description === 'string' ? { description: rec.description } : {},
      ...rec.inputSchema !== undefined ? { inputSchema: rec.inputSchema } : {},
    })
  }
  if (tools.length === 0) return undefined
  return {
    fetchedAt: typeof source.fetchedAt === 'string' ? source.fetchedAt : '',
    serverDist: typeof source.serverDist === 'string' && source.serverDist !== '' ? source.serverDist : undefined,
    projectPath: typeof source.projectPath === 'string' && source.projectPath !== '' ? source.projectPath : undefined,
    tools,
  }
}

export class ToolsCacheStore {
  private cache: ToolsCacheFile | undefined
  private loaded = false

  constructor(private readonly file: string = DEFAULT_TOOLS_CACHE_FILE) {}

  /** 惰性加载一次;文件缺失/损坏 → undefined(不抛)。 */
  private load(): void {
    if (this.loaded) return
    this.loaded = true
    try {
      if (existsSync(this.file)) {
        this.cache = normalizeToolsCache(JSON.parse(readFileSync(this.file, 'utf8')))
      }
    } catch {
      this.cache = undefined
    }
  }

  /** 读取当前缓存快照(未加载过/损坏 → undefined)。 */
  get(): ToolsCacheFile | undefined {
    this.load()
    return this.cache
  }

  /** 覆盖写缓存快照(工具面来自 server 的成功连接);持久到文件。 */
  save(input: { serverDist?: string; projectPath?: string; tools: CachedTool[] }): void {
    this.load()
    const trimmed = normalizeToolsCache({
      fetchedAt: new Date().toISOString(),
      serverDist: input.serverDist,
      projectPath: input.projectPath,
      tools: input.tools,
    })
    this.cache = trimmed
    if (trimmed === undefined) return
    try {
      const dir = dirname(this.file)
      mkdirSync(dir, { recursive: true })
      const tmp = `${this.file}.tmp`
      writeFileSync(tmp, JSON.stringify(trimmed, null, 2), 'utf8')
      renameSync(tmp, this.file)
    } catch {
      // 写盘失败静默:内存已更新,下次写再尝试;不影响当前会话使用。
    }
  }

  /** 仅清内存(不删文件;用于测试/诊断)。 */
  reset(): void {
    this.cache = undefined
    this.loaded = false
  }
}
