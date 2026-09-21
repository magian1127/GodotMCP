using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Instances;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Daemon.Tools;

/// <summary>
/// playtest 通道与混合路由工具(issue 10):game_start / game_stop 经编辑器实例
/// 拉起/停止 playtest;运行时工具(runtime.*)路由到该实例的 Mode B 通道;
/// 截图、运行时节点检查、log_read 按 Node 桥同名 handler 的语义实现
/// (含运行时→编辑器回退与崩溃上下文)。
/// </summary>
/// <param name="instances">实例管理器,提供编辑器实例调用与运行时通道调用(按 instance 寻址)。</param>
[McpServerToolType]
public sealed class PlaytestTools(InstanceManager instances)
{
    /// <summary>
    /// game_start:经编辑器实例拉起游玩测试(playtest),并按需在 daemon 侧等待运行时就绪。
    /// <para>逻辑链:wait_for_runtime 缺省 true → 组装仅含已提供键的参数,经编辑器实例调 game.start
    /// (toolkit 失败透传错误信封)→ 阻塞等待且编辑器回报 runtime_discovery=bridge 时,daemon 侧再等
    /// 运行时连接(10 秒,与 Node bridge.waitForRuntimeConnection 同形):等到则回填 runtime_ready/
    /// runtime_port 并清掉 discovery/hint,超时则覆写 hint(建议以 if_running:'return'+runtime_poll:true
    /// 重试,或查编辑器日志)→ 非阻塞等待且 runtime_ready=false 时同样附引导性 hint →
    /// 实例调用异常映射为错误信封。</para>
    /// </summary>
    /// <param name="scene_path">'main'、'current'(默认)或 res:// 场景路径。</param>
    /// <param name="wait_for_runtime">是否阻塞等待运行时就绪(默认 true;false 时启动后立即返回)。</param>
    /// <param name="runtime_poll">配合 if_running:'return' 使用:重新检查已在跑的游戏是否已连上运行时。</param>
    /// <param name="if_running">'return' 启用幂等模式(已在跑时直接返回现状而不报错)。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>启动结果 JSON(可能含 runtime_ready/runtime_port 或引导性 hint);失败时为错误信封。</returns>
    [McpServerTool(Name = "game_start", ReadOnly = false, OpenWorld = false, Destructive = false)]
    [Description("启动游玩测试。默认等待运行时就绪；wait_for_runtime:false 会启动后立即返回。scene_path 可取 'main'、'current'（默认）或 res:// 路径。if_running:'return' 启用幂等模式。")]
    public async Task<CallToolResult> GameStart(
        string? scene_path = null,
        bool? wait_for_runtime = null,
        bool? runtime_poll = null,
        string? if_running = null,
        string? instance = null)
    {
        var effectiveWait = wait_for_runtime ?? true; // Node zod default(true)
        var args = new JsonObject { ["wait_for_runtime"] = effectiveWait };
        if (scene_path is not null) args["scene_path"] = scene_path;
        if (runtime_poll is not null) args["runtime_poll"] = runtime_poll;
        if (if_running is not null) args["if_running"] = if_running;
        try
        {
            var result = await instances.CallInstanceAsync(
                instance, "game.start", args.ToJsonString(), ToolRouting.CallTimeout, CancellationToken.None);
            if (ToolResults.IsFailure(result))
            {
                return ToolResults.FromToolkitResult(result);
            }

            var payload = JsonNode.Parse(result.GetRawText())!.AsObject();
            if (effectiveWait && payload["runtime_discovery"] is JsonValue discovery
                && discovery.TryGetValue<string>(out var discoveryValue) && discoveryValue == "bridge")
            {
                // 服务器侧等待运行时(吸收异步间隙,让代理单次调用即启动):
                // 与 Node game_start handler 的 bridge.waitForRuntimeConnection(10s) 同形。
                var runtimePort = await instances.WaitForRuntimeConnectedAsync(
                    instance, TimeSpan.FromSeconds(10), CancellationToken.None);
                if (runtimePort is not null)
                {
                    payload["runtime_ready"] = true;
                    payload["runtime_port"] = runtimePort.Value;
                    payload.Remove("runtime_discovery");
                    payload.Remove("hint");
                }
                else
                {
                    payload["hint"] =
                        "Game launched but runtime did not connect within 10s. " +
                        "Follow up with game_start(if_running:'return', runtime_poll:true) to retry, " +
                        "or check log_read(channel='editor') for startup errors.";
                }
            }
            else if (!effectiveWait && payload["runtime_ready"] is JsonValue readyValue
                && readyValue.TryGetValue<bool>(out var ready) && !ready)
            {
                payload["hint"] =
                    "runtime_ready is false — runtime tools are not yet available. " +
                    "Call game_start with wait_for_runtime:true to block until ready, " +
                    "or poll with game_start(if_running:'return', runtime_poll:true).";
            }
            return ToolResults.Json(payload.ToJsonString());
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }
    }

