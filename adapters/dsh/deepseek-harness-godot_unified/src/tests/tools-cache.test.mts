// 工具清单缓存存储:归一化/持久化/路径绑定字段。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { mkdtempSync, rmSync, readFileSync, writeFileSync, existsSync } from 'node:fs'
import { ToolsCacheStore, normalizeToolsCache, type ToolsCacheFile } from '../bridge/tools-cache.js'

function tempFile(): string {
  const dir = mkdtempSync(join(tmpdir(), 'dsh-godot-cache-'))
  return join(dir, 'tools-cache.json')
}

test('normalizeToolsCache:合法快照保留,空/非法回退 undefined', () => {
  const good = normalizeToolsCache({
    fetchedAt: '2026-09-08T00:00:00Z',
    serverDist: 'D:/x/dist/index.js',
    projectPath: 'D:/games/x',
    tools: [{ name: 'scene_get_tree', description: 'd', inputSchema: { type: 'object' } }],
  })
  assert.equal(good?.tools.length, 1)
  assert.equal(good?.tools[0]!.name, 'scene_get_tree')
  assert.equal(normalizeToolsCache(null), undefined)
  assert.equal(normalizeToolsCache({ tools: 'junk' }), undefined)
  assert.equal(normalizeToolsCache({ tools: [{ name: '' }, null] }), undefined)
  // 部分非法条目被过滤;上限 500。
  const many = normalizeToolsCache({ tools: Array.from({ length: 600 }, (_, i) => ({ name: `t${i}` })) })
  assert.equal(many?.tools.length, 500)
})

test('ToolsCacheStore:save/get 往返(记忆 + 落盘);损坏文件回退 undefined', () => {
  const file = tempFile()
  try {
    const store = new ToolsCacheStore(file)
    assert.equal(store.get(), undefined)
    store.save({ serverDist: 'D:/dist/index.js', tools: [{ name: 'scene_get_tree', inputSchema: { type: 'object' } }] })
    const cached = store.get()
    assert.equal(cached?.tools[0]!.name, 'scene_get_tree')
    assert.ok(existsSync(file))
    const onDisk = JSON.parse(readFileSync(file, 'utf8')) as ToolsCacheFile
    assert.equal(onDisk.tools[0]!.name, 'scene_get_tree')
    assert.equal(onDisk.serverDist, 'D:/dist/index.js')
    // 新实例跨进程读回。
    const second = new ToolsCacheStore(file)
    assert.equal(second.get()?.tools.length, 1)
    // 损坏文件 → 安全回退。
    writeFileSync(file, '{oops', 'utf8')
    const third = new ToolsCacheStore(file)
    assert.equal(third.get(), undefined)
  } finally {
    rmSync(join(file, '..'), { recursive: true, force: true })
  }
})
