using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// v3.5（Phase 3）：单条/多条到点提醒弹窗。
/// 深色无边框 Topmost，显示于悬浮球上方；同分钟多条合并成一个列表。
/// 每条按钮：已完成(Done)/稍后提醒(due + SnoozeMinutes)/已知悉(Read)。
/// 所有 UpdateTodo 正文一律用 EditedContent ?? Content 重建（防编辑过的待办被旧正文覆盖）。
/// v3.7：PopupAutoCloseSeconds 秒后自动收起时，未处理条目默认执行「稍后提醒」（防任务遗漏）。
/// </summary>
public partial class ReminderPopupWindow : Window
{
    private readonly NoteService _notes;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _autoClose;
    // v3.7：跟踪当前展示的行（行 → 条目），自动收起时对未处理行执行「稍后提醒」
    private readonly List<(UIElement Row, NoteEntry Entry)> _rows = new();

    public ReminderPopupWindow(NoteService notes, AppSettings settings)
    {
        InitializeComponent();
        _notes = notes;
        _settings = settings;
        _autoClose = new DispatcherTimer();
        // v3.7 修复：弹窗超时未操作 → 默认逐条执行「稍后提醒」（DueTime + SnoozeMinutes），
        // 而不是静默隐藏导致任务不再提醒（用户没在电脑旁时的遗漏兜底）
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); SnoozeAllAndHide(); };
    }

    /// <summary>显示一组到点待办（同分钟合并传入），anchor 为悬浮球所在坐标，定位在球上方。</summary>
    public void ShowPopups(List<NoteEntry> items, double anchorLeft, double anchorTop)
    {
        PopupList.Children.Clear();
        _rows.Clear();
        if (items == null || items.Count == 0) { HidePopup(); return; }

        HeaderText.Text = items.Count > 1 ? $"有 {items.Count} 条提醒" : "提醒时间到";
        foreach (var e in items)
        {
            var row = BuildRow(e);
            PopupList.Children.Add(row);
            _rows.Add((row, e));
        }

        // 先布局一次拿到真实尺寸再定位
        Show();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Arrange(new Rect(0, 0, ActualWidth, ActualHeight));
        UpdateLayout();

        var wa = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 320;
        double h = ActualHeight > 0 ? ActualHeight : 120;
        double left = anchorLeft - w / 2;
        double top = anchorTop - h - 10;                     // 悬浮球上方
        if (top < wa.Top + 4) top = anchorTop + 34;          // 上方空间不足 → 放到球下方
        if (left < wa.Left + 4) left = wa.Left + 4;
        if (left + w > wa.Right - 4) left = wa.Right - w - 4;
        Left = left; Top = top;
        Topmost = true;

        _autoClose.Stop();
        _autoClose.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PopupAutoCloseSeconds));
        _autoClose.Start();
    }

    /// <summary>为一条待办构建显示行（内容 + 时间 + 三按钮）。</summary>
    private UIElement BuildRow(NoteEntry e)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8)
        };

        var contentText = new TextBlock
        {
            Text = e.EditedContent ?? e.Content,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300
        };
        var timeText = new TextBlock
        {
            Text = $"提醒时间：{e.DueTime:yyyy-MM-dd HH:mm}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 6)
        };

        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var btnDone = MakeButton("已完成", Color.FromRgb(0x4C, 0xAF, 0x50));
        var btnSnooze = MakeButton("稍后提醒", Color.FromRgb(0xE0, 0xE0, 0xE0));
        var btnRead = MakeButton("已知悉", Color.FromRgb(0xE0, 0xE0, 0xE0));
        btnBar.Children.Add(btnDone);
        btnBar.Children.Add(btnSnooze);
        btnBar.Children.Add(btnRead);

        btnDone.Click += (_, _) =>
        {
            _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Done);
            RemoveRow(row);
        };
        btnSnooze.Click += (_, _) =>
        {
            // 稍后提醒：DueTime + SnoozeMinutes → 防重复 key（DueTime）变 → 必重弹
            var due = (e.DueTime ?? DateTime.Now).AddMinutes(Math.Max(1, _settings.SnoozeMinutes));
            _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, dueTime: due);
            RemoveRow(row);
        };
        btnRead.Click += (_, _) =>
        {
            _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Read);
            RemoveRow(row);
        };

        var panel = new StackPanel();
        panel.Children.Add(contentText);
        panel.Children.Add(timeText);
        panel.Children.Add(btnBar);
        row.Child = panel;
        return row;
    }

    private static Button MakeButton(string text, Color accent) => new()
    {
        Content = text,
        Margin = new Thickness(6, 0, 0, 0),
        Foreground = new SolidColorBrush(accent),
        BorderBrush = new SolidColorBrush(accent),
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        FontSize = 12
    };

    /// <summary>移除已处理的行；全部处理完 → 自动收起（此时无需再补稍后提醒）。</summary>
    private void RemoveRow(UIElement row)
    {
        PopupList.Children.Remove(row);
        _rows.RemoveAll(r => ReferenceEquals(r.Row, row));
        if (PopupList.Children.Count == 0) HidePopup();
    }

    /// <summary>
    /// v3.7：超时未操作收起 → 对所有未处理行逐条执行「稍后提醒」（DueTime + SnoozeMinutes），
    /// 与手动点击「稍后提醒」按钮完全等效，避免用户不在电脑旁时任务从此不再提醒。
    /// </summary>
    private void SnoozeAllAndHide()
    {
        try
        {
            var due = Math.Max(1, _settings.SnoozeMinutes);
            foreach (var (_, e) in _rows)
            {
                var newDue = (e.DueTime ?? DateTime.Now).AddMinutes(due);
                _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, dueTime: newDue);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FocusCapture] 弹窗超时补稍后提醒失败: {ex.Message}"); }
        HidePopup();
    }

    private void HidePopup()
    {
        _autoClose.Stop();
        Hide();
        PopupList.Children.Clear();
        _rows.Clear();
    }
}