using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows.Controls;

/// <summary>侧边栏的纯分区标题（置顶 / 最近）—— 不可点击，只是把列表分组。</summary>
public sealed class SidebarSectionHeader
{
    public string Name { get; }
    public SidebarSectionHeader(string name) => Name = name;
}

/// <summary>
/// 侧边栏的可点击分组行。收藏是内置保留分区，也走这一支（靠 IsFavorite 区分）。
/// 认的始终是 <see cref="GroupId"/> 而不是名字 —— 用户可以把任意分组改名叫「收藏」。
/// </summary>
public sealed class SidebarGroupHeader
{
    public string Name { get; }
    public string GroupId { get; }
    public bool IsFavorite { get; }
    public bool IsPinned { get; }

    public SidebarGroupHeader(string name, string groupId, bool isFavorite, bool isPinned = false)
    {
        Name = name;
        GroupId = groupId;
        IsFavorite = isFavorite;
        IsPinned = isPinned;
    }

    /// <summary>收藏带一个记号，免得与用户自己建的同名分组混淆</summary>
    public string DisplayName => IsFavorite ? "★  " + Name : Name;

    public string Tooltip => IsFavorite
        ? "收藏的对话（收藏是分组的另一种形式：一条会话要么在某分组、要么在收藏）"
        : $"进入分组「{Name}」，在里面说的指令默认都对着这个分组";
}

/// <summary>分组三点菜单的动作（宿主执行）</summary>
public enum GroupMenuAction
{
    Rename,   // 重命名
    Pin,      // 置顶此分组
    Delete,   // 删除此分组
}

/// <summary>
/// AI 问答侧边栏（2026-09-23 重构，取代历史抽屉）。
///
/// 与旧抽屉的两条关键行为差异（都是用户拍板的）：
/// ① **已分组的会话不出现在「最近」里** —— 只有点进分组才看得到。旧抽屉把所有会话平铺分区，找不到重点。
/// ② **置顶是叠加的** —— 置顶会话既在「置顶」区、也在它所属的分组里（旧实现会把它从分组里摘出去）。
///
/// 纪律同旧抽屉：数据由宿主经 <see cref="Load"/> 注入，业务动作一律抛事件给宿主执行，
/// 本控件不直接改会话文件。
/// </summary>
public partial class ChatSidebar : UserControl
{
    /// <summary>扁平列表（分区头 / 分组行 / 会话条目混排，靠隐式 DataTemplate 区分）</summary>
    public ObservableCollection<object> Items { get; } = new();

    /// <summary>点「新对话」</summary>
    public event Action? NewChatRequested;

    /// <summary>点「新分组」</summary>
    public event Action? NewGroupRequested;

    /// <summary>点某条会话</summary>
    public event Action<HistoryItemViewModel>? SessionSelected;

    /// <summary>点某个分组行（进入分组视图；收藏也走这里）</summary>
    public event Action<SidebarGroupHeader>? GroupSelected;

    /// <summary>分组三点菜单</summary>
    public event Action<SidebarGroupHeader, GroupMenuAction>? GroupAction;

    /// <summary>会话三点菜单（context：Group = 目标分组 Id、Export = 格式名，其余 null）</summary>
    public event Action<HistoryItemViewModel, ChatItemAction, string?>? SessionAction;

    /// <summary>点「回收站」</summary>
    public event Action? RecycleBinRequested;

    public ChatSidebar()
    {
        InitializeComponent();
        ListHost.ItemsSource = Items;
    }

    // ── 相对时间每分钟刷新（2026-09-26 用户要求）──
    // 侧边栏改成"刚刚 / N分钟前 / N小时前 / N天前"后必须自己走表：不重算的话侧边栏一直开着，
    // 时间会永远停在打开那一刻（"刚刚"永远是"刚刚"）。
    // 停表时机：控件卸载、或侧边栏收起（宽度 0，列表根本不在图上）—— 收起态白遍历几十条没意义。
    // 刻意**不重建列表**：重建会打断悬停态与批量选中高亮，只借 ViewModel 自带的 INPC 改文案。
    private DispatcherTimer? _relativeTimer;

