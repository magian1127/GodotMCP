// 技能文件读取(GodotMCP skills 目录) + 提示词面组装(systemPrompt.assemble)。
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'

export interface SkillDoc { id: string; title: string; locale: 'zh' | 'en'; content: string }

/** 读取 GodotMCP 的技能目录（zh 主 SKILL.md,en 用 SKILL.en.md,互为回退）。 */
export function readSkills(godotMcpRoot: string | undefined, locale: 'zh' | 'en'): SkillDoc[] {
  if (godotMcpRoot === undefined || godotMcpRoot === '') return []
  const dir = join(godotMcpRoot, 'plugin', 'godot-mcp-unified', 'skills')
  if (!existsSync(dir)) return []
  const out: SkillDoc[] = []
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue
    const primary = join(dir, entry.name, locale === 'zh' ? 'SKILL.md' : 'SKILL.en.md')
    const fallback = join(dir, entry.name, locale === 'zh' ? 'SKILL.en.md' : 'SKILL.md')
    const path = existsSync(primary) ? primary : existsSync(fallback) ? fallback : undefined
    if (path === undefined) continue
    const content = readFileSync(path, 'utf8')
    const title = content.split('\n').find(line => line.startsWith('# '))?.slice(2).trim() ?? entry.name
    out.push({ id: entry.name, title, locale, content })
  }
  return out.sort((a, b) => (a.id < b.id ? -1 : 1))
}

/** 当前提示词 sections(systemPrompt.assemble;服务缺席/失败 → 空)。 */
export function promptSections(systemPrompt: { assemble(input?: unknown): Promise<{ sections?: Array<{ name: string; text: string }> }> } | undefined | null): Promise<Array<{ name: string; text: string }>> {
  if (systemPrompt === undefined || systemPrompt === null || typeof systemPrompt.assemble !== 'function') {
    return Promise.resolve([])
  }
  return systemPrompt.assemble({})
    .then(r => (Array.isArray(r?.sections) ? r.sections.map(s => ({ name: String(s.name), text: String(s.text ?? '') })) : []))
    .catch(() => [])
}
