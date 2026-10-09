using System.Windows;
using System.Windows.Input;
using FocusCapture.Services;
using FocusCapture.Services.Files;

namespace FocusCapture.Windows;

/// <summary>报告里的一条待处理记录（绑定用；IsSelected 走双向绑定）。</summary>
public sealed class CloudVerifyItem
{
    public FileMetadata Meta { get; init; } = new();

    /// <summary>是否勾选（默认全选：报告里的都是"建议处理"的）</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>标题行：文件名 + 类型</summary>
    public string Title { get; init; } = "";

    /// <summary>副行：登记时间、大小、为什么判成缺失</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// 「与云端核对」的结果报告（2026-09-27 新增）。
///
/// 这是一个**真对话框**（ShowDialog）：用户看一眼差异 → 决定要不要写回 → 关掉，生命周期很短，
/// 与"长驻面板"（灵感速览/AI 问答，那些必须用 Show()）不是一回事。
///
/// <b>两个出口语义不同，刻意分开放，别混</b>：
/// <list type="bullet">
/// <item><b>应用标注</b>：在**保留记录**的前提下标注「云端已不存在」—— 信息最全，是推荐动作。
/// Agent 之后读到该状态就会如实告诉用户"网盘里已经没有它了"。</item>
/// <item><b>移除选中记录</b>：把记录本身撤掉（打墓碑，多设备同步一并撤）。
/// <b>刻意不删本机文件</b> —— 出口只对"云端已不存在"的记录开放，
/// 那种情况下本机这份很可能是**唯一幸存的一份**，删了就真没了。</item>
/// </list>
/// </summary>
public partial class CloudVerifyWindow : Window
{
    private readonly CloudVerifyReport _report;
    private readonly List<CloudVerifyItem> _missing = new();

    public CloudVerifyWindow(CloudVerifyReport report)
    {
        InitializeComponent();
        _report = report;
        BuildContent();
    }

    private void BuildContent()
    {
        var r = _report.Result;

        SummaryText.Text =
            $"云端 {r.CloudTotal} 个文件 ／ 本地 {r.Scanned} 条记录 ／ 命中 {r.Hit} 条";

        foreach (var m in _report.MissingItems)
        {
            _missing.Add(new CloudVerifyItem
            {
                Meta = m,
                Title = $"{m.Name}",
                Detail = $"{FileTypes.Label(m.Type)}  ·  {m.CreatedAt:yyyy-MM-dd HH:mm}  ·  "
                       + $"{RootMigrationService.FormatSize(m.Size)}",
            });
        }
        MissingList.ItemsSource = _missing;

        MissingHeaderText.Text = _missing.Count == 0
            ? "云端已不存在：没有（本地记录与网盘一致）"
            : $"云端已不存在（{_missing.Count} 条）—— 勾选后可移除记录，或直接应用标注保留它们";

        if (_report.RestoredItems.Count > 0)
            MissingHeaderText.Text += $"；另有 {_report.RestoredItems.Count} 条此前标过、这次发现云端又有了（应用标注时会自动撤销）";

        CloudOnlyHeaderText.Text = _report.CloudOnlyPaths.Count == 0
            ? "云端多出：没有"
            : $"云端多出（{_report.CloudOnlyPaths.Count} 个）";
        CloudOnlyList.ItemsSource = _report.CloudOnlyPaths;

        // 没有任何可处理的记录时，把动作按钮禁掉 —— 避免"点了没反应"
        BtnApply.IsEnabled = r.HasChanges;
        BtnRemoveSelected.IsEnabled = _missing.Count > 0;

        if (!r.HasChanges && _report.CloudOnlyPaths.Count == 0)
            SummaryText.Text += "　—　没有发现任何差异。";
    }

    private void ItemCheck_Click(object sender, RoutedEventArgs e) { /* 触发绑定回写；无需额外处理 */ }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void BtnInvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _missing) it.IsSelected = !it.IsSelected;
        RefreshList();
    }

    private void SetAll(bool value)
    {
        foreach (var it in _missing) it.IsSelected = value;
        RefreshList();
    }

    /// <summary>ItemsSource 重载会换掉条目对象引用，所以只做整体刷新（列表小，代价可忽略）。</summary>
    private void RefreshList()
    {
        var snapshot = _missing.ToList();
        MissingList.ItemsSource = null;
        MissingList.ItemsSource = snapshot;
    }

    // ── 出口 1：应用标注（保留记录，只改状态）──

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var (marked, restored) = CloudVerifyService.Apply(_report);

        var parts = new List<string>();
        if (marked > 0) parts.Add($"已把 {marked} 条标为「云端已不存在」");
        if (restored > 0) parts.Add($"已撤销 {restored} 条旧标注（云端又有了）");
        var msg = parts.Count == 0 ? "没有需要写回的改动。" : string.Join("；", parts) + "。";

        MessageBox.Show(this, msg + "\n\n记录都还在，只是多了个状态说明 —— " +
                                "以后 AI 读到这些文件时会如实说「网盘里已经没有它了」，不会再让你白跑一趟。",
            "已应用", MessageBoxButton.OK, MessageBoxImage.Information);

        // 标注已写回，本窗口的差异清单随之失效 → 关掉，避免用户重复点
        Close();
    }

    // ── 出口 2：移除记录（打墓碑，不删本机文件）──

    private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _missing.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "还没有勾选任何记录。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"要把选中的 {selected.Count} 条记录从列表里移除吗？\n\n" +
                "· 只移除记录，**本机文件不会被删除**（可在「打开文件区」里找到它们）\n" +
                "· 网盘上的那份本来就已经不在了\n" +
                "· 其他设备同步后也会一并去掉这些记录",
                "移除记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var done = 0;
        foreach (var it in selected)
            if (FileRepository.RemoveRecord(it.Meta.Id)) done++;

        foreach (var it in selected) _missing.Remove(it);
        RefreshList();

        MissingHeaderText.Text = $"云端已不存在：已移除 {done} 条，剩余 {_missing.Count} 条";
        BtnRemoveSelected.IsEnabled = _missing.Count > 0;

        if (_missing.Count == 0)
            MessageBox.Show(this, $"已移除 {done} 条记录。本机文件都还在，随时可在「打开文件区」里查看。",
                "已完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Esc 关闭（所有对话框的统一习惯）。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
