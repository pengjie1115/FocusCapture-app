using System.ComponentModel;
using System.Runtime.CompilerServices;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>导入预览列表项 VM：包装 NoteEntry + IsSelected（INPC）。</summary>
public class ImportItemViewModel : INotifyPropertyChanged
{
    public NoteEntry Entry { get; }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    /// <summary>展示用时间戳字符串（无时间戳时为空）</summary>
    public string TimestampText => Entry.Timestamp == DateTime.MinValue ? "" : Entry.Timestamp.ToString("yyyy-MM-dd HH:mm");

    public string Content => Entry.Content;
    public string SourceWindow => Entry.SourceWindow;

    public ImportItemViewModel(NoteEntry entry) { Entry = entry; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>导入预览弹窗：解析结果列表（每条 CheckBox）+ 目标日期 + 确认导入。</summary>
public partial class ImportPreviewDialog : Window
{
    private List<ImportItemViewModel> _items = new();
    private readonly NoteService _noteService;

    /// <summary>ShowDialog 返回 true 时有效：用户勾选要导入的笔记 + 选的目标日期。</summary>
    public List<NoteEntry> SelectedEntries { get; private set; } = new();
    public DateTime TargetDate { get; private set; } = DateTime.Today;

    public ImportPreviewDialog(NoteImportService.ImportPreview preview, NoteService noteService)
    {
        InitializeComponent();
        _noteService = noteService;
        _items = preview.Entries.Select(e => new ImportItemViewModel(e)).ToList();
        // 订阅每个 item 的 IsSelected 变化，自动刷新"已选 N"和"导入 (N)"按钮文案
        foreach (var item in _items)
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportItemViewModel.IsSelected)) UpdateSelectionCount(); };
        PreviewList.ItemsSource = _items;
        TargetDateInput.Text = DateTime.Today.ToString("yyyy-MM-dd");
        SourcePathText.Text = $"源文件：{preview.SourcePath}";
        SummaryText.Text = $"解析成功：{preview.Entries.Count} 条（格式：{preview.Format}）";
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        var n = _items.Count(x => x.IsSelected);
        SelectedCountText.Text = $"已选 {n}/{_items.Count} 条";
        BtnOk.Content = n > 0 ? $"导入 ({n})" : "导入";
        BtnOk.IsEnabled = n > 0;
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = true;
        UpdateSelectionCount();
    }

    private void BtnInvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = !item.IsSelected;
        UpdateSelectionCount();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SelectedEntries = _items.Where(x => x.IsSelected).Select(x => x.Entry).ToList();
        TargetDate = ParseDateInput(TargetDateInput.Text) ?? DateTime.Today;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }

    // ── 2026-08-15：目标日期 TextBox 处理（同 CalendarWindow 模式） ──

    private static DateTime? ParseDateInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return d.Date;
        return null;
    }

    private void TargetDateInput_LostFocus(object sender, RoutedEventArgs e)
    {
        // 失焦时把 TextBox 内容规范化（解析失败回退到今天）
        var d = ParseDateInput(TargetDateInput.Text) ?? DateTime.Today;
        TargetDateInput.Text = d.ToString("yyyy-MM-dd");
    }

    private void TargetDateInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            TargetDateInput_LostFocus(sender, new RoutedEventArgs());
        }
    }

    private void BtnPickTargetDate_Click(object sender, RoutedEventArgs e)
    {
        // v3.8：CalendarWindow 退役，改用单选即关的 MiniCalendarPicker（与旧单日选择行为一致）
        var initial = ParseDateInput(TargetDateInput.Text) ?? DateTime.Today;
        var picker = new MiniCalendarPicker(_noteService);
        picker.DatePicked += d => TargetDateInput.Text = d.ToString("yyyy-MM-dd");
        picker.Show(sender as Button ?? BtnPickTargetDate, initial);
    }
}