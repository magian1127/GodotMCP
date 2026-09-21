using System.Net;
using System.Net.Sockets;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>测试用 loopback 端口分配(避免固定端口在并行/重复运行时互相踩踏)。</summary>
internal static class TestPorts
{
    /// <summary>向操作系统要一个当前空闲的 loopback 端口,返回前立即释放。</summary>
    /// <returns>当前空闲的端口号(释放后存在被其他进程抢占的理论窗口,由测试短暂性兜底)。</returns>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
