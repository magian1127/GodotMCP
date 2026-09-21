import type { UserConfig } from 'tsdown'

// client:src/client/index.ts → lib/client.js,DSH 浏览器经典脚本
// (window.__ModuleLoader__ 工厂;react 保持外部 require,不进 bundle——沿智谱已验证配置)。
const config: UserConfig[] = [{
  entry: { client: 'src/client/index.ts' },
  outDir: 'lib',
  format: 'cjs',
  platform: 'browser',
  target: 'es2024',
  dts: false,
  sourcemap: false,
  clean: false,
  deps: { neverBundle: ['react'] },
  outputOptions: {
    entryFileNames: 'client.js',
    banner: "window.__ModuleLoader__.load({ id: 'deepseek-harness-godot_unified', factory: (require) => {",
    footer: 'return module.exports; } });',
    intro: 'var module = { exports: {} }; var exports = module.exports;',
  },
}]
export default config
