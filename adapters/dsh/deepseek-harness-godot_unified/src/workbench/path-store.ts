// Godot 路径存储(全局 serverDist 单值 + 按工作目录 cwd 的批量项目条目),落到 DSH host 持久层。
//
// 设计:
// - **serverDist 全局单值**:不再按 cwd 区分;`serverDist` 字段即全局唯一的
//   GodotMCP server dist 完整路径。为空时回退「设置卡片」里的 `godotServerDist`
//   (host 侧兜底),再回退 CLI resolve 逻辑(godotMcpRoot + 相对路径 / env)。
// - **项目路径按 cwd 批量条目**:`projects` 是数组,每条 `{ cwd, projectPath }`
//   (cwd=会话工作目录;projectPath=含 project.godot 的 Godot 项目目录)。多条可并存,
//   按 cwd 唯一;切换会话时按其 cwd 在表里匹配 projectPath,无匹配回退全局/默认。
// - 持久载体:一个 JSON 文件 `<DSH_HOME>/godot/paths.json`,形状
//   `{ serverDist?: string, projects: Array<{ cwd: string, projectPath: string }> }`。
//   DSH_HOME 取 `process.env.DSH_HOME`,缺省 `~/.dsh`(与 DSH 数据目录惯例一致)。
// - 惰性加载(首次读/写才读盘)+ 内存缓存;写盘为临时文件 + rename 的原子替换,
//   失败静默(内存仍保留本次有效值,下次写再尝试)。
// - 归一化(参照 zhpro 自定义监控项):结构合法才保留、cwd 去重(后写覆盖)、
//   上限 100,防手改注入脏数据。
// - 不跨进程共享锁:DSH 单 host 进程持有,并发极小,采用最简单模式。
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { basename, dirname, join } from 'node:path'

/** 一条按工作目录 batched 的 Godot 项目路径条目。 */
export interface ProjectEntry {
  /** 会话工作目录(cwd);批量条目按此唯一。 */
  cwd: string
  /** 含 project.godot 的 Godot 项目目录。 */
  projectPath: string
}

/** 工作台路径配置:全局 serverDist 单值 + 按 cwd 的项目条目表。 */
export interface PathConfig {
  serverDist?: string
  projects: ProjectEntry[]
}

/** 归一化后的内部数据形状(projects 恒为数组)。 */
interface NormalizedConfig {
  serverDist?: string
  projects: ProjectEntry[]
}

const MAX_PROJECTS = 100

/** 解析 DSH home:优先 `$DSH_HOME`,否则 `<home>/.dsh`。 */
export function dshHome(): string {
  const env = process.env.DSH_HOME
  return typeof env === 'string' && env.trim() !== '' ? env.trim() : join(homedir(), '.dsh')
}

/** 默认存储文件路径(可由构造参数覆盖,测试注入临时目录)。 */
export const DEFAULT_PATHS_FILE = join(dshHome(), 'godot', 'paths.json')

/** 清洗路径字符串:trim + 剥除首尾成对包裹引号(用户从资源管理器/JSON 复制时常带 `"D:\\...`)。 */
export function cleanPath(value: string): string {
  let out = value.trim()
  if (out.length >= 2) {
    const first = out[0]
    const last = out[out.length - 1]
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) out = out.slice(1, -1).trim()
  }
  return out
}

/**
 * 项目路径归一化:GodotMCP 注册表键是**项目目录**(小写规范路径),而用户常把
 * `project.godot` **文件**填进来(工作台占位符/资源管理器复制)。文件 → 其父目录,
 * 目录原样保留;空返回 ''。桥接/CLI/加载三处共用,避免「no registry entry」类
 * AUTH_FAILED(2026-09 实测:编辑器在场但项目路径带了 project.godot)。
 */
export function projectPathToDir(value: string): string {
  const path = cleanPath(value)
  if (path === '') return ''
  const name = basename(path).toLowerCase()
  return name === 'project.godot' ? dirname(path) : path
}

