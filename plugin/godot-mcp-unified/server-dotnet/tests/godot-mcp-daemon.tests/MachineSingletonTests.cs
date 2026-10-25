using GodotMcp.Daemon.Tests.Infrastructure;

namespace GodotMcp.Daemon.Tests;

public sealed class MachineSingletonTests
{
    [Fact]
    public void second_daemon_exits_even_with_a_different_port_and_state_directory()
    {
        using var first = DaemonProcess.StartReady(new DaemonSpawnOptions { IdleSeconds = 120 });
        using var second = DaemonProcess.Start(new DaemonSpawnOptions { IdleSeconds = 120 });

        Assert.True(second.WaitForExit(TimeSpan.FromSeconds(8)),
            "第二个 daemon 应立即退出，不能通过另一个端口或状态目录多开。");
        Assert.Equal(ExitCodes.SingletonLockUnavailable, second.ExitCode);
        Assert.False(first.HasExited);
    }
}
