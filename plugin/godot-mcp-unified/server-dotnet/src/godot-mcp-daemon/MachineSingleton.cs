using System.Diagnostics;

namespace GodotMcp.Daemon;

/// <summary>所有副本、端口和状态目录共用的进程级单例；退出或崩溃后由 OS 释放。</summary>
internal sealed class MachineSingleton : IDisposable
{
    private const string Name = "Global\\godot-mcp-unified-daemon-v1";
    private readonly Mutex _mutex;

    private MachineSingleton(Mutex mutex) => _mutex = mutex;

    public static MachineSingleton Acquire()
    {
        // 兼容尚未使用全机互斥量的旧发布版；只让先启动的进程继续。
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName("godot-mcp-daemon"))
        {
            using (other)
            {
                if (other.Id == current.Id) continue;
                try
                {
                    if (!other.HasExited && other.StartTime <= current.StartTime)
                        throw new DaemonAlreadyRunningException(other.Id);
                }
                catch (InvalidOperationException) { } // 枚举后已退出。
            }
        }

        var mutex = new Mutex(false, Name);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new DaemonAlreadyRunningException(null);
        }
        return new MachineSingleton(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

internal sealed class DaemonAlreadyRunningException(int? pid)
    : Exception(pid is { } value
        ? $"已有 godot-mcp-daemon 正在运行（PID {value}），本进程退出。"
        : "已有 godot-mcp-daemon 持有全机单例互斥量，本进程退出。");