    private void ChatSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        _relativeTimer ??= new DispatcherTimer(
            TimeSpan.FromMinutes(1), DispatcherPriority.Background, OnRelativeTimeTick, Dispatcher);
        _relativeTimer.Start();
    }

    private void ChatSidebar_Unloaded(object sender, RoutedEventArgs e) => _relativeTimer?.Stop();

    private void OnRelativeTimeTick(object? sender, EventArgs e)
    {
        if (!IsVisible || ActualWidth <= 0) return;
        var now = DateTime.Now;
        foreach (var vm in Items.OfType<HistoryItemViewModel>()) vm.RefreshRelativeTime(now);
    }

    /// <summary>刷新侧边栏（分区构造见 <see cref="BuildSections"/>）</summary>
    public void Load(IEnumerable<SessionSummary> sessions, IReadOnlyList<ChatGroup> groups)
    {
        _lastSessions = sessions.ToList();   // 批量模式重刷要复用同一份会话集
        var sections = BuildSections(sessions, groups);

        Items.Clear();
        foreach (var entry in sections)
        {
            if (entry is HistoryItemViewModel vm)
            {
                vm.IsBatchMode = _batchMode;                          // 批量态必须带上：复选框显隐、三点让位都靠它
                vm.IsSelected = _batchMode && _batchSelected.Contains(vm.Id);
            }
            Items.Add(entry);
        }
    }

    /// <summary>
    /// 分区构造（**纯逻辑，刻意抽成 static 以便被慢层检查点直接守住**）。
    /// 分区顺序：收藏 · 各分组 → 置顶 → 最近（仅未分组且未置顶）。
    ///
    /// 为什么必须有检查点：这几条规则错了都不会报错，只会表现为
    /// 「某条会话莫名从列表里消失」或「同一条在同屏出现两遍」—— 靠肉眼很难发现漏了谁。
    /// </summary>
    public static List<object> BuildSections(IEnumerable<SessionSummary> sessions, IReadOnlyList<ChatGroup> groups)
    {
        var items = sessions.Select(s => new HistoryItemViewModel(
            s.Id, s.FilePath, s.SavedAt, AiModeText.Get(s.Mode),
            // 没重命名过就用预览当标题 —— 旧抽屉靠 XAML 里的空值回落，这里在源头兜掉，
            // 免得侧边栏出现一行没有文字的条目（点了也不知道是什么）。
            string.IsNullOrWhiteSpace(s.Title) ? s.Preview : s.Title,
            s.Pinned, s.GroupId, "", s.Preview)).ToList();

        var result = new List<object>();

        // ① 收藏恒在最前；其后是**置顶的分组**（2026-09-24：用户从 WorkBuddy「置顶任务」借鉴，
        //    点「置顶此分组」= 分组行本身浮到最前，而不是组内会话置顶 —— 旧语义空分组点了零反馈）；
        //    再后是普通分组（顺序跟清单走，与分组管理办法一致）
        var favorite = groups.FirstOrDefault(g => ChatGroupStore.IsFavorite(g.Id));
        if (favorite != null)
            result.Add(new SidebarGroupHeader(favorite.Name, favorite.Id, true));

        var userGroups = groups.Where(g => !ChatGroupStore.IsFavorite(g.Id)).ToList();
        foreach (var g in userGroups.Where(g => g.Pinned))
            result.Add(new SidebarGroupHeader(g.Name, g.Id, false, isPinned: true));
        foreach (var g in userGroups.Where(g => !g.Pinned))
            result.Add(new SidebarGroupHeader(g.Name, g.Id, false));

        // ② 置顶区（跨分组；置顶会话在所属分组里**也会**再出现一次 —— 这是用户要的叠加语义）
        var pinned = items.Where(i => i.Pinned).ToList();
        if (pinned.Count > 0)
        {
            result.Add(new SidebarSectionHeader("置顶"));
            result.AddRange(pinned);
        }

        // ③ 最近：只列「未分组 **且** 未置顶」的会话
        // 已分组的会话刻意不在这里出现（用户 2026-09-23 拍板：分组里的会话只有点进分组才看得到）；
        // 置顶的未分组会话也不重复列 —— 它已经在上面「置顶」区里了，同屏出现两遍只是噪音。
        var recent = items.Where(i => !i.Pinned && string.IsNullOrEmpty(i.GroupId)).ToList();
        if (recent.Count > 0)
        {
            result.Add(new SidebarSectionHeader("最近"));
            result.AddRange(recent);
        }

        return result;
    }

    /// <summary>底部用户区：昵称 + 头像（头像为空则用昵称首字画色块）</summary>
    public void SetUser(string? nickname, BitmapImage? avatar)
    {
        var name = string.IsNullOrWhiteSpace(nickname) ? "我" : nickname.Trim();
        UserNickname.Text = name;
        AvatarInitial.Text = name[..1].ToUpperInvariant();

        if (avatar != null)
        {
            AvatarImage.Source = avatar;
            AvatarImage.Visibility = Visibility.Visible;
            AvatarInitial.Visibility = Visibility.Collapsed;
        }
        else
        {
            AvatarImage.Source = null;
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarInitial.Visibility = Visibility.Visible;
        }
    }

    // ── 交互 ──

    private void BtnNewChat_Click(object sender, RoutedEventArgs e) => NewChatRequested?.Invoke();

    private void BtnNewGroup_Click(object sender, RoutedEventArgs e) => NewGroupRequested?.Invoke();

    private void BtnRecycleBin_Click(object sender, RoutedEventArgs e) => RecycleBinRequested?.Invoke();

    // ── 批量操作（2026-09-24 找回：旧抽屉的"批量操作"入口在换侧边栏时被弄丢，用户点名要回）──
    // 交互（用户拍板，2026-09-26 改版）：会话三点菜单里点「批量管理」进入多选；此后点会话 = 选中/取消（不再打开）；
    // 行首出**复选框**（不再用标题前缀"✓"），底部操作条 = 全选 + 已选计数 + 取消/移入分组/删除（与分组视图同款）。

    private bool _batchMode;
    private readonly HashSet<string> _batchSelected = new(StringComparer.Ordinal);
    private List<SessionSummary> _lastSessions = new();

    /// <summary>会话三点菜单里点「批量管理」</summary>
    public event Action? BatchModeRequested;
    /// <summary>批量删除（宿主执行，参数 = 选中的会话）</summary>
    public event Action<IReadOnlyList<HistoryItemViewModel>>? BatchDeleteRequested;
    /// <summary>批量移入分组（context = 目标分组 Id）</summary>
    public event Action<IReadOnlyList<HistoryItemViewModel>, string>? BatchGroupRequested;

    /// <summary>进入多选模式。数据沿用最近一次 Load 的会话集（宿主进入前会先刷新）。</summary>
    public void EnterBatchMode()
    {
        _batchMode = true;
        _batchSelected.Clear();
        BatchSelectAll.IsChecked = false;
        BatchBar.Visibility = Visibility.Visible;
        UpdateBatchCount();
        ReloadLastSessions();   // 补挂 IsBatchMode：不重刷的话复选框不出现、三点也不让位
    }

    /// <summary>退出多选模式并重刷列表（去复选框、恢复三点）</summary>
    public void ExitBatchMode()
    {
        _batchMode = false;
        _batchSelected.Clear();
        BatchSelectAll.IsChecked = false;
        BatchBar.Visibility = Visibility.Collapsed;
        ReloadLastSessions();
    }

    /// <summary>快照专用：批量模式下按 Id 选中（模拟行点击，不动真实数据）；
    /// 正常入口是行点击里的 _batchSelected 翻转（ChatSidebar.xaml.cs:276）。</summary>
    internal void SelectBatchForSnapshot(IEnumerable<string> ids)
    {
        if (!_batchMode) return;
        foreach (var id in ids) _batchSelected.Add(id);
        ReloadLastSessions();
        UpdateBatchCount();
    }

    /// <summary>计数文案（2026-09-26 用户拍板：不设上限，只显示「已选 n」；
    /// 原「点会话选中，再选下面的操作」提示语按用户要求删除）。</summary>
    private void UpdateBatchCount() =>
        BatchCount.Text = $"已选 {_batchSelected.Count}";

    /// <summary>全选勾选框：点它 = 全选/清空当前列表（图二千问同款行为）。
    /// Click 在 toggle 之后引发，IsChecked 已是新值，直接按它落状态。</summary>
    private void BatchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var pick = BatchSelectAll.IsChecked == true;
        _batchSelected.Clear();
        if (pick)
            foreach (var vm in Items.OfType<HistoryItemViewModel>()) _batchSelected.Add(vm.Id);
        foreach (var vm in Items.OfType<HistoryItemViewModel>()) vm.IsSelected = pick;
        UpdateBatchCount();
    }

    /// <summary>行首复选框点击：勾选状态已由 IsChecked↔IsSelected 双向绑定翻好，
    /// 这里只把 Id 同步进集合（行点击的翻转被 <see cref="FromBatchCheck"/> 拦掉，不会二次翻转）。</summary>
    private void BatchCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item }) return;
        if (item.IsSelected) _batchSelected.Add(item.Id);
        else _batchSelected.Remove(item.Id);
        UpdateBatchCount();
    }

    /// <summary>点击来源是否落在复选框里（CheckBox 内部模板元素不是 Visual 树上的 CheckBox 本体，
    /// 只能一路向上找）。批量模式下命中即在行点击里放行 —— 否则点一次勾选框会连带触发整行点击，
    /// 把刚翻好的状态又翻回去（2026-09-26 分组视图与侧边栏共用这条防线）。</summary>
    internal static bool FromBatchCheck(object? source)
    {
        for (var d = source as DependencyObject; d != null;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
            if (d is System.Windows.Controls.CheckBox) return true;
        return false;
    }

    /// <summary>用最近一次的会话集重刷（批量模式进出 / 选择变化时用）。
    /// HistoryItemViewModel 自带 IsSelected / IsBatchMode（INPC），直接用它驱动选中高亮与复选框显隐。</summary>
    private void ReloadLastSessions()
    {
        Items.Clear();
        foreach (var entry in BuildSections(_lastSessions, ChatGroupStore.Load()))
        {
            if (entry is HistoryItemViewModel vm)
            {
                vm.IsBatchMode = _batchMode;
                vm.IsSelected = _batchSelected.Contains(vm.Id);
            }
            Items.Add(entry);
        }
    }

    /// <summary>把选中的 Id 翻译回条目视图（宿主动作需要 FilePath 等字段，Summary 没有）</summary>
    private List<HistoryItemViewModel> PickedItems() =>
        Items.OfType<HistoryItemViewModel>().Where(vm => _batchSelected.Contains(vm.Id)).ToList();

    private void BtnBatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickedItems();
        if (picked.Count == 0) return;
        // 只发起、不擅自退出批量态：宿主在**真的删了**之后才调 ExitBatchMode ——
        // 用户在确认框点「否」时勾选必须还在（2026-09-26，与分组视图批量同一条纪律）
        BatchDeleteRequested?.Invoke(picked);
    }

    private void BtnBatchGroup_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickedItems();
        if (picked.Count == 0) return;

        var menu = new ContextMenu();
        foreach (var g in ChatGroupStore.Load().Where(g => !ChatGroupStore.IsFavorite(g.Id)))
        {
            var targetId = g.Id;
            var item = new MenuItem { Header = g.Name };
            item.Click += (_, _) =>
            {
                BatchGroupRequested?.Invoke(picked, targetId);
                ExitBatchMode();   // 选了目标才退出；打开菜单又关掉不动勾选（原 menu.Closed 强退已改）
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = BtnBatchGroup;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void BtnBatchCancel_Click(object sender, RoutedEventArgs e) => ExitBatchMode();

    private void SessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item }) return;

        // 点在行首复选框上：放行给它自己处理（勾选状态由 CheckBox 的绑定翻转，
        // 走到这里再翻一次会把状态翻回去 —— 见 FromBatchCheck 注释）
        if (FromBatchCheck(e.OriginalSource)) return;

        // 批量模式下点会话 = 选中/取消（不再打开会话）。
        // 改 IsSelected 走 INPC：复选框与高亮一起动，不再整表重建（重建会打断悬停态）。
        if (_batchMode)
        {
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected) _batchSelected.Add(item.Id);
            else _batchSelected.Remove(item.Id);
            UpdateBatchCount();
            return;
        }
        SessionSelected?.Invoke(item);
    }

    private void GroupRow_Click(object sender, MouseButtonEventArgs e)
    {
        // 三点按钮自己会 e.Handled = true，所以点它不会走到这里
        if (sender is FrameworkElement { Tag: SidebarGroupHeader group })
            GroupSelected?.Invoke(group);
    }

    private void GroupMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: SidebarGroupHeader group } btn) return;

        // 刻意用 new ContextMenu() 而不是对象初始化器：自带 Style 会顶掉 App.xaml 的深色模板，
        // 弹出层会变成白条（项目在待办汇总右键菜单上踩过同一个坑）。
        var menu = new ContextMenu();
        menu.Items.Add(MakeGroupMenuItem(group, GroupMenuAction.Rename, "重命名"));
        menu.Items.Add(MakeGroupMenuItem(group, GroupMenuAction.Pin,
            group.IsPinned ? "取消置顶" : "置顶此分组"));
        menu.Items.Add(MakeGroupMenuItem(group, GroupMenuAction.Delete, "删除此分组"));

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MakeGroupMenuItem(SidebarGroupHeader group, GroupMenuAction action, string header)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => GroupAction?.Invoke(group, action);
        return item;
    }

    /// <summary>导出子菜单的四种格式（分组视图三点菜单复用同一份，别在两处各列一套 —— 2026-09-26）</summary>
    internal static readonly (string Name, ExportFormat Format)[] ExportFormats =
    [
        ("Markdown", ExportFormat.Markdown),
        ("TXT", ExportFormat.Txt),
        ("JSON", ExportFormat.Json),
        ("Word", ExportFormat.Word),
    ];

    private void SessionMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item } btn) return;

        var menu = new ContextMenu();
        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Rename, "重命名"));
        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.TogglePin, item.Pinned ? "取消置顶" : "置顶"));

        // 收藏与分组是**互斥**的归属（用户 2026-09-23 定：收藏是分组的另一种形式），
        // 所以这里不叫「加入收藏」而叫「收藏」—— 点下去会把它从原分组移到收藏。
        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Favorite,
            ChatGroupStore.IsFavorite(item.GroupId) ? "取消收藏" : "收藏"));

        // ⚠️「分组到…」「导出」这类**父项**只能当子菜单容器，绝不能绑 Click ——
        // 父项经 MakeSessionMenuItem 绑的 context 是 null，而宿主把 Group+null 解释成「移出分组」。
        // 2026-09-24 实锤过这条链：用户点父项 → GroupId 被清空 → 会话留/回「最近」且 SavedAt 刷新，
        // 看起来就是"跳到最近最上面、分组里找不到"。
        var groupMenu = MakeMenuHeader("分组到…");
        foreach (var g in ChatGroupStore.Load().Where(g => !ChatGroupStore.IsFavorite(g.Id)))
        {
            var menuItem = new MenuItem { Header = g.Name, IsChecked = g.Id == item.GroupId };
            var targetId = g.Id;
            menuItem.Click += (_, _) => SessionAction?.Invoke(item, ChatItemAction.Group, targetId);
            groupMenu.Items.Add(menuItem);
        }
        // 已在某个普通分组里 → 给一个明确的「移出分组」出口（收藏走「取消收藏」，别走这里）
        if (!string.IsNullOrEmpty(item.GroupId) && !ChatGroupStore.IsFavorite(item.GroupId))
            groupMenu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Group, "移出分组", ""));
        // 一个分组都没有时，「分组到…」点开是个空菜单 —— 不如直接禁掉
        if (groupMenu.Items.Count == 0) groupMenu.IsEnabled = false;
        menu.Items.Add(groupMenu);

        var exportMenu = MakeMenuHeader("导出");
        foreach (var (formatName, format) in ExportFormats)
        {
            var menuItem = new MenuItem { Header = formatName };
            var captured = format;
            menuItem.Click += (_, _) => SessionAction?.Invoke(item, ChatItemAction.Export, captured.ToString());
            exportMenu.Items.Add(menuItem);
        }
        menu.Items.Add(exportMenu);

        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Delete, "删除会话"));

        // 批量操作入口（2026-09-24 找回）：进入多选，操作在底部操作条
        menu.Items.Add(MakeBatchMenuItem());

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MakeBatchMenuItem()
    {
        // 文案 2026-09-26 从「批量操作」统一为「批量管理」（与分组视图、千问对标口径一致）
        var item = new MenuItem { Header = "批量管理" };
        item.Click += (_, _) => BatchModeRequested?.Invoke();
        return item;
    }

    /// <summary>子菜单父项：只当容器，**不绑任何事件**。绑了就会在宿主那里被当成 context=null 的动作。</summary>
    private static MenuItem MakeMenuHeader(string header) => new() { Header = header };

    private MenuItem MakeSessionMenuItem(HistoryItemViewModel item, ChatItemAction action, string header, string? context = null)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => SessionAction?.Invoke(item, action, context);
        return menuItem;
    }
}
