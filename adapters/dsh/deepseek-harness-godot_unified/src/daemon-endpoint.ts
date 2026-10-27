/**
 * godot-mcp-daemon 的 **HTTP 端点**发现与准备(纯函数 + 少量 I/O,可注入测试)。
 *
 * v0.6 起本插件多了一条挂载通路:**不经 shim,直接用 `streamable-http` 连 daemon**
 * (上游 `godot-mcp-unified` 的 2026-07-28 stateless 面)。这样每个会话不再 spawn
 * 一个 12MB 的 shim 进程,全机只剩全机单例 daemon;会话之间仍按 `x-godot-session`
 * 键隔离工具面(键由本模块按会话/项目派生)。
 *
 * 本模块只回答四件事,全部失败一律返回 `undefined`(绝不抛,调用点在会话创建路径):
 *
 * 1. **端点在哪**:端口(`GODOT_MCP_DAEMON_PORT`,默认 6590)与机器级状态目录
 *    (Windows `%APPDATA%/godot-mcp-toolkit`,macOS `~/Library/Application Support/...`,
 *    Linux `$XDG_DATA_HOME/godot-mcp-toolkit`;可用 `GODOT_MCP_DAEMON_STATE_DIR` 覆盖)。
 * 2. **怎么拿到鉴权**:读状态目录里的机器级 `daemon-token`(上游 ADR-0004)。
 *    token 只进内存 headers,**不落盘、不写日志**。
 * 3. **daemon 没跑怎么办**:从 shim 入口同目录推导 `godot-mcp-daemon`,detach 拉起,
 *    再等端口就绪(等同 shim 的自举职责,只是换成 JS 实现)。
 * 4. **这个 daemon 能不能直连**:探一次 `server/discover`。只有返回
 *    `supportedVersions` 含 `2026-07-28` 才认为可直连 —— 旧版 daemon 在 stateless
 *    HTTP 下没有 `tools/list_changed` 通道,直连会出现"模型只看到 godot_connect,
 *    调完也永远看不到展开后的工具"这种静默故障,必须回退 stdio。
 */
import { createHash } from 'node:crypto'
import { spawn } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { connect } from 'node:net'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'

/** 直连所需的最小端点信息。 */
export interface DaemonEndpoint {
  /** 形如 `http://127.0.0.1:6590/`。 */
  baseUrl: string
  /** 机器级 Bearer token(只在内存里用)。 */
  token: string
}

/** 上游 daemon 的默认 loopback 端口。 */
export const DEFAULT_DAEMON_PORT = 6590

/** 上游机器级状态目录名。 */
export const DAEMON_STATE_DIR_NAME = 'godot-mcp-toolkit'

/** 上游机器级 token 文件名。 */
export const DAEMON_TOKEN_FILE = 'daemon-token'

/** 直连要求的协议修订(2026-07-28 才有 `subscriptions/listen`)。 */
export const REQUIRE_PROTOCOL_VERSION = '2026-07-28'

/** 端点准备的可注入依赖(测试用;生产走真实实现)。 */
export interface DaemonEndpointDeps {
  isPortOpen?: (port: number, timeoutMs: number) => Promise<boolean>
  spawnDaemon?: (exe: string) => void
  sleep?: (ms: number) => Promise<void>
  probe?: (baseUrl: string, token: string, options: ProbeOptions) => Promise<boolean>
  fetchImpl?: typeof fetch
}

/** 端点准备参数。 */
export interface PrepareDaemonEndpointOptions {
  /** server(dist)入口路径:用它的同目录推导 daemon 可执行文件。 */
  serverDist: string
  /** 环境变量快照(默认 `process.env`)。 */
  env?: NodeJS.ProcessEnv
  /** 状态目录覆盖(主要给测试)。 */
  stateDir?: string
  /** 等 daemon 就绪的上限(毫秒),默认 8000。 */
  bootstrapTimeoutMs?: number
  /** 等 token 文件写出的上限(毫秒),默认 2000。 */
  tokenTimeoutMs?: number
  /** `server/discover` 探测超时(毫秒),默认 1500。 */
  probeTimeoutMs?: number
  deps?: DaemonEndpointDeps
}

