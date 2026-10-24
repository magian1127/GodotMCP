// 同源 REST 封装(相对路径,浏览器原生 fetch)。
// 仅保留路径设置读写:host 侧 `src/path-settings-route.ts` 挂
// `/godot-config/api/path-settings`。通用工具调试界面已迁至
// `deepseek-harness-project-mcp` 的「工具调试」Tab,本包不再提供 status/tools/
// call/cancel/connect/skills/prompt-sections 端点。
export interface ProjectEntry { cwd: string; projectPath: string }
export interface PathSettings {
  serverDist: string
  projects: ProjectEntry[]
  currentCwd: string | null
  currentProjectPath: string | null
}

async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  const body = await res.json() as T & { error?: { message?: string } }
  if (!res.ok) throw new Error(body?.error?.message ?? `HTTP ${res.status}`)
  return body
}

const post = (url: string, payload: unknown): Promise<unknown> =>
  json(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload) })

const cwdQuery = (cwd: string | undefined): string => (cwd === undefined || cwd === '' ? '' : `cwd=${encodeURIComponent(cwd)}`)

const PATH_SETTINGS_URL = '/godot-config/api/path-settings'

export const api = {
  pathSettings: (cwd?: string): Promise<PathSettings> => json(`${PATH_SETTINGS_URL}${cwdQuery(cwd) ? '?' + cwdQuery(cwd) : ''}`),
  savePathSettings: (serverDist: string, projects: ProjectEntry[]): Promise<{ ok: boolean; serverDist: string; projects: ProjectEntry[] }> =>
    post(PATH_SETTINGS_URL, { serverDist, projects }) as Promise<{ ok: boolean; serverDist: string; projects: ProjectEntry[] }>,
}
