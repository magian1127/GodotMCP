using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace GodotMcp.Daemon.Tools;

/// <summary>一次成功的系统窗口捕获:PNG 绝对路径与像素尺寸、字节量。</summary>
/// <param name="Path">PNG 文件的绝对路径(已在磁盘上)。</param>
/// <param name="Width">捕获宽度(像素)。</param>
/// <param name="Height">捕获高度(像素)。</param>
/// <param name="Bytes">PNG 文件字节数。</param>
internal sealed record OsWindowShot(string Path, int Width, int Height, int Bytes);

/// <summary>
/// 系统内置截图 API 的兜底捕获(仅 Windows):引擎内截图(addon 视口捕获)失败或
/// 卡死时 — TIMEOUT / DISCONNECTED / 视口不可用 / 窗口挂起 — daemon 直接按目标
/// 进程 pid 找到其最大可见窗口,经 GDI <c>PrintWindow(PW_RENDERFULLCONTENT)</c>
/// 抓取窗口当前合成内容(DWM 表面,GPU 渲染的应用也适用)并落盘 PNG,把文件
/// 路径返回给调用方。嵌入编辑器 Game 视图的游戏窗口是编辑器 HWND 的子窗口,
/// 因此枚举同时覆盖顶层窗口与其子窗口树。<br/>
/// <br/>
/// 兜底只补位、不掩盖:任何一步失败(找不到窗口、PrintWindow 拒绝、写盘失败、
/// 非 Windows)都返回 null,让调用方回落到原始错误;整个流程被 try/catch 包裹,
/// 绝不引入新的失败形态。与引擎内捕获的语义差异必须在响应的 hint 中披露:
/// 这是窗口的屏幕合成内容 — 编辑器目标包含完整编辑器界面而非孤立的 2D/3D
/// 视口,node_path 框选在此路径下不可用。
/// </summary>
internal static class OsWindowCapture
{
    /// <summary>仅 Windows 可用(GDI/USER32);其余平台永不触发兜底。</summary>
    internal static bool Supported => OperatingSystem.IsWindows();

    /// <summary>触发兜底的原始错误码集合:引擎路径失败或卡死的可恢复形态。
    /// GAME_NOT_RUNNING 不在其中 — 游戏未运行时其窗口必然不存在,兜底无意义。</summary>
    private static readonly HashSet<string> FallbackCodes = new(StringComparer.Ordinal)
    {
        "TIMEOUT",
        "DISCONNECTED",
        "EDITOR_VIEWPORT_UNAVAILABLE",
        "RUNTIME_WINDOW_MINIMIZED",
    };

    /// <summary>捕获候选窗口的最小客户端区尺寸:过滤托盘图标、提示窗等噪声表面。</summary>
    internal const int MinWindowDimension = 64;

    /// <summary>PW_RENDERFULLCONTENT:抓取窗口的 DWM 合成内容(Win 8.1+),
    /// 对不响应 WM_PRINT 的 GPU 渲染应用(Godot 属此类)是唯一可靠路径。</summary>
    private const uint PW_RENDERFULLCONTENT = 0x2;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2:让 GetClientRect 返回
    /// 物理像素。进程级一次性设置(幂等;已设置或系统不支持时静默忽略)。</summary>
    private static int _dpiAwarenessTried;

    /// <summary>
    /// 兜底触发判定(纯函数,测试锚定):Windows 平台、调用方未要求 node_path
    /// 框选(框选必须由引擎执行,OS 截图无法替代)、且原始错误码属于可恢复集合。
    /// </summary>
    /// <param name="isWindows">当前平台是否 Windows(<see cref="Supported"/>)。</param>
    /// <param name="nodePath">调用方的 node_path 参数(非空 = 需要引擎框选)。</param>
    /// <param name="originalCode">原始失败信封的错误码。</param>
    /// <returns>应尝试 OS 兜底为 true。</returns>
    internal static bool ShouldAttempt(bool isWindows, string? nodePath, string originalCode)
    {
        return isWindows
            && string.IsNullOrEmpty(nodePath)
            && FallbackCodes.Contains(originalCode);
    }

