/**
 * 极简 MCP stdio client(spawn GodotMCP server + newline-delimited JSON-RPC)。
 *
 * 设计要点:
 * - **懒生命周期**:默认不启动;ensureStarted() 才 spawn;空闲 idleCloseMs 自动
 *   关闭(server 退出即无任何网络活动——编辑器 ws 重连循环随之消失)。
 * - **应答关联**:自增 id + 超时;notifications/tools/list_changed → onListChanged。
 * - server stderr 逐行转发主进程 stderr(上游已做重连封顶,不再刷屏)。
 * - 崩溃/退出 → onExit,标记离线;下一次调用自动重启。
 */
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { VERSION } from '../constants.js'

export interface McpToolDef {
  name: string
  description?: string
  inputSchema?: unknown
}

/** 单条 JSON-RPC 消息(stdout 行)的字节上限:超过视为 server 协议违规,
 * 立即终止桥接并让在途请求失败——防御失控 server 以无换行输出打爆 host 内存。 */
export const MAX_STDOUT_LINE_BYTES = 4 * 1024 * 1024

export interface McpCallResult {
  content?: Array<Record<string, unknown>>
  structuredContent?: unknown
  isError?: boolean
}

/** 环境清洗:凭据样式变量与 DSH_* 不下传(对齐 dsh-mcp-client 行为)。 */
function scrubEnv(base: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {}
  for (const [key, value] of Object.entries(base)) {
    if (/KEY|PASSWORD|SECRET|TOKEN/i.test(key)) continue
    if (key.startsWith('DSH_')) continue
    env[key] = value
  }
  return env
}

