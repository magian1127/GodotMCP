import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { promptSections, readSkills } from '../workbench/skills.js'
import { resolveSkillsRoot } from '../workbench/handlers.js'

function skillDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'wb-skills-'))
  const base = join(dir, 'plugin', 'godot-mcp-unified', 'skills')
  mkdirSync(join(base, 'godot-playtest'), { recursive: true })
  writeFileSync(join(base, 'godot-playtest', 'SKILL.md'), '# 游玩测试\n中文正文')
  writeFileSync(join(base, 'godot-playtest', 'SKILL.en.md'), '# Playtest\nEnglish body')
  return dir
}

test('readSkills:zh 主/en 回退;无根返回空', () => {
  const dir = skillDir()
  try {
    const zh = readSkills(dir, 'zh')
    assert.equal(zh.length, 1)
    assert.equal(zh[0]!.title, '游玩测试')
    assert.ok(zh[0]!.content.includes('中文正文'))
    const en = readSkills(dir, 'en')
    assert.equal(en[0]!.title, 'Playtest')
    assert.deepEqual(readSkills(undefined, 'zh'), [])
  } finally { rmSync(dir, { recursive: true, force: true }) }
})

test('promptSections:assemble 投影;服务缺席/抛错 → 空', async () => {
  const ok = await promptSections({ assemble: async () => ({ sections: [{ name: 'a', text: 't' }] }) })
  assert.deepEqual(ok, [{ name: 'a', text: 't' }])
  assert.deepEqual(await promptSections(undefined), [])
  assert.deepEqual(await promptSections({ assemble: async () => { throw new Error('x') } }), [])
})

test('resolveSkillsRoot:优先 godotMcpRoot;否则从 serverDist 向上推导工作区根', () => {
  // 显式 godotMcpRoot 优先。
  assert.equal(resolveSkillsRoot({ serverName: 'godot', godotMcpRoot: 'D:/ws/GodotMCP' } as never), 'D:/ws/GodotMCP')
  // serverDist 以固定后缀结尾 → 推导工作区根(正斜杠)。
  assert.equal(
    resolveSkillsRoot({ serverName: 'godot', serverDistPath: 'D:/repo/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js' } as never),
    'D:/repo/GodotMCP',
  )
  // 反斜杠路径同义。
  assert.equal(
    resolveSkillsRoot({ serverName: 'godot', serverDistPath: 'D:\\repo\\GodotMCP\\plugin\\godot-mcp-unified\\server\\dist\\index.js' } as never),
    'D:/repo/GodotMCP',
  )
  // serverDist 不以该后缀结尾 → undefined(避免把无关全路径误当根)。
  assert.equal(resolveSkillsRoot({ serverName: 'godot', serverDistPath: 'D:/x/dist/index.js' } as never), undefined)
  // 无 serverDist 也无 godotMcpRoot → undefined。
  assert.equal(resolveSkillsRoot({ serverName: 'godot' } as never), undefined)
})

test('resolveSkillsRoot:pathStore.getServerDist 优先于 serverDistPath', () => {
  const store = { getServerDist: () => 'D:/store/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js' }
  assert.equal(
    resolveSkillsRoot({ serverName: 'godot', serverDistPath: 'D:/cfg/other.js', pathStore: store } as never),
    'D:/store/GodotMCP',
  )
})
