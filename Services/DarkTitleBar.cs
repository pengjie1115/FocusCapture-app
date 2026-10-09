using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusCapture.Services;

/// <summary>
/// 原生标题栏深色开关（2026-09-21）。
/// WPF 不主动申请深色标题栏：即使系统处于深色模式，窗口标题栏也可能渲染成白底
/// —— AI 问答窗口的白条就是这么来的；设置窗口的深色标题栏是系统行为，不是代码保证，不可依赖。
/// 这里走 DWM 的 ImmersiveDarkMode 属性显式申请；老系统不认识该属性时调用失败，
/// 按原样回退（与改动前的表现一致），不抛、不重试。
/// </summary>
public static class DarkTitleBar
{
    /// <summary>
    /// DWM 窗口属性号：19 = Win10 1809 的旧值，20 = Win10 1903 起的正式值。
    /// 两个都设：不认识的属性返回错误码，无害；认识的那个生效。
    /// </summary>
    internal static readonly int[] DarkModeAttributes = { 19, 20 };

    /// <summary>在窗口句柄创建后生效；构造函数里调用即可，不必等 Loaded。</summary>
    public static void Enable(Window window) => Enable(window, dark: true);

    /// <summary>同上，但按 dark 决定申请深色还是还原系统浅色（2026-09-27 AI 问答明暗滑块用）。</summary>
    public static void Enable(Window window, bool dark)
        => window.SourceInitialized += (_, _) => Apply(window, dark);

    /// <summary>对句柄已存在的窗口直接生效（重复调用无害）。</summary>
    public static void Apply(Window window) => Apply(window, dark: true);

    /// <summary>显式开关深色标题栏（2026-09-27：AI 问答明暗滑块切到浅色端时要还原系统浅色标题栏）。</summary>
    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        foreach (var attr in DarkModeAttributes)
        {
            var on = dark ? 1 : 0;
            // 返回码刻意忽略：不支持该属性的旧系统按原样回退，功能不受影响
            _ = DwmSetWindowAttribute(hwnd, attr, ref on, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
