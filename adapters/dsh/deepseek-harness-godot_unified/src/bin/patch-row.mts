/**
 * profile 用户 patch 层受管 mcp-client 行的幂等读写(标记文本层)——CLI 专用。
 *
 * 编辑策略(沿智谱三件套已验证模式):只追加/删除本 CLI 自己的
 * `# dsh-godot:begin/end` 标记块,绝不重写用户其它内容;文件始终是合法的
 * 顶层 YAML 数组(空时保留 `[]`)。原子写 + 同目录写锁 + 陈锁回收。
 */
import { closeSync, existsSync, openSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from 'node:fs'
import { BUNDLE_ROW_ID, MCP_ROW_ID, ROW_BEGIN, ROW_END } from './cli/constants.mjs'
import { patchPath } from './cli/paths.mjs'

function escapeRe(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/** 新建 patch 文件时的说明头。 */
const NEW_FILE_HEADER = `# Your patch layer for this dsh profile, applied after every bundle layer:
# a top-level YAML array of loader patch entries (id-targeted config
# overrides, disables, and insert lists).
`

/** 原子写入:同目录临时文件 + rename,失败时清理临时文件。 */
function writeAtomic(path: string, data: string): void {
  const tmp = `${path}.tmp-${process.pid}-${Date.now()}`
  try {
    writeFileSync(tmp, data)
    renameSync(tmp, path)
  } catch (error: unknown) {
    try {
      unlinkSync(tmp)
    } catch {
      // 临时文件已不存在,忽略。
    }
    throw error
  }
}

/** 锁参数:退避 25ms、总上限 5s;超过 30s 的锁视为崩溃残留并回收。 */
const LOCK_RETRY_MS = 25
const LOCK_TIMEOUT_MS = 5_000
const LOCK_STALE_MS = 30_000

/** 同步退避(Atomics.wait 是 Node 主线程的标准同步睡眠)。 */
function sleepSync(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms)
}

/**
 * 获取 patch 写锁(同目录 `<patch>.lock`,排他创建)后执行 fn,finally 释放。
 * 只防本 CLI 多实例并发的读-改-写丢失;官方 dsh 工具不识别此锁,对它们
 * 仍有 writeAtomic 的中断原子性。
 */
function withPatchLock<T>(path: string, fn: () => T): T {
  const lockPath = `${path}.lock`
  const deadline = Date.now() + LOCK_TIMEOUT_MS
  for (;;) {
    try {
      const handle = openSync(lockPath, 'wx')
      closeSync(handle)
      break
    } catch (error: unknown) {
      if ((error as { code?: string }).code !== 'EEXIST') throw error
      try {
        if (Date.now() - statSync(lockPath).mtimeMs > LOCK_STALE_MS) {
          unlinkSync(lockPath)
          continue
        }
      } catch {
        continue // 锁文件刚被释放或被回收:立即重试。
      }
      if (Date.now() >= deadline) {
        throw new Error(`无法获得 patch 写锁: ${lockPath} 被其它实例占用超过 ${LOCK_TIMEOUT_MS}ms`)
      }
      sleepSync(LOCK_RETRY_MS)
    }
  }
  try {
    return fn()
  } finally {
    try {
      unlinkSync(lockPath)
    } catch {
      // 锁已被过期回收或已不存在:忽略。
    }
  }
}

/** mcp-client 行的受管块配置来源(install 参数)。 */
export interface McpRowOptions {
  rowId: string
  serverName: string
  /** server dist 的绝对路径(已转正斜杠)。 */
  serverDist: string
  /** Godot 项目绝对路径(已转正斜杠)。 */
  projectPath: string
  /** GodotMCP 工作区根(正斜杠);--server-dist 直装时可缺省。 */
  godotMcpRoot?: string
  readOnly: boolean
  rateLimit: number | null
  unsafe?: boolean
  editorPort: number | null
  runtimePort: number | null
  /** 每次工具调用超时(ms);null = 用 mcp-client 默认 60s。 */
  timeoutMs: number | null
}

/** YAML 单引号标量:内部单引号加倍。 */
export function yamlScalar(value: string): string {
  return `'${value.replace(/'/g, "''")}'`
}

/** 生成受管块内容:mcp-client 桥接行。 */
export function mcpRowBlock(opts: McpRowOptions): string {
  const env: Array<[string, string]> = [
    ['GODOT_MCP_PROJECT_PATH', opts.projectPath],
    ['GODOT_MCP_CONFIG_VERSION', '1'],
  ]
  if (opts.readOnly) env.push(['GODOT_MCP_READ_ONLY', '1'])
  if (opts.rateLimit !== null && opts.rateLimit > 0) env.push(['GODOT_MCP_RATE_LIMIT', String(opts.rateLimit)])
  if (opts.unsafe) env.push(['GODOT_MCP_UNSAFE', '1'])
  if (opts.editorPort !== null) env.push(['GODOT_MCP_EDITOR_PORT', String(opts.editorPort)])
  if (opts.runtimePort !== null) env.push(['GODOT_MCP_RUNTIME_PORT', String(opts.runtimePort)])

  const lines: string[] = [
    `${ROW_BEGIN} — managed by dsh-godot CLI (install/uninstall); do not edit by hand`,
    '- insert:',
    `    - id: ${opts.rowId}`,
    `      name: '@deepseek-ai/dsh-mcp-client'`,
    '      config:',
    `        serverName: ${opts.serverName}`,
    '        transport: stdio',
    '        command: node',
    '        args:',
    `          - ${yamlScalar(opts.serverDist)}`,
    '        env:',
  ]
  for (const [key, value] of env) lines.push(`          ${key}: ${yamlScalar(value)}`)
  if (opts.timeoutMs !== null) lines.push(`        toolCallTimeoutMs: ${opts.timeoutMs}`)
  // 双条目:同块追加 dsh-godot bundle 行 config 覆盖(serverName + 可选根/项目)。
  const configLines: string[] = [
    `- id: ${BUNDLE_ROW_ID}`,
    '  config:',
    `    serverName: ${opts.serverName}`,
  ]
  if (opts.godotMcpRoot !== undefined && opts.godotMcpRoot !== '') {
    configLines.push(`    godotMcpRoot: ${yamlScalar(opts.godotMcpRoot)}`)
  }
  if (opts.projectPath !== '') {
    configLines.push(`    projectPath: ${yamlScalar(opts.projectPath)}`)
  }
  if (opts.unsafe === true) configLines.push('    unsafe: true')
  lines.push(...configLines)
  lines.push(ROW_END)
  return lines.join('\n')
}

/** 从受管块文本解析关键配置(status 用);块缺失返回 null。 */
export function parseMcpRowBlock(block: string): { serverName: string; serverDist: string; projectPath: string; readOnly: boolean } | null {
  const serverName = block.match(/^ {8}serverName: (\S+)$/m)?.[1]
  const serverDist = block.match(/^ {10}- '([^']+)'$/m)?.[1]
  const projectPath = block.match(/^ {10}GODOT_MCP_PROJECT_PATH: '([^']+)'$/m)?.[1]
  if (serverName === undefined || serverDist === undefined || projectPath === undefined) return null
  return { serverName, serverDist, projectPath, readOnly: /GODOT_MCP_READ_ONLY: '1'/.test(block) }
}

