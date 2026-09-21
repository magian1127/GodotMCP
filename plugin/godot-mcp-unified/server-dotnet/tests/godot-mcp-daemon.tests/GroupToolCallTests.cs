using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Daemon.Tests;

/// <summary>
/// issue 13 验收:组工具全量调用路由(74 行覆盖 58 个去重工具)—— 激活全部 31 组后,每个工具对 fake 实例
/// 走正确的 wire 方法、携带精确的 params(特例工具的键集/键名与 Node 处理器逐一对照;
/// instance 仅用于寻址、绝不上线);runtime 通道工具经 Mode B 送达运行时替身
/// (via 标记区分编辑器/运行时通道);project_delete 的 dry_run 不触 wire。
/// </summary>
public class GroupToolCallTests
{
    /// <summary>全部调用行共用的目标实例路径(编辑器替身以它注册,断言阶段注入 instance 参数)。</summary>
    private const string ProjectPath = @"D:\proj\gru";

    /// <summary>一条调用期望行:MCP 工具 + 入参 → 应命中的 wire 方法、替身应见到的 params、通道标记。</summary>
    /// <param name="Tool">MCP 工具名。</param>
    /// <param name="ArgsJson">工具入参(JSON 对象文本;instance 由断言阶段统一注入)。</param>
    /// <param name="Method">期望命中的 wire 方法(经 Row 工厂缺省取 NodeToolTable 对照)。</param>
    /// <param name="ExpectedParamsJson">期望替身回显的 params(缺省与入参逐字一致)。</param>
    /// <param name="Via">期望回显的通道标记 editor/runtime;null 表示不断言该字段。</param>
    private sealed record CallRow(
        string Tool, string ArgsJson, string Method, string ExpectedParamsJson, string? Via = null);

