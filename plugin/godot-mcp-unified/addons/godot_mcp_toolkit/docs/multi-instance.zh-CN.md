[英文原文](multi-instance.md)

# 多实例 / 多人游戏设置

测试 P2P 或多人网络的开发者经常会同时运行两个 Godot 编辑器（一个作为服务器，一个作为客户端）。MCP 架构可以处理这种情况，但必须采用正确设置。共有三种模式：

---

## 模式 A —— 不同目录中的两份副本：支持（推荐）

使用 `git worktree add` 或复制项目文件夹，使每个编辑器实例打开不同的绝对路径。插件按路径索引的注册表、每个 worktree 独立的 token 哈希以及动态端口分配，会为每个实例提供独立的端口、token、注册表条目和 MCP 客户端会话。

```
git worktree add ../MyGame-client client-branch
# Open each folder in a separate Godot editor
# Each gets its own MCP port + token automatically
```

**工作原理：**
- 每个编辑器扫描 6550–6560 端口，并绑定第一个空闲端口。
- 系统范围的 `projects.json` 注册表将每个绝对路径映射到其端口。
- TypeScript 桥接器通过将 `process.cwd()`（或 `GODOT_MCP_PROJECT_PATH`）与注册表匹配，解析正确端口。
- 每个 worktree 的 token 哈希确保认证 token 不会冲突。

**日志竞争注意事项：**如果两份副本在项目设置中使用相同的 `config/name`，它们会共享 `user://logs/godot.log`。这会影响 MCP 日志命令：

- `log_read(channel:"editor", source:"file")` 和 `log_read(channel:"runtime", source:"file")` 读取此共享日志，因此两个实例的输出会交错。
- 在 **Godot 4.5+** 中，`source:"buffer"` 使用内存中的 LogBuffer（Logger API），每个编辑器进程相互隔离——不会跨实例污染。多实例设置优先使用 `source:"buffer"`。
- 在 **Godot 4.2–4.4** 中，buffer 模式会回退到文件尾部读取，因此会读取同一共享日志文件，同样会受到交错影响。

**解决方法：**在项目设置中为每份副本设置不同的 `application/config/name`。Godot 使用它推导 `user://` 目录路径，从而分离日志文件。使用 git worktree 时很简单——只需在该 worktree 副本中修改名称。

---

## 模式 B —— Godot 内置“Run Multiple Instances”：大体支持

单个编辑器，多个游戏进程（Project Settings > Run > Run Multiple Instances）。

- **编辑器连接：**不受影响——只有一个编辑器、一个编辑器通道端口。
- **运行时连接（正在运行的游戏）：**动态端口分配有效——每个游戏进程从 6570–6585 范围绑定不同端口。不过，注册表每个条目只有一个 `runtime_port` 字段，因此桥接器只能发现最后启动的游戏进程。

这是一个较小的限制。多人测试期间，MCP 的使用主要是编辑器工具（内省），而不是运行时工具（操作运行中的游戏）。

编辑器的调试桥接器也有相同的单会话限制：它只跟踪最近启动的游戏进程，因此 `debug_inspect(mode:"state")`、`debug_continue` 和 `log_read(channel:"runtime")` 只反映最后一个实例。若要同时调试多个运行中的游戏实例，请使用模式 A（分离的编辑器），每个实例都有自己的编辑器和调试桥接器。

---

## 模式 C —— 同一项目、同一目录、两个编辑器：不支持

同时用两个 Godot 编辑器打开同一项目目录会导致：

- **注册表键冲突：**注册表按项目根目录哈希建立键，因此两个编辑器会映射到**同一个**槽位。注册表只跟踪**最后注册**的实例——这是最后写入者胜出，**不是**保证最新者胜出，因此第二个编辑器会覆盖第一个条目（端口、token 路径），桥接器解析到哪个实例也未定义。
- **Token 冲突：**相同绝对路径会产生相同的哈希 token 文件名，因此重新连接时认证失败。
- **Godot 层面的问题：**元数据锁警告、`user://` 缓存损坏风险。这同样是 Godot 的反模式——引擎本身不正式支持用两个编辑器打开一个项目路径（GDScript 调试器端口是单一全局设置，并且并发编辑器会争抢共享的 `user://` 临时文件）：参见上游 godotengine/godot#58723 和 #16679。

**请改用模式 A。**`git worktree add` 只需几秒，就能提供完整隔离。

---

## 两个编辑器同时使用 GDScript LSP

`lsp_diagnostics`、`lsp_symbols`、`lsp_hover`、`lsp_completion` 和 `lsp_navigate` 会访问 Godot 内置的 **GDScript Language Server**。它绑定单一的**机器级** TCP 端口（默认 **6005**），而**不是**每项目端口。它独立于上面的每项目 WebSocket：**任意两个编辑器都会在此冲突——无论是同项目 worktree（模式 A），还是两个互不相关的项目**——因为它们共享同一个 6005。当第二个编辑器无法绑定 6005 时，其 LSP 在 Godot 4.2–4.6 上会静默失败（4.7 会在 Output dock 记录失败，但插件无法读取该记录），所以没有下面的设置时，第二个项目的 LSP 工具会访问**第一个**编辑器。