/**
 * 幂等写入受管块。返回 true 表示本次实际写入。
 * 已存在标记块时按 incoming 原地替换;无标记块时追加(处理流式 `[]` 尾)。
 */
export function addManagedRow(block: string, profile: string = 'web'): boolean {
  const path = patchPath(profile)
  return withPatchLock(path, function () {
    const existing = existsSync(path) ? readFileSync(path, 'utf8') : null
    if (existing === null) {
      writeAtomic(path, NEW_FILE_HEADER + block + '\n')
      return true
    }
    if (existing.includes(ROW_BEGIN)) {
      const re = new RegExp(`${escapeRe(ROW_BEGIN)}[^\\n]*\\n[\\s\\S]*?\\n${escapeRe(ROW_END)}[^\\n]*\\n?`, 'g')
      const next = existing.replace(re, block + '\n')
      if (next === existing) return false
      writeAtomic(path, next)
      return true
    }
    let next = existing
    // 去掉行尾的流式空数组 `[]`,以便追加块式条目。
    const lines = next.split('\n')
    let tail = lines.length - 1
    while (tail >= 0 && lines[tail].trim() === '') tail -= 1
    if (tail >= 0 && /^\s*\[\]\s*$/.test(lines[tail])) lines.splice(tail, 1)
    next = lines.join('\n')
    if (next !== '' && !next.endsWith('\n')) next += '\n'
    if (next !== '') next += '\n'
    writeAtomic(path, next + block + '\n')
    return true
  })
}

