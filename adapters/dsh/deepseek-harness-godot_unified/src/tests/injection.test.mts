import assert from 'node:assert/strict'
import test from 'node:test'
import { isGodotPreset, resolveSettings, shouldInjectPrompt, toolNamePrefix, SETTINGS_DEFAULTS } from '../injection.js'

test('resolveSettings:缺失/非法形状逐键回退默认(默认不注入)', () => {
  assert.deepEqual(resolveSettings(undefined), SETTINGS_DEFAULTS)
  assert.deepEqual(resolveSettings(null), SETTINGS_DEFAULTS)
  assert.deepEqual(resolveSettings('junk'), SETTINGS_DEFAULTS)
  assert.deepEqual(resolveSettings({ injectLegacyMode: 'yes', promptGuidance: 1, zhPrompt: null }), SETTINGS_DEFAULTS)
})

test('resolveSettings:合法值逐键采纳,字符串/布尔分型', () => {
  assert.deepEqual(resolveSettings({ injectLegacyMode: true }), { injectLegacyMode: true, promptGuidance: true, zhPrompt: false, godotServerDist: '', godotProjectPath: '' })
  assert.deepEqual(resolveSettings({ zhPrompt: true, promptGuidance: false }), { injectLegacyMode: false, promptGuidance: false, zhPrompt: true, godotServerDist: '', godotProjectPath: '' })
  assert.deepEqual(
    resolveSettings({ godotServerDist: 'D:/x/godot-mcp-shim.exe', godotProjectPath: 'D:/game' }),
    { injectLegacyMode: false, promptGuidance: true, zhPrompt: false, godotServerDist: 'D:/x/godot-mcp-shim.exe', godotProjectPath: 'D:/game' },
  )
  // 非字符串的路径字段回退默认。
  assert.equal(resolveSettings({ godotServerDist: 42 }).godotServerDist, '')
})

test('toolNamePrefix:官方命名约定 mcp__<serverName>__', () => {
  assert.equal(toolNamePrefix('godot'), 'mcp__godot__')
  assert.equal(toolNamePrefix('gd47'), 'mcp__gd47__')
  assert.equal(`${toolNamePrefix('godot')}discover_tools`, 'mcp__godot__discover_tools')
})

test('isGodotPreset:只有 godot 是 Godot preset', () => {
  assert.equal(isGodotPreset('godot'), true)
  assert.equal(isGodotPreset('standard'), false)
  assert.equal(isGodotPreset(undefined), false)
  assert.equal(isGodotPreset(''), false)
})

test('shouldInjectPrompt:仅 Godot preset 或原版模式,且开关开启', () => {
  assert.equal(shouldInjectPrompt({ injectLegacyMode: false, presetId: 'godot', promptGuidance: true }), true)
  assert.equal(shouldInjectPrompt({ injectLegacyMode: false, presetId: 'godot', promptGuidance: false }), false)
  assert.equal(shouldInjectPrompt({ injectLegacyMode: false, presetId: 'standard', promptGuidance: true }), false)
  assert.equal(shouldInjectPrompt({ injectLegacyMode: false, presetId: undefined, promptGuidance: true }), false)
  // 原版模式与 preset 无关(全局 section)。
  assert.equal(shouldInjectPrompt({ injectLegacyMode: true, presetId: undefined, promptGuidance: true }), true)
  assert.equal(shouldInjectPrompt({ injectLegacyMode: true, presetId: 'standard', promptGuidance: true }), true)
  assert.equal(shouldInjectPrompt({ injectLegacyMode: true, presetId: 'godot', promptGuidance: false }), false)
})
