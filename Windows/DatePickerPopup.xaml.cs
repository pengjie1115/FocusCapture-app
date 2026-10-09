using System.Windows.Input;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>时间筛选档位：6 个固定周期 + 自定义</summary>
public enum QuickTimePreset
{
    Today, Yesterday, Last7Days, Last30Days, ThisMonth, LastMonth, Custom
}

/// <summary>一次时间选择的结果（预设名 + 起止日期；单日时 Start == End）</summary>
public record TimeFilterSelection(QuickTimePreset Preset, DateTime Start, DateTime End);

/// <summary>
/// 时间筛选弹层（v3.8，替代灵感速览的 CalendarWindow 入口）：
/// 左列 7 个选项常驻（今天/昨天/近 7 天/近 30 天/本月/上月/自定义），点预设即选即关；
/// 点「自定义」展开右列双月日历——第一击选起始、第二击选终止（同日两击 = 单日），
/// 悬停实时预览区间。日期格用小圆点标注：绿色 = 当天笔记数（深浅分档），橙色 = 有未到期的待办。
/// 组件自包含（输入 anchor + 当前状态，输出 Selected 事件），供后续可组装标题栏直接复用。
/// </summary>
public partial class DatePickerPopup : UserControl
{
    private readonly NoteService _noteService;
    private readonly ThemeColors _theme;

    private QuickTimePreset _preset = QuickTimePreset.Today;   // 当前回显档位
    private DateTime _selStart = DateTime.Today;               // 当前回显区间（自定义档用）
    private DateTime _selEnd = DateTime.Today;
    private DateTime? _pendingStart;                           // 自定义第一击（等待第二击）
    private DateTime? _hoverDate;                              // 悬停日期（区间预览用）
    private DateTime _leftMonth;                               // 左侧月历的当月 1 号
    private FrameworkElement? _anchor;
    private Window? _hostWindow;

    /// <summary>单元格记录：渲染时建一次，选区变化时只重刷样式不重读数据</summary>
    private readonly List<DayCell> _cells = new();

    private sealed record DayCell(DateTime Date, Button Button);

    /// <summary>用户完成一次选择（预设点击或自定义区间两击）时触发；自定义只展开日历不触发</summary>
    public event Action<TimeFilterSelection>? Selected;

    public bool IsOpen => PickerPopup.IsOpen;

    public DatePickerPopup(NoteService noteService)
    {
        InitializeComponent();
        _noteService = noteService;
        _theme = new ThemeService().GetColors();
        BuildPresetMenu();
    }

    // ── 对外打开/关闭 ──

    /// <summary>在锚点控件下方弹出。preset/start/end 为当前生效筛选，用于菜单勾选与日历回显。</summary>
    public void Open(FrameworkElement anchor, QuickTimePreset preset, DateTime start, DateTime end)
    {
        _anchor = anchor;
        _preset = preset;
        _selStart = start.Date;
        _selEnd = end.Date;
        _pendingStart = null;
        _hoverDate = null;
        _leftMonth = FirstOfMonth(preset == QuickTimePreset.Custom ? start : DateTime.Today);

        CalendarPanel.Visibility = preset == QuickTimePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        RefreshMenuChecks();
        if (preset == QuickTimePreset.Custom) RenderCalendars();

        PickerPopup.PlacementTarget = anchor;
        PickerPopup.Placement = PlacementMode.Bottom;
        PickerPopup.IsOpen = true;

        // 外击/宿主窗口失活自动收起（防重入：先解再挂）
        _hostWindow = Window.GetWindow(anchor);
        if (_hostWindow != null)
        {
            Mouse.RemoveMouseDownHandler(_hostWindow, OnHostMouseDownOutside);
            Mouse.AddMouseDownHandler(_hostWindow, OnHostMouseDownOutside);
            _hostWindow.Deactivated -= OnHostDeactivated;
            _hostWindow.Deactivated += OnHostDeactivated;
        }
    }

