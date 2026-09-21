import assert from 'node:assert/strict'
import test from 'node:test'
import { godotToolNames, needsScopedPrompt, resolveSettings, resolveToolDescription, shouldHideAgent, toolFailureMessage, SETTINGS_DEFAULTS } from '../injection.js'

test('resolveSettings: 缺失/非法形状逐键回退默认(默认不注入)', () => {
  assert.deepEqual(resolveSettings(undefined), SETTINGS_DEFAULTS)
  assert.deepEqual(resolveSettings(null), SETTINGS_DEFAULTS)
  assert.deepEqual(resolveSettings('junk'), SETTINGS_DEFAULTS)
  assert.deepEqual(resolveSettings({ injectLegacyMode: 'yes', promptGuidance: 1, zhPrompt: null }), SETTINGS_DEFAULTS)
})

test('resolveSettings: 合法值逐键采纳,字符串/布尔分型', () => {
  assert.deepEqual(resolveSettings({ injectLegacyMode: true }), { injectLegacyMode: true, promptGuidance: true, zhPrompt: false, godotServerDist: '', godotProjectPath: '' })
  assert.deepEqual(resolveSettings({ zhPrompt: true, promptGuidance: false }), { injectLegacyMode: false, promptGuidance: false, zhPrompt: true, godotServerDist: '', godotProjectPath: '' })
  assert.deepEqual(
    resolveSettings({ godotServerDist: 'D:/x/dist/index.js', godotProjectPath: 'D:/game' }),
    { injectLegacyMode: false, promptGuidance: true, zhPrompt: false, godotServerDist: 'D:/x/dist/index.js', godotProjectPath: 'D:/game' },
  )
  // 非字符串的路径字段回退默认。
  assert.equal(resolveSettings({ godotServerDist: 42 }).godotServerDist, '')
})

test('godotToolNames: 按服务器前缀过滤 schema 目录', () => {
  const schemas = [
    { name: 'read' },
    { name: 'godot_discover_tools' },
    { name: 'godot_scene_get_tree' },
    { name: 'other_tool' },
    { name: 'godotish_tool' },
  ]
  assert.deepEqual(godotToolNames(schemas, 'godot_'), ['godot_discover_tools', 'godot_scene_get_tree'])
  assert.deepEqual(godotToolNames([], 'godot_'), [])
})

test('shouldHideAgent: 默认隐藏;Godot preset 与原版模式放行', () => {
  assert.equal(shouldHideAgent({ injectLegacyMode: false, presetId: undefined }), true)
  assert.equal(shouldHideAgent({ injectLegacyMode: false, presetId: 'standard' }), true)
  assert.equal(shouldHideAgent({ injectLegacyMode: false, presetId: 'godot' }), false)
  assert.equal(shouldHideAgent({ injectLegacyMode: true, presetId: undefined }), false)
  assert.equal(shouldHideAgent({ injectLegacyMode: true, presetId: 'standard' }), false)
})

test('needsScopedPrompt: 仅 Godot preset 且非 legacy 且开关开启', () => {
  assert.equal(needsScopedPrompt({ injectLegacyMode: false, presetId: 'godot', promptGuidance: true }), true)
  assert.equal(needsScopedPrompt({ injectLegacyMode: false, presetId: 'godot', promptGuidance: false }), false)
  assert.equal(needsScopedPrompt({ injectLegacyMode: true, presetId: 'godot', promptGuidance: true }), false)
  assert.equal(needsScopedPrompt({ injectLegacyMode: false, presetId: 'standard', promptGuidance: true }), false)
})

test('resolveToolDescription: zh 优先本地化(无前缀),缺失回退原文;非 zh 原文/兜底', () => {
  const zhFull = '使用同一响应契约捕获运行中游戏或编辑器视口；运行时捕获需要活动的游玩测试。'
  // zh:本地化优先(在线/离线统一,无「Godot 桥接工具」前缀)。
  assert.equal(resolveToolDescription(true, zhFull, 'Capture either the running game...', 'fallback'), zhFull)
  // zh:本地化缺失时回退上游原文。
  assert.equal(resolveToolDescription(true, undefined, 'Capture the viewport.', 'fallback'), 'Capture the viewport.')
  // zh:均缺失时中文占位。
  assert.equal(resolveToolDescription(true, undefined, undefined, 'fallback'), '（暂无中文说明）')
  // 非 zh:上游原文直出。
  assert.equal(resolveToolDescription(false, zhFull, 'Capture the viewport.', 'fallback'), 'Capture the viewport.')
  // 非 zh:无原文时用兜底(离线英文摘要)。
  assert.equal(resolveToolDescription(false, undefined, undefined, 'Godot bridge tool (x).'), 'Godot bridge tool (x).')
})

test('toolFailureMessage: zh 包中文前缀保留上游错误码;非 zh 维持原样', () => {
  assert.equal(toolFailureMessage(true, 'scene_get_tree', 'AUTH_FAILED'), '「scene_get_tree」执行失败：AUTH_FAILED')
  assert.equal(toolFailureMessage(true, 'scene_get_tree', ''), '「scene_get_tree」执行失败')
  assert.equal(toolFailureMessage(false, 'scene_get_tree', 'AUTH_FAILED'), 'AUTH_FAILED')
  assert.equal(toolFailureMessage(false, 'scene_get_tree', ''), 'scene_get_tree failed')
})