/** 协议探测参数。 */
export interface ProbeOptions {
  timeoutMs: number
  fetchImpl?: typeof fetch
}

function positivePort(value: unknown): number | undefined {
  const parsed = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : Number.NaN
  if (!Number.isInteger(parsed) || parsed <= 0 || parsed > 65535) return undefined
  return parsed
}

/**
 * daemon 端口:优先 `GODOT_MCP_DAEMON_PORT`(与上游同名,编辑器插件与 shim 共用),
 * 非法或缺失回退 6590。
 */
export function resolveDaemonPort(env: NodeJS.ProcessEnv = process.env): number {
  return positivePort(env.GODOT_MCP_DAEMON_PORT) ?? DEFAULT_DAEMON_PORT
}

/**
 * 机器级状态目录(与上游 `DaemonOptions` 同口径):
 * `GODOT_MCP_DAEMON_STATE_DIR` > 平台默认路径。
 */
export function resolveDaemonStateDir(options: {
  env?: NodeJS.ProcessEnv
  platform?: NodeJS.Platform
  home?: string
} = {}): string {
  const env = options.env ?? process.env
  const override = (env.GODOT_MCP_DAEMON_STATE_DIR ?? '').trim()
  if (override !== '') return override
  const platform = options.platform ?? process.platform
  const home = options.home ?? homedir()
  if (platform === 'win32') {
    const appData = (env.APPDATA ?? '').trim()
    const root = appData !== '' ? appData : join(home, 'AppData', 'Roaming')
    return join(root, DAEMON_STATE_DIR_NAME)
  }
  if (platform === 'darwin') return join(home, 'Library', 'Application Support', DAEMON_STATE_DIR_NAME)
  const xdg = (env.XDG_DATA_HOME ?? '').trim()
  return join(xdg !== '' ? xdg : join(home, '.local', 'share'), DAEMON_STATE_DIR_NAME)
}

/** 读机器级 token;文件缺失/为空/不可读一律 `undefined`(不抛)。 */
export function readDaemonToken(stateDir: string): string | undefined {
  try {
    const token = readFileSync(join(stateDir, DAEMON_TOKEN_FILE), 'utf8').trim()
    return token === '' ? undefined : token
  } catch {
    return undefined
  }
}

/** 入口是否为需经 node 执行的脚本(与 mcp-mount 的 `isScriptEntry` 同判据)。 */
function isScriptEntry(path: string): boolean {
  return /\.(mjs|cjs|js)$/i.test(path.replace(/\\/g, '/'))
}

/** daemon 可执行文件名(Windows 带 .exe)。 */
export function daemonExecutableName(platform: NodeJS.Platform = process.platform): string {
  return platform === 'win32' ? 'godot-mcp-daemon.exe' : 'godot-mcp-daemon'
}

/**
 * 从 shim 入口推导 daemon 可执行文件;推导不出(脚本入口/空路径)返回 `undefined`。
 *
 * 上游发布物把 shim 与 daemon 放同一目录,所以只做同目录同名替换,不猜别处。
 */
export function daemonExeFromShim(serverDist: string, platform: NodeJS.Platform = process.platform): string | undefined {
  const dist = serverDist.trim()
  if (dist === '' || isScriptEntry(dist)) return undefined
  return join(dirname(dist), daemonExecutableName(platform))
}

/**
 * 环境变量 `GODOT_MCP_DAEMON_EXE`(上游同名覆盖口)推导的 daemon 路径。
 *
 * 上游允许指向 `*.dll`(开发形态,经 `dotnet exec` 启动);本模块只做 detach 直启,
 * 因此 `.dll` 形态不进自举(拿不到运行入口就让端点准备失败、回退 stdio)。
 */
