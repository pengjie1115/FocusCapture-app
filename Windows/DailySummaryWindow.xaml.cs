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
/// v3.5（Phase 3）：每日汇总弹窗。列出所有 Type=Todo && TodoStatus=Open（未读未办，不含已读）待办。
/// 每条按钮按类型补全（两类都有「已完成」，防无法直接标记完成）：
///   - 纯待办（无 DueTime）：[已完成][已知悉][稍后查看]（稍后查看 = 仅收起不改变状态，下次汇总再弹）
///   - 有时间待办：[已完成][顺延到明天][已知悉]（已知悉 = 标 Read 挂起，与单条弹窗一致）
/// 空态文案「今天没有待处理事项」；PopupAutoCloseSeconds 秒自动收起。
/// 所有 UpdateTodo 正文一律用 EditedContent ?? Content 重建（防编辑过待办被旧正文覆盖）。
/// </summary>
public partial class DailySummaryWindow : Window
{
    private readonly NoteService _notes;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _autoClose;

    public DailySummaryWindow(NoteService notes, AppSettings settings)
    {
        InitializeComponent();
        _notes = notes;
        _settings = settings;
        _autoClose = new DispatcherTimer();
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); HidePopup(); };
    }

    /// <summary>显示汇总（items 应为 Open 待办列表），定位在悬浮球上方。</summary>
    public void ShowSummary(List<NoteEntry> items, double anchorLeft, double anchorTop)
    {
        SummaryList.Children.Clear();
        _autoClose.Stop();

        var open = items ?? new List<NoteEntry>();
        // v2（2026-08-28）：每日汇总只列「今天及以前」到期的（无 DueTime 的纯待办也算）；明天及以后的不弹（去灵感速览「未到期」档看）
        var today = DateTime.Today;
        open = open.FindAll(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open
            && (!e.DueTime.HasValue || e.DueTime.Value.Date <= today));

        EmptyText.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = open.Count > 0 ? $"今日待办汇总（{open.Count}）" : "今日待办汇总";
        foreach (var e in open) SummaryList.Children.Add(BuildRow(e));

        // 先布局拿尺寸再定位
        Show();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Arrange(new Rect(0, 0, ActualWidth, ActualHeight));
        UpdateLayout();

        var wa = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 340;
        double h = ActualHeight > 0 ? ActualHeight : 140;
        double left = anchorLeft - w / 2;
        double top = anchorTop - h - 10;
        if (top < wa.Top + 4) top = anchorTop + 34;
        if (left < wa.Left + 4) left = wa.Left + 4;
        if (left + w > wa.Right - 4) left = wa.Right - w - 4;
        Left = left; Top = top;
        Topmost = true;

        if (open.Count > 0)
        {
            _autoClose.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PopupAutoCloseSeconds));
            _autoClose.Start();
        }
    }

    /// <summary>构建一条待办行；按钮按类型补全。</summary>
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
            MaxWidth = 340
        };
        var timeText = new TextBlock
        {
            Text = e.DueTime.HasValue ? $"提醒时间:{e.DueTime:yyyy-MM-dd HH:mm}" : "无提醒时间",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 6)
        };

        var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnDone = MakeButton("已完成", Color.FromRgb(0x4C, 0xAF, 0x50));
        var btnRead = MakeButton("已知悉", Color.FromRgb(0xE0, 0xE0, 0xE0));
        var btnRight = e.DueTime.HasValue
            ? MakeButton("顺延到明天", Color.FromRgb(0xE0, 0xE0, 0xE0))
            : MakeButton("稍后查看", Color.FromRgb(0xE0, 0xE0, 0xE0));

        btnDone.Click += (_, _) =>
        {
            _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Done);
            RemoveRow(row);
        };
        if (e.DueTime.HasValue)
        {
            // 顺延到明天：dueTime + 1 天
            btnRight.Click += (_, _) =>
            {
                _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content,
                    dueTime: (e.DueTime ?? DateTime.Now).AddDays(1));
                RemoveRow(row);
            };
        }
        else
        {
            // 稍后查看 = 仅收起该行，不改变状态（下次汇总再弹）
            btnRight.Click += (_, _) => RemoveRow(row);
        }
        btnRead.Click += (_, _) =>
        {
            _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Read);
            RemoveRow(row);
        };

        btnBar.Children.Add(btnDone);
        btnBar.Children.Add(btnRight);
        btnBar.Children.Add(btnRead);

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

    /// <summary>移除已处理行；全空 → 收起（空态文案在最初就定，不再依赖剩余行）。</summary>
    private void RemoveRow(UIElement row)
    {
        SummaryList.Children.Remove(row);
        if (SummaryList.Children.Count == 0) HidePopup();
    }

    private void HidePopup()
    {
        _autoClose.Stop();
        Hide();
        SummaryList.Children.Clear();
    }

    /// <summary>v3.7：右上角 ✕ 关闭按钮 —— 手动关闭，任何状态（含空态）都可退出弹窗。</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => HidePopup();
}