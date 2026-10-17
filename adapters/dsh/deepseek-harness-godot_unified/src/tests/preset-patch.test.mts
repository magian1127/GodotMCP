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

test('persona 行不携带 suffix/模板变量:DSH 0.2.1 起移除 {{cwd}} prompt 变量', () => {
  // 0.2.1 升级指南:persona 模板残留 {{cwd}} 会在请求准入前被严格插值拒绝
  // (报错 "unknown prompt variable {{cwd}} in section deployment:persona-suffix")。
  // 官方 shipped standard 的 persona 行已不带 suffix;目录由 working-directory
  // 服务以用户上下文供给。{{model}} 仍是注册变量,允许保留。只查非注释行:
  // 注释里教学性地提及 {{cwd}} 不是 YAML 值,不得误报。
  const row = presetRowText().split('\n').filter(line => !/^\s*#/.test(line)).join('\n')
  assert.ok(!row.includes('{{cwd}}'), 'patch 值里不得引用已移除的 {{cwd}} 变量')
  assert.ok(!/^ {16}suffix:/m.test(row), 'persona 行不得再声明 suffix(官方 0.2.1 已删)')
})

test('subagent 行不携带已移除的 backgroundMode/enableRunInBackground(DSH 0.2.1)', () => {
  // 0.2.1 升级指南(subagent-activations):tool-subagent 移除 backgroundMode、
  // enableRunInBackground 与模型参数 run_in_background,改为每次委派返回
  // { kind: 'activation', subagentId }、最终答案走父级完成通知。残留这两个键
  // 不会加载失败(Schemastery 不拒绝未知键),只会成为无声无效配置——正因如此
  // 必须靠本测试锁住,否则下次照抄旧 patch 也没人发现。
  const row = presetRowText().split('\n').filter(line => !/^\s*#/.test(line)).join('\n')
  assert.ok(!row.includes('backgroundMode:'), 'subagent 行不得再声明 backgroundMode')
  assert.ok(!row.includes('enableRunInBackground:'), 'subagent 行不得再声明 enableRunInBackground')
})

test('preset 跟随 shipped standard 的 0.2.1 能力行(time-context / tool-schedule)', () => {
  // 本 preset 是 shipped `standard` 的副本 + 本包技能目录,standard 增行后
  // 不跟就会让 Godot 会话少能力(时间上下文、提醒工具)。0.2.1 的 standard
  // 含 time-context 与 tool-schedule,二者都是无 config 的行。
  const row = presetRowText()
  assert.ok(row.includes("name: '@deepseek-ai/dsh-time-context'"), 'time-context 行在场')
  assert.ok(row.includes("name: '@deepseek-ai/dsh-tool-schedule'"), 'tool-schedule 行在场')
  // 官方包名仍在 0.2.1 存在,拆包/改名会让这两行静默失效。
  assert.ok(row.includes('- id: time-context'), 'time-context 行 id 与 standard 一致')
  assert.ok(row.includes('- id: tool-schedule'), 'tool-schedule 行 id 与 standard 一致')
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
