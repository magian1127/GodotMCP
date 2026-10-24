// client 半边:注册插件页配置表单(侧栏插件页 → 本组合包页面,plugins.bundle.config
// 槽位;DSH 0.1.6 起接替已退役的 settings.plugin.item 设置卡片)。
// 通用工具调试界面不在本包——已迁至 `deepseek-harness-project-mcp` 的「工具调试」Tab。
import { registerConfigPage } from './config-page.js'

const PKG = 'deepseek-harness-godot_unified'

// fiber 注入声明:浏览器端 Loader 从 bundle exports 读 inject 构建 fiber 的
// 服务注入集(hashline 同款通道);缺了它 ctx.slots/ctx.configForms 访问会抛
// "cannot get property ... without inject" 并让 boot 页把插件判为加载失败。
// 注意与 package.json 的 dsh.client.inject(包名级 factory 先行边)是两回事。
// 配置表单只用 slots/configForms/locale 三个服务;connection 随工作台一并移除。
export const inject = ['slots', 'locale', 'configForms']

export function apply(ctx: Record<string, unknown>): void {
  // 插件页配置表单:configForms/locale 缺失时跳过(不抛错,页面留空即可)。
  try {
    registerConfigPage(ctx)
  } catch (error) {
    console.warn(`[${PKG}] 插件页配置表单注册失败:`, error)
  }
}
