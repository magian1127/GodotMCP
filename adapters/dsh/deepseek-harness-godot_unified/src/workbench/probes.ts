// 工作台 status 探测:GodotMCP 注册表/端口/pid/server dist——CLI 探测逻辑 host 化。
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createConnection } from 'node:net'

export interface RegistryEntryShape {
  port?: number
  runtime_port?: number | null
  pid?: number
  godot_version?: string
}

export interface EditorStatus {
  project: string
  port: number | null
  pid: number | null
  godotVersion: string | null
  alive: boolean
  listening: boolean
}

/** 注册表目录(与 server src/registry.ts 的配方一致)。 */
export function registryDir(): string {
  if (process.platform === 'win32') {
    const appData = process.env.APPDATA ?? join(homedir(), 'AppData', 'Roaming')
    return join(appData, 'godot-mcp-toolkit')
  }
  if (process.platform === 'darwin') {
    return join(homedir(), 'Library', 'Application Support', 'godot-mcp-toolkit')
  }
  const xdg = process.env.XDG_DATA_HOME ?? join(homedir(), '.local', 'share')
  return join(xdg, 'godot-mcp-toolkit')
}

/** 旧布局注册表路径(projects.json;仅为兼容回退)。 */
export function registryFilePath(): string {
  return join(registryDir(), 'projects.json')
}

/** 与 server normalizePath 一致的键规范化。 */
export function normalizeProjectKey(path: string): string {
  let key = path.replace(/\\/g, '/')
  while (key.length > 1 && key.endsWith('/')) key = key.slice(0, -1)
  if (process.platform === 'win32' || process.platform === 'darwin') key = key.toLowerCase()
  return key
}

/**
 * 读取全部注册表条目;缺失/损坏返回空表。
 *
 * Godot 插件(上游 addon)的写入侧布局已迁移为
 * `entries/<hash>.json`(每条含 `_key` 小写规范路径),同时仍重建聚合
 * `projects.json`(`by_path{}`)作为旧布局读模型。上游 server 与这里一致地
 * 两者都兼容:**先读旧 `projects.json` by_path,再扫 `entries/*.json` 并以其
 * `_key` 覆盖**,使新布局优先而旧用户/旧版本不回归;任一损坏都不属于
 * 本插件故障面。
 */
export function readRegistryEntries(): Map<string, RegistryEntryShape> {
  const map = new Map<string, RegistryEntryShape>()
  const dir = registryDir()
  const legacy = join(dir, 'projects.json')
  if (existsSync(legacy)) {
    try {
      const parsed = JSON.parse(readFileSync(legacy, 'utf8')) as { by_path?: Record<string, RegistryEntryShape> }
      for (const [key, entry] of Object.entries(parsed.by_path ?? {})) {
        map.set(normalizeProjectKey(key), entry)
      }
    } catch { /* 注册表损坏不属于本插件故障面 */ }
  }
  const entriesDir = join(dir, 'entries')
  if (existsSync(entriesDir)) {
    try {
      for (const name of readdirSync(entriesDir)) {
        // 仅编辑器条目(<hash>.json);跳过运行时条目(<hash>.runtime.json):
        // 游戏从编辑器启动时它由运行时自动加载写入,port 恒为 -1、且 _key
        // 与编辑器条目相同——若在此覆盖,会把编辑器 port(如 6550)覆盖成 -1,
        // portListening(-1) 同步抛 ERR_SOCKET_BAD_PORT 使 status 路由 500
        // (2026-09-18 实测:编辑器单开正常,游戏启动后工作台即报内部错误)。
        // runtime_port/runtime_pid 属于 server 侧运行时通道,工作台不消费。
        if (!name.endsWith('.json') || name.endsWith('.runtime.json')) continue
        try {
          const entry = JSON.parse(readFileSync(join(entriesDir, name), 'utf8')) as RegistryEntryShape & { _key?: string }
          if (entry !== null && typeof entry === 'object' && typeof entry._key === 'string') {
            map.set(normalizeProjectKey(entry._key), entry)
          }
        } catch { /* 忽略损坏条目 */ }
      }
    } catch { /* entries 目录不可读不属于本插件故障面 */ }
  }
  return map
}

function pidAlive(pid: number | undefined): boolean | undefined {
  if (pid === undefined) return undefined
  try {
    process.kill(pid, 0)
    return true
  } catch (error: unknown) {
    return (error as { code?: string }).code === 'EPERM' ? true : false
  }
}

function portListening(port: number): Promise<boolean> {
  // 无效端口直接判未监听:createConnection 对负数/越界端口同步抛
  // ERR_SOCKET_BAD_PORT(异常会沿 statusData → 路由 500)。残留的运行时
  // 条目(port -1)或损坏注册表都可能到达这里,防御而非崩掉整个 status。
  if (!Number.isInteger(port) || port < 0 || port > 65535) return Promise.resolve(false)
  return new Promise((resolvePromise) => {
    const socket = createConnection({ host: '127.0.0.1', port })
    const finish = (ok: boolean): void => { socket.removeAllListeners(); socket.destroy(); resolvePromise(ok) }
    socket.setTimeout(1000)
    socket.once('connect', () => finish(true))
    socket.once('timeout', () => finish(false))
    socket.once('error', () => finish(false))
  })
}

/** 项目注册表条目 → 编辑器状态(无条目/无项目路径 → null)。 */
export async function editorStatus(projectPath: string | undefined): Promise<EditorStatus | null> {
  if (projectPath === undefined || projectPath === '') return null
  const entry = readRegistryEntries().get(normalizeProjectKey(projectPath))
  if (entry === undefined) return null
  const listening = entry.port !== undefined ? await portListening(entry.port) : false
  return {
    project: projectPath,
    port: entry.port ?? null,
    pid: entry.pid ?? null,
    godotVersion: entry.godot_version ?? null,
    alive: pidAlive(entry.pid) ?? false,
    listening,
  }
}

/** server dist 存在性(路径统一正斜杠)。 */
export function serverDistStatus(godotMcpRoot: string | undefined, rel: string): { path: string | null; exists: boolean } {
  if (godotMcpRoot === undefined || godotMcpRoot === '') return { path: null, exists: false }
  const p = join(godotMcpRoot, ...rel.split('/'))
  try {
    return { path: p.replace(/\\/g, '/'), exists: statSync(p).isFile() }
  } catch {
    return { path: p.replace(/\\/g, '/'), exists: false }
  }
}