    /// <summary>
    /// 调用期望总表:74 条行覆盖 58 个工具、31 组中的 28 组(lsp_code_analysis /
    /// lsp_code_navigation / editor_advanced 由 LspToolsTests 与 EagerToolsTests 负责调用验证)。
    /// 普通工具方法与 params 直通;改写类工具(双方法分支、键名归一如 orthogonal_size → size、
    /// 通道改路由)以显式期望值表达,行内注释标注各组特例。
    /// </summary>
    private static readonly CallRow[] Rows =
    [
        // ── runtime_advanced(仅运行时替身持有脚本 —— 路由错误将直接失败)──
        Row("animation_player_control", """{"node_path":"/root/Main/Player","operation":"play","animation_name":"run"}""", via: "runtime"),
        Row("runtime_time_control", """{"action":"step","frames":3}""", via: "runtime"),

        // ── signals ──
        Row("signal_list", """{"node_path":"/root/Main","include_connections":true}"""),
        Row("signal_manage", """{"action":"connect","node_path":"/root/Main","signal_name":"pressed","target_path":"/root/Main/Player","method_name":"_on_pressed"}"""),
        Row("signal_emit", """{"node_path":"/root/Main","signal_name":"ready","args":[1]}""", via: "editor"),
        Row("signal_emit", """{"node_path":"/root/Main","signal_name":"ready","channel":"runtime"}""",
            expectedParams: """{"node_path":"/root/Main","signal_name":"ready","args":[]}""", via: "runtime"),

        // ── animation_authoring ──
        Row("animation_keyframe", """{"action":"add","player_path":"/root/Main/Player","animation_name":"run","track_path":"position","time":0.5,"value":{"x":1,"y":2}}"""),
        Row("animation_get_keys", """{"player_path":"/root/Main/Player","animation_name":"run","track_path":"position"}"""),
        Row("animationtree_edit", """{"node_path":"/root/Main/Anim","action":"set_root","root_type":"AnimationNodeStateMachine"}"""),
        Row("animationtree_list", """{"node_path":"/root/Main/Anim"}"""),

        // ── input_map(特例:action 映射 + 双方法分支)──
        Row("input_map_edit", """{"action":"bind","name":"jump","event":{"type":"key","keycode":32}}""",
            "input_map.event", """{"action":"bind","name":"jump","event":{"type":"key","keycode":32}}"""),
        Row("input_map_edit", """{"action":"add_action","name":"dash","deadzone":0.75}""",
            "input_map.action", """{"action":"add","name":"dash","deadzone":0.75}"""),

        // ── resource_io ──
        Row("folder_create", """{"path":"res://generated"}"""),
        Row("resource_load", """{"file_path":"res://resources/font.tres"}"""),
        Row("resource_write", """{"file_path":"res://resources/mat.tres","properties":{"metallic":1.0},"type":"StandardMaterial3D"}"""),

        // ── asset_ops(特例:双方法分支)──
        Row("asset_query", """{"mode":"list","name_glob":"*.png","limit":5}""",
            "asset.list", """{"name_glob":"*.png","limit":5}"""),
        Row("asset_query", """{"mode":"dependencies","file_path":"res://scenes/Main.tscn","include_transitive":true}""",
            "asset.get_dependencies", """{"file_path":"res://scenes/Main.tscn","include_transitive":true}"""),
        Row("asset_query", """{"mode":"uid_to_path","uid":"uid://b1a2c3d4e5f6g7"}""",
            "asset.resolve_uid", """{"uid":"uid://b1a2c3d4e5f6g7"}"""),
        Row("asset_query", """{"mode":"path_to_uid","file_path":"res://scenes/Main.tscn"}""",
            "asset.uid_of", """{"file_path":"res://scenes/Main.tscn"}"""),
        Row("asset_query", """{"mode":"dependents","file_path":"res://assets/hero.png","include_transitive":true,"refresh":true}""",
            "asset.get_dependents", """{"file_path":"res://assets/hero.png","include_transitive":true,"refresh":true}"""),
        Row("asset_import", """{"source_path":"C:/tmp/icon.png","dest_path":"res://assets/icon.png","if_exists":"replace"}"""),

        // ── cleanup(特例:kind 推断 + dry_run 另行断言;file/resource 的 force 透传)──
        Row("project_delete", """{"path":"res://scripts/x.gd"}""", "script.delete", """{"file_path":"res://scripts/x.gd"}"""),
        Row("project_delete", """{"path":"res://resources/mat.tres","kind":"resource","force":true}""", "resource.delete", """{"file_path":"res://resources/mat.tres","force":true}"""),
        Row("project_delete", """{"path":"res://tmp","kind":"folder","recursive":true}""", "folder.delete", """{"path":"res://tmp","recursive":true}"""),
        Row("scene_close", """{"file_path":"res://Main.tscn"}"""),

        // ── user_data(特例:auto 目录判定)──
        Row("user_data_read", """{"path":"user://save1.dat"}""", "save.read", """{"path":"user://save1.dat"}"""),
        Row("user_data_read", """{"path":"user://saves","mode":"directory"}""", "save.list", """{"path":"user://saves"}"""),
        Row("save_write", """{"path":"user://save1.dat","content":"abc"}"""),
        Row("save_delete", """{"path":"user://save1.dat"}"""),

        // ── scene_advanced ──
        Row("scene_diff", """{"before":"res://a.tscn","after":"res://b.tscn"}"""),
        Row("scene_instantiate", """{"parent_path":"/root/Main","scene_path":"res://Enemy.tscn","as_name":"Enemy1"}"""),

        // ── tilemap ──
        Row("tilemap_read_cells", """{"node_path":"/root/Main/Tiles","region":{"x":0,"y":0,"width":2,"height":1},"layer":0}"""),
        Row("tilemap_set_cells", """{"node_path":"/root/Main/Tiles","layer":0,"cells":[{"x":1,"y":1,"source_id":0,"atlas_x":0,"atlas_y":0}]}"""),

        // ── tileset ──
        Row("tileset_create", """{"file_path":"res://tiles/new.tres","texture_path":"res://tiles/atlas.png","tile_size":{"x":16,"y":16}}"""),
        Row("tileset_add_source", """{"file_path":"res://tiles/new.tres","texture_path":"res://tiles/atlas2.png","tile_size":{"x":16,"y":16}}"""),
        Row("tileset_remove_source", """{"file_path":"res://tiles/new.tres","source_id":1}"""),
        Row("tileset_add_alternative", """{"file_path":"res://tiles/new.tres","source_id":0,"atlas_x":0,"atlas_y":0,"flip_h":true}"""),
        Row("tileset_remove_alternative", """{"file_path":"res://tiles/new.tres","source_id":0,"atlas_x":0,"atlas_y":0,"alternative_id":1}"""),
        Row("tileset_setup_layers", """{"file_path":"res://tiles/new.tres","physics_layers":[{"name":"ground","collision_layer":1}]}"""),

        // ── tileset_edit(特例:aspect 选择分面方法)──
        Row("tileset_edit", """{"aspect":"physics","file_path":"res://tiles/new.tres","tiles":[{"source_id":0,"atlas_x":0,"atlas_y":0}]}""",
            "tileset.edit_physics", """{"file_path":"res://tiles/new.tres","tiles":[{"source_id":0,"atlas_x":0,"atlas_y":0}]}"""),

        // ── theme ──
        Row("theme_edit", """{"file_path":"res://ui/theme.tres","edits":[{"type":"color","name":"font_color","value":"#ffffff"}]}"""),

        // ── layer_naming ──
        Row("layer_names_set", """{"category":"2d_physics","layers":{"1":"terrain"}}"""),
        Row("layer_names_get", """{"category":"3d_render"}"""),

        // ── path_editing ──
        Row("path2d_edit_curve", """{"node_path":"/root/Main/Path2D","action":"set","points":[{"x":0,"y":0},{"x":10,"y":5}]}"""),
        Row("collision_from_texture", """{"sprite_path":"res://assets/ship.png","parent_path":"/root/Main"}"""),

        // ── 3d_tools(特例:kind 分支 + mesh_size/orthogonal_size → size)──
        Row("scene_create_3d", """{"kind":"primitive","parent_path":"/root/Main","primitive":"box","mesh_size":{"x":1,"y":2,"z":1}}""",
            "3d.create_primitive", """{"parent_path":"/root/Main","primitive":"box","size":{"x":1,"y":2,"z":1}}"""),
        Row("scene_create_3d", """{"kind":"light","parent_path":"/root/Main","light_type":"omni","energy":2.0}""",
            "3d.create_light", """{"parent_path":"/root/Main","light_type":"omni","energy":2.0}"""),
        Row("scene_create_3d", """{"kind":"camera","parent_path":"/root/Main","projection":"perspective","fov":70.0,"orthogonal_size":5.0}""",
            "3d.create_camera", """{"parent_path":"/root/Main","projection":"perspective","fov":70.0,"size":5.0}"""),
        Row("scene_create_3d", """{"kind":"environment","parent_path":"/root/Main","tonemap":"aces"}""",
            "3d.setup_environment", """{"parent_path":"/root/Main","tonemap":"aces"}"""),

        // ── procedural ──
        Row("procedural_edit_gradient", """{"file_path":"res://grad.tres","action":"set","points":[{"offset":0,"color":"#000000"}]}"""),
        Row("procedural_edit_curve", """{"file_path":"res://curve.tres","action":"set","points":[{"offset":0,"value":1}]}"""),
        Row("procedural_edit_noise", """{"file_path":"res://noise.tres","noise_type":"simplex","frequency":0.05}"""),

        // ── scene_inheritance ──
        Row("scene_create_inherited", """{"file_path":"res://enemy_variant.tscn","base_scene":"res://Enemy.tscn","root_name":"EnemyVariant"}"""),

        // ── audio ──
        Row("audiobus_edit", """{"action":"set_bus","bus_name":"Master","volume_db":-3.0}"""),
        Row("audiobus_list", """{}"""),

        // ── spriteframes(特例:frames / spritesheet 双方法)──
        Row("spriteframes_create", """{"file_path":"res://anim.tres","source":"frames","animations":[{"name":"idle","frames":[{"texture_path":"res://a.png"}]}]}""",
            "spriteframes.create", """{"file_path":"res://anim.tres","animations":[{"name":"idle","frames":[{"texture_path":"res://a.png"}]}]}"""),
        Row("spriteframes_create", """{"file_path":"res://anim2.tres","source":"spritesheet","texture_path":"res://sheet.png","frame_size":{"x":32,"y":32},"animations":[{"name":"run","frame_count":4}]}""",
            "spriteframes.from_spritesheet", """{"file_path":"res://anim2.tres","texture_path":"res://sheet.png","frame_size":{"x":32,"y":32},"animations":[{"name":"run","frame_count":4}]}"""),
        Row("spriteframes_edit", """{"file_path":"res://anim.tres","action":"set_fps","animation_name":"idle","fps":10.0}"""),

        // ── particles ──
        Row("particles_create", """{"parent_path":"/root/Main","type":"3d","preset":"fire","amount":32}"""),

        // ── navigation ──
        Row("navigation_edit", """{"node_path":"/root/Main/NavRegion","action":"set","outlines":[{"points":[[0,0],[10,0],[10,10]]}]}"""),

        // ── debugger(特例:mode 选择方法 + 空 params)──
        Row("debug_inspect", """{"mode":"state"}""", "debug.state", """{}"""),
        Row("debug_inspect", """{"mode":"breakpoints"}""", "debug.list_breakpoints", """{}"""),
        Row("debug_set_breakpoint", """{"file_path":"res://scripts/x.gd","line":10,"enabled":true}"""),
        Row("debug_continue", """{}"""),

        // ── classdb(特例:双方法分支)──
        Row("classdb_query", """{"mode":"search","pattern":"Node","limit":10}""",
            "classdb.search", """{"pattern":"Node","limit":10}"""),
        Row("classdb_query", """{"mode":"info","class_name":"Node2D","include_inherited":true}""",
            "classdb.get_info", """{"class_name":"Node2D","include_inherited":true}"""),

        // ── project_config ──
        Row("project_set_setting", """{"setting":"application/run/main_scene","value":"res://Main.tscn"}"""),
        Row("autoload_manage", """{"action":"register","name":"GameState","script_path":"res://autoload/game_state.gd"}"""),

        // ── node_advanced ──
        Row("node_groups", """{"action":"add","node_path":"/root/Main","group":"enemies"}"""),
        Row("control_set_layout", """{"node_path":"/root/Main/Panel","preset":"full_rect","resize_mode":"set_to_anchors"}"""),

        // ── scene_spatial ──
        Row("scene_spatial_map", """{"detail":"brief","max_nodes":50}"""),

        // ── unsafe(特例:execute_code 通道;node_call_method 默认编辑器)──
        Row("execute_code", """{"code":"1+1","channel":"editor"}""",
            "execute.code", """{"code":"1+1","channel":"editor"}""", via: "editor"),
        Row("execute_code", """{"code":"get_node('/root/Main').name"}""",
            "execute.code", """{"code":"get_node('/root/Main').name"}""", via: "runtime"),
        Row("node_call_method", """{"node_path":"/root/Main","method_name":"queue_free"}"""),

        // 注:lsp_code_analysis / lsp_code_navigation / editor_advanced 三组由
        // LspToolsTests(11 号)与 EagerToolsTests(09 号)提供调用验证记录;
        // 本测试覆盖其余 28 组、共 74 条调用行(58 个去重工具,含同工具双通道/双方法行)。
    ];

