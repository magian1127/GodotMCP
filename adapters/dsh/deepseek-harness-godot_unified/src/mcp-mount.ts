/**
 * 官方 MCP 客户端挂载的**纯粹辅助**:server 入口启动方案与 `mcp-client` Config 组装。
 *
 * v0.5 起本包不再实现 MCP 客户端:连接、握手、工具发现、重连、图片投影全部交给
 * 宿主自带的 `@deepseek-ai/dsh-mcp-client`(从 profile 解析,不写依赖)。本模块只回答
 * 两件事,都不做 I/O、不碰 Cordis:
 *
 * 1. **怎么启动 server 入口**:GodotMCP 的随包入口是原生 shim 可执行文件
 *    (`godot-mcp-shim[.exe]`,由它确保机器级单例 daemon 在跑再把 stdio 转发到
 *    daemon 的 HTTP 面),直接 spawn 即可;历史/回滚入口是 `.mjs`/`.js` 脚本,
 *    需要经 node 执行。
 * 2. **Config 怎么填**:stdio + 命令/参数 + `GODOT_MCP_PROJECT_PATH` + cwd。
 *    项目路径按**调用会话的 cwd**解析后传入(同一台机器上不同会话可以是不同
 *    Godot 项目);cwd 同时作为子进程工作目录,shim 在没拿到显式 env 时会按
 *    cwd 查机器级注册表兜底。
 *
 * 为什么不在这里读盘/读服务:路径来自 `PathStore` 与设置快照,由 `index.ts` 注入;
 * 纯函数让"配置形状"与"何时挂载"两件事可以分别单测。
 */

/** 启动方案:可执行文件 + 参数(参数不经 shell 解释)。 */
export interface SpawnPlan {
  command: string
  args: string[]
}

/** 入口是否为「需经 node 执行的脚本」——否则按可执行文件直接 spawn。 */
export function isScriptEntry(path: string): boolean {
  return /\.(mjs|cjs|js)$/i.test(path.replace(/\\/g, '/'))
}

/**
 * server 入口的启动方案。
 *
 * 默认入口是随包 shim(native 可执行文件,由 daemon 侧发布产出)——host 直接 spawn
 * 它,shim 负责确保机器级单例 daemon 在跑、再把 stdio 转发到 daemon 的 HTTP 面;
 * 仅当入口是 .mjs/.js 脚本(历史 Node 桥/回滚通道)时才经 node 执行。
 */
export function buildSpawnPlan(dist: string, nodeExecPath: string = process.execPath): SpawnPlan {
  if (isScriptEntry(dist)) return { command: nodeExecPath, args: [dist] }
  return { command: dist, args: [] }
}

/** 传给 GodotMCP server 的项目路径环境变量(上游同名契约)。 */
export const PROJECT_PATH_ENV = 'GODOT_MCP_PROJECT_PATH'

/** 单次工具调用/资源请求的默认超时(与官方 mcp-client 默认一致,显式写出以便审计)。 */
export const DEFAULT_TOOL_CALL_TIMEOUT_MS = 60_000

/**
 * 交给 `@deepseek-ai/dsh-mcp-client` 的 Config(stdio 分支;只列本插件会设置的值)。
 *
 * 刻意**不**声明 `@deepseek-ai/dsh-mcp-client` 的类型:本包不把 DSH 运行时模块写进
 * dependencies(工作区硬约定),挂载目标是运行时从 profile 解析出来的模块对象。
 */
export interface GodotMcpConfig {
  transport: 'stdio'
  serverName: string
  command: string
  args: string[]
  env: Record<string, string>
  cwd?: string
  toolCallTimeoutMs: number
  failOnStartupError: boolean
}

/** Config 组装输入(路径均为已归一化的绝对路径;空串表示未配置)。 */
export interface MountConfigInput {
  /** MCP 命名空间(`mcp__<serverName>__<tool>`);非法形状直接抛错,不产生配置。 */
  serverName: string
  /** server 入口全路径(shim 可执行文件或 .mjs/.js 脚本)。 */
  dist: string
  /** 已归一化的 Godot 项目目录(可为空:此时不设 env,由 shim 按 cwd 兜底)。 */
  projectPath?: string | undefined
  /** 会话工作目录(cwd);为空表示沿用宿主进程工作目录。 */
  cwd?: string | undefined
  /** 测试注入:脚本入口用哪个 node 执行(默认 `process.execPath`)。 */
  nodeExecPath?: string
  /** 覆盖单次调用超时(测试用;默认 60s)。 */
  toolCallTimeoutMs?: number
}

/** serverName 的官方约束(`mcp-client` 的 Config schema 同款)。 */
const SERVER_NAME_PATTERN = /^[A-Za-z0-9_-]{1,32}$/

/**
 * 组装一个 stdio MCP Config。
 *
 * `failOnStartupError: false` 是刻意的:Godot 编辑器没开、daemon 没起、路径暂时
 * 写错都不该让宿主或会话启动失败——官方客户端会把错误记日志并进入重连循环,
 * 工具就位后自然可用。会话创建路径也绝不因它抛错(见 index.ts 的挂载点)。
 */
export function buildMountConfig(input: MountConfigInput): GodotMcpConfig {
  const serverName = input.serverName.trim()
  if (!SERVER_NAME_PATTERN.test(serverName)) {
    throw new Error(`invalid MCP serverName: ${JSON.stringify(input.serverName)}`)
  }
  const dist = input.dist.trim()
  if (dist === '') throw new Error('godot server dist 未配置')
  const plan = buildSpawnPlan(dist, input.nodeExecPath)
  const projectPath = (input.projectPath ?? '').trim()
  const cwd = (input.cwd ?? '').trim()
  return {
    transport: 'stdio',
    serverName,
    command: plan.command,
    args: plan.args,
    env: projectPath === '' ? {} : { [PROJECT_PATH_ENV]: projectPath },
    ...(cwd === '' ? {} : { cwd }),
    toolCallTimeoutMs: input.toolCallTimeoutMs ?? DEFAULT_TOOL_CALL_TIMEOUT_MS,
    failOnStartupError: false,
  }
}

/**
 * 挂载身份键:用于判断"已经挂着的那个连接"与"现在应当挂的连接"是不是同一份配置
 * (设置/路径/会话 cwd 变化 → 键变 → 需要重挂)。
 */
export function mountConfigKey(config: GodotMcpConfig): string {
  return [
    config.serverName,
    config.command,
    config.args.join('\u0001'),
    Object.entries(config.env).map(([key, value]) => `${key}=${value}`).sort().join('\u0002'),
    config.cwd ?? '',
  ].join('\u0000')
}

/**
 * 错误类型名(只记类型,不记 message)。
 *
 * 与 project-mcp 同款口径:Config 校验失败的 message 可能回显**整个 config**
 * (含 env 值),因此挂载诊断一律只记 `error.name` 与固定文案。
 */
export function errorName(error: unknown): string {
  if (error instanceof Error && typeof error.name === 'string' && error.name !== '') return error.name
  return typeof error === 'object' && error !== null && typeof (error as { name?: unknown }).name === 'string'
    ? (error as { name: string }).name
    : 'Error'
}
