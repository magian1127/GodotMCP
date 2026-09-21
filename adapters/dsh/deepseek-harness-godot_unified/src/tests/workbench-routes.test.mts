// 路由装配:6 条路径、同源校验、方法校验、/call JSON 体 → executor 投影。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { mountWorkbenchRoutes } from '../workbench/routes.js'
import { WorkbenchExecutor } from '../workbench/executor.js'
import { ToolsCatalogueCache } from '../workbench/handlers.js'
import { PathStore, cleanPath, normalizeProjects } from '../workbench/path-store.js'
import type { ToolsServiceShape, WebServerServiceShape } from '../types.js'

interface Captured { method: string; path: string; handler: (req: unknown, res: unknown) => void }

function fakeCtx() {
  const captured: Captured[] = []
  const webServer: WebServerServiceShape = {
    register: opt => { captured.push({ method: 'ANY', path: opt.path, handler: opt.handler }); return () => {} },
  }
  const effects: Array<() => void> = []
  return {
    captured,
    effects,
    get: (name: string) => (name === 'webServer' ? webServer : undefined),
    effect: (fn: () => (() => void) | void) => { effects.push(() => { void fn() }) },
    on: () => {},
  }
}

function fakeTools(): ToolsServiceShape {
  return {
    schemas: () => [{ name: 'godot_log_read' }],
    get: () => undefined,
    execute: async () => ({ isError: false, value: { content: [{ type: 'text', text: 'ok' }] } }),
  }
}

function reqRes(method: string, origin?: string, body?: string, host = '127.0.0.1:3080', remoteAddress = '127.0.0.1', closeOnly = false, omitSocket = false, extraHeaders: Record<string, string> = {}) {
  const chunks: Buffer[] = []
  const req = {
    method,
    url: '/x',
    headers: { ...(origin !== undefined ? { origin } : {}), host, ...extraHeaders },
    ...(!omitSocket ? { socket: { remoteAddress } } : {}),
    on: (event: string, cb: (chunk?: Buffer) => void) => {
      if (event === 'data' && body !== undefined) cb(Buffer.from(body))
      if (event === 'end' && !closeOnly) cb()
      if (event === 'close' && closeOnly) cb()
    },
  }
  const res = {
    statusCode: 0,
    body: '',
    setHeader: () => {},
    end: (payload?: string) => { res.body = payload ?? '' },
  }
  return { req, res }
}

test('路由装配:7 条路径;同源校验与方法校验', async () => {
  const ctx = fakeCtx()
  const dispose = mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() })
  assert.notEqual(dispose, undefined)
  const paths = ctx.captured.map(c => c.path)
  for (const p of ['/godot-workbench/api/status', '/godot-workbench/api/tools', '/godot-workbench/api/connect', '/godot-workbench/api/path-settings', '/godot-workbench/api/call', '/godot-workbench/api/cancel', '/godot-workbench/api/skills', '/godot-workbench/api/prompt-sections']) {
    assert.ok(paths.includes(p), `缺 ${p}`)
  }
  const status = ctx.captured.find(c => c.path.endsWith('/status'))!
  const ok = reqRes('GET')
  await new Promise<void>(r => { status.handler(ok.req, ok.res); setImmediate(r) })
  assert.equal(JSON.parse(ok.res.body).bridge.toolCount, 1)
  const foreign = reqRes('GET', 'http://evil.example')
  await new Promise<void>(r => { status.handler(foreign.req, foreign.res); setImmediate(r) })
  assert.equal(foreign.res.statusCode, 403)
  const wrong = reqRes('POST')
  await new Promise<void>(r => { status.handler(wrong.req, wrong.res); setImmediate(r) })
  assert.equal(wrong.res.statusCode, 405)
})

