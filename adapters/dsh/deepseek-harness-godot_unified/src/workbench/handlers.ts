// 路由数据函数(纯逻辑,mock 友好)。
import { existsSync } from 'node:fs'
import type { ToolSchemaShape, ToolsServiceShape } from '../types.js'
import { editorStatus, serverDistStatus, type EditorStatus } from './probes.js'
import { WorkbenchExecutor } from './executor.js'
import type { PathStore } from './path-store.js'

export interface WorkbenchConfig {
  serverName: string
  godotMcpRoot?: string
  projectPath?: string
  unsafe?: boolean
  /** 设置驱动的 server dist 完整路径(自研注册模式;status 直接报告其存在性)。 */
  serverDistPath?: string
  /** 路径存储(全局 serverDist + 按 cwd 批量项目条目):传入了才按「当前会话 cwd」解析;缺省回退全局设置。 */
  pathStore?: PathStore
  /** 工作台「连接 Godot」:spawn server + 同步工具清单 + 写缓存(有缓存时零连接;首次调用或此按钮才连)。
   *  `cwd` 为工作台当前会话工作目录(per-cwd 路径解析键)。 */
  connect?: (cwd?: string) => Promise<{ ok: boolean; toolCount: number }>
}

/** 旧 Node 桥 server 入口在工作区布局中的固定相对路径(Node 桥 2026-09-14 退役,保留作回滚通道)。 */
export const SERVER_DIST_RELATIVE = 'plugin/godot-mcp-unified/server/dist/index.js'

/** daemon HTTP 自举桥入口的固定相对路径(stdio 行帧 ↔ daemon HTTP;DSH 翻转后的默认入口)。 */
export const BRIDGE_ENTRY_RELATIVE = 'adapters/dsh/godot-http-bridge.mjs'

/** server 入口候选(bridge 优先,legacy 兼容)——根推导后缀与 legacy 探测共用同一顺序。 */
const SERVER_ENTRY_CANDIDATES = [BRIDGE_ENTRY_RELATIVE, SERVER_DIST_RELATIVE]

/** 从 server 入口反推工作区根的候选后缀。 */
const SERVER_ENTRY_SUFFIXES = SERVER_ENTRY_CANDIDATES.map(rel => '/' + rel)

/** 解析生效的 server dist / 项目路径:serverDist 全局单值;projectPath 按「当前会话 cwd」在批量条目里匹配,无匹配回退全局设置。 */
export function resolveWorkbenchPaths(config: WorkbenchConfig, cwd?: string): { serverDistPath?: string; projectPath?: string } {
  const store = config.pathStore
  const serverDistPath = store?.getServerDist() ?? config.serverDistPath
  const projectPath = store?.getProjectPath(cwd) ?? config.projectPath
  return { serverDistPath, projectPath }
}

/**
 * 解析 GodotMCP 工作区根(技能/提示词面定位,readSkills 依赖它)。
 * 优先组合行 config.godotMcpRoot;v0.4 后安装只写 serverDist 完整路径、不再存
 * godotMcpRoot,故未显式配置时从生效 serverDist 向上推导——serverDist 形如
 * `<根>/adapters/dsh/godot-http-bridge.mjs`(daemon 桥)或旧
 * `<根>/plugin/godot-mcp-unified/server/dist/index.js`,去掉固定相对后缀
 * (SERVER_ENTRY_SUFFIXES)即得工作区根。仅当 serverDist 确实以候选后缀之一结尾时才
 * 推导,避免把无关全路径误当根(推导失败回退 undefined)。
 */
export function resolveSkillsRoot(config: WorkbenchConfig): string | undefined {
  if (config.godotMcpRoot !== undefined && config.godotMcpRoot !== '') return config.godotMcpRoot
  const serverDist = config.pathStore?.getServerDist() ?? config.serverDistPath
  if (serverDist === undefined || serverDist === '') return undefined
  const normalized = serverDist.replace(/\\/g, '/')
  for (const suffix of SERVER_ENTRY_SUFFIXES) {
    if (normalized.endsWith(suffix)) {
      const root = normalized.slice(0, -suffix.length)
      if (root !== '') return root
    }
  }
  return undefined
}

export interface StatusPayload {
  bridge: { serverName: string; toolCount: number }
  editor: EditorStatus | null
  readOnly: boolean
  unsafe: boolean
  serverDist: { path: string | null; exists: boolean }
  node: string
}

/** 只读判据:桥接有工具而常驻修改类工具缺席(mcp-client 不桥接注解的保守替代)。 */
const READONLY_PROBE_TOOLS = ['editor_save_scene', 'node_set_property'] as const

