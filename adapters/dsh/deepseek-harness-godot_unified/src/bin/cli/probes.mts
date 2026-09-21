// profile/注册表/端口探测(status 用)。
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { createConnection } from 'node:net'
import { join } from 'node:path'
import { PKG, type ProfileManifest, type RegistryEntry } from './constants.mjs'
import { manifestPath, registryDir } from './paths.mjs'

/** profile 的 bundles 列表是否已包含本插件(bundle 行就绪)。 */
export function bundlesHasPlugin(name: string = 'web'): boolean {
  const path = manifestPath(name)
  if (!existsSync(path)) return false
  try {
    const manifest: ProfileManifest = JSON.parse(readFileSync(path, 'utf8'))
    return (manifest.dsh?.profile?.bundles ?? []).includes(PKG)
  } catch {
    return false
  }
}

/**
 * 与 server src/registry.ts normalizePath 一致的注册表键规范化:
 * 反斜杠→`/`、去尾分隔符;win32/darwin 转小写。
 */
export function normalizeProjectKey(path: string): string {
  let key = path.replace(/\\/g, '/')
  while (key.length > 1 && key.endsWith('/')) key = key.slice(0, -1)
  if (process.platform === 'win32' || process.platform === 'darwin') key = key.toLowerCase()
  return key
}

/**
 * 读取注册表全部条目;缺失/损坏返回空表(status 降级,不失败)。
 *
 * 兼容上游两代布局:旧 `projects.json`(`by_path{}`)与新 `entries/<hash>.json`
 * (每条含 `_key` 小写规范路径)。先读旧格式,再扫 `entries/*.json` 并以 `_key`
 * 覆盖,使新布局优先而旧版本不回归。
 */
export function readRegistry(): Map<string, RegistryEntry> {
  const dir = registryDir()
  const map = new Map<string, RegistryEntry>()
  const legacy = join(dir, 'projects.json')
  if (existsSync(legacy)) {
    try {
      const parsed = JSON.parse(readFileSync(legacy, 'utf8')) as { by_path?: Record<string, RegistryEntry> }
      for (const [key, entry] of Object.entries(parsed.by_path ?? {})) {
        map.set(normalizeProjectKey(key), entry)
      }
    } catch {
      // 注册表损坏不属于本插件故障面:status 如实报告无条目。
    }
  }
  const entriesDir = join(dir, 'entries')
  if (existsSync(entriesDir)) {
    try {
      for (const name of readdirSync(entriesDir)) {
        if (!name.endsWith('.json')) continue
        try {
          const entry = JSON.parse(readFileSync(join(entriesDir, name), 'utf8')) as RegistryEntry & { _key?: string }
          if (entry !== null && typeof entry === 'object' && typeof entry._key === 'string') {
            map.set(normalizeProjectKey(entry._key), entry)
          }
        } catch {
          // 忽略损坏条目。
        }
      }
    } catch {
      // entries 目录不可读不属于本插件故障面。
    }
  }
  return map
}

/** 回环 TCP 探活(1s 超时)。 */
export function portListening(port: number): Promise<boolean> {
  return new Promise((resolvePromise) => {
    const socket = createConnection({ host: '127.0.0.1', port })
    const finish = (ok: boolean): void => {
      socket.removeAllListeners()
      socket.destroy()
      resolvePromise(ok)
    }
    socket.setTimeout(1000)
    socket.once('connect', () => finish(true))
    socket.once('timeout', () => finish(false))
    socket.once('error', () => finish(false))
  })
}

/** 注册表条目里记录的 pid 是否仍存活(粗判,跨平台)。 */
export function pidAlive(pid: number | undefined): boolean | undefined {
  if (pid === undefined) return undefined
  try {
    process.kill(pid, 0)
    return true
  } catch (error: unknown) {
    return (error as { code?: string }).code === 'EPERM' ? true : false
  }
}

/** profile 目录下是否存在 node_modules 副本的粗提示(安装层参考)。 */
export function profileDirExists(name: string = 'web'): boolean {
  return existsSync(join(manifestPath(name), '..'))
}
