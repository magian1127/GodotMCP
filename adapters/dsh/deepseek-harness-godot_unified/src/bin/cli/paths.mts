// DSH 主目录与 profile 路径解析(与官方 resolveDshHome/resolveProfileDir 语义一致)。
import { homedir } from 'node:os'
import { join } from 'node:path'
import { validateProfileName } from './validate.mjs'

/** DSH 主目录:非空白 `$DSH_HOME`,否则官方默认 `~/.dsh`。 */
export function dshHome(): string {
  const envHome = process.env.DSH_HOME
  if (envHome !== undefined && envHome.trim().length > 0) return envHome
  return join(homedir(), '.dsh')
}

/** profile 目录。 */
export function profileDir(profile: string = 'web'): string {
  return join(dshHome(), 'profiles', validateProfileName(profile))
}

/** profile 用户 patch 层路径。 */
export function patchPath(profile: string = 'web'): string {
  return join(profileDir(profile), 'cordis.patch.yml')
}

/** profile package.json(bundles/dependencies 真值)。 */
export function manifestPath(profile: string = 'web'): string {
  return join(profileDir(profile), 'package.json')
}

/** GodotMCP 工具包机器级注册表目录(与 server src/registry.ts 的配方一致)。 */
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
