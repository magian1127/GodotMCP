// client 半边:注册 Godot 工作台顶级 Tab(conversation.view list slot,沿 ui-trajectory 模式)
// 与插件页配置表单(侧栏插件页 → 本组合包页面,plugins.bundle.config 槽位;
// DSH 0.1.6 起接替已退役的 settings.plugin.item 设置卡片)。
import React from 'react'
import { GodotWorkbenchRoot } from './Workbench.js'
import { injectCss } from './css.js'
import { registerConfigPage } from './config-page.js'

const PKG = 'deepseek-harness-godot_unified'

// fiber 注入声明:浏览器端 Loader 从 bundle exports 读 inject 构建 fiber 的
// 服务注入集(hashline 同款通道);缺了它 ctx.slots/ctx.settingsScope 访问会抛
// "cannot get property ... without inject" 并让 boot 页把插件判为加载失败。
// 注意与 package.json 的 dsh.client.inject(包名级 factory 先行边)是两回事。
export const inject = ['slots', 'locale', 'connection', 'remote', 'settingsScope']

export function apply(ctx: Record<string, unknown>): void {
  const slots = ctx.slots as
    | { inject(slot: string, register: () => void): void; register(options: Record<string, unknown>, component: unknown): unknown }
    | undefined
  if (slots === undefined || typeof slots.inject !== 'function' || typeof slots.register !== 'function') return
  const effect = ctx.effect as ((fn: () => (() => void) | void, label?: string) => void) | undefined
  if (typeof effect === 'function') {
    effect(() => injectCss(), `${PKG}: workbench css`)
  } else {
    injectCss()
  }
  slots.inject('conversation.view', () => {
    // 官方契约(ui-chat 同款):回调须返回注册清理句柄——声明 epoch 变化(HMR/
    // 插件重挂)时会先撤销旧注册再重跑回调;缺失则旧注册泄漏,而 conversation.view
    // 的 Tab 列表按全部条目渲染 → 出现重复 Godot Tab。
    return slots.register({
      name: 'conversation.view',
      id: 'godot',
      order: 15,
      label: () => 'Godot',
    }, GodotWorkbenchRoot) as () => void
  })
  // 插件页配置表单:settingsScope/locale/connection/remote 缺失时跳过(工作台不受影响)。
  try {
    registerConfigPage(ctx)
  } catch (error) {
    console.warn(`[${PKG}] 插件页配置表单注册失败:`, error)
  }
}