test('/connect:调用 config.connect 并失效目录缓存;未装配时 400', async () => {
  let called = 0
  const ctx = fakeCtx()
  const cache = new ToolsCatalogueCache(60_000)
  mountWorkbenchRoutes(ctx as never, {
    tools: fakeTools(),
    config: { serverName: 'godot', connect: async () => { called += 1; return { ok: true, toolCount: 20 } } },
    executor: new WorkbenchExecutor(),
    cache,
  })
  const connect = ctx.captured.find(c => c.path.endsWith('/connect'))!
  const r = reqRes('POST')
  await new Promise<void>(resolve => { connect.handler(r.req, r.res); setImmediate(() => setImmediate(resolve)) })
  assert.equal(called, 1)
  assert.equal(r.res.statusCode, 200)
  assert.deepEqual(JSON.parse(r.res.body), { ok: true, toolCount: 20 })
  // 失败的连接结果原样呈现(ok:false + error)。
  const failing = fakeCtx()
  mountWorkbenchRoutes(failing as never, {
    tools: fakeTools(),
    config: { serverName: 'godot', connect: async () => { throw new Error('boom') } },
    executor: new WorkbenchExecutor(),
    cache: new ToolsCatalogueCache(),
  })
  const failingConnect = failing.captured.find(c => c.path.endsWith('/connect'))!
  const bad = reqRes('POST')
  await new Promise<void>(resolve => { failingConnect.handler(bad.req, bad.res); setImmediate(() => setImmediate(resolve)) })
  assert.deepEqual(JSON.parse(bad.res.body), { ok: false, toolCount: 0, error: { code: 'INTERNAL', message: 'boom' } })
  // 无 connect 回调:400。
  const none = fakeCtx()
  mountWorkbenchRoutes(none as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() })
  const noneConnect = none.captured.find(c => c.path.endsWith('/connect'))!
  const nr = reqRes('POST')
  await new Promise<void>(resolve => { noneConnect.handler(nr.req, nr.res); setImmediate(resolve) })
  assert.equal(nr.res.statusCode, 400)
})

test('/call:JSON 体 → executor 投影', async () => {
  const ctx = fakeCtx()
  const executor = new WorkbenchExecutor()
  mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor, cache: new ToolsCatalogueCache() })
  const call = ctx.captured.find(c => c.path.endsWith('/call'))!
  const { req, res } = reqRes('POST', undefined, JSON.stringify({ name: 'godot_log_read', arguments: {} }))
  await new Promise<void>(r => { call.handler(req, res); setImmediate(() => setImmediate(r)) })
  const payload = JSON.parse(res.body) as { isError: boolean; content: Array<{ type: string; text?: string }> }
  assert.equal(payload.isError, false)
  assert.equal(payload.content[0]!.text, 'ok')
  const bad = reqRes('POST', undefined, '{oops')
  await new Promise<void>(r => { call.handler(bad.req, bad.res); setImmediate(() => setImmediate(r)) })
  assert.equal(bad.res.statusCode, 400)
})

test('守卫:回环 Host 白名单 + rebinding 拒绝', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() })
  const status = ctx.captured.find(c => c.path.endsWith('/status'))!
  for (const [origin, host, code] of [['https://evil.com', 'evil.com', 403], [undefined, 'example.com:3080', 403], [undefined, '127.0.0.1:3080', 200], ['http://localhost:3080', 'localhost:3080', 200]] as const) {
    const r = reqRes('GET', origin, undefined, host); await new Promise<void>(resolve => { status.handler(r.req, r.res); setImmediate(resolve) }); assert.equal(r.res.statusCode || 200, code)
  }
})

test('守卫:sec-fetch-site 第三层围栏(跨站拒绝,同源/none/缺省放行)', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() })
  const status = ctx.captured.find(c => c.path.endsWith('/status'))!
  for (const [site, code] of [['cross-site', 403], ['same-site', 403], ['same-origin', 200], ['none', 200]] as const) {
    // 不带 Origin:Host+peer 均合法,仅靠 sec-fetch-site 区分——模拟现代浏览器跨站请求。
    const r = reqRes('GET', undefined, undefined, undefined, undefined, false, false, { 'sec-fetch-site': site })
    await new Promise<void>(resolve => { status.handler(r.req, r.res); setImmediate(resolve) })
    assert.equal(r.res.statusCode || 200, code, `sec-fetch-site=${site}`)
  }
})

test('500:固定文案不泄漏底层错误;原始错误经 warn 上报 host 日志', async () => {
  const warns: string[] = []
  const ctx = fakeCtx()
  const tools: ToolsServiceShape = {
    schemas: () => [{ name: 'godot_log_read' }],
    get: () => undefined,
    execute: async () => { throw new Error('SECRET internal detail C:\\Users\\x\\key.json') },
  }
  mountWorkbenchRoutes(ctx as never, { tools, config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache(), warn: message => { warns.push(message) } })
  const call = ctx.captured.find(c => c.path.endsWith('/call'))!
  const { req, res } = reqRes('POST', undefined, JSON.stringify({ name: 'godot_log_read', arguments: {} }))
  await new Promise<void>(r => { call.handler(req, res); setImmediate(() => setImmediate(r)) })
  assert.equal(res.statusCode, 500)
  const payload = JSON.parse(res.body) as { error: { message: string } }
  assert.equal(payload.error.message, '内部错误(详情已记录到 host 日志)')
  assert.ok(!res.body.includes('SECRET'), '底层错误细节不进 HTTP 响应')
  assert.equal(warns.length, 1)
  assert.ok(warns[0]!.includes('SECRET'), '原始错误经 warn 走 host 日志')
})

