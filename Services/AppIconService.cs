using System.Windows.Media.Imaging;

namespace FocusCapture.Services;

/// <summary>
/// 应用图标统一入口（2026-09-19）——「设置 → 外观 → 自定义图标」选的那张图，
/// 同时决定**任务栏窗口图标**（任务栏按钮 / Alt+Tab / 任务视图）与**托盘图标**，两处同源。
///
/// 为什么要有这个类：窗口图标此前**一处都没设过**（全项目 20 个窗口都走 exe 编译期嵌的 app.ico），
/// 自定义图标只在托盘那一处生效 —— 于是用户换了图标，托盘变了、任务栏没变。
/// 逐个窗口去设必然会漏（包括以后新增的窗口），所以这里用 ClassHandler 在 Window.Loaded 上一处注册，
/// 全部窗口自动跟随。
///
/// 改本文件前必读的四条：
/// - **默认不碰窗口**：没有自定义图标时，只清「本服务曾设过图标」的窗口；从未设过的窗口一个都不动，
///   故默认外观与改造前完全一致 —— 本机制不该带来任何默认外观变化。
/// - **绝不抛**：加载失败、图片损坏、个别窗口设不上，全部只回落，不能让一张坏图带崩启动或开窗。
/// - **改不了的边界（别让用户白等）**：固定到任务栏的快捷方式、开始菜单 / 桌面快捷方式、
///   资源管理器里 exe 文件上的图标，读的都是 exe **内嵌资源**，运行期改不了，只有重新编译或换 exe 才行。
/// - 句柄生命周期见 <see cref="CopyFromHBitmap"/>：托盘图标的 Icon 必须自己拥有句柄。
/// </summary>
public static class AppIconService
{
    /// <summary>自定义图标落盘位置（设置页选图后复制到这里，路径固定，与用户原图所在位置无关）。</summary>
    public static string DefaultIconPath => FocusCapturePaths.Combine("custom_icon.png");

    /// <summary>已载入的自定义图标（已 Freeze，可被所有窗口共用）。null = 用 exe 图标。</summary>
    private static ImageSource? _windowIcon;

    private static bool _hooked;

    /// <summary>当前是否已载入可用的自定义图标（false = 不干预窗口图标）。</summary>
    public static bool HasCustomIcon => _windowIcon != null;

    /// <summary>图标文件是否可用（只看文件在不在，不解析内容）。</summary>
    public static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// 重新载入自定义图标。路径为空 / 文件不存在 / 图片损坏 → 清空（回落到 exe 图标）并返回 false。
    /// 永远不抛。可在任意时刻重复调用（设置里换图后用它即时生效）。
    /// </summary>
    public static bool Reload(string? path)
    {
        _windowIcon = null;
        if (!IsUsable(path)) return false;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;                  // 读完即放文件句柄，不锁着用户的图
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;    // 同一路径换了内容也必须重读，不吃缓存
            bmp.UriSource = new Uri(path!, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();                                                // 冻结后才能被各窗口 / 各线程共用
            _windowIcon = bmp;
            return true;
        }
        catch
        {
            _windowIcon = null;   // 图片坏了就当没有自定义图标，绝不向上抛
            return false;
        }
    }

    /// <summary>
    /// 注册全局窗口钩子：此后每个窗口首次显示（Loaded）时自动套用当前图标。
    /// 幂等。**必须在第一个窗口 Show 之前调用** —— 晚于某个窗口的 Loaded，那个窗口就漏掉了。
    /// </summary>
    public static void HookWindowIcon()
    {
        if (_hooked) return;
        _hooked = true;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply(sender as Window)));
    }

    /// <summary>
    /// 把当前图标套到指定窗口。两种分支都要管，缺一个就会出现"两处不同步"：
    /// - **有自定义图标** → 设上去（任务栏 / Alt+Tab 跟着变）；
    /// - **没有自定义图标** → 把**本服务曾经设过图标**的窗口清回默认（否则用户点「恢复默认图标」后，
    ///   托盘已经变回来了，已打开窗口的任务栏图标却还挂着旧图，要重启才恢复）。
    ///   从未被设过的窗口一律不碰 —— 判据就是窗口自己的 Icon 属性（没赋过值时为 null），
    ///   这样「默认外观与改造前完全一致」这条约束才守得住。永不抛。
    /// </summary>
    public static void Apply(Window? window)
    {
        if (window == null) return;

        try
        {
            if (_windowIcon != null)
            {
                window.Icon = _windowIcon;
                return;
            }

            if (window.Icon == null) return;   // 从没设过 → 不干预，保持 exe 图标
            window.Icon = null;
            ClearNativeIcon(window);           // 双保险，理由见下
        }
        catch { /* 个别窗口设不上不影响其他窗口 */ }
    }

    /// <summary>
    /// 显式清掉窗口的原生图标（WM_SETICON 传 0），让 Windows 回落到 exe 编译期图标。
    /// 为什么不只靠 <c>window.Icon = null</c>：WPF 置 null 之后会不会真把 WM_SETICON 发到窗口上，
    /// 不由我们控制；而"恢复默认后已打开窗口仍显示旧图标"正是这个缺口造成的，所以这里直接发消息。
    /// </summary>
    private static void ClearNativeIcon(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;   // 窗口还没有原生句柄，等它 Loaded 时 Apply 会再走一遍

        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
    }

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>刷新所有已打开的窗口（设置里换图后立即生效，不必重启应用）。</summary>
    public static void RefreshOpenWindows()
    {
        var app = Application.Current;
        if (app == null) return;

        foreach (Window w in app.Windows) Apply(w);
    }

    /// <summary>
    /// 构造托盘用的图标：有自定义图标就用它，否则用 <paramref name="fallbackColor"/> 画一个 32×32 方块。
    /// **返回值由调用方负责 Dispose**（它拥有自己的 GDI 句柄）。永不抛。
    /// </summary>
    public static System.Drawing.Icon BuildTrayIcon(string? customPath, System.Drawing.Color fallbackColor)
    {
        try
        {
            if (IsUsable(customPath))
            {
                using var img = System.Drawing.Image.FromFile(customPath!);
                using var bmp = new System.Drawing.Bitmap(img);
                return CopyFromHBitmap(bmp);
            }
        }
        catch { /* 图片损坏 → 落到下面的兜底方块 */ }

        using var solid = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(solid))
            g.Clear(fallbackColor);
        return CopyFromHBitmap(solid);
    }

    /// <summary>
    /// 由位图取一个**自己拥有句柄**的 Icon。
    ///
    /// 为什么不直接 <c>Icon.FromHandle(bmp.GetHicon())</c>（2026-09-19 修正的旧写法）：
    /// 那种 Icon **不拥有**句柄，句柄得靠调用方 <c>DestroyIcon</c> 释放 —— 而旧代码恰好是
    /// 「交给 NotifyIcon 之后立刻 DestroyIcon」，托盘于是长期拿着一个已失效的句柄
    /// （use-after-free：图标可能变空白或画错，只是不报错，很难当场发现）。
    /// 这里 Clone 出一份独立副本、随即销毁临时句柄，句柄生命周期自洽：调用方只管 Dispose。
    /// </summary>
    private static System.Drawing.Icon CopyFromHBitmap(System.Drawing.Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