export class GodotBridge {
  private proc: ChildProcessWithoutNullStreams | undefined
  private buffer = ''
  private nextId = 1
  private readonly pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }>()
  private readyPromise: Promise<void> | undefined
  private idleTimer: NodeJS.Timeout | undefined
  private stopping = false
  /** 当前调用会话的工作目录(per-cwd 桥接配置的键);由 setCwd/callTool 更新。 */
  private activeCwd: string | undefined
  /** 当前已启动 server 所用的 cwd(用于 cwd 变化时重启以切换配置)。 */
  private startedCwd: string | undefined
  /** 最近一次 listTools() 成功返回的精确清单;settings(zhPrompt)变化时无需重启 server 即可重注册。 */
  private lastToolDefs: McpToolDef[] | undefined

  constructor(
    private readonly opts: {
      /** server dist 路径解析:按当前 cwd 取,缺省回退全局设置。 */
      serverDist: (cwd?: string) => string | undefined
      /** Godot 项目路径解析:按当前 cwd 取,缺省回退全局设置。 */
      projectPath: (cwd?: string) => string | undefined
      onListChanged: () => void
      onExit: () => void
      idleCloseMs?: number
    },
  ) {}

  /** 设置当前调用会话 cwd(per-cwd 解析键)。 */
  setCwd(cwd?: string): void {
    this.activeCwd = cwd
  }

  get running(): boolean {
    return this.proc !== undefined && this.proc.exitCode === null
  }

  /** 启动(幂等)+ initialize 握手;返回就绪 promise。 */
  async ensureStarted(): Promise<void> {
    if (this.stopping) throw new Error('godot bridge is stopping; retry shortly')
    if (this.proc !== undefined && this.proc.exitCode === null) {
      // cwd 已变化:当前 server 用的是旧配置,需重启以切换到新 cwd 的路径。
      if (this.startedCwd !== this.activeCwd) {
        this.stop()
      } else {
        await this.readyPromise
        this.armIdleClose()
        return
      }
    }
    const dist = this.opts.serverDist(this.activeCwd)
    if (dist === undefined || dist === '') throw new Error('godot server dist 未配置:运行 dsh-godot install 或在设置中填写 server dist 路径')
    const projectPath = this.opts.projectPath(this.activeCwd)
    if (projectPath === undefined || projectPath === '') {
      throw new Error('Godot 项目路径未配置:请在侧栏「插件」页 → 本包页面的 Godot 路径区填写(或 dsh-godot install --project <目录>);当前工作目录与任何项目条目都不匹配')
    }
    this.stopping = false
    this.startedCwd = this.activeCwd
    const env = scrubEnv(process.env)
    env.GODOT_MCP_PROJECT_PATH = projectPath
    const proc = spawn(process.execPath, [dist], { env, stdio: ['pipe', 'pipe', 'pipe'] })
    this.proc = proc
    proc.stdout.setEncoding('utf8')
    proc.stdout.on('data', (chunk: string) => {
      this.buffer += chunk
      if (Buffer.byteLength(this.buffer, 'utf8') > MAX_STDOUT_LINE_BYTES) {
        process.stderr.write(`[godot-mcp] stdout 单行超过 ${MAX_STDOUT_LINE_BYTES} 字节上限,终止桥接(server 协议违规)\n`)
        this.failAllPending(`godot bridge: stdout line exceeded ${MAX_STDOUT_LINE_BYTES} bytes (protocol violation)`)
        this.stop()
        return
      }
      for (;;) {
        const index = this.buffer.indexOf('\n')
        if (index === -1) break
        const line = this.buffer.slice(0, index).trim()
        this.buffer = this.buffer.slice(index + 1)
        if (line !== '') this.handleLine(line)
      }
    })
    proc.stderr.setEncoding('utf8')
    proc.stderr.on('data', (chunk: string) => {
      process.stderr.write(`[godot-mcp] ${chunk.endsWith('\n') ? chunk : chunk + '\n'}`)
    })
    proc.on('exit', () => {
      // 代数保护:cwd 切换重启时旧进程的 exit 会晚于新进程接替到达——只有
      // 当前注册进程自己退出才清状态/触发 onExit,否则会把新进程的注册误清,
      // stop() 从此杀不到它(进程泄漏)。
      if (this.proc === proc) {
        this.proc = undefined
        this.readyPromise = undefined
        this.buffer = ''
        if (this.idleTimer !== undefined) { clearTimeout(this.idleTimer); this.idleTimer = undefined }
        this.opts.onExit()
      }
    })
    this.readyPromise = this.request('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: { name: 'deepseek-harness-godot_unified', version: VERSION },
    }, 30_000).then(() => {
      this.send({ jsonrpc: '2.0', method: 'notifications/initialized' })
    })
    await this.readyPromise
    this.armIdleClose()
  }

  /** 在线时拉取精确工具清单(含已激活组)。 */
  async listTools(): Promise<McpToolDef[]> {
    await this.ensureStarted()
    const result = await this.request('tools/list', {}, 30_000) as { tools?: McpToolDef[] } | undefined
    const tools = result?.tools ?? []
    this.lastToolDefs = tools
    return tools
  }

  /** 已解析的工具清单缓存(尚未在线时 undefined)。 */
  get toolDefsCache(): McpToolDef[] | undefined {
    return this.lastToolDefs
  }

  /** 转发 tools/call;首次调用会自动启动桥接。`cwd` 为调用会话工作目录(per-cwd 配置键)。 */
  async callTool(name: string, args: unknown, cwd?: string, timeoutMs = 60_000): Promise<McpCallResult> {
    if (cwd !== undefined) this.activeCwd = cwd
    await this.ensureStarted()
    const result = await this.request('tools/call', { name, arguments: typeof args === 'object' && args !== null ? args : {} }, timeoutMs)
    return (result ?? {}) as McpCallResult
  }

  stop(): void {
    this.stopping = true
    if (this.idleTimer !== undefined) { clearTimeout(this.idleTimer); this.idleTimer = undefined }
    const proc = this.proc
    this.proc = undefined
    this.readyPromise = undefined
    this.buffer = ''
    if (proc !== undefined) {
      try { proc.kill() } catch { /* 进程已死不致命 */ }
    }
    this.stopping = false
  }

  private armIdleClose(): void {
    if (this.idleTimer !== undefined) clearTimeout(this.idleTimer)
    const ms = this.opts.idleCloseMs ?? 0
    if (ms <= 0) return
    this.idleTimer = setTimeout(() => { this.stop() }, ms)
    this.idleTimer.unref?.()
  }

  private handleLine(line: string): void {
    let message: Record<string, unknown>
    try { message = JSON.parse(line) as Record<string, unknown> } catch { return }
    const method = typeof message.method === 'string' ? message.method : undefined
    const id = message.id
    if (id === undefined || id === null) {
      if (method === 'notifications/tools/list_changed') this.opts.onListChanged()
      return
    }
    const pending = this.pending.get(Number(id))
    if (pending === undefined) return
    clearTimeout(pending.timer)
    this.pending.delete(Number(id))
    if (message.error !== undefined && message.error !== null) {
      const err = message.error as { message?: string }
      pending.reject(new Error(typeof err.message === 'string' ? err.message : JSON.stringify(message.error)))
    } else {
      pending.resolve(message.result)
    }
  }

  private send(message: Record<string, unknown>): void {
    this.proc?.stdin.write(JSON.stringify(message) + '\n')
  }

  /** 立即失败全部在途请求(协议违规/终止时);之后 stop() 兜底超时定时器清理。 */
  private failAllPending(message: string): void {
    for (const [id, pending] of [...this.pending]) {
      clearTimeout(pending.timer)
      this.pending.delete(id)
      pending.reject(new Error(message))
    }
  }

  private request(method: string, params: unknown, timeoutMs: number): Promise<unknown> {
    const id = this.nextId++
    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`godot bridge ${method} timed out after ${timeoutMs}ms`))
      }, timeoutMs)
      this.pending.set(id, { resolve, reject, timer })
      this.send({ jsonrpc: '2.0', id, method, params })
    })
  }
}
