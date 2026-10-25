// godot-mcp-daemon 进程入口(顶级语句)。完整启动链(全部失败出口按 ExitCodes 归因):
// 1. 参数解析:DaemonOptions.FromEnvironment 读三个环境变量,非法值即启动期环境故障;
// 2. 单例锁获取:DaemonState.Acquire 打开机器级锁文件,顺带确保稳定 token(DaemonToken.EnsureStable)——
//    锁被另一实例明确持有 → 退出码 2;锁状态不明确(Unix)→ 3;
// 3. 主机构建:UseUrls 绑定 127.0.0.1:Port;日志全部落 stderr + daemon.log(stdout 保留给 MCP 协议通道);
// 3b. 作业对象自检(只读):daemon 若诞生在宿主"关闭即杀"的 Job 中,宿主退出会把它连带杀掉 ——
//    命中即告警(JobMembership),不影响启动;
// 4. MCP server 注册:Stateless HTTP;ServerInfo 维持 godot-mcp-unified;每请求会话经 GroupService
//    按组激活状态 + NodeToolTable schema + 版本门控裁剪工具面;subscriptions/listen 长流由
//    GroupListenStream 自持,承载 tools/list_changed;
// 5. DI 注册:options、IdleMonitor、IdleExitService(空闲自退)、InstanceManager(宿主服务)、
//    GroupService、ExtensionService;
// 6. 组合接线:InstanceManager.ExtraMethodGates ← ExtensionService.GateForMethod;
//    ConnectionNotification 中的 extensions.changed → ExtensionService.ApplyChanged →
//    GroupService.NotifyToolSurfaceChanged 广播 tools/list_changed;
// 7. 中间件管道:RequireDaemonTokenMiddleware(Bearer token 门禁,401 不计空闲)→
//    空闲计量中间件(Enter/Exit 包裹下游)→ MapMcp 端点;
// 8. 运行与退出:Run 抛 IOException 即监听绑定失败(端口被占)→ 退出码 4;正常关机 → 0。

using GodotMcp.Daemon;
using GodotMcp.Daemon.Extensions;
using GodotMcp.Daemon.Groups;
using GodotMcp.Daemon.Instances;
using GodotMcp.Daemon.Sessions;
using GodotMcp.Daemon.Tools;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

