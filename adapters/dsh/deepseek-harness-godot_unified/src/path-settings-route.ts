// 路径设置 HTTP 路由(同源/回环校验;全部可逆)。
//
// 唯一职责:让浏览器端配置表单读写 host 侧 `PathStore`(全局 serverDist + 按 cwd
// 批量项目条目)。原「Godot 工作台」的其余路由(status/tools/call/cancel/skills/
// prompt-sections)已随工作台整体移除;通用工具调试界面迁至
// `deepseek-harness-project-mcp` 的「工具调试」Tab。
//
// 注意:webServer 按 path 精确注册、不区分 HTTP 方法,GET/POST 必须合并为**一条**
// 路由并在 handler 内按 req.method 分支,否则同路径的后注册会覆盖先注册的那个。
import type { WebServerServiceShape } from './types.js'
import type { PathStore, ProjectEntry } from './path-store.js'

/** 路径设置路由前缀(与 client 侧 `src/client/api.ts` 同源同步)。 */
export const PATH_SETTINGS_ROUTE = '/godot-config/api/path-settings'

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

function errorPayload(): unknown {
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

/** sec-fetch-site 第三层围栏(zh_pro isTrustedApiRequest 同款):现代浏览器对所有
 * 请求都携带该头;跨站请求即使 Origin 缺失/伪造也在此拦截。非浏览器客户端不发送
 * 该头,不受影响。合法值仅 same-origin(同源 fetch)与 none(地址栏直达)。 */
function fetchSiteAllowed(req: MinimalRequest): boolean {
  const site = req.headers?.['sec-fetch-site']
  const value = Array.isArray(site) ? site[0] : site
  if (typeof value !== 'string' || value === '') return true
  return value === 'same-origin' || value === 'none'
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

class RequestBodyTooLargeError extends Error {}
/** 读请求体(8MiB 上限;超限抛 RequestBodyTooLargeError,提前 close 不悬挂)。 */
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

export interface PathSettingsRouteDeps {
  /** 路径存储(host 侧持久层);缺席时 POST 返回 400。 */
  pathStore?: PathStore
  /**
   * 取设置卡片里的 `godotServerDist`(store 未显式设置时的生效回退值)。
   *
   * **必须是 getter,不能是值**:按值传入会在挂载路由那一刻把 config 快照钉死,
   * 之后 volatile 更新重赋值也不会反映到这里 —— 于是 GET 一直显示旧值,
   * 用户改了插件行 config 却在页面上看不到变化。允许 `string` 只为兼容既有测试。
   */
  serverDistPath?: (() => string) | string
  /** 设置卡片里的 `godotProjectPath`。项目路径表已删除,此字段不再参与解析。 */
  projectPath?: string
  /** 路由 500 原始错误的去向(host logger.warn;缺省静默)。 */
  warn?: (message: string) => void
}

/** 解析 `serverDistPath`(可能是 getter,也可能是值)。 */
function resolveServerDistPath(deps: PathSettingsRouteDeps): string {
  const source = deps.serverDistPath
  if (source === undefined) return ''
  return typeof source === 'function' ? source() : source
}

/**
 * 装配 1 条路由(GET/POST 合并)到 `PATH_SETTINGS_ROUTE`。
 * webServer 缺席时返回 undefined(不报错),调用方据此跳过 teardown 登记。
 */
export function mountPathSettingsRoute(
  ctx: { get(name: string): unknown },
  deps: PathSettingsRouteDeps,
): (() => void) | undefined {
  const webServer = ctx.get('webServer') as WebServerServiceShape | undefined | null
  if (webServer === undefined || webServer === null || typeof webServer.register !== 'function') return undefined

  const dispose = webServer.register({
    kind: 'exact',
    path: PATH_SETTINGS_ROUTE,
    handler: (rawReq: unknown, rawRes: unknown) => {
      const req = rawReq as MinimalRequest
      const res = rawRes as MinimalResponse
      if (req.method !== 'GET' && req.method !== 'POST') {
        sendJson(res, 405, { error: { code: 'METHOD', message: '期望 GET/POST' } })
        return
      }
      const guard = requestAllowed(req)
      if (!guard.ok) {
        sendJson(res, 403, { error: { code: 'FORBIDDEN', message: guard.message } })
        return
      }
      void handle(req, res, deps).catch(error => {
        try { deps.warn?.(`[godot-config] 路由处理失败: ${error instanceof Error ? error.message : String(error)}`) } catch { /* 日志不可用不致命 */ }
        sendJson(res, 500, errorPayload())
      })
    },
  })
  return () => { try { dispose() } catch { /* teardown 尽力而为 */ } }
}

async function handle(req: MinimalRequest, res: MinimalResponse, deps: PathSettingsRouteDeps): Promise<void> {
  if (req.method === 'GET') {
    // serverDist 全局单值;store 未显式设置时回退设置卡片 godotServerDist(生效值)。
    const config = deps.pathStore?.getConfig()
    const effectiveServerDist = config?.serverDist !== undefined && config.serverDist !== '' ? config.serverDist : resolveServerDistPath(deps)
    sendJson(res, 200, {
      serverDist: effectiveServerDist,
      // **projects 恒为空**:项目路径表已删除。
      //
      // 一个工作区常有多个 Godot 项目,按 cwd 查表只能给一个;会话要连别的项目还得先改表。
      // 现在目标由 daemon 侧解析(godot_connect 的 project 参数 → 本会话默认目标 →
      // shim 上报的 x-godot-cwd 下的 project.godot → 唯一活跃实例)。
      // 保留空数组是为了让旧版前端拿到的形状仍然合法。
      projects: [],
      currentCwd: null,
      currentProjectPath: null,
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
  const store: PathStore | undefined = deps.pathStore
  if (store === undefined) {
    sendJson(res, 400, { error: { code: 'UNSUPPORTED', message: '路径存储未启用' } })
    return
  }
  const serverDist = typeof body.serverDist === 'string' ? body.serverDist : ''
  // 请求里若还带 projects,一律忽略且不持久化:留着它只会让用户以为改了项目路径就生效。
  store.setConfig({ serverDist, projects: [] })
  const saved = store.getConfig()
  sendJson(res, 200, { ok: true, serverDist: saved.serverDist ?? '', projects: [] })
}
