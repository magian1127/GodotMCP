// patch-row 标记块管理测试:新建/追加/幂等替换/删除/解析/对齐断言。
import { mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { addManagedRow, hasManagedRow, mcpRowBlock, parseMcpRowBlock, readManagedBlock, removeManagedRow, type McpRowOptions } from '../bin/patch-row.mjs'
import { MCP_ROW_ID, ROW_BEGIN, ROW_END, SERVER_NAME_DEFAULT } from '../bin/cli/constants.mjs'
import { BUNDLE_ROW_ID, PKG, SECTION_NAME } from '../constants.js'

function tempHome(): string {
  const dir = mkdtempSync(join(tmpdir(), 'dsh-godot-test-'))
  mkdirSync(join(dir, 'profiles', 'web'), { recursive: true })
  process.env.DSH_HOME = dir
  return dir
}

function baseOptions(): McpRowOptions {
  return {
    rowId: MCP_ROW_ID,
    serverName: SERVER_NAME_DEFAULT,
    serverDist: 'D:/repo/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js',
    projectPath: 'D:/games/demo',
    readOnly: false,
    rateLimit: null,
    unsafe: false,
    editorPort: null,
    runtimePort: null,
    timeoutMs: null,
  }
}

test('标记与行 id 在 host/CLI 两侧保持一致', () => {
  // cordis.patch.yml 的行 id 与 bundle 侧常量同步。
  assert.equal(BUNDLE_ROW_ID, 'dsh-godot')
  assert.equal(PKG, 'deepseek-harness-godot_unified')
  assert.equal(SECTION_NAME, 'godot-mcp-workflow')
  assert.equal(ROW_BEGIN, '# dsh-godot:begin')
  assert.equal(ROW_END, '# dsh-godot:end')
})

test('mcpRowBlock 生成合法 YAML 形态且 parseMcpRowBlock 可往返', () => {
  const block = mcpRowBlock({ ...baseOptions(), readOnly: true, rateLimit: 5, timeoutMs: 90000, unsafe: true })
  assert.ok(block.startsWith(ROW_BEGIN))
  assert.ok(block.endsWith(ROW_END))
  assert.ok(block.includes(`- id: ${MCP_ROW_ID}`))
  assert.ok(block.includes("name: '@deepseek-ai/dsh-mcp-client'"))
  assert.ok(block.includes('transport: stdio'))
  assert.ok(block.includes("GODOT_MCP_PROJECT_PATH: 'D:/games/demo'"))
  assert.ok(block.includes("GODOT_MCP_READ_ONLY: '1'"))
  assert.ok(block.includes("GODOT_MCP_RATE_LIMIT: '5'"))
  assert.ok(block.includes('    unsafe: true'))
  assert.ok(block.includes('toolCallTimeoutMs: 90000'))
  // 双条目:含 dsh-godot config 覆盖行。
  assert.ok(block.includes('- id: dsh-godot'))
  assert.ok(block.includes('serverName: godot'))
  const parsed = parseMcpRowBlock(block)
  assert.notEqual(parsed, null)
  assert.equal(parsed!.serverName, 'godot')
  assert.equal(parsed!.serverDist, 'D:/repo/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js')
  assert.equal(parsed!.projectPath, 'D:/games/demo')
  assert.equal(parsed!.readOnly, true)
  // 默认(非只读)块解析出 readOnly=false。
  const plain = parseMcpRowBlock(mcpRowBlock(baseOptions()))
  assert.equal(plain!.readOnly, false)
})

test('install:新建文件(带说明头)→ 幂等替换 → uninstall 恢复 []', () => {
  const home = tempHome()
  try {
    const block = mcpRowBlock(baseOptions())
    assert.equal(addManagedRow(block, 'web'), true)
    let text = readFileSync(join(home, 'profiles', 'web', 'cordis.patch.yml'), 'utf8')
    assert.ok(text.includes('# Your patch layer'))
    assert.ok(text.includes(ROW_BEGIN))
    assert.ok(text.endsWith(ROW_END + '\n'))

    // 幂等:相同块替换为 no-op;参数变化时原地重写。
    assert.equal(addManagedRow(block, 'web'), false)
    const changed = mcpRowBlock({ ...baseOptions(), readOnly: true })
    assert.equal(addManagedRow(changed, 'web'), true)
    text = readFileSync(join(home, 'profiles', 'web', 'cordis.patch.yml'), 'utf8')
    assert.ok(text.includes("GODOT_MCP_READ_ONLY: '1'"))
    assert.equal((text.match(new RegExp(ROW_BEGIN, 'g')) ?? []).length, 1)

    assert.equal(removeManagedRow('web'), true)
    text = readFileSync(join(home, 'profiles', 'web', 'cordis.patch.yml'), 'utf8')
    assert.ok(!text.includes(ROW_BEGIN))
    assert.ok(text.trim().endsWith('[]'))
    assert.equal(hasManagedRow('web'), false)
    assert.equal(removeManagedRow('web'), false)
  } finally {
    rmSync(home, { recursive: true, force: true })
  }
})

test('install:既有 `[]` 尾被安全替换,用户行绝不被改动', () => {
  const home = tempHome()
  try {
    const patchFile = join(home, 'profiles', 'web', 'cordis.patch.yml')
    const userContent = '# user header\n[]\n'
    writeFileSync(patchFile, userContent)
    addManagedRow(mcpRowBlock(baseOptions()), 'web')
    const text = readFileSync(patchFile, 'utf8')
    assert.ok(text.includes('# user header'))
    assert.ok(text.includes(ROW_BEGIN))
    // 用户行场景:手工 insert 行保留。
    writeFileSync(patchFile, '- id: user-row\n  name: \'some-package\'\n')
    addManagedRow(mcpRowBlock(baseOptions()), 'web')
    const after = readFileSync(patchFile, 'utf8')
    assert.ok(after.includes('- id: user-row'))
    assert.ok(after.includes(ROW_BEGIN))
    removeManagedRow('web')
    const final = readFileSync(patchFile, 'utf8')
    assert.ok(final.includes('- id: user-row'))
    assert.ok(!final.includes(ROW_BEGIN))
    // 只剩注释/空时不误删文件。
    assert.ok(existsSync(patchFile))
  } finally {
    rmSync(home, { recursive: true, force: true })
  }
})

test('字节保留:块外 CRLF 与尾空白在 install/uninstall 后不变', () => {
  const home = tempHome(); try {
    const file = join(home, 'profiles', 'web', 'cordis.patch.yml'); const user = "# user note \r\n- id: keep\r\n  name: 'x'\r\n"; writeFileSync(file, user)
    addManagedRow(mcpRowBlock(baseOptions()), 'web'); removeManagedRow('web'); const text = readFileSync(file, 'utf8'); assert.ok(text.includes(user))
  } finally { rmSync(home, { recursive: true, force: true }) }
})

test('字节保留:comment-only 分支逐字节', () => {
  const home = tempHome()
  try {
    const patchFile = join(home, 'profiles', 'web', 'cordis.patch.yml')
    const comments = '# a  \r\n\r\n  \r\n# b\r\n'
    writeFileSync(patchFile, comments)
    addManagedRow(mcpRowBlock(baseOptions()), 'web')
    removeManagedRow('web')
    const after = readFileSync(patchFile, 'utf8')
    assert.ok(after.startsWith(comments), '注释区逐字节保留')
    assert.match(after.slice(comments.length), /^[\s\r\n]*\[\]\r\n$/, '剩余仅为空白+合法数组')
  } finally {
    rmSync(home, { recursive: true, force: true })
  }
})

test('readManagedBlock:无块返回 null,有块返回块体', () => {
  const home = tempHome()
  try {
    assert.equal(readManagedBlock('web'), null)
    addManagedRow(mcpRowBlock(baseOptions()), 'web')
    const block = readManagedBlock('web')
    assert.notEqual(block, null)
    assert.ok(block!.includes(`- id: ${MCP_ROW_ID}`))
    assert.ok(!block!.includes(ROW_BEGIN))
  } finally {
    rmSync(home, { recursive: true, force: true })
  }
})

test('双条目:--server-dist 安装(无根)时省略 godotMcpRoot', () => {
  const block = mcpRowBlock({ ...baseOptions(), serverDist: 'D:/x/dist/index.js' })
  assert.ok(block.includes('projectPath: ' + "'D:/games/demo'"))
  assert.ok(!block.includes('godotMcpRoot'))
})

test('uninstall 同时清理 godot-mcp 行与 dsh-godot 覆盖行', () => {
  const home = tempHome()
  try {
    addManagedRow(mcpRowBlock(baseOptions()), 'web')
    assert.equal(removeManagedRow('web'), true)
    const text = readFileSync(join(home, 'profiles', 'web', 'cordis.patch.yml'), 'utf8')
    assert.ok(!text.includes('godot-mcp'))
    assert.ok(!text.includes('dsh-godot'))
    assert.ok(!text.includes(ROW_BEGIN))
  } finally {
    rmSync(home, { recursive: true, force: true })
  }
})
