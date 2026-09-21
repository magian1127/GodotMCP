import { test } from 'node:test'
import assert from 'node:assert/strict'
import { groupTools, type ToolEntry } from '../client/grouping.js'
import { buildArguments, formFields } from '../client/form-schema.js'
import { loadHistory, pushHistory, saveHistory, summarize, type HistoryItem } from '../client/history.js'

const tools: ToolEntry[] = [
  { name: 'godot_scene_get_tree', description: '场景树' },
  { name: 'godot_asset_query', description: '资源组工具' },
  { name: 'godot_log_read', description: '日志' },
  { name: 'read', description: '读文件' },
  { name: 'pwsh', description: 'shell' },
  { name: 'github_search_doc', description: 'zhipu' },
]

test('groupTools:godot 置顶分离已激活组工具;内置/插件分组', () => {
  const active = new Set(['godot_asset_query'])
  const { groups, godotTotal } = groupTools(tools, 'godot_', active)
  assert.equal(godotTotal, 3)
  const godot = groups.find(g => g.kind === 'godot-resident')!
  assert.equal(godot.tools.length, 2)
  assert.equal(groups[0]!.kind, 'godot-resident')
  const builtin = groups.find(g => g.kind === 'builtin')!
  assert.deepEqual(builtin.tools.map(t => t.name).sort(), ['pwsh', 'read'])
  const plugin = groups.find(g => g.kind === 'plugin')!
  assert.deepEqual(plugin.tools.map(t => t.name), ['github_search_doc'])
  const group = groups.find(g => g.kind === 'godot-group')!
  assert.deepEqual(group.tools.map(t => t.name), ['godot_asset_query'])
})

test('formFields/buildArguments:类型映射与校验', () => {
  const fields = formFields({
    type: 'object',
    properties: {
      name: { type: 'string', description: '名称' },
      count: { type: 'integer' },
      flag: { type: 'boolean' },
      mode: { type: 'string', enum: ['a', 'b'] },
      nested: { type: 'object' },
    },
    required: ['name'],
  })
  assert.deepEqual(fields.map(f => f.type), ['string', 'number', 'boolean', 'enum', 'json'])
  assert.equal(fields[0]!.required, true)
  assert.equal(fields[1]!.required, false)
  const built = buildArguments(fields, { name: 'x', count: '3', flag: true, mode: 'a', nested: '{"k":1}' })
  assert.deepEqual(built.errors, [])
  assert.deepEqual(built.args, { name: 'x', count: 3, flag: true, mode: 'a', nested: { k: 1 } })
  const bad = buildArguments(fields, { count: 'NaN', nested: '{oops' })
  assert.equal(bad.errors.length, 3) // name 必填 + count 非数字 + nested 非法 JSON
})

test('history:载入/压栈/截断/摘要', () => {
  const store = new Map<string, string>()
  const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v) } }
  assert.deepEqual(loadHistory(storage), [])
  let items: HistoryItem[] = []
  for (let i = 0; i < 60; i++) items = pushHistory(items, { id: String(i), name: 't', argsJson: '{}', ok: true, durationMs: 1, at: i, summary: 's' })
  assert.equal(items.length, 50)
  saveHistory(storage, items)
  assert.equal(loadHistory(storage).length, 50)
  assert.equal(summarize([{ type: 'text', text: 'x'.repeat(100) }]).length <= 8 * 1024, true)
})
