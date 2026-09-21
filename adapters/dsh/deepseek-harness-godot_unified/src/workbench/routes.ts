// 工作台 HTTP 路由装配(同源校验;全部可逆)。
import type { ToolsServiceShape, WebServerServiceShape } from '../types.js'
import { statusData, resolveSkillsRoot, ToolsCatalogueCache, type WorkbenchConfig } from './handlers.js'
import type { PathStore, ProjectEntry } from './path-store.js'
import { CallIdConflictError, type WorkbenchExecutor } from './executor.js'
import { readSkills, promptSections } from './skills.js'

interface MinimalRequest {
  method?: string
  url?: string
  headers?: Record<string, string | string[] | undefined>
  socket?: { remoteAddress?: unknown }
  on(eventName: string, cb: (chunk?: Buffer | Error) => void): unknown
}

interface MinimalResponse {
  statusCode: number
  setHeader(key: string, value: string): unknown
  end(body?: string | Buffer): unknown
}

function sendJson(res: MinimalResponse, code: number, payload: unknown): void {
  res.statusCode = code
  res.setHeader('content-type', 'application/json; charset=utf-8')
  res.end(JSON.stringify(payload))
}

/** 已序列化 JSON 字节直发(缓存命中路径,零重序列化)。 */
function sendRawJson(res: MinimalResponse, code: number, body: string): void {
  res.statusCode = code
  res.setHeader('content-type', 'application/json; charset=utf-8')
  res.end(body)
}

/** 从查询串取参数(lenient:解析失败返回 undefined,不影响路由)。 */
function queryParam(req: MinimalRequest, name: string): string | undefined {
  const url = req.url ?? ''
  if (url.indexOf('?') === -1) return undefined
  try {
    return new URL(url, 'http://localhost').searchParams.get(name) ?? undefined
  } catch {
    return undefined
  }
}

function errorPayload(_error: unknown): unknown {
  // 固定文案:底层 error.message 可能带绝对路径等内部细节,不进 HTTP 响应;
  // 原始错误经 deps.warn 走 host 日志(与 zh_pro routeErrorMessage 同策略)。
  return { error: { code: 'INTERNAL', message: '内部错误(详情已记录到 host 日志)' } }
}

/** 允许的本机 authority(Host 头主体,不含端口)。 */
const ALLOWED_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]'])
function hostAllowed(host: string): boolean {
  const authority = host.startsWith('[') ? host.slice(0, host.indexOf(']') + 1) : host.split(':')[0] ?? ''
  return ALLOWED_HOSTS.has(authority)
}
/** peer 回环判定:IPv4 127/8、::1、v4-mapped(::ffff:127.x)。 */
function isLoopback(remote: string): boolean {
  const addr = remote.startsWith('::ffff:') ? remote.slice(7) : remote
  return addr === '::1' || /^127\./.test(addr)
}
function requestAllowed(req: MinimalRequest): { ok: true } | { ok: false; message: string } {
  const remote = (req.socket as { remoteAddress?: unknown } | undefined)?.remoteAddress
  if (typeof remote !== 'string' || remote === '' || !isLoopback(remote)) {
    return { ok: false, message: '仅本机回环可访问' }
  }
  const host = req.headers?.host
  if (typeof host !== 'string' || host === '' || !hostAllowed(host)) return { ok: false, message: '仅本机回环可访问' }
  if (!fetchSiteAllowed(req)) return { ok: false, message: '仅同源可访问' }
  const origin = req.headers?.origin
  if (typeof origin !== 'string' || origin === '') return { ok: true }
  try { return new URL(origin).host === host ? { ok: true } : { ok: false, message: '仅同源可访问' } } catch { return { ok: false, message: '仅同源可访问' } }
}

/** sec-fetch-site 第三层围栏(zh_pro isTrustedApiRequest 同款):现代浏览器对所有
 * 请求都携带该头;跨站请求即使 Origin 缺失/伪造也在此拦截。非浏览器客户端不发送
 * 该头,不受影响。合法值仅 same-origin(同源 fetch)与 none(地址栏直达)。 */
function fetchSiteAllowed(req: MinimalRequest): boolean {
  const site = req.headers?.['sec-fetch-site']
  const value = Array.isArray(site) ? site[0] : site
  if (typeof value !== 'string' || value === '') return true
  return value === 'same-origin' || value === 'none'
}
class RequestBodyTooLargeError extends Error {}
function readBody(req: MinimalRequest, limit = 8 * 1024 * 1024): Promise<string> {
  return new Promise((resolveBody, rejectBody) => {
    const chunks: Buffer[] = []; let size = 0; let settled = false
    const fail = (error: Error): void => { if (settled) return; settled = true; chunks.length = 0; rejectBody(error) }
    req.on('data', (chunk?: Buffer | Error) => {
      if (settled || chunk === undefined || chunk instanceof Error) return
      size += chunk.length
      if (size > limit) { fail(new RequestBodyTooLargeError()); return }
      chunks.push(chunk)
    })
    let ended = false
    req.on('end', () => { if (settled) return; ended = true; settled = true; resolveBody(Buffer.concat(chunks).toString('utf8')) })
    req.on('aborted', () => { fail(new Error('请求中止')) })
    req.on('close', () => { if (!ended) fail(new Error('连接提前关闭')) })
    req.on('error', (error?: Buffer | Error) => { fail(error instanceof Error ? error : new Error('请求流错误')) })
  })
}

