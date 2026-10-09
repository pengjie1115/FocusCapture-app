using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FocusCapture.Services;

public static class Win32
{
    // ── 热键 ──
    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;

    // ── 修饰键 ──
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    // ── 虚拟键码 ──
    public const uint VK_SPACE = 0x20;
    public const uint VK_V = 0x56;
    public const uint VK_RETURN = 0x0D;
    public const uint VK_ESCAPE = 0x1B;

    // ── 前台窗口 ──
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static string GetActiveWindowTitle()
    {
        var hWnd = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        return sb.ToString();
    }

    // ── 窗口置顶 ──
    public const int HWND_TOPMOST = -1;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    // ── 屏幕尺寸 ──
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    // ── 鼠标位置 ──
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    // ── 剪贴板 ──
    [DllImport("user32.dll")]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    /// <summary>获取剪贴板序列号，内容每次真正变化时递增</summary>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    public const uint CF_UNICODETEXT = 13;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    public const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// 读取剪贴板文本。占用时按指数退避重试（默认 3 次 / 15ms 起步，总窗口约 45ms ——
    /// 调用方多在 WndProc / 监控回调里，不能久睡）。打开成功但没数据 / 锁不上不是占用，不重试。
    /// 此前的单次尝试版本在远程工具占用期间会静默返回 null，表现为「剪贴板监控丢条目」。
    /// </summary>
    public static string? GetClipboardText(int attempts = 3, int baseDelayMs = 15)
    {
        for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var hData = GetClipboardData(CF_UNICODETEXT);
                    if (hData == IntPtr.Zero) return null;
                    var ptr = GlobalLock(hData);
                    if (ptr == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { GlobalUnlock(hData); }
                }
                finally { CloseClipboard(); }
            }
            if (attempt < attempts) Thread.Sleep(baseDelayMs << Math.Min(attempt - 1, 2));
        }
        return null;
    }

    /// <summary>
    /// Win32 经典路径写 CF_UNICODETEXT（单格式、**立即渲染**）。与 WPF 的 OLE 路径相比：
    /// 持有剪贴板锁的只有几个微秒级 API 序列，数据立刻进内核、不依赖本进程存活。
    /// 用途：OLE 写入失败（剪贴板被远程工具等占用）后的固化兜底 —— OleSetClipboard 成功而
    /// OleFlushClipboard 被打断会留下「延迟渲染」中间态（剪贴板查看器看不到、稍后却能粘贴），
    /// 本函数用 EmptyClipboard 把该中间态清掉换成真实数据。占用重试由调用方（SafeClipboard）负责。
    /// </summary>
    public static bool WriteClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var bytes = Encoding.Unicode.GetBytes(text);
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(bytes.Length + 2));
        if (hMem == IntPtr.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) { GlobalFree(hMem); return false; }
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteInt16(ptr, bytes.Length, 0);   // UTF-16 终止符
        }
        finally { GlobalUnlock(hMem); }

        if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(hMem); return false; }
        try
        {
            var ok = SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
            if (!ok) GlobalFree(hMem);   // 交系统接管失败要自己释放；成功后 hMem 归系统，不得再 Free
            return ok;
        }
        finally { CloseClipboard(); }
    }

    // ── 工作区域（排除任务栏） ──
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
    public const uint SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ── 窗口扩展样式 ──
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>把 Modifiers 枚举值转换为 Win32 修饰键</summary>
    public static uint ModifiersToWin32(int modifiers)
    {
        uint result = 0;
        if ((modifiers & 1) != 0) result |= MOD_ALT;
        if ((modifiers & 2) != 0) result |= MOD_CONTROL;
        if ((modifiers & 4) != 0) result |= MOD_SHIFT;
        if ((modifiers & 8) != 0) result |= MOD_WIN;
        return result;
    }

    /// <summary>生成热键描述文本</summary>
    public static string HotkeyToString(Models.HotkeyBinding hk)
    {
        var parts = new List<string>();
        if ((hk.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((hk.Modifiers & 1) != 0) parts.Add("Alt");
        if ((hk.Modifiers & 4) != 0) parts.Add("Shift");
        if ((hk.Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(((System.Windows.Forms.Keys)hk.Key).ToString());
        return string.Join(" + ", parts);
    }
}