    /// <summary>期望行工厂:wire 方法缺省回落到 NodeToolTable 对照,期望 params 缺省回落到入参原文。</summary>
    /// <param name="tool">MCP 工具名。</param>
    /// <param name="args">工具入参 JSON 文本。</param>
    /// <param name="method">显式期望的 wire 方法(仅改写路由的特例工具传)。</param>
    /// <param name="expectedParams">显式期望的 params(仅改写参数的特例工具传)。</param>
    /// <param name="via">期望通道标记(仅编辑器/运行时改路由的工具传)。</param>
    /// <returns>填好回落值的调用期望行。</returns>
    private static CallRow Row(string tool, string args, string? method = null, string? expectedParams = null, string? via = null)
        => new(tool, args, method ?? WireMethod(tool), expectedParams ?? args, via);

    /// <summary>默认工具的行直接取表内 wire 方法(杜绝手抄方法名漂移)。</summary>
    private static string WireMethod(string tool) =>
        NodeToolTable.AllEntries.First(t => t.Name == tool).Method!;

    /// <summary>
    /// 全量组工具调用路由:激活全部 31 组后,对编辑器 + 运行时双替身逐行核对总表 74 条调用的
    /// wire 方法、精确 params 与通道,另验证 project_delete 的 dry_run 只回计划、不触 wire。
    /// <para>断言链:UnsafeEnabled 拉起 daemon 并经 SessionConnector 连入 → 编辑器替身持全部
    /// 编辑器方法脚本,运行时替身(Mode B、共享 token、裸 ack)仅持 runtime 方法;登记运行时
    /// 端口后断言其 AuthedPeerCount 达 1(通道建立)→ discover_tools 请求全部组名,断言恰
    /// 返回 31 组且全部 activated → 总表逐行经 AssertRowAsync 断言非错误、params_seen 深相等、
    /// method 一致、行内声明 via 时通道一致 → 最后发 project_delete(dry_run=true),断言返回
    /// dry_run/path/kind/recursive 计划字段(替身无对应脚本,一旦触 wire 即失败)。</para>
    /// </summary>
    [Fact]
    public async Task all_group_tools_route_to_fake_instances()
    {
        var stateDir = TestPaths.NewStateDir();
        using var daemon = DaemonProcess.StartReady(new DaemonSpawnOptions
        {
            StateDir = stateDir,
            IdleSeconds = 180,
            UnsafeEnabled = true,
        });
        await using var client = await SessionConnector.ConnectAsync(daemon.Port, daemon.Token);

        using var editor = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ProjectPath,
            StateDir = stateDir,
            Scripts = BuildScripts(EditorMethods, "editor"),
        });
        // 运行时替身(Mode B,裸 ack):仅持有 runtime 路由方法 —— 编辑器误收将暴露为失败。
        using var runtime = FakeGodotInstance.Start(new FakeGodotOptions
        {
            ProjectPath = ProjectPath,
            StateDir = null,
            Token = editor.Token,
            AuthAckJson = """{"authed":true}""",
            Scripts = BuildScripts(RuntimeMethods, "runtime"),
        });
        await WaitConnectedAsync(client, ProjectPath);
        FakeGodotRegistry.UpdateRuntimeFields(stateDir, ProjectPath, runtime.Port, Environment.ProcessId);
        var runtimeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (runtime.AuthedPeerCount == 0 && DateTime.UtcNow < runtimeDeadline)
        {
            await Task.Delay(100);
        }
        Assert.Equal(1, runtime.AuthedPeerCount);

        // 激活全部 31 组(unsafe 经 GODOT_MCP_UNSAFE=1 启用)。
        var discover = await client.CallToolAsync("discover_tools", new Dictionary<string, object?>
        {
            ["request"] = GroupCatalogue.Groups.Select(g => g.Name).ToList(),
        });
        Assert.True(discover.IsError is null or false, TextOf(discover));
        using (var payload = JsonDocument.Parse(TextOf(discover)))
        {
            var groups = payload.RootElement.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(31, groups.Count);
            Assert.All(groups, g => Assert.Equal("activated", g.GetProperty("status").GetString()));
        }

        // 74 条调用行逐一:正确方法 + 精确 params + 正确通道。
        foreach (var row in Rows)
        {
            await AssertRowAsync(client, row);
        }

        // project_delete dry_run:只回计划,不触 wire(替身无 project.delete 脚本)。
        var dryRun = await client.CallToolAsync("project_delete", new Dictionary<string, object?>
        {
            ["path"] = "res://tmp_cleanup_dir",
            ["kind"] = "folder",
            ["recursive"] = true,
            ["dry_run"] = true,
            ["instance"] = ProjectPath,
        });
        Assert.True(dryRun.IsError is null or false, TextOf(dryRun));
        using (var payload = JsonDocument.Parse(TextOf(dryRun)))
        {
            Assert.True(payload.RootElement.GetProperty("dry_run").GetBoolean());
            Assert.Equal("res://tmp_cleanup_dir", payload.RootElement.GetProperty("path").GetString());
            Assert.Equal("folder", payload.RootElement.GetProperty("kind").GetString());
            Assert.True(payload.RootElement.GetProperty("recursive").GetBoolean());
        }
    }

    // ── 替身脚本 ─────────────────────────────────────────────────

    /// <summary>编辑器替身持有的全部方法(每方法一个可复用回显脚本)。</summary>
    private static readonly string[] EditorMethods =
    [
        "signal.list", "signal.manage", "signal.emit",
        "animation.keyframe", "animation.get_keys", "animationtree.edit", "animationtree.list",
        "input_map.event", "input_map.action",
        "folder.create", "resource.load", "resource.write",
        "asset.list", "asset.get_dependencies", "asset.resolve_uid", "asset.uid_of", "asset.get_dependents", "asset.import",
        "script.delete", "folder.delete", "scene.close", "resource.delete", "file.delete",
        "save.read", "save.list", "save.write", "save.delete",
        "scene.diff", "scene.instantiate",
        "tilemap.read_cells", "tilemap.set_cells",
        "tileset.create", "tileset.add_source", "tileset.remove_source",
        "tileset.add_alternative", "tileset.remove_alternative", "tileset.setup_layers",
        "tileset.edit_physics",
        "theme.edit",
        "project.set_layer_names", "project.get_layer_names",
        "path2d.edit_curve", "node.collision_from_sprite",
        "3d.create_primitive", "3d.create_light", "3d.create_camera", "3d.setup_environment",
        "procedural.edit_gradient", "procedural.edit_curve", "procedural.edit_noise",
        "scene.create_inherited",
        "audiobus.edit", "audiobus.list",
        "spriteframes.create", "spriteframes.from_spritesheet", "spriteframes.edit",
        "particles.create", "navigation.edit_polygon",
        "debug.state", "debug.list_breakpoints", "debug.set_breakpoint", "debug.continue",
        "classdb.search", "classdb.get_info",
        "project.set_setting", "autoload.manage",
        "node.groups", "control.set_layout", "scene.spatial_map",
        "execute.code", "node.call_method",
    ];

    /// <summary>runtime 替身:仅 runtime 路由方法(编辑器/fake 各自 via 标记区分)。</summary>
    private static readonly string[] RuntimeMethods =
    [
        "animation_player.control", "runtime.time_control", "signal.emit", "execute.code",
    ];

    /// <summary>为每个方法生成一条可复用回显脚本(单帧,回显里带 via 与 method 标记)。</summary>
    /// <param name="methods">wire 方法名集合。</param>
    /// <param name="via">写进回显帧的通道标记(editor/runtime)。</param>
    /// <returns>替身脚本列表。</returns>
    private static List<FakeScript> BuildScripts(IEnumerable<string> methods, string via)
    {
        var scripts = new List<FakeScript>();
        foreach (var method in methods)
        {
            scripts.Add(new FakeScript
            {
                Method = method,
                Reusable = true,
                Frames = { new FakeFrame { Json = EchoFrame(via, method) } },
            });
        }
        return scripts;
    }

    /// <summary>构造带通道与方法标记的回显帧:params_seen 占位符原样带回请求 params。</summary>
    /// <param name="via">通道标记。</param>
    /// <param name="method">wire 方法名。</param>
    /// <returns>可填入 FakeFrame.Json 的原始 JSON。</returns>
    private static string EchoFrame(string via, string method) =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"$request_id\",\"result\":{\"success\":true,\"via\":\"" + via +
        "\",\"method\":\"" + method + "\",\"params_seen\":\"$params\"}}";

    // ── 断言 ─────────────────────────────────────────────────────

    /// <summary>
    /// 执行一条调用行并逐点断言:入参注入 instance 后调工具 → 结果非错误 → params_seen 与
    /// 期望 JSON 深相等(JsonNode.DeepEquals,不依赖键序)→ 回显 method 与期望一致 →
    /// 行内声明了 Via 时再比对通道标记。
    /// </summary>
    /// <param name="client">已连入 daemon 的 MCP 客户端。</param>
    /// <param name="row">调用期望行。</param>
    private static async Task AssertRowAsync(McpClient client, CallRow row)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.ArgsJson)!;
        args["instance"] = ProjectPath;
        var result = await client.CallToolAsync(row.Tool, args);
        Assert.True(result.IsError is null or false, $"{row.Tool}: {TextOf(result)}");
        using var payload = JsonDocument.Parse(TextOf(result));
        var seen = payload.RootElement.GetProperty("params_seen");
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(seen.GetRawText()), JsonNode.Parse(row.ExpectedParamsJson)),
            $"{row.Tool} 的 params 不一致:\n seen={seen.GetRawText()}\n want={row.ExpectedParamsJson}");
        Assert.Equal(row.Method, payload.RootElement.GetProperty("method").GetString());
        if (row.Via is not null)
        {
            Assert.Equal(row.Via, payload.RootElement.GetProperty("via").GetString());
        }
    }

    /// <summary>取工具结果的唯一文本内容块(内容块非单、非文本即断言失败)。</summary>
    /// <param name="result">MCP 工具调用结果。</param>
    /// <returns>唯一文本块的内容。</returns>
    private static string TextOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    /// <summary>轮询 list_instances 直至指定项目路径的实例 connected 为 true(20s 超时抛 TimeoutException)。</summary>
    /// <param name="client">MCP 客户端。</param>
    /// <param name="projectPath">项目路径(按 canonical 键比对)。</param>
    private static async Task WaitConnectedAsync(McpClient client, string projectPath)
    {
        var canonical = JsonMatch.CanonicalProjectKey(projectPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await client.CallToolAsync("list_instances", new Dictionary<string, object?>());
            using var payload = JsonDocument.Parse(TextOf(result));
            var connected = payload.RootElement.GetProperty("instances").EnumerateArray()
                .Any(i => i.GetProperty("path").GetString() == canonical && i.GetProperty("connected").GetBoolean());
            if (connected)
            {
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"实例 {canonical} 未在 20s 内连接");
    }
}
