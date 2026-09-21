[英文原文](advanced_configuration.md)

# 高级配置

这些是**可选的微调旋钮**，大多数用户完全不需要调整——默认值开箱即用。它们位于 **Project → Project Settings → `mcp_toolkit/`** 下（启用 *Advanced Settings* 后可见），也可以通过 `ProjectSettings` 设置。

这些值在代码中都不会被限制：下面建议的范围只是指导，不是强制限制。允许使用极端值，后果由你负责。

## 并发

Toolkit 会将编辑器变更串行化，并仲裁多个 MCP 客户端之间的场景访问。以下键用于调整这套机制。

### `mcp_toolkit/concurrency/scan_idle_timeout_ms` —— 默认 `5000`

场景保存或打开在放弃前等待正在进行的 `EditorFileSystem` 扫描完成的时长，单位为毫秒。在扫描**期间**打开或保存场景可能读取不一致的文件系统状态并导致编辑器崩溃，因此 Toolkit 会先等待扫描结束；如果扫描未及时稳定，就以 `TIMEOUT` 错误**中止操作**——绝不会进入正在进行的扫描。

- `0` = 快速失败（完全不等待）。
- 建议 `1000`–`30000`。
- 对导入量大、扫描耗时较长的项目可以提高它，但如果扫描确实卡住，保存 / 打开也会等待更久。

### `mcp_toolkit/concurrency/mutation_watchdog_grace_ms` —— 默认 `60000`

在变更期限上额外增加的宽限时间（毫秒），之后分发**看门狗**会强制清除卡住的变更锁。期限是正在执行的命令声明的 `timeout_ms`（未声明时为系统上限 300 秒）**加上**这段宽限时间。

看门狗是**安全网**：只有当变更处理器中止或挂起、否则会阻塞所有后续变更直到编辑器重启时，它才会触发。正常运行中不会触发。降低该值可以缩短此类（罕见）卡死的恢复时间，但可能强制清除一个确实运行缓慢的处理器；宽裕的默认值让行为良好的命令几乎不可能误触发。

### `mcp_toolkit/concurrency/scene_lease_ttl_ms` —— 默认 `8000`

多个客户端编辑不同场景时，有时限的*租约*可以防止跨场景污染：不持有租约的客户端发出的依赖标签页命令，会排队直到持有者租约到期。这是持有者在等待客户端能够抢占租约前可以不续租的时长，单位为毫秒。

- 更低 = 客户端交接更灵敏，但标签页切换更多。
- 更高 = 标签页切换更少，但等待客户端阻塞更久。

## 读取大数据——上限与分页契约

可能返回大负载的读取会按次限制。公开文件读取工具是 `user_data_read(mode="file")`，它按字节偏移为 `user://` 内容分页。Codex 通过工作区文件工具读取项目脚本；工具包为 MCP resources 和扩展实现保留内部的按行窗口读取器。

### 统一分页契约

进行受上限限制的读取时，你总会得到：

- **`has_more`**（bool）——窗口后仍有数据时为 `true`，窗口已到末尾时为 `false`。成功读取时始终存在。
- **`total_<unit>`**——源的完整大小，可据此知道还剩多少。
- **`returned`**——本窗口的大小。
- **`next_<cursor>`**——用于继续读取的值，仅在 `has_more` 为 `true` 时存在（`user_data_read` 使用 `next_offset`）。
- **`hint`**——一行说明，仅在 `has_more` 为 `true` 时存在，明确告诉你应传回哪个游标。

**循环始终相同：**调用工具；如果 `has_more` 为 `true`，将返回的 `next_<cursor>` 作为对应请求参数传回并再次调用；当 `has_more` 为 `false` 时停止。

**示例——`user_data_read`（字节）。**请求 `offset`（默认 `0`）+ `max_bytes`；用 `next_offset` 继续：

```
user_data_read  mode=file  path=user://saves/big.dat  max_bytes=400
  → { returned: 400, offset: 0, next_offset: 400,
      total_bytes: 1000, has_more: true,
      hint: "more bytes remain — re-call save.read with offset = next_offset (400) until has_more is false" }
user_data_read  mode=file  path=user://saves/big.dat  offset=400  max_bytes=400
  → { returned: 400, next_offset: 800, total_bytes: 1000, has_more: true, hint: … }
user_data_read  mode=file  path=user://saves/big.dat  offset=800  max_bytes=400
  → { returned: 200, next_offset: 1000, total_bytes: 1000, has_more: false }   # done — no hint
```