export function daemonExeFromEnv(env: NodeJS.ProcessEnv = process.env): string | undefined {
  const override = (env.GODOT_MCP_DAEMON_EXE ?? '').trim()
  if (override === '') return undefined
  return override.toLowerCase().endsWith('.dll') ? undefined : override
}

/** loopback 端口探活:连上即 true,任何异常 false。 */
export function isPortOpen(port: number, timeoutMs = 400): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    const socket = connect({ host: '127.0.0.1', port })
    let settled = false
    const finish = (value: boolean): void => {
      if (settled) return
      settled = true
      socket.destroy()
      resolve(value)
    }
    socket.setTimeout(timeoutMs, () => finish(false))
    socket.once('connect', () => finish(true))
    socket.once('error', () => finish(false))
  })
}

/** detach 拉起 daemon:不继承 stdio,宿主退出不带走它。 */
export function spawnDaemonDetached(exe: string): void {
  const child = spawn(exe, [], { detached: true, stdio: 'ignore', windowsHide: true })
  // 可执行文件不存在时 spawn 不同步抛错,而是发 'error' 事件;不接会变成
  // unhandled error 直接带走宿主进程。拉起失败由调用方的端口轮询兜底。
  child.on('error', () => { /* 忽略:端口轮询会判定端点不可用 */ })
  child.unref()
}

function delay(ms: number): Promise<void> {
  return new Promise<void>((resolve) => {
    const timer = setTimeout(resolve, ms)
    timer.unref?.()
  })
}

/**
 * 会话键派生:把"任意稳定种子"变成 UUID 形状的 36 字符串。
 *
 * 为什么必须是 UUID 形状:上游 shim 用 `Guid.NewGuid().ToString()` 生成键,
 * daemon 的会话摘要与诊断展示按短键(前 8 位)读取;这里保持同形状避免
 * 下游解析假设被打破。版本位固定 5(v5 风格)。
 */
export function deriveSessionKey(seed: string): string {
  const hex = createHash('sha256').update(seed, 'utf8').digest('hex')
  const variant = ((parseInt(hex.slice(16, 17), 16) & 0x3) | 0x8).toString(16)
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    `5${hex.slice(13, 16)}`,
    `${variant}${hex.slice(17, 20)}`,
    hex.slice(20, 32),
  ].join('-')
}

/**
 * `x-godot-cwd` 头的编码:上游 shim 用 `"uri:" + Uri.EscapeDataString(dir)`。
 *
 * JS 的 `encodeURIComponent` 少编码 `!'()*`,这里补齐,保证与 .NET 逐字节一致
 * (daemon 侧按 RFC3986 解码;中文字符两边都编码成 UTF-8 百分号序列)。
 */
export function encodeCwdHeader(cwd: string): string {
  const normalized = cwd.replace(/\\/g, '/')
  return 'uri:' + encodeURIComponent(normalized).replace(/[!'()*]/g, char =>
    `%${char.charCodeAt(0).toString(16).toUpperCase()}`)
}

/** 规范化 cwd 作为派生种子(Windows 大小写不敏感;去尾斜杠)。 */
export function normalizeCwd(cwd: string): string {
  return cwd.replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase()
}

function parseSseJson(text: string): unknown {
  // daemon 的 MCP 面按 SSE 回包:`event: message\ndata: {...}`;也接受纯 JSON。
  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim()
    if (!trimmed.startsWith('data:')) continue
    const payload = trimmed.slice(5).trim()
    if (payload === '') continue
    try {
      return JSON.parse(payload)
    } catch {
      // 继续看下一行(可能是 keep-alive 注释)。
    }
  }
  try {
    return JSON.parse(text)
  } catch {
    return undefined
  }
}

