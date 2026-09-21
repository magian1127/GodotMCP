// godot-mcp-daemon 进程入口(顶级语句)。完整启动链(全部失败出口按 ExitCodes 归因):
// 1. 参数解析:DaemonOptions.FromEnvironment 读三个环境变量,非法值即启动期环境故障;
// 2. 单例锁获取:DaemonState.Acquire 打开机器级锁文件,顺带确保稳定 token(DaemonToken.EnsureStable)——
//    锁被另一实例明确持有 → 退出码 2;锁状态不明确(Unix)→ 3;
// 3. 主机构建:UseUrls 绑定 127.0.0.1:Port;日志全部落 stderr + daemon.log(stdout 保留给 MCP 协议通道);
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
using GodotMcp.Daemon.Tools;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

try
{
    var options = DaemonOptions.FromEnvironment();
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
            // tools.listChanged 必须广播:无状态 HTTP 下 host 只能经 subscriptions/listen
            // 长请求流接收 tools/list_changed(2026-07-28 修订);能力旗标与实际投递一致(issue 12)。
            o.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
            };
        })
        .WithHttpTransport(o =>
        {
            o.SessionMode = HttpServerSessionMode.Stateless;
            // 每个请求会话:把工具面对齐到 daemon 全局的组激活状态(issue 12)——
            // 已激活组工具在、未激活不在;无隐藏会话状态。
            o.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
            {
                if (serverOptions.ToolCollection is { } collection)
                {
                    httpContext.RequestServices.GetRequiredService<GroupService>()
                        .ConfigureSession(collection);
                    // 常驻工具的 schema 换用 Node 表内 zod 派生版(issue 13 parity;
                    // 组工具构造时已带表内 schema,此处只覆盖 eager 面)。
                    NodeToolTable.ApplyProtocolSchemas(collection);
                    // 版本门控可见性(issue 14):实例集无法提供的门控工具从本请求隐藏
                    // (单实例与 Node 注册门控一致;多实例并集)。
                    httpContext.RequestServices.GetRequiredService<GroupService>()
                        .ApplyVersionGates(collection);
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
            return await GroupListenStream.ServeAsync(
                groups,
                request.Server,
                request.JsonRpcRequest.Id,
                request.Params?.Notifications,
                cancellationToken);
        });

    var idleMonitor = new IdleMonitor(options.IdleTimeout);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(idleMonitor);
    builder.Services.AddHostedService<IdleExitService>();
    builder.Services.AddSingleton<InstanceManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InstanceManager>());
    builder.Services.AddSingleton<GroupService>();
    builder.Services.AddSingleton<ExtensionService>();

    var app = builder.Build();

    // issue 14 组合接线:扩展工具的版本门控 + extensions.changed 通知消费
    // (Instances → Extensions 的依赖边留在组合根,保持两者互不引用)。
    var instanceManager = app.Services.GetRequiredService<InstanceManager>();
    var extensionService = app.Services.GetRequiredService<ExtensionService>();
    instanceManager.ExtraMethodGates = extensionService.GateForMethod;
    instanceManager.ConnectionNotification += (_, type, notificationParams) =>
    {
        if (type == "extensions.changed" && extensionService.ApplyChanged(notificationParams))
        {
            app.Services.GetRequiredService<GroupService>().NotifyToolSurfaceChanged();
        }
    };

    app.UseMiddleware<RequireDaemonTokenMiddleware>(state.Token);
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
