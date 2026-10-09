using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// 「获取可用模型」的多选窗（2026-09-23）：搜索 + 全选 + 勾选后添加。
///
/// <para>列表行**在 code-behind 里建、搜索时只切 Visibility 不重建** ——
/// 重建会换掉行对象引用，用户已经勾上的状态会跟着丢（本项目在列表就地编辑上踩过这个坑）。</para>
/// </summary>
public partial class AiModelPickerWindow : Window
{
    private readonly List<ParsedModel> _models;
    private readonly List<(ParsedModel Model, CheckBox Box, FrameworkElement Row)> _rows = new();
    private bool _allSelected;

    /// <summary>用户勾选并确认的模型；取消时为 null。</summary>
    public List<ParsedModel>? Selected { get; private set; }

    public AiModelPickerWindow(IReadOnlyList<ParsedModel> models)
    {
        _models = models.ToList();
        InitializeComponent();
        DarkTitleBar.Enable(this);

        foreach (var m in _models) AddRow(m);
        UpdateCount();
    }

    private void AddRow(ParsedModel model)
    {
        var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center };

        // 两行内容：显示名（主体）+ 模型 ID（灰色小字）—— 显示名可能重名，ID 才是唯一标识
        var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = model.DisplayName,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.Equals(model.DisplayName, model.Id, StringComparison.Ordinal))
        {
            text.Children.Add(new TextBlock
            {
                Text = model.Id,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        box.Content = text;

        var row = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Child = box,
        };
        box.Checked += (_, _) => UpdateCount();
        box.Unchecked += (_, _) => UpdateCount();

        ModelList.Children.Add(row);
        _rows.Add((model, box, row));
    }

    /// <summary>按关键词过滤：**只切 Visibility**，不重建行（重建会丢勾选状态）。</summary>
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchInput.Text.Trim();
        var visible = 0;
        foreach (var (model, _, row) in _rows)
        {
            var hit = q.Length == 0
                || model.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || model.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            if (hit) visible++;
        }
        EmptyHint.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void BtnToggleAll_Click(object sender, RoutedEventArgs e)
    {
        _allSelected = !_allSelected;
        BtnToggleAll.Content = _allSelected ? "全不选" : "全选";
        // 只作用于**当前可见**的行 —— 用户搜出 3 个再点全选，意图显然是这 3 个
        foreach (var (_, box, row) in _rows)
            if (row.Visibility == Visibility.Visible) box.IsChecked = _allSelected;
    }

    private void UpdateCount()
    {
        var total = _rows.Count;
        var shown = _rows.Count(r => r.Row.Visibility == Visibility.Visible);
        var picked = _rows.Count(r => r.Box.IsChecked == true);
        CountText.Text = shown == total
            ? $"共 {total} 个，已勾选 {picked} 个"
            : $"匹配 {shown} / 共 {total} 个，已勾选 {picked} 个";
        BtnAdd.IsEnabled = picked > 0;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Selected = null;
        DialogResult = false;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        Selected = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Model).ToList();
        DialogResult = true;
    }
}
