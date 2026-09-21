// 纯校验函数(无 fs/环境依赖,便于测试)。

/** 与 DSH launcher 的 resolveProfileDir 保持一致的 flat profile 名校验。 */
export function validateProfileName(name: string): string {
  if (name === '' || name.includes('/') || name.includes('\\') || name === '.' || name === '..' || name === 'node_modules') {
    throw new Error(`invalid profile name ${JSON.stringify(name)}; use a flat name such as "web"`)
  }
  return name
}

/** serverName 必须满足 mcp-client 的 `[A-Za-z0-9_-]{1,32}` 约束。 */
export function validateServerName(name: string): string {
  if (!/^[A-Za-z0-9_-]{1,32}$/.test(name)) {
    throw new Error(`serverName must match [A-Za-z0-9_-]{1,32} (got: ${JSON.stringify(name)})`)
  }
  return name
}

/** 行 id:小写字母/数字/连字符,防 YAML 结构注入。 */
export function validateRowId(id: string): string {
  if (!/^[a-z0-9][a-z0-9-]*$/.test(id)) {
    throw new Error(`row id must be lowercase [a-z0-9-] with letter/digit head (got: ${JSON.stringify(id)})`)
  }
  return id
}

/** 端口:1-65535。 */
export function validatePort(value: string, flag: string): number {
  const port = Number.parseInt(value, 10)
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`${flag} must be an integer in 1-65535 (got: ${JSON.stringify(value)})`)
  }
  return port
}

/** 非负整数。 */
export function validateNonNegativeInt(value: string, flag: string): number {
  const n = Number.parseInt(value, 10)
  if (!Number.isInteger(n) || n < 0) {
    throw new Error(`${flag} must be a non-negative integer (got: ${JSON.stringify(value)})`)
  }
  return n
}