内部 script resource 读取器使用相同响应字段，但游标单位为行。Codex 工作流使用工作区文件读取，不再把该传输原语暴露为另一个 MCP 工具。

### 上限变更何时生效（无需重启）

下面的限制设置由处理器**每次调用时**读取，因此通过 dock 的微调框、Settings UI 或 `meta_set_limits` 做出的变更会在**下一次调用**立即生效。无需重启，也无需重新连接。

**服务器不会被推送该值。** MCP 服务器公布的是作为安全上限的静态 `max_bytes` schema 绑定；**真实的实时上限由 Toolkit 在调用时执行**。超过实时上限的请求会以 `INVALID_PARAMS` 拒绝，消息中会写出**当前**上限——因此当你降低上限时，模型会从该错误**即时**学到新限制，而不是从公布的 schema 学到。（这就是 schema 看似允许的值仍可能被拒绝的原因：实时上限才是权威。）

## 限制

### `mcp_toolkit/limits/save_read_cap_kb` —— 默认 `256`

`user_data_read(mode="file")` 单次调用返回的最大窗口，单位为 KB（最小 64）。大于该上限的 `user://` 文件仍可通过分页完整读取——关于 `offset` / `next_offset` 循环，参见上面的**读取大数据**。这是读取大于 WebSocket 帧上限的文件的唯一方式（见下方 `ws_buffer_kb`），因为响应会整体发送。

将它提高到 `ws_buffer_kb` 之上是一个陷阱：如果窗口无法容纳在 WebSocket 缓冲区中，请求会在发送前以 `FILE_TOO_LARGE` 拒绝，而不是静默丢弃。提高该上限时，同时提高 `ws_buffer_kb`。

### `mcp_toolkit/limits/script_read_cap_kb` —— 默认 `256`

内部 script resource 读取器单次调用返回的最大负载，单位为 KB。超过上限的项目脚本读取会以 `FILE_TOO_LARGE` 拒绝；Codex 工作流应使用工作区文件读取。

### `mcp_toolkit/limits/ws_buffer_kb` —— 默认 `1024`

每个 WebSocket 对等端的缓冲区大小，单位为 KB（最小 256）。该设置同时调整**两个** MCP 通道——编辑器服务器与 playtest 运行时服务器共享同一传输层，一帧对一边放得下就对另一边也放得下。如果发送非常大的扩展或素材负载并在高负载下看到连接被截断或丢弃，可以提高它。

截图会对该上限自动适配：放不下的内联截图先逐级降采样（`full` → `mid` → `low`，实际级别在响应的 `image_detail` / `hint` 中披露），全部放不下则把全分辨率 PNG 存盘并只返回路径——截图绝不会以 `RESPONSE_TOO_LARGE` 失败。因此提高该设置只是抬高内联上限（减少降级），并非让截图工作的必要条件。

## 监听端口（编辑器与运行时）

Toolkit 的三个 MCP 通道中有两个绑定**动态** TCP 端口。默认情况下，每个通道都会**扫描**一小段端口，并将绑定端口发布到机器范围的注册表，让 MCP 服务器无需配置即可发现。你也可以按通道**固定**精确端口，或**迁移**扫描范围。这些值从进程环境读取（`.mcp.json` 的 `env` 块，或启动前 shell 中的 `export`）——Toolkit 从不写入它们。

| 环境变量 | 通道 | 作用 | 默认值 |
|---|---|---|---|
| `GODOT_MCP_EDITOR_PORT` | 编辑器通道 | **固定**——绑定此精确端口，否则失败 | —（扫描） |
| `GODOT_MCP_EDITOR_PORT_MIN` / `_MAX` | 编辑器通道 | **迁移**扫描范围（含首尾） | `6550` / `6560` |
| `GODOT_MCP_RUNTIME_PORT` | 运行时通道（运行中的游戏） | **固定**——绑定此精确端口，否则失败 | —（扫描） |
| `GODOT_MCP_RUNTIME_PORT_MIN` / `_MAX` | 运行时通道 | **迁移**扫描范围（含首尾） | `6570` / `6585` |