    /// <summary>
    /// 对 [param pid] 所属进程执行一次窗口捕获并落盘 PNG。任何失败返回 null
    /// (调用方回落原始错误),绝不抛出。
    /// </summary>
    /// <param name="pid">目标进程 id(编辑器实例或运行中游戏)。</param>
    /// <param name="tag">文件名标签(如 editor/runtime),仅用于人读。</param>
    /// <returns>捕获成功为 <see cref="OsWindowShot"/>;失败为 null。</returns>
    internal static OsWindowShot? TryCapture(int pid, string tag)
    {
        if (!Supported || pid <= 0)
        {
            return null;
        }
        if (!OperatingSystem.IsWindows())
        {
            return null; // 平台分析器可识别的直接守卫(Supported 属性的同一事实)。
        }
        try
        {
            EnsurePerMonitorDpiAware();
            var hwnd = FindLargestVisibleWindow((uint)pid);
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }
            if (!GetClientRect(hwnd, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            {
                return null;
            }
            return CaptureWindowToPng(hwnd, rect.Right, rect.Bottom, tag);
        }
        catch (Exception)
        {
            // 兜底绝不引入新的失败形态 — 任何异常都回落原始错误。
            return null;
        }
    }

    /// <summary>构建兜底成功响应的 JSON 信封:磁盘载荷形态 + 捕获来源披露。
    /// hint 说明与引擎内捕获的语义差异(全窗口内容、非孤立视口、无 node 框选)。</summary>
    /// <param name="shot">捕获结果。</param>
    /// <param name="originalCode">触发兜底的原始错误码(披露进响应)。</param>
    /// <param name="target">捕获目标(runtime/editor),披露进响应。</param>
    /// <returns>信封 JSON 文本。</returns>
    internal static string BuildEnvelope(OsWindowShot shot, string originalCode, string target)
    {
        var envelope = new JsonObject
        {
            ["path"] = shot.Path,
            ["width"] = shot.Width,
            ["height"] = shot.Height,
            ["bytes"] = shot.Bytes,
            ["mime_type"] = "image/png",
            ["returned"] = $"{shot.Width}x{shot.Height}",
            ["capture_source"] = "os_window",
            ["original_code"] = originalCode,
            ["hint"] =
                $"The in-engine capture failed ({originalCode}), so the daemon captured the {target} " +
                $"window via the OS screenshot API (PrintWindow) instead → {shot.Path}. This is the " +
                "window's composed on-screen content: for target:editor it includes the full editor UI " +
                "rather than the isolated 2D/3D viewport, and node_path framing is unavailable on this " +
                "path. Read the file to inspect it; restore/unminimize the window and retry the " +
                "in-engine capture for viewport-exact output.",
        };
        return envelope.ToJsonString();
    }

    /// <summary>兜底 PNG 的落盘目录:%LOCALAPPDATA%\godot-mcp-daemon\screenshots
    /// (daemon 自有产物,与 Godot 侧 user://screenshots/ 互不依赖)。</summary>
    /// <returns>目录绝对路径。</returns>
    internal static string ScreenshotsDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "godot-mcp-daemon", "screenshots");
    }

    /// <summary>兜底 PNG 的唯一文件名:标签 + 毫秒精度时间戳(同一毫秒并发捕获仍可能
    /// 相撞,概率可忽略;撞名时后写覆盖前写,响应的 path 与磁盘内容保持一致)。</summary>
    /// <param name="tag">目标标签(editor/runtime)。</param>
    /// <param name="now">时间戳来源(注入以便测试)。</param>
    /// <returns>文件全名(不含目录)。</returns>
    internal static string BuildFileName(string tag, DateTime now)
    {
        return $"{tag}-{now:yyyyMMdd-HHmmssfff}.png";
    }

    /// <summary>进程级一次性设置 per-monitor DPI 感知;失败(已设置/系统过旧)静默忽略 —
    /// 最坏结果是捕获到 DPI 虚拟化后的缩放图像,仍是有内容的有效截图。</summary>
    private static void EnsurePerMonitorDpiAware()
    {
        if (Interlocked.Exchange(ref _dpiAwarenessTried, 1) == 1)
        {
            return;
        }
        try
        {
            _ = SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);
        }
        catch (Exception)
        {
            // EntryPointNotFound(旧系统)/已设置(ACCESS_DENIED)— 都不影响可用性。
        }
    }

    /// <summary>
    /// 枚举 [param pid] 的可见窗口并返回客户端区面积最大者:先查顶层窗口
    /// (浮动游戏窗口、编辑器主窗),再查每个可见顶层窗口的子窗口树
    /// (嵌入编辑器 Game 视图的游戏窗口是编辑器的子 HWND,顶层枚举看不到它)。
    /// </summary>
    /// <param name="pid">目标进程 id。</param>
    /// <returns>最大窗口句柄;无候选为 IntPtr.Zero。</returns>
    private static IntPtr FindLargestVisibleWindow(uint pid)
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0L;

        foreach (var hwnd in EnumTopLevelWindows())
        {
            _ = GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd))
            {
                IsCandidate(hwnd, ref best, ref bestArea);
            }
            // 嵌入窗口:无论顶层归谁,子树里都可能藏着目标进程的 HWND。
            // (lambda 参数不可命名 "_":双参数下 "_" 是真实形参,会遮蔽丢弃符
            // 并让体内的 "_ =" 丢弃赋值变成对它的类型不符赋值。)
            EnumChildWindows(hwnd, (child, lParam) =>
            {
                _ = GetWindowThreadProcessId(child, out var childOwner);
                if (childOwner == pid && IsWindowVisible(child))
                {
                    IsCandidate(child, ref best, ref bestArea);
                }
                return true;
            }, IntPtr.Zero);
        }
        return best;
    }

    /// <summary>尺寸守卫 + 面积竞争:客户端区达到最小尺寸且大于现最优时更新纪录。
    /// 直接操作枚举循环的 best/bestArea(以 ref 传递,保持枚举路径无分配)。</summary>
    private static bool IsCandidate(IntPtr hwnd, ref IntPtr best, ref long bestArea)
    {
        if (!GetClientRect(hwnd, out var rect))
        {
            return false;
        }
        if (rect.Right < MinWindowDimension || rect.Bottom < MinWindowDimension)
        {
            return false;
        }
        var area = (long)rect.Right * rect.Bottom;
        if (area <= bestArea)
        {
            return false;
        }
        best = hwnd;
        bestArea = area;
        return true;
    }

    /// <summary>把窗口内容打印到内存位图并编码为 PNG 落盘。先取 DWM 合成内容
    /// (PW_RENDERFULLCONTENT),被拒绝时退回经典 WM_PRINT 路径;两者皆拒返回 null。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static OsWindowShot? CaptureWindowToPng(IntPtr hwnd, int width, int height, string tag)
    {
        var hdcWindow = GetWindowDC(hwnd);
        if (hdcWindow == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var hdcMem = CreateCompatibleDC(hdcWindow);
            if (hdcMem == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var hbm = CreateCompatibleBitmap(hdcWindow, width, height);
                if (hbm == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    var old = SelectObject(hdcMem, hbm);
                    var printed = PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT)
                        || PrintWindow(hwnd, hdcMem, 0);
                    _ = SelectObject(hdcMem, old);
                    if (!printed)
                    {
                        return null;
                    }
                    return SaveBitmapAsPng(hbm, tag);
                }
                finally
                {
                    _ = DeleteObject(hbm);
                }
            }
            finally
            {
                _ = DeleteDC(hdcMem);
            }
        }
        finally
        {
            _ = ReleaseDC(hwnd, hdcWindow);
        }
    }

    /// <summary>从 HBITMAP 编码 PNG 写入兜底目录。System.Drawing.Common 为
    /// Windows 专属包 — 调用方已由 <see cref="Supported"/> 门控。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static OsWindowShot? SaveBitmapAsPng(IntPtr hbm, string tag)
    {
        var directory = ScreenshotsDir();
        Directory.CreateDirectory(directory);
        var fullPath = Path.Combine(directory, BuildFileName(tag, DateTime.Now));
        using var bitmap = System.Drawing.Image.FromHbitmap(hbm);
        bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
        var file = new FileInfo(fullPath);
        return new OsWindowShot(fullPath, bitmap.Width, bitmap.Height, (int)file.Length);
    }

    /// <summary>枚举全部顶层窗口(快照到列表,避免在回调里做重活)。</summary>
    private static List<IntPtr> EnumTopLevelWindows()
    {
        var windows = new List<IntPtr>(64);
        EnumWindows((hWnd, lParam) =>
        {
            windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