test('413:超限请求体', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() })
  const call = ctx.captured.find(c => c.path.endsWith('/call'))!
  const r = reqRes('POST', undefined, 'x'.repeat(9 * 1024 * 1024)); await new Promise<void>(resolve => { call.handler(r.req, r.res); setImmediate(() => setImmediate(resolve)) }); assert.equal(r.res.statusCode, 413)
})

test('守卫:peer 回环校验', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() }); const s = ctx.captured.find(c => c.path.endsWith('/status'))!
  for (const [remote, code] of [['192.168.1.5', 403], ['127.0.0.1', 200], ['::ffff:127.0.0.1', 200]] as const) { const r = reqRes('GET', undefined, undefined, undefined, remote); await new Promise<void>(x => { s.handler(r.req, r.res); setImmediate(x) }); assert.equal(r.res.statusCode || 200, code) }
})

test('守卫:缺失 peer 地址时拒绝', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() }); const s = ctx.captured.find(c => c.path.endsWith('/status'))!
  const r = reqRes('GET', undefined, undefined, undefined, undefined, false, true)
  await new Promise<void>(resolve => { s.handler(r.req, r.res); setImmediate(resolve) })
  assert.equal(r.res.statusCode, 403)
})

test('守卫:空 peer 地址时拒绝', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() }); const s = ctx.captured.find(c => c.path.endsWith('/status'))!
  const r = reqRes('GET', undefined, undefined, undefined, '')
  await new Promise<void>(resolve => { s.handler(r.req, r.res); setImmediate(resolve) })
  assert.equal(r.res.statusCode, 403)
})
test('readBody:提前 close 不悬挂', async () => {
  const ctx = fakeCtx(); mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache() }); const call = ctx.captured.find(c => c.path.endsWith('/call'))!
  const handlers = new Map<string, Array<(arg?: unknown) => void>>(); const req = { method: 'POST', url: '/x', headers: { host: '127.0.0.1:3080' }, socket: { remoteAddress: '127.0.0.1' }, on: (event: string, cb: (arg?: unknown) => void) => { const list = handlers.get(event) ?? []; list.push(cb); handlers.set(event, list) } }; let ended = false; const res = { statusCode: 0, body: '', setHeader: () => {}, end: (payload?: string) => { ended = true; res.body = payload ?? '' } }
  call.handler(req, res); await new Promise<void>(r => setImmediate(r)); const invoke = (event: string, arg?: unknown): void => { for (const cb of handlers.get(event) ?? []) cb(arg) }; invoke('close'); await new Promise<void>(r => setImmediate(r)); assert.equal(ended, true); assert.equal(res.statusCode, 500)
})

test('/call:透传 callId', async () => {
  const ctx = fakeCtx()
  const executor = new WorkbenchExecutor()
  mountWorkbenchRoutes(ctx as never, { tools: fakeTools(), config: { serverName: 'godot' }, executor, cache: new ToolsCatalogueCache() })
  const call = ctx.captured.find(c => c.path.endsWith('/call'))!
  const { req, res } = reqRes('POST', undefined, JSON.stringify({ name: 'godot_log_read', arguments: {}, callId: 'gwbc-xyz' }))
  await new Promise<void>(r => { call.handler(req, res); setImmediate(() => setImmediate(r)) })
  const payload = JSON.parse(res.body) as { callId: string }
  assert.equal(payload.callId, 'gwbc-xyz')
})


test('/api/tools:缓存命中零重算;/api/call 完成后失效重算', async () => {
  const ctx = fakeCtx()
  let schemaReads = 0
  const tools: ToolsServiceShape = {
    schemas: () => { schemaReads += 1; return [{ name: 'godot_log_read' }] },
    get: () => undefined,
    execute: async () => ({ isError: false, value: { content: [{ type: 'text', text: 'ok' }] } }),
  }
  mountWorkbenchRoutes(ctx as never, { tools, config: { serverName: 'godot' }, executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache(60_000) })
  const toolsRoute = ctx.captured.find(c => c.path.endsWith('/tools'))!
  const callRoute = ctx.captured.find(c => c.path.endsWith('/call'))!
  const get = async (): Promise<string> => {
    const r = reqRes('GET')
    await new Promise<void>(resolve => { toolsRoute.handler(r.req, r.res); setImmediate(resolve) })
    return r.res.body
  }
  const first = await get()
  assert.equal(schemaReads, 1, '首请求真实计算')
  const second = await get()
  assert.equal(second, first)
  assert.equal(schemaReads, 1, 'TTL 内缓存直回,不重算')
  // /api/call 完成 → 缓存失效 → 下一次 /api/tools 重算。
  const c = reqRes('POST', undefined, JSON.stringify({ name: 'godot_log_read', arguments: {} }))
  await new Promise<void>(r => { callRoute.handler(c.req, c.res); setImmediate(() => setImmediate(r)) })
  await get()
  assert.equal(schemaReads, 2, 'call 完成后失效重算')
})