MCP 服务器也会读取相同的 `GODOT_MCP_EDITOR_PORT` / `GODOT_MCP_RUNTIME_PORT` 来决定要**拨号**的端口——两个进程继承同一值时，固定端口让监听和拨号无需发现即可一致。`_MIN` / `_MAX` 范围变量**仅用于监听端**（服务器不会读取；扫描场景由发现机制覆盖）。第三个通道——GDScript **LSP**——由 Godot 负责绑定，因此只能通过服务器侧的 `GODOT_MCP_LSP_PORT` / `GODOT_MCP_LSP_HOST` 进行**仅连接固定**（下一节）。

### 固定与扫描——两种模式互斥

- **固定**（设置了 `*_PORT` 固定值）：监听器绑定该**精确**端口，否则**明确失败**——绝不会扫描其他端口。如果端口已占用，会短暂重试同一个端口（等待之前的实例释放），然后报告**dock 警告**（编辑器）或响亮的游戏控制台错误（运行时），并写出端口。固定值会让 `_MIN` / `_MAX` 范围**无关**——它会被忽略（日志会记录一行说明）。
- **扫描**（没有固定值）：监听器扫描范围（默认范围，或设置的 `_MIN` / `_MAX`），绑定第一个空闲端口并发布用于发现。这是**低摩擦**默认方式——无需在两侧管理环境变量。如果**整个范围**都被占用，编辑器会显示同样的 dock 警告，写出范围并持续重试。

格式错误的固定值、超出范围的端口（有效范围 `1–65535`），或 `MIN > MAX` 都会在 dock + 控制台中给出**明确错误**，绝不会静默回退到默认值。

### 环境变量不是同步通道

编辑器进程（**监听**）和 MCP 服务器进程（**拨号**）会**独立**读取环境。只有当**两个**进程继承相同值时，固定值才会让它们一致：

- **普通 `.mcp.json` 情况只为服务器设置 `env`。** 如果你还从**桌面快捷方式**启动 Godot 编辑器，该快捷方式不会继承 shell 的临时 `export`（Windows 上尤其明显），因此固定值只到达服务器，编辑器却没有——服务器随后会拨号到无人监听的端口。现在它会用精确消息**快速失败**而不是挂起（编辑器 dock 也会显示不匹配），但真正的修复是让双方继承固定值。
- **支持的模式（测试工具 / 并行测试）：**只 `export` 一次固定值，然后从同一环境启动编辑器**和** MCP 服务器 / 客户端，使两者都继承它：

  ```bash
  export GODOT_MCP_EDITOR_PORT=6557
  godot --editor --path /path/to/project &   # editor inherits the pin (listens on 6557)
  # …launch the MCP client from the same shell so its server inherits it too (dials 6557)
  ```

- 如果不想在两侧管理环境，请**优先使用扫描模式**——注册表发现会自动保持两侧一致。

## macOS：启动 MCP 客户端（daemon HTTP）

标准配置让 MCP 客户端经回环 HTTP(`http://127.0.0.1:6590/`)连接机器级 daemon——每会话 Node 服务器已随插件 1.1.0 退役，不再有 `node` 命令或 `PATH` 问题。

**如果客户端在 macOS 上无法连接**，依次检查：

- **从终端启动客户端以查看错误。**`open` 没有帮助——从 shell 启动应用的二进制文件，才能打印客户端真正的启动错误。
- **确认项目根目录存在 `.mcp.json`。** 如果缺少，请在 Godot MCP Unified dock 中点击 **Write .mcp.json**（写入的是 daemon HTTP 条目）。
- **确认 daemon 正在监听** `127.0.0.1:6590`——编辑器的 auto-spawn 边车或宿主安装器（工作区仓库的 `adapters/zcode/install-http-face.ps1`）会按需拉起它。

## 语言服务器（LSP）

`lsp_diagnostics`、`lsp_symbols`、`lsp_hover`、`lsp_completion` 和 `lsp_navigate` 连接 Godot 内置的 GDScript 语言服务器。MCP 服务器通过注册表按项目发现正确端点，因此**单个编辑器无需配置**。以下两个环境变量（在项目的 `.mcp.json` `env` 块中设置）会覆盖发现机制——仅在**同时有多个编辑器运行 LSP**时需要（参见 `docs/multi-instance.md`）。

### `GODOT_MCP_LSP_PORT` —— 默认：发现（否则为 `6005`）

项目服务器连接的 GDScript LSP 端口。优先级最高——绕过注册表发现。将它设置为启动该编辑器时使用的 `--lsp-port`。

### `GODOT_MCP_LSP_HOST` —— 默认：发现（否则为 `127.0.0.1`）

