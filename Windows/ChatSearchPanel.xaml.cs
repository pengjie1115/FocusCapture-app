using System.Collections.ObjectModel;
using System.Windows.Threading;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>搜索词历史胶囊视图（面板第③区未输入关键词时显示）。</summary>
public sealed class SearchTermVm
{
    public string Term { get; }
    public SearchTermVm(string term) => Term = term;
}

/// <summary>全局搜索结果条目视图（供 XAML 绑定）。</summary>
public sealed class SearchResultVm
{
    public string Title { get; }
    public string TimeText { get; }
    public string Snippet { get; }
    public string FilePath { get; }
    public string SessionId { get; }

    public SearchResultVm(ChatSearchHit hit)
    {
        Title = string.IsNullOrWhiteSpace(hit.Title) ? hit.Preview : hit.Title;
        TimeText = hit.SavedAt.ToString("MM-dd HH:mm");
        Snippet = hit.Snippet;
        FilePath = hit.FilePath;
        SessionId = hit.SessionId;
    }
}

/// <summary>
/// 全局搜索面板（2026-09-25）：原独立窗体 ChatSearchWindow 改造为 AIDialogWindow 内的居中覆盖层。
///
/// 为什么搬进主窗（用户拍板）：面板要居中浮在 AI 问答窗口正中、面板以外区域毛玻璃模糊 ——
/// 独立窗体做不到模糊宿主内容，覆盖层 + 主内容 BlurEffect 才行。独立窗体不挤占对话区、关掉即走的
/// 原有优点由「点面板外/Esc/× 即关」保持。
///
/// 三段结构：头部（搜索历史 + 清空 + ×）/ 搜索框 / 列表区两态：
/// 未输入 = 搜索词历史胶囊（2026-09-25 由「最近会话列表」改版，用户拍板：面板该记的是搜过的词，
/// 胶囊流式排布，点胶囊=再搜，×=删该条，头部「清空」=一键清）；输入后 = 全文搜索结果（防抖 280ms）。
/// 搜索在后台线程（ChatSearchService.Search 同步阻塞 + 磁盘 IO），结果经 Dispatcher 回 UI。
/// </summary>
public partial class ChatSearchPanel : UserControl
{
    private readonly DispatcherTimer _debounce;
    private string _lastQuery = "";
    private AIDialogWindow _owner = null!;

    /// <summary>宿主窗口（XAML 常驻实例化走默认构造，owner 由 AIDialogWindow 构造时注入）。</summary>
    public AIDialogWindow OwnerWindow { set => _owner = value; }

    public ObservableCollection<SearchResultVm> Results { get; } = new();
    public ObservableCollection<SearchTermVm> HistoryTerms { get; } = new();

    /// <summary>XAML 直接实例化必须留默认构造；此时 owner 未定，回调方法在 owner 注入前不会被触发。</summary>
    public ChatSearchPanel()
    {
        InitializeComponent();
        HistoryList.ItemsSource = HistoryTerms;
        ResultList.ItemsSource = Results;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = DoSearchAsync(); };
    }

    /// <summary>面板每次打开时刷新搜索词历史（别处删改后不至于显示旧数据）。</summary>
    public void ReloadHistory()
    {
        HistoryTerms.Clear();
        foreach (var term in ChatSearchHistoryStore.Load())
            HistoryTerms.Add(new SearchTermVm(term));
        ShowHistoryView();
    }

    /// <summary>重新打开面板时清空上次的输入，回到搜索历史视图。</summary>
    public void ResetInput()
    {
        SearchBox.Text = "";
        _lastQuery = "";
        _debounce.Stop();
        Results.Clear();
        ShowHistoryView();
    }

    public void FocusSearchBox() => SearchBox.Focus();

    private void ShowHistoryView()
    {
        HistoryList.Visibility = Visibility.Visible;
        ResultList.Visibility = Visibility.Collapsed;
        EmptyHint.Text = "暂无搜索记录";
        EmptyHint.Visibility = HistoryTerms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchingHint.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _lastQuery = q;
        _debounce.Stop();
        if (q.Length == 0)
        {
            Results.Clear();
            ShowHistoryView();
            return;
        }
        _debounce.Start();
    }

    private async Task DoSearchAsync()
    {
        var q = _lastQuery;
        if (q.Length == 0) return;

        // 搜一次记一条：真正执行过的词才入历史（防抖确认后的词，不是打字过程）。纯本机文件。
        ChatSearchHistoryStore.Record(q);

        SearchingHint.Visibility = Visibility.Visible;
        // 铁律：ChatSearchService.Search 同步阻塞 + 磁盘 IO，必须在后台线程，否则卡住 UI 消息循环
        var hits = await Task.Run(() => ChatSearchService.Search(q, null));

        HistoryList.Visibility = Visibility.Collapsed;
        ResultList.Visibility = Visibility.Visible;
        Results.Clear();
        foreach (var h in hits)
            Results.Add(new SearchResultVm(h));

        EmptyHint.Text = "未找到相关消息";
        EmptyHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchingHint.Visibility = Visibility.Collapsed;
    }

    private void HistoryChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SearchTermVm vm }) return;
        // 点胶囊 = 用该词再搜一次：直接填框，TextChanged 自会走防抖 → DoSearchAsync
        SearchBox.Text = vm.Term;
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void HistoryChipRemove_Click(object sender, MouseButtonEventArgs e)
    {
        // × 的点击必须在这里掐断冒泡，否则同一次点击会落到胶囊 Border 上变成"搜索"
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: SearchTermVm vm }) return;
        ChatSearchHistoryStore.Remove(vm.Term);
        HistoryTerms.Remove(vm);
        if (HistoryTerms.Count == 0 && ResultList.Visibility != Visibility.Visible)
            ShowHistoryView();   // 删空了把「暂无搜索记录」提示亮出来
    }

    private void BtnClearHistory_Click(object sender, MouseButtonEventArgs e)
    {
        ChatSearchHistoryStore.Clear();
        HistoryTerms.Clear();
        if (ResultList.Visibility != Visibility.Visible) ShowHistoryView();
    }

    private void ResultRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SearchResultVm vm }) return;
        _owner.OpenSessionFromSearchPanel(vm.FilePath, _lastQuery);
    }

    private void BtnClose_Click(object sender, MouseButtonEventArgs e) => _owner.CloseSearchOverlay();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _owner.CloseSearchOverlay();
    }
}
