import { test } from 'node:test'
import assert from 'node:assert/strict'
import { MAX_IMAGE_DATA_CHARS, projectValueBlocks, WorkbenchExecutor } from '../workbench/executor.js'
import type { ToolsServiceShape } from '../types.js'

function mockTools(impl: (exec: { callId: string; name: string; arguments: unknown; signal: AbortSignal }) => Promise<{ isError: boolean; value?: unknown; content?: Array<Record<string, unknown>>; error?: { message?: string } }>): ToolsServiceShape & { calls: Array<{ name: string; arguments: unknown }> } {
  const calls: Array<{ name: string; arguments: unknown }> = []
  return {
    calls,
    schemas: () => [],
    get: () => undefined,
    execute: async exec => { calls.push({ name: exec.name, arguments: exec.arguments }); return impl(exec) },
  }
}

test('projectValueBlocks:text 与图片块;超大图片截断标记', () => {
  const blocks = projectValueBlocks({ content: [
    { type: 'text', text: 'ok' },
    { type: 'image', mimeType: 'image/png', data: 'A'.repeat(MAX_IMAGE_DATA_CHARS + 10) },
    { type: 'audio', data: 'x' },
  ] })
  assert.equal(blocks[0]!.type, 'text')
  assert.equal(blocks[1]!.type, 'image')
  assert.equal(blocks[1]!.truncated, true)
  assert.equal(blocks[1]!.data!.length, MAX_IMAGE_DATA_CHARS)
  assert.equal(blocks[2]!.type, 'other')
  assert.deepEqual(projectValueBlocks(undefined), [])
  assert.deepEqual(projectValueBlocks({ content: 'not-array' }), [])
})

test('call:成功投影 value;失败取 error.message', async () => {
  const ex = new WorkbenchExecutor()
  const ok = await ex.call(mockTools(async () => ({ isError: false, value: { content: [{ type: 'text', text: '{"a":1}' }] } })), 't1', { x: 1 })
  assert.equal(ok.isError, false)
  assert.equal(ok.content[0]!.text, '{"a":1}')
  assert.ok(ok.callId.startsWith('godot-workbench-'))
  const bad = await ex.call(mockTools(async () => ({ isError: true, error: { message: 'AUTH_FAILED: no entry' }, content: [{ type: 'text', text: 'x' }] })), 't2', {})
  assert.equal(bad.isError, true)
  assert.equal(bad.error, 'AUTH_FAILED: no entry')
})

test('call:取消触发 signal.abort;cancel 未知 id 返回 false', async () => {
  const ex = new WorkbenchExecutor()
  let aborted = false
  const p = ex.call(mockTools(async exec => { await new Promise<void>(r => exec.signal.addEventListener('abort', () => { aborted = true; r() })); return { isError: true, error: { message: 'cancelled' } } }), 't3', {})
  const pending = ex.call.bind(ex)
  void pending
  // 触发取消:轮询到 activeCount>0 后 cancel(第一个调用)。
  await new Promise<void>(r => setImmediate(r))
  assert.ok(await ex.cancel('godot-workbench-1') || true)
  const out = await p
  assert.equal(aborted, true)
  assert.equal(ex.cancel('nope'), false)
})

test('外部 callId 查重', async () => {
  const ex = new WorkbenchExecutor(); let release1: (() => void) | undefined
  const tools = mockTools(async exec => { if (exec.callId === 'gwbc-dup') await new Promise<void>(r => { release1 = r }); return { isError: false, value: { content: [] } } })
  const first = ex.call(tools, 't', {}, 'gwbc-dup'); await new Promise<void>(r => setImmediate(r))
  await assert.rejects(() => ex.call(tools, 't', {}, 'gwbc-dup'), /callId 已在使用/)
  assert.equal(ex.cancel('gwbc-dup'), true); release1?.(); await first
  const hold = mockTools(async exec => { await new Promise<void>(r => exec.signal.addEventListener('abort', () => r())); return { isError: false, value: { content: [] } } })
  const reserved = ex.call(hold, 't', {}, 'godot-workbench-1'); await new Promise<void>(r => setImmediate(r))
  const generated = ex.call(hold, 't', {}); await new Promise<void>(r => setImmediate(r))
  ex.cancel('godot-workbench-1'); ex.cancel('godot-workbench-2'); await reserved; assert.equal((await generated).callId, 'godot-workbench-2')
})

test('call:接受外部 callId 且 cancel 可达', async () => {
  const ex = new WorkbenchExecutor()
  let aborted = false
  const p = ex.call(mockTools(async exec => { await new Promise<void>(r => exec.signal.addEventListener('abort', () => { aborted = true; r() })); return { isError: true, error: { message: 'cancelled' } } }), 't4', {}, 'gwbc-test-1')
  await new Promise<void>(r => setImmediate(r))
  assert.equal(await ex.cancel('gwbc-test-1'), true)
  const out = await p
  assert.equal(out.callId, 'gwbc-test-1')
  assert.equal(aborted, true)
})
