// bundle patch 声明守护:DSH 0.1.6+ 起 agent preset 只能由 bundle patch 里的
// `@deepseek-ai/dsh-agent-preset` 声明行注册(legacy 的 $DSH_HOME/.agent-presets/<id>/
// 目录已不再被读取)。此回归测试锁住随包发布的 cordis.patch.yml 形状,防止
// preset 声明行被误删/改 id/丢 skills 解析——该 bug 的表现是"模式选择器里
// 没有 Godot",单测若只断言 host 插件行为无法捕获。
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { BUNDLE_ROW_ID, PKG } from '../constants.js'

/** 包根(patch 文件与被 resolve 的 package.json 同在包目录)。 */
const PACKAGE_DIR = fileURLToPath(new URL('../..', import.meta.url))
const PATCH_FILE = join(PACKAGE_DIR, 'cordis.patch.yml')
const PRESET_ROW_ID = 'preset-godot'
const GODOT_PRESET_ID = 'godot'

/** 极简行扫描:本测试只需顶层 insert 条目与 preset 的 config 标量,不引入 YAML 依赖。 */
function presetRowText(): string {
  const text = readFileSync(PATCH_FILE, 'utf8')
  const start = text.indexOf(`    - id: ${PRESET_ROW_ID}`)
  assert.notEqual(start, -1, `cordis.patch.yml 缺少 ${PRESET_ROW_ID} 声明行`)
  return text.slice(start)
}

test('bundle patch 声明 dsh-godot 与 preset-godot 两行', () => {
  const text = readFileSync(PATCH_FILE, 'utf8')
  assert.ok(text.includes(`- id: ${BUNDLE_ROW_ID}`), '宿主插件行在场')
  assert.ok(text.includes(`- id: ${PRESET_ROW_ID}`), 'preset 声明行在场')
  assert.ok(text.includes("insert:"), '仍为 insert patch')
})

test('preset 行是 @deepseek-ai/dsh-agent-preset 声明且 id 与插件侧判定一致', () => {
  const row = presetRowText()
  assert.ok(row.includes("name: '@deepseek-ai/dsh-agent-preset'"), '模块名正确')
  assert.match(row, new RegExp(`^\\s+id: ${GODOT_PRESET_ID}\\s*$`, 'm'), 'config.id 为 godot')
  assert.match(row, /^\s+name: Godot\s*$/m, '显示名为 Godot')
  assert.match(row, /^\s+description: .+/m, 'description 非空(选择器副标题)')
})

test('preset plugins 非空且保留 standard 的关键能力行', () => {
  const row = presetRowText()
  for (const required of [
    '@deepseek-ai/dsh-persona',
    '@deepseek-ai/dsh-agent-instructions',
    '@deepseek-ai/dsh-tool-fs',
    '@deepseek-ai/dsh-tool-skill',
    '@deepseek-ai/dsh-skill-filesystem',
    '@deepseek-ai/dsh-tool-subagent',
    '@deepseek-ai/dsh-tool-web',
    '@deepseek-ai/dsh-tool-present',
  ]) {
    assert.ok(row.includes(required), `plugins 含 ${required}`)
  }
  // 发布服务的行必须带 realm,否则第二个 preset 挂载时冲突。
  assert.ok(row.includes('isolate:'), 'isolate realm 在场')
  assert.ok(row.includes('planMode: true'), 'plan mode realm 在场')
  assert.ok(row.includes('workflowEngine: true'), 'workflow realm 在场')
})

test('customSkillDirs 从本包 realpath 解析 skills(不得回退 preset 目录)', () => {
  const row = presetRowText()
  assert.ok(row.includes('customSkillDirs:'), 'skills 目录已挂载')
  // 锚点在包内:从 baseUrl 解析本包 package.json,再取同目录 skills。
  assert.ok(row.includes(`createRequire(baseUrl).resolve('${PKG}/package.json')`), '从本包 realpath 解析')
  assert.ok(row.includes("'skills')"), '取包内 skills 子目录')
  // 禁止回到 legacy preset 目录的相对解析(该目录已不被 DSH 读取)。
  assert.ok(!row.includes("new URL('skills/', baseUrl)"), '不得使用 preset 目录相对解析')
})
