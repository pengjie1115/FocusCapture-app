using System.Windows;

namespace FocusCapture.Windows;

/// <summary>
/// 拖放浮层的贴球定位（小条与卡片共用）。
/// 规则：球在屏幕左半 → 浮层弹在球**右侧**；球在右半 → 弹在球**左侧**；垂直方向上与球居中对齐。
/// 折叠态（吸附）下球只有 8×36，展开后才有 56×56 —— 定位一律按**传进来的实时几何**算，不写死尺寸。
/// </summary>
internal static class OverlayPlacement
{
    /// <summary>浮层与球之间的间隙。</summary>
    private const double Gap = 8;

    /// <summary>贴边留白，避免浮层压到工作区边缘外。</summary>
    private const double EdgeMargin = 4;

    public static void PlaceBeside(Window overlay, double ballLeft, double ballTop,
        double ballWidth, double ballHeight)
    {
        var wa = SystemParameters.WorkArea;

        // SizeToContent 的窗口在 UpdateLayout() 之后才有 Actual*；取不到时退回声明尺寸
        var w = overlay.ActualWidth > 0 ? overlay.ActualWidth : overlay.Width;
        var h = overlay.ActualHeight > 0 ? overlay.ActualHeight : overlay.Height;
        if (double.IsNaN(w) || w <= 0) w = 96;
        if (double.IsNaN(h) || h <= 0) h = 66;

        var ballCenterX = ballLeft + ballWidth / 2;
        var screenCenterX = wa.Left + wa.Width / 2;

        var left = ballCenterX < screenCenterX
            ? ballLeft + ballWidth + Gap     // 球在左半 → 弹右侧
            : ballLeft - w - Gap;            // 球在右半 → 弹左侧

        // 放不下不硬挤到屏幕外：折到另一侧。两侧都放不下（屏幕极窄）时退回"贴右边缘"，
        // 至少保证看得见、点得到。
        if (left + w > wa.Right - EdgeMargin) left = ballLeft - w - Gap;
        if (left < wa.Left + EdgeMargin) left = ballLeft + ballWidth + Gap;
        if (left + w > wa.Right - EdgeMargin) left = wa.Right - w - EdgeMargin;
        if (left < wa.Left + EdgeMargin) left = wa.Left + EdgeMargin;

        var top = ballTop + ballHeight / 2 - h / 2;   // 与球垂直居中对齐
        var maxTop = Math.Max(wa.Top + EdgeMargin, wa.Bottom - h - EdgeMargin);
        top = Math.Clamp(top, wa.Top + EdgeMargin, maxTop);

        overlay.Left = left;
        overlay.Top = top;
    }
}