try
{
    var options = DaemonOptions.FromEnvironment();

    // --status：只读查询，**不取单例锁**，因此可以在 daemon 正在运行时执行
    // （这正是它的用途：连不上时先看现状）。spec §10。
    if (args.Any(a => string.Equals(a, "--status", StringComparison.OrdinalIgnoreCase)))
    {
        return DaemonStatus.Print(options);
    }

    using var machineSingleton = MachineSingleton.Acquire();
    using var state = DaemonState.Acquire(options);

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
    // stdout 保留给协议通道(spec user story 24) —— 全部日志只落 stderr 与 daemon.log。
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(new StateLogFileLoggerProvider(
        Path.Combine(options.StateDir, StateLogFileLoggerProvider.LogFileName)));

    builder.Services.AddMcpServer(o =>
        {
            // 对 host 无感迁移:ServerInfo name 维持 godot-mcp-unified(spec 命名决策)。
            o.ServerInfo = new Implementation
            {
                Name = "godot-mcp-unified",
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            };
            // 三句说明:模型在 tools/list 之前就会读到它,因此必须讲清"为什么只看到一个工具"。
            // 不长篇罗列工具 —— 那正是本次改造要削减的上下文成本。
            o.ServerInstructions =
                "这个服务器操作 Godot 编辑器与运行中的游戏。**未连接时只会看到一个工具 godot_connect**，" +
                "这是设计如此:请先调用它(可带 project 绝对路径、instance 短 id 或 groups 数组),工具面才会展开为" +
                "9 个核心工具 + 所选工具组。之后可用 discover_tools 按名称或关键词激活更多组(共 17 组);" +
                "激活只影响当前会话。";
            // tools.listChanged 必须广播:无状态 HTTP 下 host 只能经 subscriptions/listen
            // 长请求流接收 tools/list_changed(2026-07-28 修订);能力旗标与实际投递一致(issue 12)。
            o.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
            };
            // 未命中兜底:被隐藏的工具(未激活组 / 从未创建的懒建工具 / 会话未就绪)由这里
            // 返回带原因的 isError,而不是让 SDK 回 JSON-RPC "Unknown tool"(ADR-0007)。
            o.Handlers.CallToolHandler = static (request, cancellationToken) =>
            {
                var fallback = request.Services!.GetRequiredService<ToolCallFallback>();
                return fallback.HandleAsync(request, cancellationToken);
            };
            // tools/list 缓存语义:ttlMs=0 + cacheScope=private。
            // 缺这两项 SDK 只 warn 不 fail,而客户端会按默认值缓存 —— 工具面"该变不变"。
            // 注意:过滤器在 Options.Filters.Request 下,不在 Handlers 下。
            o.Filters.Request.ListToolsFilters.Add(ToolListMetadata.Create());
        })
        .WithHttpTransport(o =>
        {
            o.SessionMode = HttpServerSessionMode.Stateless;
            // 每个请求:把工具面裁剪到"本会话键可见的集合"(ADR-0007)。
            // 未连接(无 header / 键不存在)→ 只有 godot_connect;已连接 → core 9 ∪ 本键组。
            // 调试面(GODOT_MCP_DEBUG_UNKEYED=1 且无 header)→ 全量。
            o.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
            {
                if (serverOptions.ToolCollection is { } collection)
                {
                    var services = httpContext.RequestServices;
                    services.GetRequiredService<ToolFaceComposer>().Compose(httpContext, collection);
                }
                return Task.CompletedTask;
            };
        })
        .WithToolsFromAssembly()
        // subscriptions/listen 长流:无状态 HTTP 下 host 接收 tools/list_changed 的唯一通道
        // (SDK 无状态内建处理器按设计不授予任何通知,SEE SEP-2575 / issue #1662)——
        // 由 daemon 自持该流:acknowledged + 标注订阅 id 的变更投递(issue 12)。
        .WithSubscriptionsListenHandler(async (request, cancellationToken) =>
        {
            var groups = request.Services!.GetRequiredService<GroupService>();
            // 本流的会话键:通知只投给同键的流(ADR-0006 §1.7)。
            // 取不到(atypical)时传 null → 退化为"接收全部",不会因此断流。
            var sessionKey = request.Services!.GetService<SessionKeyAccessor>()?.CurrentKey;
            return await GroupListenStream.ServeAsync(
                groups,
                request.Server,
                request.JsonRpcRequest.Id,
                request.Params?.Notifications,
                sessionKey,
                cancellationToken);
        });

    var idleMonitor = new IdleMonitor(options.IdleTimeout);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(idleMonitor);
    builder.Services.AddHostedService<IdleExitService>();
    // 会话键(ADR-0006):无状态 HTTP 下唯一持有"会话身份"的地方。
    // 策略是纯内存常量(会话过期三档 + 键数上限),生产不读环境变量;
    // TimeProvider 注入使状态机与 reaper 可以用假时钟测试,不必真等 30s/10min。
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(SessionKeyPolicy.FromEnvironment());
    builder.Services.AddSingleton<SessionKeyRegistry>();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<SessionKeyAccessor>();
    builder.Services.AddSingleton<SessionKeyDiagnostics>();
    // 进程身份:shim 靠它判断"我手里的会话状态还算数吗"(见 DaemonEpoch)。
    builder.Services.AddSingleton<DaemonEpoch>();
    builder.Services.AddHostedService<SessionKeyReaper>();

    // 会话快照落 state/sessions.json:--status 与用户都能直接读"现在有哪些会话"。
    builder.Services.AddHostedService<SessionSnapshotWriter>();
    builder.Services.AddSingleton<InstanceManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InstanceManager>());
    // 会话态载体(请求作用域):GroupService / ConnectTool / ToolCallFallback /
    // SessionTargetResolver 都从它读"当前是哪个会话",避免四处各读一套。
    builder.Services.AddSingleton<SessionRequestScope>();
    // 按本键默认目标寻址(ADR-0008)。必须先于 GroupService:它的构造依赖它。
    builder.Services.AddSingleton<SessionTargetResolver>();
    builder.Services.AddSingleton<GroupService>();
    builder.Services.AddSingleton<ToolFaceComposer>();
    builder.Services.AddSingleton<ToolCallFallback>();
    builder.Services.AddSingleton<ExtensionService>();

    var app = builder.Build();

    // 机器级常驻的前提:daemon 不能诞生在宿主"关闭即杀"的作业对象里。只读自检,命中即告警
    // (非 Windows / 探测失败一律静默)——把"服务莫名消失"的隐形风险变成可见告警。
    if (JobMembership.DescribeKillOnCloseRisk() is { } jobRisk)
    {
        app.Logger.LogWarning("作业对象风险:{Risk}", jobRisk);
    }

    // issue 14 组合接线:扩展工具的版本门控 + extensions.changed 通知消费
    // (Instances → Extensions 的依赖边留在组合根,保持两者互不引用)。
    var instanceManager = app.Services.GetRequiredService<InstanceManager>();
    var extensionService = app.Services.GetRequiredService<ExtensionService>();

    // ADR-0008 接线:让**所有**寻址入口都拿到本会话键的默认目标。
    // 用委托而不是让 InstanceManager 直接依赖 SessionTargetResolver —— 后者依赖前者,会成环。
    // 必须在 app.Build() 之后尽早接:漏接会让不带 instance 的调用继续报 AMBIGUOUS_INSTANCE,
    // 表现为"有的工具能用、有的不能"。
    var targetResolver = app.Services.GetRequiredService<SessionTargetResolver>();
    instanceManager.DefaultTargetProvider = targetResolver.DefaultTargetOfCurrentSession;

    // 键被回收 → 清掉该键的组桶。不接这条线:桶无界增长,而且同一 UUID 重连后
    // 一次"只浏览"的 discover_tools 会把旧组静默复活。
    var sessionKeys = app.Services.GetRequiredService<SessionKeyRegistry>();
    var groupService = app.Services.GetRequiredService<GroupService>();
    var sessionDiagnostics = app.Services.GetRequiredService<SessionKeyDiagnostics>();
    sessionKeys.KeyRecycled += groupService.ForgetKey;

    // 会话变化(建键 / 连接 / 组 / 默认目标)立刻刷新快照:
    // 等周期定时器会让排查的人看到旧值,而"现在到底连没连上"正是他要看的。
    var snapshotWriter = app.Services.GetServices<IHostedService>()
        .OfType<SessionSnapshotWriter>()
        .FirstOrDefault();
    if (snapshotWriter is not null)
    {
        sessionKeys.Changed += () => snapshotWriter.WriteOnce();
    }
    sessionKeys.KeyRecycled += key => sessionDiagnostics.KeyRecycled(
        key.Length <= 8 ? key : key[..8],
        "过期或容量淘汰");

    // 启动即打印生效策略:事后核对"当时用的是哪套 TTL"时,这一行是唯一依据。
    sessionDiagnostics.PolicyInEffect(app.Services.GetRequiredService<SessionKeyPolicy>());

    instanceManager.ExtraMethodGates = extensionService.GateForMethod;
    instanceManager.ConnectionNotification += (_, type, notificationParams) =>
    {
        if (type == "extensions.changed" && extensionService.ApplyChanged(notificationParams))
        {
            app.Services.GetRequiredService<GroupService>().NotifyToolSurfaceChanged();
        }
    };

    // 进程身份头必须写在**所有中间件之前**（审核 S3）：token 中间件会给 401 直接短路，
    // 若头写在它之后，shim 在"令牌不匹配"这条最需要检测的路径上反而看不到 epoch，
    // 只能等下一轮 —— 而那一轮可能又是 401。放在最前面，任何响应（含 401/400/503）都带。
    var daemonEpoch = app.Services.GetRequiredService<DaemonEpoch>();
    app.Use(async (context, next) =>
    {
        context.Response.Headers[DaemonEpoch.HeaderName] = daemonEpoch.Value;
        await next(context).ConfigureAwait(false);
    });

    app.UseMiddleware<RequireDaemonTokenMiddleware>(state.Token);
    // 会话键:读 x-godot-session,驱动注册表的在途计数,并把键写进 HttpContext.Items
    // 供工具实现读取。放在 token 之后 —— 401 探测不该产生会话活动。
    app.Use(async (context, next) =>
    {
        var registry = context.RequestServices.GetRequiredService<SessionKeyRegistry>();
        var accessor = context.RequestServices.GetRequiredService<SessionKeyAccessor>();
        var diagnostics = context.RequestServices.GetRequiredService<SessionKeyDiagnostics>();
        await SessionKeyMiddleware.HandleAsync(context, registry, accessor, diagnostics, next)
            .ConfigureAwait(false);
    });
    // 空闲计量只覆盖通过认证的请求:401 探测不算活动。
    app.Use(async (context, next) =>
    {
        idleMonitor.Enter();
        try
        {
            await next(context);
        }
        finally
        {
            idleMonitor.Exit();
        }
    });
    app.MapMcp();

    try
    {
        app.Run();
    }
    catch (IOException ex)
    {
        // 运行期 IO 失败即监听绑定失败(Kestrel:Failed to bind to address);
        // 锁/token/日志等 setup 期 IO 失败不会走到这里 —— 它们由外层按环境故障归因。
        Console.Error.WriteLine(
            $"[godot-mcp-daemon] 无法绑定监听地址(通常为端口被占用):{ex.Message}");
        return ExitCodes.BindFailure;
    }

    return ExitCodes.Ok;
}
catch (DaemonAlreadyRunningException ex)
{
    Console.Error.WriteLine($"[godot-mcp-daemon] {ex.Message}");
    return ExitCodes.SingletonLockUnavailable;
}
catch (SingletonLockUnavailableException ex)
{
    // 第二个实例:锁被另一 daemon 明确持有 —— 绝不双监听(退出码 2)。
    Console.Error.WriteLine($"[godot-mcp-daemon] {ex.Message}");
    return ExitCodes.SingletonLockUnavailable;
}
catch (LockStateUnclearException ex)
{
    // Unix 无法区分锁冲突与环境故障 —— 独立退出码告诉拉起方:可重试,别当"已有实例"放弃自愈。
    Console.Error.WriteLine($"[godot-mcp-daemon] {ex.Message}");
    return ExitCodes.LockStateUnclear;
}
catch (IOException ex)
{
    // setup 期 IO 失败(状态目录、锁、token、日志):环境故障而非端口/单例冲突 ——
    // 保留原始信息以退出码 1 退出,不冒充绑定失败。
    Console.Error.WriteLine($"[godot-mcp-daemon] 启动期环境故障:{ex.Message}");
    return ExitCodes.EnvironmentFailure;
}
