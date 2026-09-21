using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace GodotMcp.Daemon;

/// <summary>
/// HTTP 面的 Bearer token 门禁(ADR-0004 本地安全面):不是 "Bearer &lt;token&gt;" 一律 401,
/// 防止其他本机工具误碰端口。固定时间比较,不泄露时序信息。
/// </summary>
public sealed class RequireDaemonTokenMiddleware
{
    /// <summary>管道中的下一个中间件,鉴权通过后调用。</summary>
    private readonly RequestDelegate _next;

    /// <summary>期望 token 的 UTF-8 字节,构造时预编码,避免每次请求重复转换。</summary>
    private readonly byte[] _expectedToken;

    /// <summary>
    /// 由 <c>UseMiddleware&lt;T&gt;(state.Token)</c> 实例化,token 来自 DaemonState(即状态目录内的稳定 token)。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <param name="token">期望的持有者令牌,与 DaemonToken.EnsureStable 生成/复用的值一致。</param>
    public RequireDaemonTokenMiddleware(RequestDelegate next, string token)
    {
        _next = next;
        _expectedToken = Encoding.UTF8.GetBytes(token);
    }

    /// <summary>
    /// 请求入口:每个 HTTP 请求先过 token 门禁,再放行到 MCP 端点。
    /// </summary>
    /// <param name="context">当前 HTTP 请求上下文。</param>
    /// <returns>代表整段管道处理的任务。</returns>
    /// <para>逻辑链:Authorization 头校验失败(<see cref="IsAuthorized"/> 为 false)→ 直接写 401 短路返回,
    /// 不进入下游(该路径也不计空闲,见 Program 的计量中间件顺序)→ 通过则调用下一中间件。</para>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAuthorized(context.Request.Headers.Authorization.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// 校验 Authorization 头是否为匹配的 "Bearer &lt;token&gt;"。
    /// </summary>
    /// <param name="header">原始 Authorization 头字符串,可能缺失或格式任意。</param>
    /// <returns>scheme 为 Bearer(忽略大小写)且参数与期望 token 字节一致时为 true。</returns>
    /// <para>逻辑链:AuthenticationHeaderValue.TryParse 失败或 scheme 非 Bearer → false →
    /// 否则对参数做 UTF-8 编码后用 CryptographicOperations.FixedTimeEquals 与期望字节定长比较 ——
    /// 固定时间比较不因前缀匹配提前返回,不泄露时序信息;长度不等时返回 false
    /// (仅泄露长度,不含内容)。</para>
    private bool IsAuthorized(string header)
    {
        if (!AuthenticationHeaderValue.TryParse(header, out var parsed)
            || !parsed.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            _expectedToken,
            Encoding.UTF8.GetBytes(parsed.Parameter ?? string.Empty));
    }
}