export interface WorkbenchDeps {
  tools: ToolsServiceShape
  systemPrompt?: { assemble(input?: unknown): Promise<{ sections?: Array<{ name: string; text: string }> }> } | null
  config: WorkbenchConfig
  executor: WorkbenchExecutor
  /** tools 目录缓存(挂载风暴下 /api/tools 直回缓存字节)。 */
  cache: ToolsCatalogueCache
  /** 路由 500 原始错误的去向(host logger.warn;缺省静默)。 */
  warn?: (message: string) => void
}

/** 装配 6 条路由;webServer 缺席时返回 undefined(不报错)。 */
export function mountWorkbenchRoutes(ctx: { get(name: string): unknown }, deps: WorkbenchDeps): (() => void) | undefined {
  const webServer = ctx.get('webServer') as WebServerServiceShape | undefined | null
  if (webServer === undefined || webServer === null || typeof webServer.register !== 'function') return undefined
  const disposers: Array<() => void> = []
  const register = (method: 'GET' | 'POST' | Array<'GET' | 'POST'>, path: string, handler: (req: MinimalRequest, res: MinimalResponse) => void | Promise<void>): void => {
    disposers.push(webServer.register({
      kind: 'exact',
      path,
      handler: (rawReq: unknown, rawRes: unknown) => {
        const req = rawReq as MinimalRequest
        const res = rawRes as MinimalResponse
        const accepted = Array.isArray(method) ? method : [method]
        if (!accepted.includes(req.method as 'GET' | 'POST')) {
          sendJson(res, 405, { error: { code: 'METHOD', message: `期望 ${accepted.join('/')}` } })
          return
        }
        const guard = requestAllowed(req)
        if (!guard.ok) {
          sendJson(res, 403, { error: { code: 'FORBIDDEN', message: guard.message } })
          return
        }
        void Promise.resolve(handler(req, res)).catch(error => {
          try { deps.warn?.(`[godot-workbench] 路由处理失败: ${error instanceof Error ? error.message : String(error)}`) } catch { /* 日志不可用不致命 */ }
          sendJson(res, 500, errorPayload(error))
        })
      },
    }))
  }

  register('GET', '/godot-workbench/api/status', async (req, res) => {
    sendJson(res, 200, await statusData(deps.tools, deps.config, queryParam(req, 'cwd')))
  })
  register('GET', '/godot-workbench/api/tools', async (_req, res) => {
    sendRawJson(res, 200, await deps.cache.bodyOf(deps.tools, deps.config))
  })
  // 「连接 Godot」:显式建连(有缓存时工具已可见;此按钮用于无缓存/需要刷新时拉起 server)。
  // cwd 由工作台当前会话传入,host 侧按 per-cwd 解析项目路径。
  register('POST', '/godot-workbench/api/connect', async (req, res) => {
    const connect = deps.config.connect
    if (connect === undefined || typeof connect !== 'function') {
      sendJson(res, 400, { error: { code: 'UNSUPPORTED', message: '桥接连接不可用(工具/桥接服务未装配)' } })
      return
    }
    try {
      const outcome = await connect(queryParam(req, 'cwd'))
      // 连接可能改变工具面;完成即失效目录缓存。
      deps.cache.invalidate()
      sendJson(res, 200, outcome)
    } catch (error) {
      sendJson(res, 200, { ok: false, toolCount: 0, error: { code: 'INTERNAL', message: error instanceof Error ? error.message : String(error) } })
    }
  })
  // 路径设置:GET 读「全局 serverDist + 按 cwd 批量项目条目」整份;POST 整份覆盖写回。
  // 注意:webServer 按 path 精确注册、不区分 HTTP 方法,GET/POST 必须合并为一条路由,
  // 在 handler 内按 req.method 分支,否则同路径的后注册会覆盖先注册的那个(405)。
  register(['GET', 'POST'], '/godot-workbench/api/path-settings', async (req, res) => {
    if (req.method === 'GET') {
      const cwd = queryParam(req, 'cwd') ?? ''
      const config = deps.config.pathStore?.getConfig()
      const currentCwd = cwd === '' ? undefined : cwd
      // serverDist 全局单值;store 未显式设置时回退设置卡片 godotServerDist(与 statusData 一致的生效值)。
      const effectiveServerDist = config?.serverDist !== undefined && config.serverDist !== '' ? config.serverDist : (deps.config.serverDistPath ?? '')
      const effectiveCurrentProject = currentCwd !== undefined
        ? (deps.config.pathStore?.getProjectPath(currentCwd) ?? deps.config.projectPath ?? '')
        : ''
      sendJson(res, 200, {
        serverDist: effectiveServerDist,
        projects: config?.projects ?? [],
        currentCwd: currentCwd ?? null,
        currentProjectPath: currentCwd !== undefined ? (effectiveCurrentProject || null) : null,
      })
      return
    }
    let body: { serverDist?: unknown; projects?: unknown }
    try {
      body = JSON.parse(await readBody(req)) as { serverDist?: unknown; projects?: unknown }
    } catch (error) {
      if (error instanceof RequestBodyTooLargeError) { sendJson(res, 413, { error: { code: 'PAYLOAD_TOO_LARGE', message: '请求体超过 8MiB 上限' } }); return }
      if (!(error instanceof SyntaxError)) throw error
      sendJson(res, 400, { error: { code: 'BAD_REQUEST', message: '请求体不是有效 JSON' } })
      return
    }
    const store: PathStore | undefined = deps.config.pathStore
    if (store === undefined) {
      sendJson(res, 400, { error: { code: 'UNSUPPORTED', message: '路径存储未启用' } })
      return
    }
    const serverDist = typeof body.serverDist === 'string' ? body.serverDist : ''
    const projects: ProjectEntry[] = []
    if (Array.isArray(body.projects)) {
      for (const item of body.projects as unknown[]) {
        if (item === null || typeof item !== 'object') continue
        const rec = item as Record<string, unknown>
        const cwd = typeof rec.cwd === 'string' ? rec.cwd : ''
        const projectPath = typeof rec.projectPath === 'string' ? rec.projectPath : ''
        if (cwd.trim() === '' || projectPath.trim() === '') continue
        projects.push({ cwd, projectPath })
      }
    }
    store.setConfig({ serverDist, projects })
    const saved = store.getConfig()
    sendJson(res, 200, { ok: true, serverDist: saved.serverDist ?? '', projects: saved.projects })
  })
  register('POST', '/godot-workbench/api/call', async (req, res) => {
    let body: { name?: unknown; arguments?: unknown; callId?: unknown }
    try {
      body = JSON.parse(await readBody(req)) as { name?: unknown; arguments?: unknown; callId?: unknown }
    } catch (error) {
      if (error instanceof RequestBodyTooLargeError) { sendJson(res, 413, { error: { code: 'PAYLOAD_TOO_LARGE', message: '请求体超过 8MiB 上限' } }); return }
      if (!(error instanceof SyntaxError)) throw error
      sendJson(res, 400, { error: { code: 'BAD_REQUEST', message: '请求体不是有效 JSON' } })
      return
    }
    if (typeof body.name !== 'string' || body.name === '') {
      sendJson(res, 400, { error: { code: 'BAD_REQUEST', message: 'name 必填' } })
      return
    }
    try {
      const outcome = await deps.executor.call(deps.tools, body.name, body.arguments ?? {}, typeof body.callId === 'string' ? body.callId : undefined)
      // 组激活类调用会改变工具面;完成后立即失效目录缓存,下次 /api/tools 反映新面。
      deps.cache.invalidate()
      sendJson(res, 200, outcome)
    } catch (error) {
      if (error instanceof CallIdConflictError) { sendJson(res, 409, { error: { code: 'CALLID_CONFLICT', message: error.message } }); return }
      throw error
    }
  })
  register('POST', '/godot-workbench/api/cancel', async (req, res) => {
    let body: { callId?: unknown }
    try {
      body = JSON.parse(await readBody(req)) as { callId?: unknown }
    } catch (error) {
      if (error instanceof RequestBodyTooLargeError) { sendJson(res, 413, { error: { code: 'PAYLOAD_TOO_LARGE', message: '请求体超过 8MiB 上限' } }); return }
      if (!(error instanceof SyntaxError)) throw error
      sendJson(res, 400, { error: { code: 'BAD_REQUEST', message: '请求体不是有效 JSON' } })
      return
    }
    sendJson(res, 200, { ok: typeof body.callId === 'string' && deps.executor.cancel(body.callId) })
  })
  register('GET', '/godot-workbench/api/skills', async (req, res) => {
    const locale = (req.url ?? '').includes('locale=en') ? 'en' : 'zh'
    // 技能根优先 config.godotMcpRoot;未配置时从生效 serverDist 向上推导(见 resolveSkillsRoot),
    // 使 v0.4 后仅配 serverDist(+projects) 的场景也能读到技能,修复技能面板空白。
    sendJson(res, 200, { skills: readSkills(resolveSkillsRoot(deps.config), locale) })
  })
  register('GET', '/godot-workbench/api/prompt-sections', async (_req, res) => {
    sendJson(res, 200, { sections: await promptSections(deps.systemPrompt) })
  })

  return () => { for (const dispose of disposers.splice(0)) { try { dispose() } catch { /* 继续清理其余 */ } } }
}
