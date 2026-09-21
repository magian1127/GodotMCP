using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Tools;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Groups;

/// <summary>
/// 组工具的调用路由(Node groups/groupToolHandlers.ts 全语义的 C# 移植):
/// 默认路径 = zod 剥离后的入参原样作为 wire params 发往 def.method(运行时工具走 Mode B);
/// 特例工具在分发前做参数整形/方法选择(asset_query、classdb_query、debug_inspect、
/// input_map_edit、user_data_read、spriteframes_create、scene_create_3d、tileset_edit、
/// project_delete、signal_emit、execute_code),与 Node 同名处理器逐字对齐
/// (含 INVALID_PARAMS 文案与 params 键集)。
/// 成功路径附 successHint(负载尚无 hint 时,Node injectSuccessHint 同规矩)。
/// </summary>
internal sealed class GroupToolInvoker(InstanceManager instances)
{
    /// <summary>组工具单次 wire 调用上限(与 ToolRouting.CallTimeout 一致)。</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>组工具统一入口:按工具名分发到特例处理器或默认调用路径。</summary>
    /// <para>逻辑链:11 个特例工具(signal_emit、execute_code、asset_query、classdb_query、
    /// debug_inspect、input_map_edit、user_data_read、spriteframes_create、scene_create_3d、
    /// tileset_edit、project_delete)→ 各自的参数整形/方法选择处理器;其余 → 默认路径
    /// (DeclaredParams 剥离未声明键后原样作为 wire params 走 def.Method)。
    /// def.Method 缺失时抛 InvalidOperationException(表数据损坏)。</para>
    /// <param name="def">工具表定义(名称/wire 方法/schema/通道标记)。</param>
    /// <param name="rawArgs">调用方入参(zod 剥离后的键值;null 视为空)。</param>
    /// <param name="cancellationToken">调用取消令牌。</param>
    /// <returns>映射后的 CallToolResult(不抛业务异常,失败以 isError 表达)。</returns>
    public Task<CallToolResult> InvokeAsync(
        NodeToolDef def, IDictionary<string, JsonElement>? rawArgs, CancellationToken cancellationToken)
    {
        var args = rawArgs ?? new Dictionary<string, JsonElement>();
        return def.Name switch
        {
            "signal_emit" => SignalEmitAsync(def, args, cancellationToken),
            "execute_code" => ExecuteCodeAsync(def, args, cancellationToken),
            "asset_query" => AssetQueryAsync(def, args, cancellationToken),
            "classdb_query" => ClassdbQueryAsync(def, args, cancellationToken),
            "debug_inspect" => DebugInspectAsync(def, args, cancellationToken),
            "input_map_edit" => InputMapEditAsync(def, args, cancellationToken),
            "user_data_read" => UserDataReadAsync(def, args, cancellationToken),
            "spriteframes_create" => SpriteframesCreateAsync(def, args, cancellationToken),
            "scene_create_3d" => SceneCreate3dAsync(def, args, cancellationToken),
            "tileset_edit" => TilesetEditAsync(def, args, cancellationToken),
            "project_delete" => ProjectDeleteAsync(def, args, cancellationToken),
            _ => CallAsync(def, IsRuntime(def), args,
                def.Method ?? throw new InvalidOperationException($"组工具缺少 wire method:{def.Name}"),
                DeclaredParams(def, args), cancellationToken),
        };
    }

    // ── 特例处理器(Node 同名函式逐一对照)─────────────────────

