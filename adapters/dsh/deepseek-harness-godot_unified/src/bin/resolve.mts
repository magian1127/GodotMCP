/**
 * server dist 与 Godot 项目的解析/校验(纯函数 + 可注入探测,便于测试)。
 */
import { existsSync, statSync } from 'node:fs'
import { isAbsolute, join, resolve } from 'node:path'
import { SERVER_ENTRY_CANDIDATES, shimEntryRelative } from './cli/constants.mjs'

/** 解析输入(flag 与环境变量已展开)。 */
export interface ResolveInputs {
  serverDist?: string
  godotMcpRoot?: string
  env?: Record<string, string | undefined>
  /** 候选入口存在性探测(可注入;缺省 existsSync)——root 推导在 bridge/legacy 候选间选择。 */
  fileExists?: (path: string) => boolean
}

export type ResolveResult = { ok: true; path: string } | { ok: false; reason: string }

/** 路径统一转正斜杠(YAML 单引号标量友好;server 与注册表都会规范化)。 */
export function toForwardSlashes(p: string): string {
  return p.replace(/\\/g, '/')
}

/**
 * server dist 解析优先级:
 * 1. --server-dist flag
 * 2. $GODOT_MCP_SERVER_DIST
 * 3. --godot-mcp-root flag / $GODOT_MCP_ROOT + 固定相对路径(随包 shim 优先,bridge 兜底,legacy 兼容)
 * 都缺失时报错并给出可行动指引。
 */
export function resolveServerDist(inputs: ResolveInputs): ResolveResult {
  const env = inputs.env ?? {}
  const flag = inputs.serverDist?.trim()
  if (flag !== undefined && flag !== '') return { ok: true, path: toForwardSlashes(resolve(flag)) }
  const envDist = env.GODOT_MCP_SERVER_DIST?.trim()
  if (envDist !== undefined && envDist !== '') return { ok: true, path: toForwardSlashes(resolve(envDist)) }
  const root = inputs.godotMcpRoot?.trim() || env.GODOT_MCP_ROOT?.trim()
  if (root !== undefined && root !== '') {
    const resolvedRoot = resolve(root)
    const fileExists = inputs.fileExists ?? existsSync
    for (const rel of SERVER_ENTRY_CANDIDATES) {
      const candidate = toForwardSlashes(join(resolvedRoot, ...rel.split('/')))
      if (fileExists(candidate)) return { ok: true, path: candidate }
    }
    // 都不存在时返回随包 shim 候选(标准形态),让 validate 报出可行动的新形态路径。
    return { ok: true, path: toForwardSlashes(join(resolvedRoot, ...shimEntryRelative().split('/'))) }
  }
  return {
    ok: false,
    reason: '未找到 GodotMCP 桥接 server:请传 --server-dist 指向随包 shim 的绝对路径(server-dotnet/publish/<rid>/godot-mcp-shim[.exe]),或 --godot-mcp-root <GodotMCP 工作区根目录>,或设置环境变量 GODOT_MCP_SERVER_DIST / GODOT_MCP_ROOT',
  }
}

/** 校验解析出的 server dist 是存在的普通文件。 */
export function validateServerDistFile(path: string): ResolveResult {
  try {
    if (!statSync(path).isFile()) return { ok: false, reason: `server dist 不是普通文件: ${path}` }
    return { ok: true, path }
  } catch {
    return { ok: false, reason: `server dist 不存在(Node 桥已退役;先在仓库根发布服务:dotnet publish plugin/godot-mcp-unified/server-dotnet/src/godot-mcp-shim -c Release -p:PublishProfile=<rid>,路径应指向 server-dotnet/publish/<rid>/godot-mcp-shim[.exe];仅 stdio 兜底才用 adapters/dsh/godot-http-bridge.mjs): ${path}` }
  }
}

/** 校验目录是 Godot 项目(含 project.godot)。 */
export function validateGodotProject(rawDir: string): ResolveResult {
  const dir = resolve(rawDir)
  if (!isAbsolute(dir)) return { ok: false, reason: `项目路径必须是绝对路径: ${rawDir}` }
  if (!existsSync(dir)) return { ok: false, reason: `项目目录不存在: ${dir}` }
  if (!existsSync(join(dir, 'project.godot'))) {
    return { ok: false, reason: `目录缺少 project.godot(不是 Godot 项目根): ${dir}` }
  }
  return { ok: true, path: toForwardSlashes(dir) }
}