    /// <summary>game_stop:经实例路由(编辑器实例)停止当前正在运行的场景。幂等:没有场景在跑时返回 was_running:false。</summary>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>toolkit 结果信封(失败映射为 isError)。</returns>
    [McpServerTool(Name = "game_stop", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("停止当前正在运行的场景。保持幂等：没有场景运行时返回 was_running:false。无需参数。")]
    public Task<CallToolResult> GameStop(string? instance = null)
    {
        return ToolRouting.RouteAsync(instances, instance, "game.stop", new Dictionary<string, object?>());
    }

    /// <summary>
    /// capture_screenshot:按 target 捕获运行中的游戏或编辑器视口(screenshot),统一构建响应(含传输上限降级)。
    /// <para>逻辑链:target=editor 走编辑器实例 editor.screenshot(仅此分支携带 node_path 与
    /// force_foreground_editor);否则走运行时通道 runtime.screenshot(带 force_foreground_game)
    /// → toolkit 失败透传错误信封 → BuildScreenshotResult 构建响应(编辑器目标追加"无图且无 path
    /// 即失败"的空内容检查)→ 实例调用异常:editor 直接映射错误信封,runtime 先附崩溃上下文再返回。</para>
    /// </summary>
    /// <param name="target">捕获目标:runtime(需要活动的游玩测试)或 editor。</param>
    /// <param name="node_path">仅编辑器目标生效:聚焦并框选一个节点。</param>
    /// <param name="save_path">disk/both 模式的保存位置(runtime 接受 user://screenshots/,编辑器还接受 res://)。</param>
    /// <param name="image_response_mode">inline 内嵌图片;disk 返回保存路径;both 两者都返回。</param>
    /// <param name="image_detail">内嵌图片的分辨率(full/mid/low);保存的文件始终保留全分辨率。</param>
    /// <param name="force_foreground">捕获前还原并聚焦选定目标窗口,默认 false。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>图像内容块 + 元数据 JSON,或仅磁盘信封(path 等);失败时为错误信封(运行时目标可能附崩溃上下文)。</returns>
    [McpServerTool(Name = "capture_screenshot", ReadOnly = true, OpenWorld = false)]
    [Description("使用统一的响应格式捕获运行中的游戏或编辑器视口。运行时捕获需要活动的游玩测试。内联图像超过传输缓冲上限时自动适配：先降低 image_detail 级别（响应的 image_detail/hint 披露实际级别），仍放不下则保存全分辨率 PNG 到磁盘并返回 path（不会报 RESPONSE_TOO_LARGE）。")]
    public async Task<CallToolResult> CaptureScreenshot(
        string target,
        string? node_path = null,
        string? save_path = null,
        string? image_response_mode = null,
        string? image_detail = null,
        bool? force_foreground = null,
        string? instance = null)
    {
        var isEditor = target == "editor";
        var args = new JsonObject();
        if (save_path is not null) args["save_path"] = save_path;
        if (image_response_mode is not null) args["image_response_mode"] = image_response_mode;
        if (image_detail is not null) args["image_detail"] = image_detail;
        if (isEditor)
        {
            if (node_path is not null) args["node_path"] = node_path;
            if (force_foreground is not null) args["force_foreground_editor"] = force_foreground;
        }
        else if (force_foreground is not null)
        {
            args["force_foreground_game"] = force_foreground;
        }

        try
        {
            var result = isEditor
                ? await instances.CallInstanceAsync(
                    instance, "editor.screenshot", args.ToJsonString(), ToolRouting.CallTimeout, CancellationToken.None)
                : await instances.CallRuntimeAsync(
                    instance, "runtime.screenshot", args.ToJsonString(), ToolRouting.CallTimeout, CancellationToken.None);
            if (ToolResults.IsFailure(result))
            {
                return ToolResults.FromToolkitResult(result);
            }
            // 编辑器截图在"无 path 且无 image_bytes"时视为失败(Node editorScreenshotHandler 的空内容保护)。
            return BuildScreenshotResult(result, applyEmptyContentCheck: isEditor);
        }
        catch (InstanceCallException ex)
        {
            return isEditor
                ? ToolResults.FromException(ex)
                : await RuntimeErrors.WithCrashContextAsync(instances, ex, instance);
        }
    }

    /// <summary>
    /// input_simulate:向运行中的游戏注入输入事件序列(key/mouse_button/mouse_motion/action/
    /// click/click_node/send_text)。
    /// <para>逻辑链:单事件对象规范化为单元素数组(Node inputSimulateHandler 同形)→ 组装 events
    /// (可选 summary)经运行时通道调 input.simulate → toolkit 结果透传;实例调用异常映射为错误信封
    /// (游戏未运行即 GAME_NOT_RUNNING,不做崩溃上下文补全)。</para>
    /// </summary>
    /// <param name="events">单个事件对象或事件数组,元素形如 {event_type, event_data?, delay_before_ms?, delay_after_ms?}。</param>
    /// <param name="summary">是否在返回中附带精简摘要。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>toolkit 结果(含每个事件的诊断信息);失败时为错误信封。</returns>
    [McpServerTool(Name = "input_simulate", ReadOnly = false, OpenWorld = false, Destructive = false)]
    [Description(
        "向运行中的游戏注入输入。events 可为单个 {event_type, event_data?, delay_before_ms?, delay_after_ms?}，也可为事件序列数组；连续操作优先在一次调用中传入多个事件。event_type 可取 key、mouse_button、mouse_motion、action、click、click_node、send_text。click 通过 push_input 依次按下、等待 50 毫秒、释放，不改变操作系统焦点或鼠标位置，可安全并行。click_node 接受 {node_path}，调用 grab_focus 并在 BaseButton 上发出 pressed 信号，无需猜测坐标。send_text 通过 push_input 逐字符合成按键事件，将字符串输入已聚焦的文本框，或 event_data.node_path 指定的 Control；会触发直接设置 .text 时跳过的真实 text_changed/text_submitted 信号。send_text 的 event_data：text 必填，node_path 可选（先聚焦该 Control），submit 可选（追加回车）。返回 focus_target、focus_source、text_changed、text_after（隐去密码字段）、chars_sent 和提示。\n\n鼠标坐标模式：\n- position: {x, y}：原始视口/屏幕坐标（默认），适用于按钮、菜单等界面元素。\n- world_position: {x, y}：游戏世界坐标，通过画布变换自动换算，包含摄像机偏移与缩放，适用于点击游戏内的指定位置。\n\n鼠标事件通过 push_input（position + global_position）执行 CanvasLayer/GUI 命中检测，不改变操作系统焦点或鼠标位置，可安全并行。返回每个事件的诊断信息。")]
    public async Task<CallToolResult> InputSimulate(
        JsonElement events,
        bool? summary = null,
        string? instance = null)
    {
        // 单事件对象规范化为数组(Node inputSimulateHandler 同形)。
        JsonNode eventsNode = JsonNode.Parse(events.GetRawText())!;
        if (eventsNode is JsonObject)
        {
            eventsNode = new JsonArray(eventsNode);
        }
        var payload = new JsonObject { ["events"] = eventsNode };
        if (summary is not null)
        {
            payload["summary"] = summary;
        }
        try
        {
            var result = await instances.CallRuntimeAsync(
                instance, "input.simulate", payload.ToJsonString(), ToolRouting.CallTimeout, CancellationToken.None);
            return ToolResults.FromToolkitResult(result);
        }
        catch (InstanceCallException ex)
        {
            return ToolResults.FromException(ex);
        }
    }

    /// <summary>
    /// runtime_inspect_node:检查运行中游戏的节点,按需合并引擎状态与脚本变量两段。
    /// <para>逻辑链:include 缺省取 engine+script 两段 → engine 段调 runtime.get_node_state,
    /// script 段调 runtime.get_script_vars(visibility 缺省 all);任一段 toolkit 失败立即中断并透传
    /// 错误 → 两段齐备时合并为一个 success 信封 → 实例调用异常附崩溃上下文后返回。</para>
    /// </summary>
    /// <param name="node_path">运行时节点的绝对路径,例如 /root/Main/Player。</param>
    /// <param name="include">要返回的分区(engine/script)子集,缺省两者都返回。</param>
    /// <param name="visibility">脚本变量的可见性筛选(public/private/all),缺省 all。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>合并 JSON 信封(success + node_path + 各段);失败时为错误信封(可能附崩溃上下文)。</returns>
    [McpServerTool(Name = "runtime_inspect_node", ReadOnly = true, OpenWorld = false)]
    [Description("检查运行中游戏的节点，在有大小限制的单次响应中返回引擎/检查器状态、脚本变量或两者。")]
    public async Task<CallToolResult> RuntimeInspectNode(
        string node_path,
        string[]? include = null,
        string? visibility = null,
        string? instance = null)
    {
        var sections = new HashSet<string>(include ?? new[] { "engine", "script" });
        var output = new JsonObject
        {
            ["success"] = true,
            ["node_path"] = node_path,
        };
        try
        {
            if (sections.Contains("engine"))
            {
                var engine = await instances.CallRuntimeAsync(
                    instance,
                    "runtime.get_node_state",
                    new JsonObject { ["node_path"] = node_path }.ToJsonString(),
                    ToolRouting.CallTimeout,
                    CancellationToken.None);
                if (ToolResults.IsFailure(engine))
                {
                    return ToolResults.FromToolkitResult(engine);
                }
                output["engine"] = JsonNode.Parse(engine.GetRawText());
            }
            if (sections.Contains("script"))
            {
                var scriptArgs = new JsonObject
                {
                    ["node_path"] = node_path,
                    ["visibility"] = visibility ?? "all", // Node zod default("all")
                };
                var script = await instances.CallRuntimeAsync(
                    instance, "runtime.get_script_vars", scriptArgs.ToJsonString(),
                    ToolRouting.CallTimeout, CancellationToken.None);
                if (ToolResults.IsFailure(script))
                {
                    return ToolResults.FromToolkitResult(script);
                }
                output["script"] = JsonNode.Parse(script.GetRawText());
            }
            return ToolResults.Json(output.ToJsonString());
        }
        catch (InstanceCallException ex)
        {
            return await RuntimeErrors.WithCrashContextAsync(instances, ex, instance);
        }
    }

    /// <summary>
    /// log_read:读取编辑器或游戏输出;channel=editor 直读编辑器控制台,auto/runtime 运行时优先、
    /// 编辑器缓存回退(fallback)。
    /// <para>逻辑链:channel 缺省 auto → editor 分支调 editor.get_console(level_filter 单值规整为
    /// 数组,透传 since_id/clear_buffer 等)并注入行数摘要,toolkit 失败透传 → auto/runtime 分支先以
    /// 5 秒短超时调运行时 debugger.get_log(快速检出冻结的游戏),成功注入行数摘要 → 失败且错误码
    /// 属于 GAME_NOT_RUNNING/TIMEOUT/DISCONNECTED/CLOSED/INTERNAL 时回退编辑器侧 debugger.get_log
    /// 缓存:成功注入回退摘要(区分调试桥 error_buffer 条数与日志缓存行数);缓存失败或不可达时,
    /// 以原始异常附崩溃上下文返回。</para>
    /// </summary>
    /// <param name="channel">auto(默认,运行时优先)/ runtime(仅运行时)/ editor(仅编辑器控制台)。</param>
    /// <param name="limit">最多返回的条目数。</param>
    /// <param name="source">buffer(内存缓冲,默认)或 file(日志文件)。</param>
    /// <param name="level_filter">仅编辑器通道:级别筛选,单值或数组。</param>
    /// <param name="since_id">仅编辑器通道:增量读取游标。</param>
    /// <param name="text_filter">不区分大小写的子串过滤;is_regex=true 时按正则解释。</param>
    /// <param name="is_regex">把 text_filter 当正则表达式解释。</param>
    /// <param name="clear_buffer">仅编辑器通道:读取前清空缓冲。</param>
    /// <param name="instance">目标 Godot 实例:规范化项目路径或 12 位短 id;恰好一个实例时省略。</param>
    /// <returns>日志 JSON(注入 _summary 行数摘要);回退成功时摘要注明缓存来源;最终失败附崩溃上下文。</returns>
    [McpServerTool(Name = "log_read", ReadOnly = true, OpenWorld = false)]
    [Description("读取编辑器或游戏输出。auto 优先读取活动的运行时，无法读取时回退到编辑器侧的崩溃或会话缓存。")]
    public async Task<CallToolResult> LogRead(
        string? channel = null,
        int? limit = null,
        string? source = null,
        JsonElement? level_filter = null,
        double? since_id = null,
        string? text_filter = null,
        bool? is_regex = null,
        bool? clear_buffer = null,
        string? instance = null)
    {
        var effectiveChannel = channel ?? "auto"; // Node zod default("auto")
        var runtimeArgs = new JsonObject();
        if (limit is not null) runtimeArgs["limit"] = limit;
        if (source is not null) runtimeArgs["source"] = source;
        if (text_filter is not null) runtimeArgs["text_filter"] = text_filter;
        if (is_regex is not null) runtimeArgs["is_regex"] = is_regex;

        if (effectiveChannel == "editor")
        {
            // consoleSummaryHandler:level_filter 单值→数组;editor.get_console + _summary 注入。
            var args = new JsonObject();
            if (limit is not null) args["limit"] = limit;
            if (source is not null) args["source"] = source;
            if (level_filter is not null)
            {
                var lf = level_filter.Value;
                args["level_filter"] = lf.ValueKind == JsonValueKind.String
                    ? new JsonArray(lf.GetString())
                    : JsonNode.Parse(lf.GetRawText());
            }
            if (since_id is not null) args["since_id"] = since_id;
            if (text_filter is not null) args["text_filter"] = text_filter;
            if (is_regex is not null) args["is_regex"] = is_regex;
            if (clear_buffer is not null) args["clear_buffer"] = clear_buffer;
            try
            {
                var result = await instances.CallInstanceAsync(
                    instance, "editor.get_console", args.ToJsonString(), ToolRouting.CallTimeout, CancellationToken.None);
                if (ToolResults.IsFailure(result))
                {
                    return ToolResults.FromToolkitResult(result);
                }
                return ToolResults.Json(WithLineSummary(result));
            }
            catch (InstanceCallException ex)
            {
                return ToolResults.FromException(ex);
            }
        }

        // auto/runtime:运行时优先(5s 短超时快速检出冻结游戏)→ 编辑器缓存回退。
        try
        {
            var result = await instances.CallRuntimeAsync(
                instance, "debugger.get_log", runtimeArgs.ToJsonString(),
                TimeSpan.FromSeconds(5), CancellationToken.None);
            if (ToolResults.IsFailure(result))
            {
                return ToolResults.FromToolkitResult(result);
            }
            return ToolResults.Json(WithLineSummary(result));
        }
        catch (InstanceCallException ex) when (ex.Code is "GAME_NOT_RUNNING" or "TIMEOUT" or "DISCONNECTED" or "CLOSED" or "INTERNAL")
        {
            try
            {
                var cached = await instances.CallInstanceAsync(
                    instance, "debugger.get_log", runtimeArgs.ToJsonString(),
                    TimeSpan.FromSeconds(5), CancellationToken.None);
                if (ToolResults.IsFailure(cached))
                {
                    return await RuntimeErrors.WithCrashContextAsync(instances, ex, instance);
                }
                return ToolResults.Json(WithFallbackSummary(cached));
            }
            catch (InstanceCallException)
            {
                return await RuntimeErrors.WithCrashContextAsync(instances, ex, instance);
            }
        }
    }

    // ── 共享：行数摘要 / 截图构建 / 崩溃上下文(与 Node 同形) ─────

    /// <summary>给日志结果注入 _summary 行数摘要("N line(s) (of M total)");total 缺失时以 returned 兜底。</summary>
    /// <param name="result">运行时/编辑器日志调用的原始结果。</param>
    /// <returns>注入摘要后的 JSON 字符串。</returns>
    private static string WithLineSummary(JsonElement result)
    {
        var returned = GetNumberOrNull(result, "returned") ?? 0;
        var total = GetNumberOrNull(result, "total_lines") ?? returned;
        var node = JsonNode.Parse(result.GetRawText())!.AsObject();
        node["_summary"] = $"{returned} line{(returned != 1 ? "s" : "")} (of {total} total)";
        return node.ToJsonString();
    }

    /// <summary>给回退读取的结果注入 _summary:区分调试桥 error_buffer 条数与日志文件缓存行数,两者皆空时注明上次会话无输出。</summary>
    /// <param name="result">编辑器侧 debugger.get_log 缓存结果。</param>
    /// <returns>注入摘要后的 JSON 字符串。</returns>
    private static string WithFallbackSummary(JsonElement result)
    {
        var returned = GetNumberOrNull(result, "returned") ?? 0;
        var errorBuffer = result.TryGetProperty("error_buffer", out var eb) && eb.ValueKind == JsonValueKind.Array
            ? eb.GetArrayLength()
            : 0;
        var parts = new List<string>();
        if (errorBuffer > 0)
        {
            parts.Add($"{errorBuffer} error{(errorBuffer != 1 ? "s" : "")} from debugger bridge");
        }
        if (returned > 0)
        {
            parts.Add($"{returned} cached line{(returned != 1 ? "s" : "")} from log file");
        }
        var summary = parts.Count > 0 ? string.Join(", ", parts) : "no output from last game session";
        var node = JsonNode.Parse(result.GetRawText())!.AsObject();
        node["_summary"] = summary;
        return node.ToJsonString();
    }

    /// <summary>截图响应构建器(Node screenshotResponse.ts 同形):图像优先;仅磁盘时返回精简文本信封。</summary>
    private static CallToolResult BuildScreenshotResult(JsonElement result, bool applyEmptyContentCheck)
    {
        var base64 = GetStringOrNull(result, "image_base64");
        var path = GetStringOrNull(result, "path");
        if (string.IsNullOrEmpty(base64))
        {
            if (applyEmptyContentCheck && path is null)
            {
                return ToolResults.Error(
                    "EMPTY_CONTENT",
                    "screenshot returned no image bytes — node may lack visual content. Capture the full editor viewport instead.");
            }
            var envelope = new JsonObject();
            AddIfPresent(envelope, "path", path);
            AddIfPresent(envelope, "width", GetNumberOrNull(result, "width"));
            AddIfPresent(envelope, "height", GetNumberOrNull(result, "height"));
            AddIfPresent(envelope, "bytes", GetNumberOrNull(result, "bytes"));
            AddIfPresent(envelope, "mime_type", GetStringOrNull(result, "mime_type"));
            AddIfPresent(envelope, "remediation", CloneOrNull(result, "remediation"));
            AddIfPresent(envelope, "hint", GetStringOrNull(result, "hint"));
            AddIfPresent(envelope, "image_detail", GetStringOrNull(result, "image_detail"));
            AddIfPresent(envelope, "returned", GetStringOrNull(result, "returned"));
            return ToolResults.Json(envelope.ToJsonString());
        }

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return ToolResults.Error("INTERNAL", "screenshot image_base64 不是合法 base64");
        }
        var meta = new JsonObject();
        AddIfPresent(meta, "width", GetNumberOrNull(result, "width"));
        AddIfPresent(meta, "height", GetNumberOrNull(result, "height"));
        AddIfPresent(meta, "bytes", GetNumberOrNull(result, "bytes"));
        AddIfPresent(meta, "path", path);
        AddIfPresent(meta, "remediation", CloneOrNull(result, "remediation"));
        AddIfPresent(meta, "hint", GetStringOrNull(result, "hint"));
        AddIfPresent(meta, "image_detail", GetStringOrNull(result, "image_detail"));
        AddIfPresent(meta, "returned", GetStringOrNull(result, "returned"));
        return new CallToolResult
        {
            Content =
            [
                // Data 期望"已编码 base64"(BinaryData);FromBytes 工厂才负责 原始字节→base64 的编码。
                ImageContentBlock.FromBytes(imageBytes, GetStringOrNull(result, "mime_type") ?? "image/png"),
                new TextContentBlock { Text = meta.ToJsonString() },
            ],
        };
    }

    /// <summary>值非 null 才写入目标 JSON 对象(保持"省略键"与"显式空值"的语义区分)。</summary>
    /// <param name="target">目标 JSON 对象。</param>
    /// <param name="key">属性名。</param>
    /// <param name="value">待写入的值。</param>
    private static void AddIfPresent(JsonObject target, string key, JsonNode? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    /// <summary>取属性并深拷贝为 JSON 节点:缺键或显式 null 返回 null。</summary>
    /// <param name="element">源 JSON 元素。</param>
    /// <param name="key">属性名。</param>
    /// <returns>深拷贝的 JSON 节点或 null。</returns>
    private static JsonNode? CloneOrNull(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(value.GetRawText())
            : null;
    }

    /// <summary>取字符串属性:缺键或类型不符返回 null。</summary>
    /// <param name="element">源 JSON 元素。</param>
    /// <param name="key">属性名。</param>
    /// <returns>字符串值或 null。</returns>
    private static string? GetStringOrNull(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>取数值属性:缺键或类型不符返回 null。</summary>
    /// <param name="element">源 JSON 元素。</param>
    /// <param name="key">属性名。</param>
    /// <returns>数值或 null。</returns>
    private static double? GetNumberOrNull(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }
}