/**
 * 删除受管块(含旧式无标记的 godot-mcp 行)。返回 true 表示删到了。
 * 删除后若只剩注释,写回合法 `[]`。
 */
export function removeManagedRow(profile: string = 'web'): boolean {
  const path = patchPath(profile)
  if (!existsSync(path)) return false
  return withPatchLock(path, function () {
    if (!existsSync(path)) return false // 锁等待期间被并发删除:锁内重验。
    const original = readFileSync(path, 'utf8')
    let next = original
    let removed = false
    if (next.includes(ROW_BEGIN)) {
      const re = new RegExp(`\\n?${escapeRe(ROW_BEGIN)}[^\\n]*\\n[\\s\\S]*?\\n${escapeRe(ROW_END)}[^\\n]*\\n?`, 'g')
      const after = next.replace(re, '\n')
      removed = after !== next
      next = after
    }
    // 兼容手写行(无标记块):按 id 清除(mcp 行 + dsh-godot 覆盖行)。
    for (const id of [MCP_ROW_ID, BUNDLE_ROW_ID]) {
      const legacy = new RegExp(`\\n?- id: ${escapeRe(id)}\\n([^\\n]*(?:\\n(?!\\s*- )[ ]{2,}[^\\n]*)*)`, 'g')
      const after = next.replace(legacy, '')
      if (after !== next) {
        next = after
        removed = true
      }
    }
    if (!removed) return false
    const meaningful = next.split('\n').filter((line) => {
      const t = line.trim()
      return t !== '' && !t.startsWith('#') && !/^\[\]\s*$/.test(t)
    })
    if (meaningful.length === 0) {
      const eol = next.includes('\r\n') ? '\r\n' : '\n'
      let preserved = next
      if (preserved.trim() !== '' && !preserved.endsWith(eol)) preserved += eol
      writeAtomic(path, preserved + '[]' + eol)
      return true
    }
    writeAtomic(path, next)
    return true
  })
}

/** patch 中是否存在受管块或本 CLI 的行。 */
export function hasManagedRow(profile: string = 'web'): boolean {
  const path = patchPath(profile)
  if (!existsSync(path)) return false
  const text = readFileSync(path, 'utf8')
  return text.includes(ROW_BEGIN) || text.includes(`- id: ${MCP_ROW_ID}`)
}

/** 读取受管块文本(存在时);否则 null。 */
export function readManagedBlock(profile: string = 'web'): string | null {
  const path = patchPath(profile)
  if (!existsSync(path)) return null
  const text = readFileSync(path, 'utf8')
  if (!text.includes(ROW_BEGIN)) return null
  const match = text.match(new RegExp(`${escapeRe(ROW_BEGIN)}[^\\n]*\\n([\\s\\S]*?)\\n${escapeRe(ROW_END)}`))
  return match !== null ? match[1] : null
}
