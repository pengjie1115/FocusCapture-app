using System.Windows;
using System.Windows.Media;

namespace FocusCapture.Windows;

/// <summary>
/// 轻量单行输入对话框（代码构造，无 XAML）：AI 会话重命名 / 新建分组等复用。
/// ShowDialog 返回；确认后经 Input 属性取值，取消返回 null 语义（IsConfirmed=false）。
/// </summary>
public static class PromptDialog
{
    /// <summary>弹出输入框。确认返回输入文本（可空串），取消返回 null。
    /// <paramref name="multiline"/> = true 时给多行文本框 —— 分组指令要写一整段话，
    /// 塞进单行框里写两百字是折磨（2026-09-23 分组指令用）。</summary>
    public static string? Show(Window owner, string title, string label, string initial = "", bool multiline = false)
    {
        var text = new System.Windows.Controls.TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };

        if (multiline)
        {
            text.AcceptsReturn = true;
            text.TextWrapping = TextWrapping.Wrap;
            text.Height = 140;
            text.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        }

        var ok = new System.Windows.Controls.Button
        {
            Content = "确定",
            Width = 72,
            IsDefault = true,
            Margin = new Thickness(0, 12, 8, 0),
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 72,
            IsCancel = true,
            Margin = new Thickness(0, 12, 0, 0),
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = text.Text.Trim();
            Window.GetWindow(ok)!.Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            MaxWidth = multiline ? 560 : 480,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Content = panel,
        };
        // ⚠️ Focus/SelectAll 必须等窗口加载完成（Loaded）后再做 —— ShowDialog 之前调用时窗口
        // 还没进可视树，设置会被忽略：重命名时初始值虽然传进来了，但既不聚焦也不全选，
        // 用户以为要重新打一遍（2026-09-24 用户反馈「重命名直接清空原命名」的第二半根因）。
        win.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        win.ShowDialog();
        return result;
    }
}