    /// <summary>signal_emit 按 channel 路由(编辑器/runtime 别名 game);args 缺省 []。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(channel/node_path/signal_name/args)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> SignalEmitAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var channel = StringArg(args, "channel") == "game" ? "runtime" : StringArg(args, "channel") ?? "editor";
        var parameters = Pick(args, "node_path", "signal_name");
        parameters["args"] = NodeArg(args, "args") ?? new JsonArray();
        return CallAsync(def, channel == "runtime", args, "signal.emit", parameters, cancellationToken);
    }

    /// <summary>execute_code:别名 expression → code;channel 缺省 runtime、game → runtime。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(code/expression/scope_path/channel)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> ExecuteCodeAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var channel = StringArg(args, "channel");
        var effectiveChannel = channel is null or "game" ? "runtime" : channel;
        var parameters = Pick(args, "scope_path", "channel");
        parameters["code"] = NodeArg(args, "code") ?? NodeArg(args, "expression");
        return CallAsync(def, effectiveChannel != "editor", args, "execute.code", parameters, cancellationToken);
    }

    /// <summary>asset_query 按 mode 分流:dependencies 查依赖(必填 file_path),
    /// uid_to_path/path_to_uid 做 uid 互转(必填 uid/file_path),否则列资产。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(mode/file_path/uid/path_prefix/name_glob 等)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>wire 结果;dependencies/uid 互转缺必填键时回 INVALID_PARAMS。</returns>
    private Task<CallToolResult> AssetQueryAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        switch (StringArg(args, "mode"))
        {
            case "dependencies":
                if (!HasNonEmptyString(args, "file_path"))
                {
                    return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "asset_query dependencies mode requires file_path"));
                }
                return CallAsync(def, false, args, "asset.get_dependencies",
                    Pick(args, "file_path", "include_transitive", "limit"), cancellationToken);
            case "uid_to_path":
                if (!HasNonEmptyString(args, "uid"))
                {
                    return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "asset_query uid_to_path mode requires uid"));
                }
                return CallAsync(def, false, args, "asset.resolve_uid",
                    Pick(args, "uid"), cancellationToken);
            case "path_to_uid":
                if (!HasNonEmptyString(args, "file_path"))
                {
                    return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "asset_query path_to_uid mode requires file_path"));
                }
                return CallAsync(def, false, args, "asset.uid_of",
                    Pick(args, "file_path"), cancellationToken);
            case "dependents":
                if (!HasNonEmptyString(args, "file_path"))
                {
                    return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "asset_query dependents mode requires file_path"));
                }
                return CallAsync(def, false, args, "asset.get_dependents",
                    Pick(args, "file_path", "include_transitive", "limit", "refresh"), cancellationToken);
            default:
                return CallAsync(def, false, args, "asset.list",
                    Pick(args, "path_prefix", "name_glob", "class_filter", "extension_filter", "limit"), cancellationToken);
        }
    }

    /// <summary>classdb_query 按 mode 分流:info 查单类详情(必填 class_name),否则按模式搜索。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(mode/class_name/base_class/pattern 等)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>wire 结果;info 缺 class_name 时回 INVALID_PARAMS。</returns>
    private Task<CallToolResult> ClassdbQueryAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        if (StringArg(args, "mode") == "info")
        {
            if (!HasNonEmptyString(args, "class_name"))
            {
                return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "classdb_query info mode requires class_name"));
            }
            return CallAsync(def, false, args, "classdb.get_info",
                Pick(args, "class_name", "include_inherited", "sections", "offset", "limit"), cancellationToken);
        }
        return CallAsync(def, false, args, "classdb.search",
            Pick(args, "base_class", "pattern", "instantiable_only", "include_global", "offset", "limit"), cancellationToken);
    }

    /// <summary>debug_inspect:mode 选择 wire 方法,params 恒为空对象。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(mode=breakpoints 时列断点,否则查状态)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> DebugInspectAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var method = StringArg(args, "mode") == "breakpoints" ? "debug.list_breakpoints" : "debug.state";
        return CallAsync(def, false, args, method, new JsonObject(), cancellationToken);
    }

    /// <summary>input_map_edit 按 action 分流:bind/unbind 走 event 键,其余归一为 add/remove 动作。</summary>
    /// <para>逻辑链:action 为 bind/unbind → 缺 event 即回 INVALID_PARAMS,走 input_map.event;
    /// 其余 action → 映射为 remove(仅 remove_action)或 add,插入 action 键后走 input_map.action。</para>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(action/name/event/deadzone 等)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>wire 结果;bind/unbind 缺 event 时回 INVALID_PARAMS。</returns>
    private Task<CallToolResult> InputMapEditAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var action = StringArg(args, "action");
        if (action is "bind" or "unbind")
        {
            if (!args.ContainsKey("event"))
            {
                return Task.FromResult(ToolResults.Error("INVALID_PARAMS", $"input_map_edit {action} requires event"));
            }
            return CallAsync(def, false, args, "input_map.event",
                Pick(args, "action", "name", "event"), cancellationToken);
        }
        var mapped = action == "remove_action" ? "remove" : "add";
        var parameters = Pick(args, "name", "deadzone");
        parameters.Insert(0, "action", JsonValue.Create(mapped));
        return CallAsync(def, false, args, "input_map.action", parameters, cancellationToken);
    }

    /// <summary>user_data_read:auto 模式按尾部 / 判目录;目录 → save.list,文件 → save.read。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(path/mode/offset/max_bytes)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> UserDataReadAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var path = StringArg(args, "path") ?? "";
        var mode = StringArg(args, "mode");
        var directory = mode == "directory" || (mode != "file" && path.EndsWith('/'));
        if (directory)
        {
            return CallAsync(def, false, args, "save.list",
                new JsonObject { ["path"] = path }, cancellationToken);
        }
        return CallAsync(def, false, args, "save.read",
            Pick(args, "path", "offset", "max_bytes"), cancellationToken);
    }

    /// <summary>spriteframes_create 按 source 分流:spritesheet 从精灵表导入,否则按动画定义创建。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(source/file_path/texture_path/frame_size/animations)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> SpriteframesCreateAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        if (StringArg(args, "source") == "spritesheet")
        {
            return CallAsync(def, false, args, "spriteframes.from_spritesheet",
                Pick(args, "file_path", "texture_path", "frame_size", "animations"), cancellationToken);
        }
        return CallAsync(def, false, args, "spriteframes.create",
            Pick(args, "file_path", "animations"), cancellationToken);
    }

    /// <summary>scene_create_3d:kind 选择方法;mesh_size/orthogonal_size 线上键名为 size。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(kind/primitive/light_type/位置变换等)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>wire 结果;kind 缺失或 primitive/light 缺必填键时回 INVALID_PARAMS。</returns>
    private Task<CallToolResult> SceneCreate3dAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        switch (StringArg(args, "kind"))
        {
            case "primitive":
                if (!args.ContainsKey("primitive"))
                {
                    return Task.FromResult(ToolResults.Error(
                        "INVALID_PARAMS", "scene_create_3d primitive kind requires primitive"));
                }
                return CallAsync(def, false, args, "3d.create_primitive",
                    Rename(Pick(args, "parent_path", "name", "position", "rotation", "primitive", "mesh_size", "material"),
                        "mesh_size", "size"), cancellationToken);
            case "environment":
                return CallAsync(def, false, args, "3d.setup_environment",
                    Pick(args, "parent_path", "name", "sky", "ambient_light", "tonemap", "fog"), cancellationToken);
            case "light":
                if (!args.ContainsKey("light_type"))
                {
                    return Task.FromResult(ToolResults.Error(
                        "INVALID_PARAMS", "scene_create_3d light kind requires light_type"));
                }
                return CallAsync(def, false, args, "3d.create_light",
                    Pick(args, "parent_path", "name", "position", "rotation", "light_type", "color", "energy", "shadow"),
                    cancellationToken);
            case "camera":
                return CallAsync(def, false, args, "3d.create_camera",
                    Rename(Pick(args, "parent_path", "name", "position", "rotation", "projection", "fov",
                        "orthogonal_size", "current"), "orthogonal_size", "size"), cancellationToken);
            default:
                return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "scene_create_3d requires kind"));
        }
    }

    /// <summary>tileset_edit:aspect 选择分面方法(physics/terrain/navigation/visuals/custom_data)。</summary>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(aspect/file_path/source_id/tiles)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>wire 结果;aspect 不合法时回 INVALID_PARAMS。</returns>
    private Task<CallToolResult> TilesetEditAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var method = StringArg(args, "aspect") switch
        {
            "physics" => "tileset.edit_physics",
            "terrain" => "tileset.edit_terrain",
            "navigation" => "tileset.edit_navigation",
            "visuals" => "tileset.edit_visuals",
            "custom_data" => "tileset.edit_custom_data",
            _ => null,
        };
        if (method is null)
        {
            return Task.FromResult(ToolResults.Error("INVALID_PARAMS", "tileset_edit requires aspect"));
        }
        return CallAsync(def, false, args, method,
            Pick(args, "file_path", "source_id", "tiles"), cancellationToken);
    }

    /// <summary>project_delete:kind 推断(auto)+ dry_run 计划(Node file.ts 同形,不触 wire)。</summary>
    /// <para>逻辑链:kind 缺省或 auto → 按路径后缀推断;dry_run=true → 组装计划 JSON
    /// 直接返回(含 recursive 附注与安全性提示,不发起删除);否则按 kind 选 wire 方法
    /// (folder 走 path+recursive,其余走 file_path)执行真删除。</para>
    /// <param name="def">工具表定义。</param>
    /// <param name="args">入参(path/kind/recursive/dry_run)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>dry_run 计划 JSON 或删除结果。</returns>
    private Task<CallToolResult> ProjectDeleteAsync(
        NodeToolDef def, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var path = StringArg(args, "path") ?? "";
        var requestedKind = StringArg(args, "kind");
        var kind = string.IsNullOrEmpty(requestedKind) || requestedKind == "auto"
            ? InferDeleteKind(path)
            : requestedKind;
        var method = kind switch
        {
            "folder" => "folder.delete",
            "scene" => "scene.delete",
            "script" => "script.delete",
            "resource" => "resource.delete",
            _ => "file.delete",
        };

        if (args.TryGetValue("dry_run", out var dryRunEl) && dryRunEl.ValueKind == JsonValueKind.True)
        {
            var plan = new JsonObject
            {
                ["success"] = true,
                ["dry_run"] = true,
                ["path"] = path,
                ["kind"] = kind,
            };
            if (kind == "folder")
            {
                plan["recursive"] = args.TryGetValue("recursive", out var recursiveEl) && recursiveEl.ValueKind == JsonValueKind.True;
            }
            plan["note"] = "The actual call may still be refused by active-use and editor-version safety checks.";
            return Task.FromResult(ToolResults.Json(plan.ToJsonString()));
        }

        var parameters = kind == "folder"
            ? Pick(args, "path", "recursive")
            : BuildFileDeleteParams(args, path);
        return CallAsync(def, false, args, method, parameters, cancellationToken);
    }

    /// <summary>非 folder 删除的 wire params:file_path + 调用方显式 force
    /// (file/resource 删除的引用安全检查覆盖开关;folder/scene/script 的删除
    /// 不做引用检查,force 对它们是无效键,不透传)。</summary>
    /// <param name="args">调用方入参。</param>
    /// <param name="path">删除目标 res:// 路径。</param>
    /// <returns>{file_path, force?} 形状的 wire params。</returns>
    private static JsonObject BuildFileDeleteParams(IDictionary<string, JsonElement> args, string path)
    {
        var fileParams = new JsonObject { ["file_path"] = path };
        if (args.TryGetValue("force", out var forceEl) && forceEl.ValueKind == JsonValueKind.True)
        {
            fileParams["force"] = true;
        }
        return fileParams;
    }

    /// <summary>按路径形态推断删除对象类型(仅 kind=auto 时使用)。</summary>
    /// <para>逻辑链:尾部 "/" → folder;按扩展名(.tscn/.gd/.cs/.gdshader/.gdshaderinc/
    /// .tres/.res)归为 scene/script/resource → 其余含点名为 file,无点名为 folder。</para>
    /// <param name="path">待删除的 res:// 路径。</param>
    /// <returns>folder / scene / script / resource / file 之一。</returns>
    private static string InferDeleteKind(string path)
    {
        if (path.EndsWith('/'))
        {
            return "folder";
        }
        if (path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
        {
            return "scene";
        }
        if (path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gdshaderinc", StringComparison.OrdinalIgnoreCase))
        {
            return "script";
        }
        if (path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
        {
            return "resource";
        }
        var tail = path.Split('/')[^1];
        return tail.Contains('.') ? "file" : "folder";
    }

    // ── 调用原语(callAndWrap 同语义)────────────────────────────

    /// <summary>统一调用与包装:按通道发起 wire 调用,成功附 successHint,失败按通道补崩溃上下文。</summary>
    /// <para>逻辑链:从 args 抽 instance → runtime 为真走 CallRuntimeAsync,否则
    /// CallInstanceAsync(均 30 秒超时)→ toolkit 失败信封转 isError,成功且定义带
    /// SuccessHint 时注入 hint 后透传;InstanceCallException → runtime 错误经
    /// RuntimeErrors.WithCrashContextAsync 补崩溃上下文,编辑器错误走通用错误信封。</para>
    /// <param name="def">工具表定义(取 SuccessHint)。</param>
    /// <param name="runtime">true 走运行时通道,false 走编辑器通道。</param>
    /// <param name="args">原始入参(仅用于抽 instance 寻址)。</param>
    /// <param name="method">wire 方法名。</param>
    /// <param name="parameters">整形后的 wire params(不含 instance)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>映射后的 CallToolResult(不抛业务异常)。</returns>
    private async Task<CallToolResult> CallAsync(
        NodeToolDef def,
        bool runtime,
        IDictionary<string, JsonElement> args,
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var instance = InstanceOf(args);
        try
        {
            var paramsJson = parameters.ToJsonString();
            var result = runtime
                ? await instances.CallRuntimeAsync(instance, method, paramsJson, CallTimeout, cancellationToken)
                : await instances.CallInstanceAsync(instance, method, paramsJson, CallTimeout, cancellationToken);
            if (!ToolResults.IsFailure(result) && def.SuccessHint is not null)
            {
                return ToolResults.Json(InjectSuccessHint(result, def.SuccessHint));
            }
            return ToolResults.FromToolkitResult(result);
        }
        catch (InstanceCallException ex)
        {
            return runtime
                ? await RuntimeErrors.WithCrashContextAsync(instances, ex, instance)
                : ToolResults.FromException(ex);
        }
    }

    /// <summary>Node injectSuccessHint 同规:负载为对象且尚无 hint 时附加;否则原样透传。</summary>
    /// <param name="result">toolkit 成功结果 JSON。</param>
    /// <param name="hint">要注入的成功提示文本。</param>
    /// <returns>注入 hint 后的 JSON 文本,或原样文本(非对象/已有 hint)。</returns>
    private static string InjectSuccessHint(JsonElement result, string hint)
    {
        if (result.ValueKind == JsonValueKind.Object && !result.TryGetProperty("hint", out _))
        {
            var payload = JsonNode.Parse(result.GetRawText())!.AsObject();
            payload["hint"] = hint;
            return payload.ToJsonString();
        }
        return result.GetRawText();
    }

    /// <summary>channel 相关特例之外,表内 runtime 标记决定通道(animation_player_control 等)。</summary>
    /// <param name="def">工具表定义。</param>
    /// <returns>定义为运行时工具返回 true。</returns>
    private static bool IsRuntime(NodeToolDef def) => def.Runtime;

    // ── 参数工具 ─────────────────────────────────────────────────

    /// <summary>Node zod 剥离语义:仅保留 schema 声明的属性;instance 由 daemon 消化。</summary>
    /// <param name="def">工具表定义(取 schema 声明键集)。</param>
    /// <param name="args">调用方入参。</param>
    /// <returns>仅含声明键(不含 instance)的 wire params。</returns>
    private static JsonObject DeclaredParams(NodeToolDef def, IDictionary<string, JsonElement> args)
    {
        var declared = DeclaredProperties(def);
        var obj = new JsonObject();
        foreach (var (key, value) in args)
        {
            if (key != "instance" && declared.Contains(key))
            {
                obj[key] = JsonNode.Parse(value.GetRawText());
            }
        }
        return obj;
    }

    /// <summary>提取工具 schema 的 properties 声明键集(zod 剥离的判定依据)。</summary>
    /// <param name="def">工具表定义。</param>
    /// <returns>schema 声明的属性名集合(序数比较)。</returns>
    private static HashSet<string> DeclaredProperties(NodeToolDef def)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (def.InputSchema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                set.Add(property.Name);
            }
        }
        return set;
    }

    /// <summary>按显式键集构造 params(仅携带调用方提供的键;Node 显式键集同形)。</summary>
    /// <param name="args">调用方入参。</param>
    /// <param name="keys">允许透传的键集(按此顺序组装)。</param>
    /// <returns>仅含命中所给键的 params 对象。</returns>
    private static JsonObject Pick(IDictionary<string, JsonElement> args, params string[] keys)
    {
        var obj = new JsonObject();
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var value))
            {
                obj[key] = JsonNode.Parse(value.GetRawText());
            }
        }
        return obj;
    }

    /// <summary>线上键名重命名(scene_create_3d 的 mesh_size/orthogonal_size → size)。</summary>
    /// <param name="obj">待改键的 params 对象(原位修改)。</param>
    /// <param name="from">原键名。</param>
    /// <param name="to">新键名。</param>
    /// <returns>同一对象(便于链式书写)。</returns>
    private static JsonObject Rename(JsonObject obj, string from, string to)
    {
        if (obj.TryGetPropertyValue(from, out var value))
        {
            obj.Remove(from);
            obj[to] = value;
        }
        return obj;
    }

    /// <summary>提取 instance 寻址参数(由 daemon 消化,不透传 toolkit)。</summary>
    /// <param name="args">调用方入参。</param>
    /// <returns>instance 字符串;缺省为 null。</returns>
    private static string? InstanceOf(IDictionary<string, JsonElement> args) => StringArg(args, "instance");

    /// <summary>取字符串参数;缺失或非字符串返回 null。</summary>
    /// <param name="args">调用方入参。</param>
    /// <param name="key">参数键名。</param>
    /// <returns>字符串值或 null。</returns>
    private static string? StringArg(IDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>判定参数为非空字符串。</summary>
    /// <param name="args">调用方入参。</param>
    /// <param name="key">参数键名。</param>
    /// <returns>存在且为长度大于 0 的字符串返回 true。</returns>
    private static bool HasNonEmptyString(IDictionary<string, JsonElement> args, string key) =>
        StringArg(args, key) is { Length: > 0 };

    /// <summary>取任意 JSON 参数;缺失或显式 null 返回 null。</summary>
    /// <param name="args">调用方入参。</param>
    /// <param name="key">参数键名。</param>
    /// <returns>解析后的 JSON 节点或 null。</returns>
    private static JsonNode? NodeArg(IDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(value.GetRawText())
            : null;
}
