// schema → 表单字段(纯函数)。仅顶层标量/枚举成控件,复杂类型落 JSON 编辑域。
export interface FieldSpec {
  name: string
  type: 'string' | 'number' | 'boolean' | 'enum' | 'json'
  enumValues?: string[]
  required: boolean
  description?: string
  placeholder?: string
}

export function formFields(parameters: unknown): FieldSpec[] {
  const props = (parameters as { properties?: Record<string, Record<string, unknown>> } | undefined)?.properties
  if (props === undefined || typeof props !== 'object') return []
  const required = new Set(((parameters as { required?: unknown[] }).required ?? []).map(String))
  return Object.entries(props).map(([name, p]) => {
    const type = typeof p.type === 'string' ? p.type : 'object'
    const base = { name, required: required.has(name), description: typeof p.description === 'string' ? p.description : undefined }
    if (Array.isArray(p.enum)) return { ...base, type: 'enum' as const, enumValues: p.enum.map(String) }
    if (type === 'string') return { ...base, type: 'string' as const }
    if (type === 'number' || type === 'integer') return { ...base, type: 'number' as const }
    if (type === 'boolean') return { ...base, type: 'boolean' as const }
    return { ...base, type: 'json' as const, placeholder: type === 'array' ? '[]' : '{}' }
  })
}

export function buildArguments(fields: FieldSpec[], values: Record<string, string | boolean>): { args: Record<string, unknown>; errors: string[] } {
  const args: Record<string, unknown> = {}
  const errors: string[] = []
  for (const field of fields) {
    const raw = values[field.name]
    if (field.type === 'boolean') {
      if (raw === true) args[field.name] = true
      continue
    }
    const text = typeof raw === 'string' ? raw.trim() : ''
    if (text === '') {
      if (field.required) errors.push(`${field.name} 为必填`)
      continue
    }
    if (field.type === 'string' || field.type === 'enum') args[field.name] = text
    else if (field.type === 'number') {
      const n = Number(text)
      if (!Number.isFinite(n)) errors.push(`${field.name} 不是有效数字`)
      else args[field.name] = n
    } else {
      try { args[field.name] = JSON.parse(text) } catch { errors.push(`${field.name} 不是有效 JSON`) }
    }
  }
  return { args, errors }
}