    public void Close()
    {
        PickerPopup.IsOpen = false;
        if (_hostWindow != null)
        {
            Mouse.RemoveMouseDownHandler(_hostWindow, OnHostMouseDownOutside);
            _hostWindow.Deactivated -= OnHostDeactivated;
            _hostWindow = null;
        }
    }

    private void OnHostMouseDownOutside(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            (IsDescendantOf(src, _anchor) || IsDescendantOf(src, PickerPopup.Child)))
            return;
        Close();
    }

    private void OnHostDeactivated(object? sender, EventArgs e) => Close();

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        var cur = node;
        while (cur != null)
        {
            if (cur == ancestor) return true;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    // ── 预设档位计算（静态：QuickViewWindow 的按钮文案也复用） ──

    /// <summary>预设档位对应的日期区间（Custom 无固定区间，返回今天）</summary>
    public static (DateTime Start, DateTime End) GetPresetRange(QuickTimePreset preset)
    {
        var today = DateTime.Today;
        return preset switch
        {
            QuickTimePreset.Today => (today, today),
            QuickTimePreset.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            QuickTimePreset.Last7Days => (today.AddDays(-6), today),
            QuickTimePreset.Last30Days => (today.AddDays(-29), today),
            QuickTimePreset.ThisMonth => (FirstOfMonth(today), FirstOfMonth(today).AddMonths(1).AddDays(-1)),
            QuickTimePreset.LastMonth => (FirstOfMonth(today).AddMonths(-1), FirstOfMonth(today).AddDays(-1)),
            _ => (today, today),
        };
    }

    /// <summary>由起止日期反推档位；匹配不上任何预设 = Custom</summary>
    public static QuickTimePreset MatchPreset(DateTime start, DateTime end)
    {
        start = start.Date; end = end.Date;
        foreach (var p in new[]
        {
            QuickTimePreset.Today, QuickTimePreset.Yesterday, QuickTimePreset.Last7Days,
            QuickTimePreset.Last30Days, QuickTimePreset.ThisMonth, QuickTimePreset.LastMonth,
        })
        {
            var (s, e) = GetPresetRange(p);
            if (s == start && e == end) return p;
        }
        return QuickTimePreset.Custom;
    }

    /// <summary>档位显示文案（时间按钮直接展示当前筛选状态）</summary>
    public static string GetLabel(QuickTimePreset preset, DateTime start, DateTime end) => preset switch
    {
        QuickTimePreset.Today => "今天",
        QuickTimePreset.Yesterday => "昨天",
        QuickTimePreset.Last7Days => "近 7 天",
        QuickTimePreset.Last30Days => "近 30 天",
        QuickTimePreset.ThisMonth => "本月",
        QuickTimePreset.LastMonth => "上月",
        _ when start.Date == end.Date => start.ToString("yyyy-MM-dd"),
        _ when start.Year == end.Year => $"{start:MM-dd} ~ {end:MM-dd}",
        _ => $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}",
    };

    private static string GetPresetName(QuickTimePreset preset) => preset switch
    {
        QuickTimePreset.Today => "今天",
        QuickTimePreset.Yesterday => "昨天",
        QuickTimePreset.Last7Days => "近 7 天",
        QuickTimePreset.Last30Days => "近 30 天",
        QuickTimePreset.ThisMonth => "本月",
        QuickTimePreset.LastMonth => "上月",
        _ => "自定义",
    };

    private static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    // ── 左列预设菜单 ──

    private void BuildPresetMenu()
    {
        foreach (var p in new[]
        {
            QuickTimePreset.Today, QuickTimePreset.Yesterday, QuickTimePreset.Last7Days,
            QuickTimePreset.Last30Days, QuickTimePreset.ThisMonth, QuickTimePreset.LastMonth,
            QuickTimePreset.Custom,
        })
        {
            var preset = p;
            var btn = new Button { Style = (Style)FindResource("PresetMenuItem"), Tag = preset };
            btn.Click += (_, _) => OnPresetClicked(preset);
            PresetMenu.Children.Add(btn);
        }
        RefreshMenuChecks();
    }

    private void RefreshMenuChecks()
    {
        foreach (var item in PresetMenu.Children.OfType<Button>())
        {
            if (item.Tag is not QuickTimePreset p) continue;
            var check = p == _preset;
            item.Content = check ? $"✓  {GetPresetName(p)}" : GetPresetName(p);
            item.Foreground = check
                ? FromHex(_theme.Accent)
                : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        }
    }

    private void OnPresetClicked(QuickTimePreset preset)
    {
        if (preset == QuickTimePreset.Custom)
        {
            // 自定义：只展开日历列，不触发选择、不关闭
            _preset = QuickTimePreset.Custom;
            CalendarPanel.Visibility = Visibility.Visible;
            RefreshMenuChecks();
            RenderCalendars();
            return;
        }

        var (start, end) = GetPresetRange(preset);
        Selected?.Invoke(new TimeFilterSelection(preset, start, end));
        Close();
    }

    // ── 右列双月日历 ──

    private void BtnPrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _leftMonth = _leftMonth.AddMonths(-1);
        RenderCalendars();
    }

    private void BtnNextMonth_Click(object sender, RoutedEventArgs e)
    {
        _leftMonth = _leftMonth.AddMonths(1);
        RenderCalendars();
    }

    private void RenderCalendars()
    {
        _cells.Clear();
        MonthTitleLeft.Text = _leftMonth.ToString("yyyy年M月");
        MonthTitleRight.Text = _leftMonth.AddMonths(1).ToString("yyyy年M月");

        RenderMonth(MonthLeftHost, _leftMonth);
        RenderMonth(MonthRightHost, _leftMonth.AddMonths(1));
        UpdateCellVisuals();
    }

    private void RenderMonth(StackPanel host, DateTime month)
    {
        host.Children.Clear();

        // 星期表头（周日起始，与旧日历同口径）
        var header = new UniformGrid { Columns = 7 };
        foreach (var w in new[] { "日", "一", "二", "三", "四", "五", "六" })
            header.Children.Add(new TextBlock
            {
                Text = w,
                Width = 28,
                Height = 22,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 10,
            });
        host.Children.Add(header);

        var counts = _noteService.LoadNoteCounts(month.Year, month.Month);
        var todoDates = _noteService.LoadTodoDueDates(month.Year, month.Month);
        var first = new DateTime(month.Year, month.Month, 1);

        var grid = new UniformGrid { Columns = 7 };
        for (var i = 0; i < (int)first.DayOfWeek; i++)
            grid.Children.Add(new FrameworkElement { Width = 28, Height = 38, Margin = new Thickness(1) });

        for (var day = 1; day <= DateTime.DaysInMonth(month.Year, month.Month); day++)
        {
            var date = first.AddDays(day - 1);
            var count = counts.GetValueOrDefault(date);
            // v3.7 口径：只有未来的未办待办才算「未到期」
            var hasTodo = date > DateTime.Today && todoDates.Contains(date);

            var btn = BuildDayCell(date, count, hasTodo);
            _cells.Add(new DayCell(date, btn));
            grid.Children.Add(btn);
        }
        // 补足整行保持网格整齐
        while (grid.Children.Count % 7 != 0)
            grid.Children.Add(new FrameworkElement { Width = 28, Height = 38, Margin = new Thickness(1) });
        host.Children.Add(grid);
    }

    private Button BuildDayCell(DateTime date, int count, bool hasTodo)
    {
        // 圆点：绿色 = 笔记数（深浅分档），橙色 = 未到期待办；两者并存并排
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 8,
        };
        if (count > 0)
            dots.Children.Add(MakeDot(FromHex(_theme.Accent), count switch { 1 => 0.45, <= 4 => 0.75, _ => 1.0 }));
        if (hasTodo)
            dots.Children.Add(MakeDot(new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)), 1.0));

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(),
            TextAlignment = TextAlignment.Center,
            FontSize = 11,
            Height = 16,
        });
        content.Children.Add(dots);

        var btn = new Button
        {
            Width = 28,
            Height = 38,
            Margin = new Thickness(1),
            Content = content,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = date,
        };
        btn.ToolTip = BuildTooltip(count, hasTodo);
        btn.Click += (_, _) => OnDayClicked(date);
        btn.MouseEnter += (_, _) => { _hoverDate = date; UpdateCellVisuals(); };
        btn.MouseLeave += (_, _) => { if (_hoverDate == date) { _hoverDate = null; UpdateCellVisuals(); } };
        return btn;
    }

    private static Border MakeDot(Brush color, double opacity) => new()
    {
        Width = 4,
        Height = 4,
        CornerRadius = new CornerRadius(2),
        Background = color,
        Opacity = opacity,
        Margin = new Thickness(1, 0, 1, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static object? BuildTooltip(int count, bool hasTodo)
    {
        var tip = "";
        if (count > 0) tip += $"笔记 {count} 篇";
        if (hasTodo) tip += (tip.Length == 0 ? "" : "\n") + "有未到期的待办";
        return tip.Length == 0 ? null : tip;
    }

    private void OnDayClicked(DateTime date)
    {
        if (!_pendingStart.HasValue)
        {
            // 第一击：选起始，等待第二击
            _pendingStart = date;
            UpdateCellVisuals();
            return;
        }

        // 第二击：同日 = 单日，异日 = 区间（先后顺序自动交换）
        var start = _pendingStart.Value;
        var end = date;
        _pendingStart = null;
        if (end < start) (start, end) = (end, start);

        _selStart = start;
        _selEnd = end;
        Selected?.Invoke(new TimeFilterSelection(QuickTimePreset.Custom, start, end));
        Close();
    }

    /// <summary>只重刷单元格的选区样式（实心起止 / 区间铺底 / 今天描边 / 未来灰字），不重读数据</summary>
    private void UpdateCellVisuals()
    {
        // 展示中的区间：第一击后 = 起始~悬停预览；否则 = 已确认选择（仅自定义档回显）
        DateTime rs, re;
        bool hasRange;
        if (_pendingStart.HasValue)
        {
            rs = _pendingStart.Value;
            re = _hoverDate ?? rs;
            if (re < rs) (rs, re) = (re, rs);
            hasRange = true;
        }
        else if (_preset == QuickTimePreset.Custom)
        {
            rs = _selStart;
            re = _selEnd;
            hasRange = true;
        }
        else
        {
            hasRange = false;
            rs = re = default;
        }

        var accent = ((SolidColorBrush)FromHex(_theme.Accent)).Color;
        var accentSolid = new SolidColorBrush(accent);
        var rangeFill = new SolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B));
        var whiteBrush = Brushes.White;

        foreach (var cell in _cells)
        {
            var d = cell.Date;
            var btn = cell.Button;

            var isFuture = d > DateTime.Today;
            var bg = Brushes.Transparent;
            var fg = isFuture
                ? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
                : FromHex(_theme.TextColor);
            btn.BorderThickness = new Thickness(0);

            if (hasRange && d >= rs && d <= re)
                bg = rangeFill;
            if (hasRange && (d == rs || d == re))
            {
                bg = accentSolid;
                fg = whiteBrush;
            }
            else if (d == DateTime.Today)
            {
                btn.BorderThickness = new Thickness(1);
                btn.BorderBrush = FromHex(_theme.BorderColor);
            }

            btn.Background = bg;
            btn.Foreground = fg;
        }
    }

    private static SolidColorBrush FromHex(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
