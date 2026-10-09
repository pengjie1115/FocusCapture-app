using System.Windows.Input;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 迷你日历选择器：Popup 承载紧凑月历（热力图 + 月份导航 + 今天描边），
/// 点击日期格触发 DatePicked 并自动关闭。区间筛选的 📅 按钮专用（替代弹出整个 CalendarWindow）。
/// 复用 CalendarWindow 的热力渲染逻辑（LoadNoteCounts + ThemeColors 4 档热力色）。
/// </summary>
public partial class MiniCalendarPicker : UserControl
{
    private readonly NoteService _noteService;
    private readonly ThemeColors _theme;
    private DateTime _displayMonth;   // 当月 1 号
    private DateTime _selectedDate;

    /// <summary>选中日期事件（点击日期格触发，随即关闭 Popup）</summary>
    public event Action<DateTime>? DatePicked;

    public MiniCalendarPicker(NoteService noteService)
    {
        InitializeComponent();
        _noteService = noteService;
        _theme = new ThemeService().GetColors(); // 热力色接主题
    }

    /// <summary>在指定锚点（📅 按钮）下方弹出小日历</summary>
    public void Show(UIElement placementTarget, DateTime initial)
    {
        _selectedDate = initial.Date;
        _displayMonth = new DateTime(initial.Year, initial.Month, 1);
        Render();
        PickerPopup.PlacementTarget = placementTarget;
        PickerPopup.IsOpen = true;
    }

    private void Render()
    {
        MonthTitle.Text = _displayMonth.ToString("yyyy年M月");
        DayGrid.Children.Clear();

        var counts = _noteService.LoadNoteCounts(_displayMonth.Year, _displayMonth.Month);
        var todoDates = _noteService.LoadTodoDueDates(_displayMonth.Year, _displayMonth.Month);
        var daysInMonth = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);

        // 周日起始对齐
        var first = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        for (var i = 0; i < (int)first.DayOfWeek; i++)
            DayGrid.Children.Add(CreateEmptyCell());

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(_displayMonth.Year, _displayMonth.Month, day);
            // v3.7：未来且有未办待办的日期加绿色角标（与 CalendarWindow 同口径）
            var hasTodo = date > DateTime.Today && todoDates.Contains(date);
            DayGrid.Children.Add(CreateDayCell(date, counts.GetValueOrDefault(date), hasTodo));
        }

        // 补足整行，保持网格整齐
        while (DayGrid.Children.Count % 7 != 0)
            DayGrid.Children.Add(CreateEmptyCell());
    }

    private static Border CreateEmptyCell()
        => new() { Height = 34, Margin = new Thickness(1) };

    private Button CreateDayCell(DateTime date, int count, bool hasTodo = false)
    {
        // 4 档热力色（从 ThemeService 取，不硬编码）
        var (bg, fg) = count switch
        {
            0 => (FromHex(_theme.Heat0Bg), FromHex(_theme.Heat0Fg)),
            <= 2 => (FromHex(_theme.Heat1Bg), FromHex(_theme.Heat1Fg)),
            <= 5 => (FromHex(_theme.Heat2Bg), FromHex(_theme.Heat2Fg)),
            _ => (FromHex(_theme.Heat3Bg), FromHex(_theme.Heat3Fg)),
        };

        var btn = new Button
        {
            Content = date.Day,
            Height = 34,
            Margin = new Thickness(1),
            FontSize = 11,
            Background = bg,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };

        // v3.7 描边优先级：选中 > 未来有待办（绿色边框） > 今天
        if (date == _selectedDate)
        {
            btn.BorderThickness = new Thickness(2);
            btn.BorderBrush = FromHex(_theme.Accent);
        }
        else if (hasTodo)
        {
            btn.BorderThickness = new Thickness(2);
            btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        }
        else if (date == DateTime.Today)
        {
            btn.BorderThickness = new Thickness(1);
            btn.BorderBrush = FromHex(_theme.BorderColor);
        }

        btn.Click += (_, _) => SelectDate(date);
        return btn;
    }

    private void SelectDate(DateTime date)
    {
        _selectedDate = date;
        PickerPopup.IsOpen = false;              // 单选即关
        DatePicked?.Invoke(date);
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        Render();
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        Render();
    }

    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SelectDate(DateTime.Today);
    }

    private static SolidColorBrush FromHex(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
