using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusCapture.Diagnostics;

/// <summary>
/// 图标字符可用性自检：把一批 Segoe MDL2 Assets 候选字符，用**与标题栏图标钮完全一致的样式**渲染出来。
///
/// 为什么需要它：图标字符「字体文件里有」不等于「WPF 渲染得出来」——缺字形时会渲染成豆腐块（空框），
/// 而这在静态代码里完全看不出来，只能靠渲染结果判定。挑图标代码时用它一次性过筛，
/// 比「改一次业务代码 → 编译 → 跑快照」的试错循环快得多。
///
/// 判定方法：格子里的方块若为空白小矩形 = 该字符无字形；有清晰图形 = 可用。
/// </summary>
internal static class GlyphProbe
{
    /// <summary>候选代码点（选取原则：语义贴近标题栏各功能，覆盖常见箭头/工具/文档类）。</summary>
    private static readonly int[] Candidates =
    {
        0xE70D, 0xE70E, 0xE70F, 0xE710, 0xE711, 0xE713, 0xE721, 0xE72A, 0xE72B, 0xE72C, 0xE73E,
        0xE748, 0xE749, 0xE74A, 0xE74B, 0xE74C, 0xE74D, 0xE74E, 0xE74F, 0xE753, 0xE77B, 0xE787,
        0xE7C3, 0xE896, 0xE898, 0xE8A5, 0xE8B5, 0xE8BD, 0xE8C8, 0xE8E5, 0xE8FD, 0xE9A8, 0xE9D5,
        0xE9D9, 0xE9F5, 0xEA37, 0xEBD3,
        // 铬区与进行中状态实际使用的码位（E923=还原、E895/E72C=进行中）
        0xE921, 0xE922, 0xE923, 0xE8BB, 0xE895,
    };

    /// <summary>构建自检窗口（每个格子：上方是代码点，下方是同款图标按钮）。</summary>
    public static Window Build()
    {
        var iconFont = new FontFamily("Segoe MDL2 Assets");
        var monoFont = new FontFamily("Consolas");
        var hint = new TextBlock
        {
            Text = "方框内为空 = 该字符无字形（豆腐块）；有图形 = 可用",
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            FontSize = 11,
            Margin = new Thickness(10, 10, 10, 0),
        };
        var panel = new WrapPanel { Margin = new Thickness(10, 6, 10, 10) };

        foreach (var cp in Candidates)
        {
            var cell = new StackPanel { Width = 72, Margin = new Thickness(0, 0, 4, 8) };
            cell.Children.Add(new TextBlock
            {
                Text = cp.ToString("X4"),
                FontFamily = monoFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            // 属性与 QuickViewWindow.CreateToolbarButton 的图标钮保持一致，确保结论可直接套用
            cell.Children.Add(new Button
            {
                Content = char.ConvertFromUtf32(cp),
                FontFamily = iconFont,
                FontSize = 14,
                Width = 28,
                Height = 24,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(1),
            });
            panel.Children.Add(cell);
        }

        var root = new StackPanel();
        root.Children.Add(hint);
        root.Children.Add(panel);

        return new Window
        {
            Title = "图标字符自检",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Content = root,
        };
    }
}