test('path-settings:GET 读整份(全局 serverDist + 批量 projects),POST 整份覆盖写回', async () => {
  const tmp = join(tmpdir(), `godot-paths-${Date.now()}`)
  const store = new PathStore(join(tmp, 'paths.json'))
  const cwd = 'C:/proj/alpha'
  const ctx = fakeCtx()
  mountWorkbenchRoutes(ctx as never, {
    tools: fakeTools(), config: { serverName: 'godot', serverDistPath: 'D:/global/dist/index.js', projectPath: 'D:/global/proj', pathStore: store },
    executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache(),
  })
  const entries = ctx.captured.filter(c => c.path.endsWith('/path-settings'))
  assert.equal(entries.length, 1, 'GET+POST 合并为一条路由(webServer 按 path 注册,方法在 handler 内分支)')
  const handle = entries[0]!.handler
  const get = handle
  const post = handle
  // 无 cwd(设置卡片场景):GET 返回整份全局 { serverDist, projects },current* 为 null。
  const noCwd = reqRes('GET'); noCwd.req.url = '/godot-workbench/api/path-settings'
  await new Promise<void>(r => { get(noCwd.req, noCwd.res); setImmediate(r) })
  const noCwdBody = JSON.parse(noCwd.res.body)
  assert.equal(noCwdBody.serverDist, 'D:/global/dist/index.js', '无 cwd 时 serverDist 回退全局')
  assert.deepEqual(noCwdBody.projects, [], '无 cwd 时 projects 为整份(空)')
  assert.equal(noCwdBody.currentCwd, null)
  assert.equal(noCwdBody.currentProjectPath, null)
  // 空配置时 GET:serverDist 回退全局,projects 为空,current 匹配为空。
  const before = reqRes('GET'); before.req.url = `/godot-workbench/api/path-settings?cwd=${encodeURIComponent(cwd)}`
  await new Promise<void>(r => { get(before.req, before.res); setImmediate(r) })
  const beforeBody = JSON.parse(before.res.body)
  assert.equal(beforeBody.serverDist, 'D:/global/dist/index.js')
  assert.deepEqual(beforeBody.projects, [])
  assert.equal(beforeBody.currentCwd, cwd)
  assert.equal(beforeBody.currentProjectPath, 'D:/global/proj')
  // POST 整份配置(全局 serverDist + 两条项目条目;一条按当前 cwd)。
  const write = reqRes('POST', undefined, JSON.stringify({
    serverDist: 'D:/mcp/dist/index.js',
    projects: [{ cwd, projectPath: 'C:/proj/alpha' }, { cwd: 'C:/proj/beta', projectPath: 'C:/proj/beta' }],
  }))
  await new Promise<void>(r => { post(write.req, write.res); setImmediate(r) })
  assert.equal(JSON.parse(write.res.body).ok, true)
  // 存储按 cwd 匹配;serverDist 全局单值。
  assert.equal(store.getServerDist(), 'D:/mcp/dist/index.js')
  assert.equal(store.getProjectPath(cwd), 'C:/proj/alpha')
  assert.equal(store.getProjectPath('C:/proj/beta'), 'C:/proj/beta')
  assert.equal(store.getProjectPath('C:/proj/gamma'), undefined, '未配置 cwd 无匹配')
  // 写入后 GET 读整份。
  const after = reqRes('GET'); after.req.url = `/godot-workbench/api/path-settings?cwd=${encodeURIComponent(cwd)}`
  await new Promise<void>(r => { get(after.req, after.res); setImmediate(r) })
  const afterBody = JSON.parse(after.res.body)
  assert.equal(afterBody.serverDist, 'D:/mcp/dist/index.js')
  assert.equal(afterBody.projects.length, 2)
  assert.equal(afterBody.currentProjectPath, 'C:/proj/alpha')
})

