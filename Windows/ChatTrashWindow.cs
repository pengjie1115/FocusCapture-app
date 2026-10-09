using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusCapture.Services;
using FocusCapture.Windows.Controls;

namespace FocusCapture.Windows;

/// <summary>
/// 会话回收站窗口（代码构造，无 XAML）：查看 / 恢复（移回主目录）/ 彻底删除（本地物理删）/ 清空。
/// 本期均为本地行为，不跨端传播（恢复不触发同步 Notify）。
/// </summary>
public class ChatTrashWindow : Window
{
    private readonly StackPanel _listPanel = new();
    private readonly TextBlock _countText = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        FontSize = 11,
        Margin = new Thickness(16, 12, 16, 4),
    };

    public ChatTrashWindow()
    {
        Title = "会话回收站";
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 420;
        Height = 440;
        MinWidth = 340;
        MinHeight = 300;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var emptyBtn = MakeLinkButton("清空回收站", Empty_Click);
        var closeBtn = MakeLinkButton("关闭", (_, _) => Close());

        var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 14) };
        DockPanel.SetDock(emptyBtn, Dock.Left);
        DockPanel.SetDock(closeBtn, Dock.Right);
        bottom.Children.Add(emptyBtn);
        bottom.Children.Add(closeBtn);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _listPanel,
        };

        var root = new Grid { Margin = new Thickness(0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_countText, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(bottom, 2);
        root.Children.Add(_countText);
        root.Children.Add(scroll);
        root.Children.Add(bottom);
        Content = root;

        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _listPanel.Children.Clear();
        var items = ChatSessionService.ListTrashSessions();
        _countText.Text = items.Count == 0
            ? "回收站是空的"
            : $"共 {items.Count} 个会话（删除经其他设备同步也会进入这里；恢复仅本机生效）";

        if (items.Count == 0) return;

        foreach (var s in items)
        {
            var row = new DockPanel { Margin = new Thickness(16, 4, 16, 0) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = s.Preview,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{s.SavedAt:yyyy-MM-dd HH:mm} · {AiModeText.Get(s.Mode)} · {s.MessageCount} 条消息",
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                FontSize = 11,
            });

            var delBtn = MakeLinkButton("彻底删除", (_, _) => DeleteOne(s));
            var restoreBtn = MakeLinkButton("恢复", (_, _) => RestoreOne(s));
            DockPanel.SetDock(delBtn, Dock.Right);
            DockPanel.SetDock(restoreBtn, Dock.Right);
            row.Children.Add(info);
            row.Children.Add(delBtn);
            row.Children.Add(restoreBtn);
            _listPanel.Children.Add(row);
        }
    }

    private void RestoreOne(SessionSummary s)
    {
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(ChatSessionService.TrashDir)!, Path.GetFileName(s.FilePath));
            if (File.Exists(target))
            {
                System.Windows.MessageBox.Show(this, "主目录已存在同名会话文件，无法恢复", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            File.Move(s.FilePath, target);
            Reload();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"恢复失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteOne(SessionSummary s)
    {
        if (System.Windows.MessageBox.Show(this, "彻底删除该会话？此操作不可恢复。",
                "彻底删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(s.FilePath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"删除失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Reload();
    }

    private void Empty_Click(object sender, RoutedEventArgs e)
    {
        var items = ChatSessionService.ListTrashSessions();
        if (items.Count == 0) return;
        if (System.Windows.MessageBox.Show(this,
                $"清空回收站？将彻底删除全部 {items.Count} 个会话，此操作不可恢复。",
                "清空回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var failed = 0;
        foreach (var s in items)
        {
            try { File.Delete(s.FilePath); }
            catch { failed++; }
        }
        if (failed > 0)
            System.Windows.MessageBox.Show(this, $"{failed} 个文件删除失败（可能被占用）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        Reload();
    }

    private static Button MakeLinkButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x9E, 0x7A)),
            Padding = new Thickness(6, 2, 6, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Click += onClick;
        return b;
    }
}
