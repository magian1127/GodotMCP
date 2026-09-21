// 构建产物校验:存在性 + 语法 + CLI usage 冒烟。
// 用法:node verify-build.mjs(在 npm run build 之后执行)。
import { spawnSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('.', import.meta.url))

let failed = 0
function check(label, ok) {
  console.log(`${ok ? '  ok ' : 'FAIL '} ${label}`)
  if (!ok) failed += 1
}

console.log('[verify-build] 产物存在性')
const artifacts = [
  'lib/index.js',
  'lib/index.d.ts',
  'lib/section-text.js',
  'lib/client.js',
  'bin/dsh-godot.mjs',
  'bin/cli/main.mjs',
  'bin/patch-row.mjs',
]
for (const artifact of artifacts) {
  check(artifact, existsSync(join(root, artifact)))
}

console.log('[verify-build] 语法检查')
for (const file of ['lib/index.js', 'lib/client.js', 'bin/dsh-godot.mjs', 'bin/cli/main.mjs', 'bin/patch-row.mjs']) {
  if (!existsSync(join(root, file))) continue
  const res = spawnSync(process.execPath, ['--check', join(root, file)], { stdio: 'inherit' })
  check(`node --check ${file}`, res.status === 0)
}

console.log('[verify-build] client bundle 格式(经典脚本,禁 ESM)')
const clientText = existsSync(join(root, 'lib/client.js')) ? readFileSync(join(root, 'lib/client.js'), 'utf8') : ''
check('window.__ModuleLoader__.load 头', clientText.includes('window.__ModuleLoader__.load'))
check('包 id', /id:\s*['"]deepseek-harness-godot_unified['"]/.test(clientText))
check("fiber 注入声明含 slots 与 settingsScope", /(?:const|var|let)\s+inject\s*=\s*\[[^\]]*["']slots["'][^\]]*\]/.test(clientText) && /["']settingsScope["']/.test(clientText) && /exports\.inject\s*=\s*inject/.test(clientText))
check('注册 conversation.view', clientText.includes('conversation.view'))
check('注册插件页配置表单(plugins.bundle.config,键为包名)', clientText.includes('plugins.bundle.config') && /BUNDLE_PACKAGE_NAME\s*=\s*["']deepseek-harness-godot_unified["']/.test(clientText))
check('Tab id godot / order 15', /id:\s*["']godot["']/.test(clientText) && /order:\s*15/.test(clientText))
check('工作台组件在场', clientText.includes('GodotWorkbench') || clientText.includes('gwb-modal'))
check('未打包 react(外部 require)', !clientText.includes('node_modules/react'))
check('无顶层 ESM 语句', !/(?:^|\n)\s*(?:export|import)\s/m.test(clientText))

console.log('[verify-build] cordis.patch.yml 行形状')
const patchText = existsSync(join(root, 'cordis.patch.yml')) ? readFileSync(join(root, 'cordis.patch.yml'), 'utf8') : ''
check("insert 行 id dsh-godot", patchText.includes("- id: dsh-godot"))
check("name 为本包", patchText.includes("name: 'deepseek-harness-godot_unified'"))

console.log('[verify-build] CLI usage 冒烟(退出码 2)')
const usage = spawnSync(process.execPath, [join(root, 'bin/dsh-godot.mjs')], { stdio: 'inherit' })
check('无参数退出码 2 且打印用法', usage.status === 2)

if (failed > 0) {
  console.error(`[verify-build] ${failed} 项失败`)
  process.exit(1)
}
console.log('[verify-build] 全部通过')
