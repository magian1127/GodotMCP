// /call 执行:AbortController 表 + 结果投影(.value.content 原始块优先,图片 4MB 截断)。
import type { ToolExecutionResultShape, ToolsServiceShape } from '../types.js'

export const MAX_IMAGE_DATA_CHARS = 4 * 1024 * 1024
export class CallIdConflictError extends Error {}

export interface OutBlock {
  type: 'text' | 'image' | 'other'
  text?: string
  mimeType?: string
  data?: string
  truncated?: boolean
  summary?: string
}

export interface CallOutcome {
  callId: string
  name: string
  isError: boolean
  durationMs: number
  content: OutBlock[]
  error?: string
}

/** 从结果 .value(McpResult)投影原始内容块;图片直通 base64(超限截断)。 */
export function projectValueBlocks(value: unknown): OutBlock[] {
  const content = (value as { content?: unknown } | undefined)?.content
  if (!Array.isArray(content)) return []
  const out: OutBlock[] = []
  for (const raw of content) {
    if (raw === null || typeof raw !== 'object') continue
    const block = raw as Record<string, unknown>
    if (block.type === 'text' && typeof block.text === 'string') {
      out.push({ type: 'text', text: block.text })
    } else if (block.type === 'image' && typeof block.data === 'string') {
      const data = block.data
      out.push(data.length > MAX_IMAGE_DATA_CHARS
        ? { type: 'image', mimeType: String(block.mimeType ?? 'image/png'), data: data.slice(0, MAX_IMAGE_DATA_CHARS), truncated: true }
        : { type: 'image', mimeType: String(block.mimeType ?? 'image/png'), data })
    } else {
      out.push({ type: 'other', summary: `未支持块类型 ${String(block.type)}` })
    }
  }
  return out
}

function contentText(content: unknown): string {
  if (!Array.isArray(content)) return ''
  return content
    .map(b => (b !== null && typeof b === 'object' && (b as Record<string, unknown>).type === 'text' ? String((b as Record<string, unknown>).text ?? '') : ''))
    .filter(Boolean).join('\n')
}

/** 工作台工具执行器:callId 序列 + 进行中调用的取消表。 */
export class WorkbenchExecutor {
  private seq = 0
  private readonly active = new Map<string, AbortController>()

  async call(tools: ToolsServiceShape, name: string, args: unknown, clientCallId?: string): Promise<CallOutcome> {
    let callId: string
    if (typeof clientCallId === 'string' && clientCallId !== '') {
      if (this.active.has(clientCallId)) throw new CallIdConflictError(`callId 已在使用: ${clientCallId}`)
      callId = clientCallId
    } else {
      do { callId = `godot-workbench-${++this.seq}` } while (this.active.has(callId))
    }
    const controller = new AbortController()
    this.active.set(callId, controller)
    const started = Date.now()
    try {
      const result = await tools.execute({ callId, name, arguments: args, signal: controller.signal })
      const durationMs = Date.now() - started
      if (result.isError) {
        const message = result.error?.message ?? contentText(result.content)
        return { callId, name, isError: true, durationMs, content: projectValueBlocks(result.value), error: message !== '' ? message : '工具执行失败' }
      }
      const projected = projectValueBlocks(result.value)
      return { callId, name, isError: false, durationMs, content: projected.length > 0 ? projected : [{ type: 'text', text: contentText(result.content) }] }
    } finally {
      this.active.delete(callId)
    }
  }

  cancel(callId: string): boolean {
    const controller = this.active.get(callId)
    if (controller === undefined) return false
    controller.abort()
    return true
  }

  get activeCount(): number {
    return this.active.size
  }
}
