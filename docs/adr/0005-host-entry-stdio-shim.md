# 0005: 宿主条目 = stdio + 随包 shim（自举 daemon）

每个宿主的 MCP 条目统一为 `type: stdio`、`command` 指向随包发布的 `godot-mcp-shim`
（`server-dotnet/publish/<rid>/`），daemon 缺席时由 shim 自举拉起。即：**条目形态服从
"任何 Godot 工程、任何宿主都能自行拉起服务"**，而不是服从"少一个转发进程"。
本文修订 [ADR-0002](0002-daemon-listening-faces.md) 的 Consequences 中
"`.mcp.json` 需切换为 http 型"一条；ADR-0002 对 Godot 方向的判断不变。

## 背景：为什么 ADR-0002 的 http 结论不成立

ADR-0002 把 host 面定为 loopback Streamable HTTP，前提是"安装器铺 Bearer token、daemon 常驻"。
2026-09-23 复核发现该前提有两处硬伤：

1. **凭据必须逐宿主铺展。** http 型要求每个宿主各存一处 token，而 tracked 的契约文件
   （如 `.codex-plugin/mcp.json`）**不能**写凭据——契约与凭据被迫分离，安装器于是成了唯一
   布线点；换机器、换宿主都要再铺一次，且"配置看起来对、实际没凭据"是静默失败。
2. **http 型下没有任何宿主侧动作能拉起 daemon。** 宿主连不上时，用户看到的是连接错误，
   而不是"服务被拉起来了"。自举责任只能外移到编辑器边车或机器级开机自启。

stdio + shim 把这两件事一起解掉：凭据留在机器级注册表、由 shim 自己读（**零铺展**），
自举留在宿主侧（**谁调用谁拉起**）。

## Considered Options

- **全 http 型（ADR-0002 原方向）**：进程数最少、无转发跳数；但放弃宿主自举、凭据需逐宿主铺展。未采用。
- **两套并存（http 为默认、shim 兜底）**：能力上最全，但两套形态都要维护与文档化，
  且"默认"与"兜底"在实践中必然漂移（谁被真正测试，谁才是默认）。未采用。
- **bat/PS 脚本当入口**：脚本能当启动器，但当不了可靠的转发器——MCP 是行帧 JSON-RPC，
  转发器还要处理 SSE 长流、401 后重读 token、背压；且 pwsh 常驻 60–80 MB 比 shim 的
  36 MB 更重，属净增开销。未采用。
- **全 stdio shim（ADR-0002 曾列为未采用）**：即本决策。ADR-0002 否它的理由是
  "每会话多一跳转发进程"——该代价被接受，因为"每宿主能自举"的价值更高。

## Consequences

- **进程模型**：每会话一个 shim + 一个机器级 daemon（N+1）；内存约 `79 MB + 36 MB × N`
  （2026-10-06 修订：shim 改为 NativeAOT 后约 `79 MB + 14 MB × N`，见文末修订记录）。
- **产物形态**：机器上有**两个** exe（daemon 107 MB + shim 71 MB，自包含单文件；2026-10-06 起
  shim 在本机发布时为 NativeAOT，约 4 MB），同目录（shim 缺省从自身同目录解析 daemon）。
- **插件只写这一种形态**：`mcp_json_sync.gd` 的写入口与各安装器（项目级 / Codex / VS Code /
  ZCode）一律产出 stdio + shim；发现逻辑把指向退役入口（`server/dist/index.js`）的旧配置
  判为"失效条目"，引导迁移而非沿用。面板不给出失效配置背书。
- **shim 成为关键路径**：它的转发实现（逐行顺序转发）决定了宿主能否用 SSE 长流订阅
  （`subscriptions/listen`）。这是本决策引入的已知待验项，不是免费的。
- **若日后要收敛进程/文件数**：减少到"一个 exe 文件"需让 daemon 支持 `--stdio` 双角色
  （先启动者持锁成为 daemon，其余退化为薄转发）；减少到"一个进程"必须放弃宿主自举、
  改由机器级开机自启常驻。两条路都不与本文冲突，但都要重开决策。

## 修订记录

### 2026-10-06：shim 改为 NativeAOT 发布（由用户要求）

**背景**：shim 只做逐行转发，却以自包含单文件发布：71 MB 的 exe 内含完整运行时与 JIT，
每个 shim 进程提交内存约 10 MB、工作集约 36 MB、16 个线程。宿主每开一个会话就多一个 shim，
这部分开销随会话数线性增长。

**新口径**：shim 的发布配置在目标系统本机发布时启用 NativeAOT（`PublishAot` +
`InvariantGlobalization` + `OptimizationPreference=Size`），代码不改（AOT 编译零警告；csproj
常开 AOT/裁剪分析器防回退）。win-x64 同负载实测（initialize + tools/list 后空闲）：exe 约 4.3 MB，
提交内存约 4.7 MB、工作集约 14 MB、8 个线程，首个应答从约 180 ms 降到约 50 ms。daemon 不变。

**代价与接受理由**：NativeAOT 不能跨系统编译，且需要本机 C/C++ 工具链（要求见
[server-dotnet 发布](../../plugin/godot-mcp-unified/server-dotnet/README.md#发布)）。交叉发布
（如在 Windows 上发布 osx-arm64）自动回退为自包含单文件，既有的交叉构建流程不受影响；
要得到某系统的 AOT 产物，在该系统本机发布即可。
