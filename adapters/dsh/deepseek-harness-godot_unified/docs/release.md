# 发布说明

## 发布前验证

1. `npm run typecheck && npm run build && npm test && npm run verify` 全绿。
2. `node bin/dsh-godot.mjs`（无参数）退出码 2 且打印用法。
3. 沙盒 install 冒烟：临时 `$DSH_HOME` 下 install → 检查生成的 patch 块形状 → uninstall → 恢复 `[]`。
4. `dsh --profile <p> --dump-config | grep godot`：bundle 行与 mcp-client 行都在组合树中。
5. （有运行中的 Godot 编辑器时）一次性会话验证进程链（握手、tools=20、WS 认证）。

## npm 发布

```sh
npm run build
npm publish --access public
```

发布后用户通道：

```sh
dsh plugin --profile web add deepseek-harness-godot_unified   # bundle(提示词 section)
npx -y deepseek-harness-godot_unified ...                      # CLI(需要本地 checkout 的 bin,或全局安装)
```

注意：CLI 未发布独立 bin 包前，`npx` 通道要求先 `npm install -g deepseek-harness-godot_unified`（bin 名 `dsh-godot`）。

## 版本记录

### 0.1.0（2026-09）

- 首版：MCP 桥接行 CLI（install/uninstall/status）、Godot 工作流提示词 section（bundle 行）、14 例单元测试与 verify-build。
- 已验证：组合校验、一次性会话进程链（spawn → MCP 握手 → tools=20 → WS 认证 Godot 4.7.2）、web profile 实际安装。
- 未验证（待用户重启 web 后补做）：GUI 会话内的模型工具调用轮。

### 0.2.0（2026-10）

- 新增 **Godot 工作台** client 半边：DSH Web 会话顶部第三个 Tab「Godot」，提供全部工具浏览（Godot 置顶 + 按需组激活）、不经对话的手动工具调用/取消、结果（文本/JSON/截图内联/错误码）与历史（localStorage 上限 50）、技能与提示词面查看、桥接链路状态条。client 为 `lib/client.js` 单文件经典脚本（零模块注入、零 react 打包）。
- 新增 host 半边：经 `webServer` 暴露 `/godot-workbench/api/*` 共 6 条同源路由（status/tools/call/cancel/skills/prompt-sections），数据源 `ctx.tools`（schemas/get/execute 全管线）+ CLI 探测逻辑 host 化 + 技能文件读取；`dsh-godot install` 额外写入 bundle 行 config 覆盖。
- 构建链扩展：新增 `tsdown.config.ts` 与 `build:client`；`verify-build.mjs` 增加 client bundle 格式断言。`package.json` 的 `files` 补入 `tsdown.config.ts`（tarball 可重跑 client 构建）。
- 验证状态：静态全绿（typecheck/build/test/verify）已执行；client 交互与 GUI 视觉（Tab 出现、截图内联、组激活后树刷新、历史重放）**待用户重启 web 后执行 GUI 验收**。

### 0.3.0（2026-10）

- **自研桥接 + 动态工具（v0.2 升级版）**：插件自己 spawn GodotMCP server（stdio）+ 内嵌极简 MCP client（`src/bridge/manager.ts` 恢复并沿用）；工具清单**来自 server 的 `tools/list`**（真实 schema/描述，随组激活动态增删），**删除 v0.2 的写死离线占位清单**（`src/bridge/manifest.ts` 移除）——工具不写死，只有桥接与翻译表（`upstream-zh.ts`）。
- **按会话 cwd 传参**：桥接按**当前调用会话的工作目录**解析项目路径（`paths.json` 按 cwd 批量条目，无匹配回退全局）并作为 `GODOT_MCP_PROJECT_PATH` 传给 server；cwd 变化时重启切换——不同会话工作区可以做不同 Godot 项目，不再绑死目录（官方 mcp-client 行方案因此弃用）。
- **工具清单持久缓存（零默认开销）**：连接成功后把 server `tools/list` 快照缓存到 `$DSH_HOME/godot/tools-cache.json`（与 server dist 绑定校验）；缓存命中时打开 DSH 无需连接（工具直接可见、零闲置开销），**只有首次工具调用或工作台「连接 Godot」按钮才拉起桥接**；无缓存时工作台点「连接」获取并缓存（新增 `POST /godot-workbench/api/connect` 路由 + 状态条按钮）。
- **无默认连接**：server 默认不启动、不连编辑器；Godot preset 会话创建只从持久缓存加载清单（不 spawn）；空闲 10 分钟自动关闭；工具门面在 server 退出后保持在册，调用时自愈。
- **防卡死**：门面注册清单 diff（无变化跳过）；`tools/change` 增量 deny 经 150ms 合并窗口（无 diff 不调 restrict，防事件回声）；工作台 `/api/tools` TTL 缓存 + 仅 `discover_tools` 在场才调用 + 前端描述截断（沿用 2026-09-05/06 修复）。
- **CLI 语义更新**：`install` 不再写官方 mcp-client 行（自研桥接不需要；安装时清理历史受管块防双轨），改把路径写入 `$DSH_HOME/godot/paths.json`；`status` 报告路径配置 + 编辑器注册表；`--read-only` 等原官方行 env 参数标记为不再生效并警告。
- 验证状态：typecheck/build/test（65 例）/verify 全绿；未执行运行时 GUI 验收（不改运行中 web；安装层组合校验与一次性会话进程链待用户环境）。

### 0.4.0（2026-10）

- **首次 npm 发布**：0.1.0–0.3.0 为开发期里程碑记录，均未发布至 npm；本次以 0.4.0 首次发布。
- 功能基线：自研桥接 + 动态工具（清单来自 server `tools/list`，随组激活 `discover_tools` 动态增删）、按会话 cwd 传参（`$DSH_HOME/godot/paths.json`）、工具清单持久缓存（`$DSH_HOME/godot/tools-cache.json`，与 server dist 绑定校验）、Godot 工作台（会话顶部第三 Tab + `/godot-workbench/api/*` 六条同源路由）、`dsh-godot` CLI（install/uninstall/status，v0.4 起不再写官方 mcp-client 行）。
- 环境要求：DeepSeek Harness ≥ `0.1.5-rc.1`；Node.js `^22.19.0 || >=24.0.0`。
- 验证状态：typecheck/build/test/verify 随提交执行；运行时 GUI 验收（工作台交互、Godot preset 会话工具调用）待用户环境。