/**
 * 探测 daemon 的 2026-07-28 面:发一次 `server/discover`,看 `supportedVersions`。
 *
 * 只做一次、短超时、失败即 false。**不能**用 initialize 判断:2026-07-28 不走
 * initialize 握手,拿 initialize 去打会得到"version is not available"的误导结论。
 */
export async function probeMcp2026(
  baseUrl: string,
  token: string,
  options: ProbeOptions,
): Promise<boolean> {
  const fetchImpl = options.fetchImpl ?? globalThis.fetch
  if (typeof fetchImpl !== 'function') return false
  const meta = {
    'io.modelcontextprotocol/protocolVersion': REQUIRE_PROTOCOL_VERSION,
    'io.modelcontextprotocol/clientInfo': { name: 'dsh-godot', version: '1' },
    'io.modelcontextprotocol/clientCapabilities': {},
  }
  const body = JSON.stringify({
    jsonrpc: '2.0',
    id: 1,
    method: 'server/discover',
    params: { _meta: meta },
  })
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), options.timeoutMs)
  timer.unref?.()
  try {
    const response = await fetchImpl(baseUrl, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
        accept: 'application/json, text/event-stream',
        'mcp-protocol-version': REQUIRE_PROTOCOL_VERSION,
        'mcp-method': 'server/discover',
      },
      body,
      signal: controller.signal,
    })
    if (!response.ok) return false
    const parsed = parseSseJson(await response.text()) as
      | { result?: { supportedVersions?: unknown } }
      | undefined
    const versions = parsed?.result?.supportedVersions
    return Array.isArray(versions) && versions.includes(REQUIRE_PROTOCOL_VERSION)
  } catch {
    return false
  } finally {
    clearTimeout(timer)
  }
}

/**
 * 准备可直连的 daemon 端点;任何一步失败返回 `undefined`(调用方回退 stdio)。
 *
 * 流程:探端口 → 不在跑则拉起并等就绪 → 读 token(短轮询覆盖 daemon 首个写盘窗口)
 * → `server/discover` 验 2026 面。token 不写日志、不进返回值以外的任何地方。
 */
export async function prepareDaemonEndpoint(
  options: PrepareDaemonEndpointOptions,
): Promise<DaemonEndpoint | undefined> {
  try {
    const env = options.env ?? process.env
    const deps = options.deps ?? {}
    const port = resolveDaemonPort(env)
    const stateDir = options.stateDir ?? resolveDaemonStateDir({ env })
    const portOpen = deps.isPortOpen ?? isPortOpen
    const sleep = deps.sleep ?? delay

    if (!(await portOpen(port, 400))) {
      const candidate = daemonExeFromEnv(env) ?? daemonExeFromShim(options.serverDist)
      if (candidate === undefined || !existsSync(candidate)) return undefined
      try {
        (deps.spawnDaemon ?? spawnDaemonDetached)(candidate)
      } catch {
        return undefined
      }
      const deadline = Date.now() + (options.bootstrapTimeoutMs ?? 8000)
      let open = false
      while (Date.now() < deadline) {
        if (await portOpen(port, 400)) {
          open = true
          break
        }
        await sleep(200)
      }
      if (!open) return undefined
    }

    // token 由 daemon 启动时原子写出;刚拉起时可能还没落盘,短轮询兜住这个窗口。
    let token = readDaemonToken(stateDir)
    if (token === undefined) {
      const deadline = Date.now() + (options.tokenTimeoutMs ?? 2000)
      while (token === undefined && Date.now() < deadline) {
        await sleep(100)
        token = readDaemonToken(stateDir)
      }
    }
    if (token === undefined) return undefined

    const baseUrl = `http://127.0.0.1:${port}/`
    const probe = deps.probe ?? probeMcp2026
    const compatible = await probe(baseUrl, token, {
      timeoutMs: options.probeTimeoutMs ?? 1500,
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    })
    if (!compatible) return undefined
    return { baseUrl, token }
  } catch {
    return undefined
  }
}