Toolkit 会将每个编辑器的 LSP 端点发布到注册表，服务器按项目发现端点，但引擎会在插件能够观察之前消费 `--lsp-port`——因此每个编辑器都需要**不同的启动端口**以及相匹配的环境变量：

> **配置方法。**为每个编辑器分配自己的 LSP 端口，包括第一个编辑器（`--lsp-port`；注意使用**空格**而不是 `=`；Godot ≥ 4.2），并通过 `GODOT_MCP_LSP_PORT` 告知该编辑器的 MCP 服务器对应端口：
>
> ```bash
> 编辑器 A 启动命令：`godot --editor --path /path/to/projectA --lsp-port 6005`
> 编辑器 B 启动命令：`godot --editor --path /path/to/projectB --lsp-port 6015`
> ```
> ```json
> // projectA/.mcp.json — env block
> "env": { "GODOT_MCP_CONFIG_VERSION": "1", "GODOT_MCP_LSP_PORT": "6005" }
>
> // projectB/.mcp.json — env block
> "env": { "GODOT_MCP_CONFIG_VERSION": "1", "GODOT_MCP_LSP_PORT": "6015" }
> ```
>
> 编辑器 A 可以完全不带参数使用默认 6005，但仍建议将它固定下来。插件发布的是编辑器的**设置**（除非你修改过，否则为 6005），而不是你在命令行传入的 `--lsp-port`，所以每个已固定的编辑器仍会注册 6005——这可能导致一个实际占用 6005 的*未固定*编辑器被报告为与已固定编辑器冲突。上面的 `GODOT_MCP_LSP_PORT` 让 A 完全不参与这项比较。`GODOT_MCP_LSP_HOST` 以同样方式覆盖主机（很少需要——LSP 位于 localhost）。这些对应熟悉的 `lsp.serverPort` / `lsp.serverHost` 客户端设置。

**避免使用 6006。**编辑器还会启动 Godot 的 Debug Adapter Protocol 服务器，默认使用 `127.0.0.1:6006`，因此每个打开的编辑器已经占用该端口——固定 LSP 到这里会绑定失败，在 4.2–4.6 上还是静默失败。从 LSP 默认值按十递增（6005、6015、6025、……）可避开这两个端口。

**如果没有**分配不同的 `--lsp-port`，第二个编辑器的服务器会报告可见的 `LSP_PORT_CONFLICT` 并拒绝响应——它**不会**静默返回另一个项目的结果。

> **Godot 4.2–4.4。**跨项目安全网（工作区根验证）需要 Godot **4.5+**。在 4.2–4.4 上，服务器无法检测 6005 端口是否被其他项目或几乎同时启动的实例占用，因此在打开多个编辑器并使用 LSP 工具前，**始终**为每个编辑器设置不同的 `--lsp-port` + `GODOT_MCP_LSP_PORT`。

---

## 并行运行的确定性端口（固定）

模式 A 依靠**自动扫描 + 注册表发现**为每个编辑器分配不同端口。若要获得完全**确定性**的并行设置——例如测试工具会同时启动多个编辑器与服务器，必须预先知道每个端口——请为每个实例固定一组三元组，并且只 `export` **一次**，让编辑器**和** MCP 服务器都继承它。（环境变量按进程生效，不是同步通道：如果只有一方获得固定值，两边就会失去同步——参见 [advanced_configuration.md](advanced_configuration.md)。）

为每个实例提供独立的**编辑器 WS**、**运行时 WS** 和 **LSP** 端口：

```bash
# Instance A — export the pins once, then launch editor + client from this shell
export GODOT_MCP_EDITOR_PORT=6551
export GODOT_MCP_RUNTIME_PORT=6571
godot --editor --path ../MyGame-a --lsp-port 6005 &
# …launch the MCP client for MyGame-a from this same shell; its .mcp.json sets
#    GODOT_MCP_LSP_PORT=6005, and the server inherits the two WS pins from the export.

# Instance B — a separate shell / environment
export GODOT_MCP_EDITOR_PORT=6552
export GODOT_MCP_RUNTIME_PORT=6572
godot --editor --path ../MyGame-b --lsp-port 6015 &
# …its .mcp.json sets GODOT_MCP_LSP_PORT=6015
```

此时每个实例都会绑定已知且不冲突的端口，**完全不依赖注册表**——没有文件锁竞争，也没有发现竞态。固定但已被占用的端口会**明确失败**（编辑器 dock 警告 + 精确的服务器错误），而不会静默地改为扫描其他端口，因此三元组冲突会立即可见，不会变成神秘的串话。项目隔离仍请使用**模式 A**（不同目录）；固定端口只是在其上增加确定性。

---

## 快速参考

| 模式 | 设置 | 状态 |
|---------|-------|--------|
| A：两份副本（git worktree） | 不同目录 | 支持 |
| B：内置多实例运行 | 单个编辑器、多个游戏进程 | 大体支持 |
| C：同一目录、两个编辑器 | 相同项目路径 | 不支持 |

## 另请参阅

- `GODOT_MCP_PROJECT_PATH` 环境变量，用于 CWD 解耦的设置