GDScript LSP 的主机。很少需要——LSP 仅位于 localhost。对应熟悉的 `lsp.serverHost` 客户端设置。

## 编辑器失去焦点时的响应性

> **注意——以下两个键位于 Editor Settings，而非 Project Settings。**打开 **Editor → Editor Settings**，搜索 `mcp_toolkit/performance`。本文其他内容都是 *Project* 设置；这两个是例外，因为它们控制**机器全局的编辑器行为**，属于个人电池 / CPU 偏好——所以它们有意**不会**写入 `project.godot`（绝不提交到版本控制）。

编辑器失去焦点时，Godot 会将进程循环限制在低功耗帧率（编辑器设置 `interface/editor/unfocused_low_processor_mode_sleep_usec`，默认约 10 fps）。Toolkit 在该循环中轮询 WebSocket，因此无焦点编辑器每秒只能响应 MCP 命令约 2–3 次。在正常 MCP 会话中编辑器确实处于无焦点状态（你正在查看聊天窗口），所以客户端连接后 Toolkit 会提高无焦点帧率，并在最后一个客户端断开时恢复。

### `mcp_toolkit/performance/keep_editor_responsive_unfocused` —— 默认 `true`

选择开关。**开启**时（默认），至少有一个 MCP 客户端连接期间，Toolkit 会提升无焦点帧率。**关闭**时，保留 Godot 默认的无焦点低功耗节流——如果你对电池 / CPU 敏感，且不介意编辑器在后台时命令响应更慢，可以选择此项。dock 的 *Server Status* 区域提供匹配的开关，以及实时 **Off / On (idle) / On · active** 指示器。

### `mcp_toolkit/performance/unfocused_responsive_sleep_usec` —— 默认 `16666`

提升后的无焦点进程睡眠时间，单位为微秒。越低 = 帧率越高 = 命令越灵敏，但后台 CPU 越多。不做限制。

- `16666` ≈ **60 fps**（默认）——最灵敏；也能让自动 smoke / sweep 运行保持快速。与早期 Toolkit 版本相比行为不变。
- `33333` ≈ **30 fps**（省电）——后台 CPU 大约减半。交互用户几乎感觉不到差异（命令延迟主要由智能体思考时间决定），完整自动 smoke 运行只增加约 20–30 秒。
- 轮询循环每 4 帧运行一次，因此有效 MCP 轮询率约为帧率的四分之一（60 fps 时约 15 Hz，30 fps 时约 7.5 Hz）。

> 一台机器上的快速 CPU 合理性检查显示，编辑器空闲后台 CPU 在 60 fps、30 fps 和默认 10 fps 之间只有适度差异；精确数字取决于机器，因此以上内容应视为指导，而非测量结果。

### 崩溃与并发安全

提升后的值是机器全局设置，而 Godot 只会在某些事件（关闭设置对话框、退出等）将编辑器设置写入磁盘，因此发生一次写盘后的崩溃，或同时运行第二个编辑器，可能会让设置滞留在提升后的值。为避免这种情况：

- 提升之前，Toolkit 会在一个小型、按 Godot 版本区分的机器级备份文件中记录一次**真实的原始**值（文件位于 Toolkit 的注册表目录——也是多实例发现使用的目录），并使用“首次写入者胜出”的文件锁。第一个编辑器已经提升时，第二个连接的编辑器**不会**覆盖此备份，因此真实原始值不会丢失。
- 最后一个客户端断开时——并且在**下一次编辑器启动时进行自愈**——Toolkit 会以**冲突感知**方式恢复设置：如果实时值仍等于 Toolkit 写入的值，则恢复真实原始值；如果你（或另一个工具）期间修改了它，则**保留你的值**并简单清除备份。无论哪种情况，没有活动连接时提升都不可能持久存在。

**已记录的边界情况：**

- 如果你在设置已经提升时，手动将该键设置为**完全相同的提升值**，Godot 不会发出变更事件（相同值写入是空操作），因此 Toolkit 无法区分你的值和自己的值——恢复时会将其视为自己的值并还原原始值。这是冲突感知检查唯一无法检测的情况。
- 两个编辑器同时连接时，如果第一个断开，它会在第二个仍连接时恢复设置，因此第二个编辑器会以默认无焦点帧率运行，直到下一次新连接。这是短暂的响应性下降，不会持久改变设置，也符合 Toolkit 早期行为。

---

*这些是高级可调项。如果不确定，请保留默认值。*
