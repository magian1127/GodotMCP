/// <summary>
/// godot-mcp-shim 入口:标准输入输出(stdio)到守护进程(daemon)HTTP 的薄适配层(shim)。
/// <para>逻辑链:控制台重设为显式 UTF-8 无 BOM(stdout 只承载协议消息,其余输出一律走 stderr)→
/// 从环境变量构建 ShimOptions,组合 DaemonBootstrap 与 HttpForwarder → 启动期 EnsureAsync 自举
/// (失败不退出:仍进入转发循环,让宿主首个调用收到明确错误而非挂死)→ 逐行读取 stdin,
/// 空行跳过,其余整行交 HttpForwarder 转发并回写 → stdin 关闭(宿主撤退)即退出,
/// daemon 交由空闲超时自管。</para>
/// </summary>
using System.Text;
using GodotMcp.Shim;

// stdout 只承载协议消息:显式 UTF-8 无 BOM,且除转发外的输出一律走 stderr。
Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

var options = ShimOptions.FromEnvironment();
var bootstrap = new DaemonBootstrap(options);
var forwarder = new HttpForwarder(options, bootstrap);

try
{
    await bootstrap.EnsureAsync(CancellationToken.None);
}
catch (Exception ex)
{
    // 启动期自举失败不退出:仍进入转发循环,让 host 的首个调用收到明确错误而非挂死;
    // 之后每次转发都会按重试纪律再次尝试自举。
    Console.Error.WriteLine($"[shim] 启动期 daemon 自举失败: {ex.Message}");
}

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (line.Length == 0)
    {
        continue;
    }
    await forwarder.ForwardLineAsync(line, Console.Out);
}

// stdin 关闭 = host 撤退;daemon 交由空闲超时自管。
