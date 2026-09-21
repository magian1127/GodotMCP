// 同源 REST 封装(相对路径,浏览器原生 fetch)。类型与 host 侧 executor 同形(有意重复:bundle 不引 host 模块)。
export interface OutBlock { type: 'text' | 'image' | 'other'; text?: string; mimeType?: string; data?: string; truncated?: boolean; summary?: string }
export interface CallOutcome { callId: string; name: string; isError: boolean; durationMs: number; content: OutBlock[]; error?: string }
export interface EditorStatus { project: string; port: number | null; pid: number | null; godotVersion: string | null; alive: boolean; listening: boolean }
export interface StatusPayload { bridge: { serverName: string; toolCount: number }; editor: EditorStatus | null; readOnly: boolean; unsafe: boolean; serverDist: { path: string | null; exists: boolean }; node: string }
export interface GodotGroupInfo { name: string; description: string; tools: string[]; active: boolean }
export interface ToolsPayload { tools: Array<{ name: string; description?: string; parameters?: unknown }>; godotGroups: GodotGroupInfo[] }
export interface SkillDoc { id: string; title: string; locale: string; content: string }
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

export const api = {
  status: (cwd?: string): Promise<StatusPayload> => json(`/godot-workbench/api/status${cwdQuery(cwd) ? '?' + cwdQuery(cwd) : ''}`),
  tools: (): Promise<ToolsPayload> => json('/godot-workbench/api/tools'),
  call: (name: string, args: unknown, callId: string): Promise<CallOutcome> => post('/godot-workbench/api/call', { name, arguments: args, callId }) as Promise<CallOutcome>,
  cancel: (callId: string): Promise<{ ok: boolean }> => post('/godot-workbench/api/cancel', { callId }) as Promise<{ ok: boolean }>,
  connect: (cwd?: string): Promise<{ ok: boolean; toolCount: number; error?: { code?: string; message?: string } }> =>
    post(`/godot-workbench/api/connect${cwdQuery(cwd) ? '?' + cwdQuery(cwd) : ''}`, {}) as Promise<{ ok: boolean; toolCount: number; error?: { code?: string; message?: string } }>,
  skills: (locale: string): Promise<{ skills: SkillDoc[] }> => json(`/godot-workbench/api/skills?locale=${locale === 'en' ? 'en' : 'zh'}`),
  promptSections: (): Promise<{ sections: Array<{ name: string; text: string }> }> => json('/godot-workbench/api/prompt-sections'),
  pathSettings: (cwd?: string): Promise<PathSettings> => json(`/godot-workbench/api/path-settings${cwdQuery(cwd) ? '?' + cwdQuery(cwd) : ''}`),
  savePathSettings: (serverDist: string, projects: ProjectEntry[]): Promise<{ ok: boolean; serverDist: string; projects: ProjectEntry[] }> =>
    post('/godot-workbench/api/path-settings', { serverDist, projects }) as Promise<{ ok: boolean; serverDist: string; projects: ProjectEntry[] }>,
}