/** legacy godotMcpRoot 探测:bridge 候选优先,legacy 后缀兼容;都不存在时报 bridge 路径(行动指引指向新形态)。 */
function legacyEntryStatus(godotMcpRoot: string | undefined): { path: string | null; exists: boolean } {
  for (const rel of SERVER_ENTRY_CANDIDATES) {
    const status = serverDistStatus(godotMcpRoot, rel)
    if (status.exists) return status
  }
  return serverDistStatus(godotMcpRoot, BRIDGE_ENTRY_RELATIVE)
}

/** status 数据;`cwd` 为当前会话工作目录(可选),用于按 per-cwd 解析生效路径。 */
export async function statusData(tools: ToolsServiceShape, config: WorkbenchConfig, cwd?: string): Promise<StatusPayload> {
  const prefix = `${config.serverName}_`
  const godotNames = new Set(tools.schemas().filter(s => s.name.startsWith(prefix)).map(s => s.name))
  const readOnly = godotNames.size > 0 && READONLY_PROBE_TOOLS.every(t => !godotNames.has(prefix + t))
  const { serverDistPath, projectPath } = resolveWorkbenchPaths(config, cwd)
  // 自研注册模式:server dist 路径来自设置/per-cwd;旧 godotMcpRoot+相对路径仍兼容。
  const serverDist = serverDistPath !== undefined && serverDistPath !== ''
    ? { path: serverDistPath, exists: existsSync(serverDistPath) }
    : legacyEntryStatus(config.godotMcpRoot)
  return {
    bridge: { serverName: config.serverName, toolCount: godotNames.size },
    editor: await editorStatus(projectPath),
    readOnly,
    unsafe: config.unsafe === true,
    serverDist,
    node: process.version,
  }
}

export interface GodotGroupInfo { name: string; description: string; tools: string[]; active: boolean }
export interface ToolsPayload { tools: ToolSchemaShape[]; godotGroups: GodotGroupInfo[] }

/** 组目录:调一次 discover_tools(空查询只读);桥接离线(官方行未同步工具)时静默为空。 */
export async function toolsData(tools: ToolsServiceShape, config: WorkbenchConfig): Promise<ToolsPayload> {
  const all = tools.schemas()
  const metaName = `${config.serverName}_discover_tools`
  let godotGroups: GodotGroupInfo[] = []
  // 仅当 discover_tools 元工具在场(官方 mcp-client 行在线、工具已同步)时才调用:
  // 目录浏览绝不应触发 server 启动或 30-60s MCP 往返(TTL 只降频不消除副作用)。
  if (all.some(s => s.name === metaName)) {
    try {
      const outcome = await new WorkbenchExecutor().call(tools, metaName, {})
      const text = outcome.content.find(b => b.type === 'text')?.text
      const parsed = text !== undefined ? JSON.parse(text) as { groups?: unknown } : undefined
      if (parsed !== undefined && Array.isArray(parsed.groups)) {
        godotGroups = parsed.groups.map((g): GodotGroupInfo => {
          const rec = g as Record<string, unknown>
          return {
            name: String(rec.name ?? ''),
            description: String(rec.description ?? ''),
            tools: Array.isArray(rec.tools) ? rec.tools.map(String) : [],
            active: rec.active === true,
          }
        })
      }
    } catch { /* 组目录不可得:留空,前端仍可手动调用元工具 */ }
  }
  return { tools: all, godotGroups }
}

/**
 * tools 目录缓存:序列化后的 JSON 字节 + 短 TTL。
 *
 * conversation.view 是会话作用域视图,随会话/视图切换反复重挂载,每次挂载都会
 * 请求 /api/tools——不缓存时每个请求都要全量 tools.schemas() 序列化 + 一次真实
 * discover_tools MCP 往返(server 再走编辑器桥),普通点击即可把 host 与 UI 打满
 * (2026-09-05 实测:编辑器在场时整个 Web 卡死)。缓存失效三通道:`tools/change`
 * 事件(任何工具面变化)、/api/call 完成后(组激活)、TTL 兜底。
 */
export class ToolsCatalogueCache {
  private body: string | null = null
  private at = 0

  constructor(private readonly ttlMs = 5000) {}

  invalidate(): void {
    this.body = null
    this.at = 0
  }

  /** 缓存命中直接回字节;未命中/过期才重算(重算期间并发请求各自完整执行,幂等无害)。 */
  async bodyOf(tools: ToolsServiceShape, config: WorkbenchConfig): Promise<string> {
    if (this.body !== null && Date.now() - this.at < this.ttlMs) return this.body
    const payload = await toolsData(tools, config)
    this.body = JSON.stringify(payload)
    this.at = Date.now()
    return this.body
  }
}