/** 归一化项目条目:结构合法(非空 cwd/projectPath 字符串)才保留,cwd 去重(后写覆盖),上限 100。 */
export function normalizeProjects(value: unknown): ProjectEntry[] {
  if (!Array.isArray(value)) return []
  const byCwd = new Map<string, ProjectEntry>()
  for (const item of value) {
    if (item === null || typeof item !== 'object' || byCwd.size >= MAX_PROJECTS) continue
    const rec = item as Record<string, unknown>
    const cwd = typeof rec.cwd === 'string' ? cleanPath(rec.cwd) : ''
    const projectPath = typeof rec.projectPath === 'string' ? projectPathToDir(rec.projectPath) : ''
    if (cwd === '' || projectPath === '') continue
    byCwd.set(cwd, { cwd, projectPath })
    if (byCwd.size >= MAX_PROJECTS) break
  }
  return Array.from(byCwd.values())
}

/** 归一化整份配置;serverDist 仅保留非空字符串,projects 走 normalizeProjects。 */
export function normalizeConfig(raw: unknown): NormalizedConfig {
  const source = typeof raw === 'object' && raw !== null ? raw as Record<string, unknown> : {}
  const serverDist = typeof source.serverDist === 'string' && source.serverDist.trim() !== '' ? cleanPath(source.serverDist) : undefined
  return { serverDist, projects: normalizeProjects(source.projects) }
}

export class PathStore {
  private data: NormalizedConfig = { projects: [] }
  private loaded = false
  constructor(private readonly file: string = DEFAULT_PATHS_FILE) {}

  /** 惰性加载一次;文件缺失/损坏 → 空配置(不抛,不影响工作台)。 */
  private load(): void {
    if (this.loaded) return
    this.loaded = true
    try {
      if (existsSync(this.file)) {
        const raw = JSON.parse(readFileSync(this.file, 'utf8')) as unknown
        this.data = normalizeConfig(raw)
      }
    } catch {
      this.data = { projects: [] }
    }
  }

  /** 当前整份配置副本(不改内部状态)。 */
  getConfig(): PathConfig {
    this.load()
    return { serverDist: this.data.serverDist, projects: this.data.projects.map(p => ({ cwd: p.cwd, projectPath: p.projectPath })) }
  }

  /** 全局 server dist 单值;空返回 undefined(回退全局设置)。 */
  getServerDist(): string | undefined {
    this.load()
    return this.data.serverDist
  }

  /** 按 cwd 匹配项目路径;无匹配返回 undefined(回退全局/默认)。 */
  getProjectPath(cwd: string | undefined): string | undefined {
    if (cwd === undefined || cwd === '') return undefined
    this.load()
    const entry = this.data.projects.find(p => p.cwd === cwd)
    return entry?.projectPath
  }

  /** 覆盖写整份配置(全局 serverDist + 批量项目条目);持久到文件。 */
  setConfig(config: PathConfig): void {
    this.load()
    this.data = normalizeConfig(config)
    this.persist()
  }

  /** 写全局 serverDist 单值(保留现有 projects)。 */
  setServerDist(serverDist: string | undefined): void {
    this.load()
    this.data = { serverDist: typeof serverDist === 'string' && serverDist.trim() !== '' ? serverDist.trim() : undefined, projects: this.data.projects }
    this.persist()
  }

  /** 写整份 projects 表(归一化,按 cwd 去重);保留现有 serverDist。 */
  setProjects(projects: ProjectEntry[]): void {
    this.load()
    this.data = { serverDist: this.data.serverDist, projects: normalizeProjects(projects) }
    this.persist()
  }

  private persist(): void {
    try {
      const dir = dirname(this.file)
      mkdirSync(dir, { recursive: true })
      const tmp = `${this.file}.tmp`
      writeFileSync(tmp, JSON.stringify(this.data, null, 2), 'utf8')
      renameSync(tmp, this.file)
    } catch {
      // 写盘失败静默:内存已更新,下次写再尝试;不影响当前会话使用。
    }
  }
}