test('path-settings:POST 增删改(整份覆盖)并按 cwd 匹配生效;非法条目被丢弃', async () => {
  const tmp = join(tmpdir(), `godot-paths-${Date.now()}`)
  const store = new PathStore(join(tmp, 'paths.json'))
  const ctx = fakeCtx()
  mountWorkbenchRoutes(ctx as never, {
    tools: fakeTools(), config: { serverName: 'godot', pathStore: store },
    executor: new WorkbenchExecutor(), cache: new ToolsCatalogueCache(),
  })
  const entries = ctx.captured.filter(c => c.path.endsWith('/path-settings'))
  const post = entries[0]!.handler
  // 改:覆盖一条 cwd 的项目路径;增:加一条;剔:不存在的先写入后覆盖删除。
  const w1 = reqRes('POST', undefined, JSON.stringify({ serverDist: 'S', projects: [{ cwd: 'A', projectPath: 'A1' }, { cwd: 'B', projectPath: 'B1' }] }))
  await new Promise<void>(r => { post(w1.req, w1.res); setImmediate(r) })
  assert.equal(store.getProjectPath('A'), 'A1')
  assert.equal(store.getProjectPath('B'), 'B1')
  // 整份覆盖:改 A 的路径、删除 B、保留 C;非法(空/重复 cwd)被丢弃。
  const w2 = reqRes('POST', undefined, JSON.stringify({
    serverDist: 'S2',
    projects: [
      { cwd: 'A', projectPath: 'A2' },
      { cwd: '', projectPath: 'x' },
      { cwd: 'D', projectPath: '' },
      { cwd: 'C', projectPath: 'C1' },
    ],
  }))
  await new Promise<void>(r => { post(w2.req, w2.res); setImmediate(r) })
  assert.equal(store.getServerDist(), 'S2')
  assert.equal(store.getProjectPath('A'), 'A2', '覆盖生效')
  assert.equal(store.getProjectPath('B'), undefined, '删除生效')
  assert.equal(store.getProjectPath('C'), 'C1')
  const saved = store.getConfig()
  assert.equal(saved.projects.length, 2, '非法条目被丢弃:cwd 为空/路径为空')
})

test('PathStore:normalizeProjects 去重、上限 100、字段校验', () => {
  const out = normalizeProjects([
    { cwd: 'X', projectPath: 'P1' },
    { cwd: 'X', projectPath: 'P2' }, // 同 cwd 后写覆盖
    { cwd: '', projectPath: 'P' },
    { cwd: 'Y', projectPath: '' },
    'z',
    null,
    { cwd: 'Z', projectPath: 'P3' },
  ])
  assert.deepEqual(out, [{ cwd: 'X', projectPath: 'P2' }, { cwd: 'Z', projectPath: 'P3' }])

  const many = Array.from({ length: 150 }, (_, i) => ({ cwd: `c${i}`, projectPath: `p${i}` }))
  assert.equal(normalizeProjects(many).length, 100, '上限 100')
})

test('PathStore:cleanPath 剥除首尾成对引号并 trim(复制粘贴脏数据)', () => {
  assert.equal(cleanPath('  "D:\\proj\\godot\\project.godot"  '), 'D:\\proj\\godot\\project.godot')
  assert.equal(cleanPath("'D:\\proj'"), 'D:\\proj')
  assert.equal(cleanPath('  D:\\proj  '), 'D:\\proj')
  assert.equal(cleanPath('"unbalanced'), '"unbalanced')
  assert.equal(cleanPath('"a"b"'), 'a"b')
  // 归一化入口同样清洗。
  const out = normalizeProjects([{ cwd: '"D:\\proj"', projectPath: '"D:\\proj\\godot\\project.godot"' }])
  assert.deepEqual(out, [{ cwd: 'D:\\proj', projectPath: 'D:\\proj\\godot' }])
})

test('PathStore:惰性加载与原子持久化(跨实例读回)', async () => {
  const tmp = join(tmpdir(), `godot-paths-${Date.now()}`)
  const file = join(tmp, 'paths.json')
  const a = new PathStore(file)
  a.setConfig({ serverDist: 'D:/mcp/dist/index.js', projects: [{ cwd: 'C:/proj', projectPath: 'C:/proj' }] })
  const b = new PathStore(file)
  assert.equal(b.getServerDist(), 'D:/mcp/dist/index.js')
  assert.equal(b.getProjectPath('C:/proj'), 'C:/proj')
  // 再覆盖写并回读。
  b.setProjects([{ cwd: 'C:/proj', projectPath: 'C:/proj' }, { cwd: 'C:/other', projectPath: 'C:/other' }])
  assert.equal(b.getServerDist(), 'D:/mcp/dist/index.js', 'setProjects 保留 serverDist')
  assert.equal(b.getProjectPath('C:/other'), 'C:/other')
})
