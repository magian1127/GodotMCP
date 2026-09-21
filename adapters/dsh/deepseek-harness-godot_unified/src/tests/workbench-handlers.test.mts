import { test } from 'node:test'
import assert from 'node:assert/strict'
import { statusData, toolsData, ToolsCatalogueCache, type WorkbenchConfig } from '../workbench/handlers.js'
import { projectPathToDir } from '../workbench/path-store.js'
import type { ToolSchemaShape, ToolsServiceShape } from '../types.js'

test('projectPathToDir:project.godot 文件归一化为其目录(注册表键形状),目录原样保留', () => {
  assert.equal(projectPathToDir('D:/Games/test-project/project.godot'), 'D:/Games/test-project')
  assert.equal(projectPathToDir('D:\\Games\\test-project\\project.godot'), 'D:\\Games\\test-project')
  assert.equal(projectPathToDir('D:/Games/test-project'), 'D:/Games/test-project')
  assert.equal(projectPathToDir(''), '')
  assert.equal(projectPathToDir('  "D:/x/project.godot"  '), 'D:/x')
})

function fakeTools(list: ToolSchemaShape[], metaResult?: { isError: boolean; value?: unknown; error?: { message?: string } }, seen?: unknown[]): ToolsServiceShape {
  return {
    schemas: () => list,
    get: name => list.find(t => t.name === name),
    execute: async ({ name, arguments: args }) => {
      if (name.endsWith('discover_tools')) { seen?.push(args); return metaResult ?? { isError: false, value: { content: [{ type: 'text', text: JSON.stringify({ groups: [{ name: 'audio', description: '音频', tools: ['a'], active: false }] }) }] } } }
      return { isError: false, value: { content: [{ type: 'text', text: 'ok' }] } }
    },
  }
}

const config: WorkbenchConfig = { serverName: 'godot', projectPath: undefined, godotMcpRoot: undefined }

test('statusData:桥接工具计数与只读判据', async () => {
  const names = ['godot_scene_get_tree', 'godot_project_get_settings', 'godot_log_read', 'other_x', 'read']
  const data = await statusData(fakeTools(names.map(n => ({ name: n }))), config)
  assert.equal(data.bridge.toolCount, 3)
  // 缺少修改类常驻工具 → 只读判定 true。
  assert.equal(data.readOnly, true)
  const full = await statusData(fakeTools([...names, 'godot_editor_save_scene', 'godot_node_set_property'].map(n => ({ name: n }))), config)
  assert.equal(full.readOnly, false)
  assert.equal(full.editor, null)
  assert.equal(data.unsafe, false)
  const unsafe = await statusData(fakeTools(names.map(n => ({ name: n }))), { ...config, unsafe: true })
  assert.equal(unsafe.unsafe, true)
})

test('toolsData:discover_tools 组目录解析与静默失败', async () => {
  const tools = fakeTools([{ name: 'godot_discover_tools', parameters: { properties: { query: { type: 'string' } } } }, { name: 'godot_scene_get_tree' }])
  const seen: unknown[] = []; const data = await toolsData(fakeTools([{ name: 'godot_discover_tools', parameters: { properties: { query: { type: 'string' } } } }, { name: 'godot_scene_get_tree' }], undefined, seen), config)
  assert.deepEqual(seen[0], {})
  assert.equal(data.tools.length, 2)
  assert.equal(data.godotGroups.length, 1)
  assert.equal(data.godotGroups[0]!.name, 'audio')
  const broken = await toolsData(fakeTools([{ name: 'godot_discover_tools' }], { isError: true, error: { message: 'x' } }), config)
  assert.deepEqual(broken.godotGroups, [])
})

test('toolsData:桥接离线(官方行未同步工具)时不调用 discover_tools(浏览目录不启动 server)', async () => {
  const seen: unknown[] = []
  // 离线=工具面没有 discover_tools 元工具(官方 mcp-client 行断连/未同步时注销全部工具)。
  const tools = fakeTools([{ name: 'godot_scene_get_tree' }], undefined, seen)
  const offline = await toolsData(tools, config)
  assert.equal(seen.length, 0)
  assert.deepEqual(offline.godotGroups, [])
  assert.equal(offline.tools.length, 1)
  // 工具面完全为空(server 冷启动中)同样安全默认。
  const absent = await toolsData(fakeTools([], undefined, seen), { serverName: 'godot' })
  assert.equal(seen.length, 0)
  assert.deepEqual(absent.godotGroups, [])
})

test('ToolsCatalogueCache:TTL 内直回字节,invalidate/到期后重算', async () => {
  const seen: unknown[] = []
  const tools = fakeTools([{ name: 'godot_discover_tools' }, { name: 'godot_scene_get_tree' }], undefined, seen)
  const cache = new ToolsCatalogueCache(60_000)
  // 首次:真实计算(1 次 discover_tools 往返)。
  const first = await cache.bodyOf(tools, config)
  assert.equal(seen.length, 1)
  // TTL 内:同字节直回,不再触发 discover_tools。
  const second = await cache.bodyOf(tools, config)
  assert.equal(second, first)
  assert.equal(seen.length, 1)
  // invalidate(对应 tools/change 或 /api/call 完成):强制重算。
  cache.invalidate()
  await cache.bodyOf(tools, config)
  assert.equal(seen.length, 2)
  // TTL=0:每次都重算。
  const zero = new ToolsCatalogueCache(0)
  await zero.bodyOf(tools, config)
  await zero.bodyOf(tools, config)
  assert.equal(seen.length, 4)
  // 缓存体与直接序列化结果一致(前端契约不变)。
  assert.equal(first, JSON.stringify(await toolsData(tools, config)))
})
