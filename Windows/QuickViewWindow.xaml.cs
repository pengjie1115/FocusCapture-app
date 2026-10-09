using System.ComponentModel;
using System.Runtime.CompilerServices;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Destinations;
using FocusCapture.Services.Destinations.GetNote;
using FocusCapture.Services.Sync;

namespace FocusCapture.Windows;

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>笔记列表项的 ViewModel：包装 NoteEntry，添加 IsSelected（INPC 支持）</summary>
public class NoteEntryViewModel : INotifyPropertyChanged
{
    public NoteEntry Entry { get; }
    // v3（2026-08-28）：待办有 DueTime → 显示 DueTime（归类到提醒日期后，时间列展示提醒时刻而非创建时刻）
    public DateTime Timestamp => NoteService.TodoDisplayTime(Entry);
    // v3.6（2026-08-28）：跨日期筛选（未到期档等）下非今天的条目带日期；今天只显示 HH:mm
    public string DisplayTime =>
        Timestamp.Date == DateTime.Today
            ? Timestamp.ToString("HH:mm")
            : Timestamp.ToString("MM-dd HH:mm");
    public string FirstLine => (Entry.EditedContent ?? Entry.Content).Split('\n')[0].Trim();
    public string SourceWindow => Entry.SourceWindow;
    public string? Tag => Entry.Tag;

    // ── v3.5 待办展示属性 ──
    public bool IsTodo => Entry.Type == NoteType.Todo;                                    // 待办徽标显示
    public bool IsDone => Entry.Type == NoteType.Todo && Entry.TodoStatus == TodoStatus.Done;  // 已办：灰显+删除线+沉底
    public bool IsRead => Entry.Type == NoteType.Todo && Entry.TodoStatus == TodoStatus.Read;  // 已读：橙字小标
    public bool IsTodoNotDone => Entry.Type == NoteType.Todo && Entry.TodoStatus != TodoStatus.Done; // 未办（Open+Read，供「待办」筛选档）
    public string? DueTimeText => Entry.DueTime?.ToString("MM-dd HH:mm");                  // 可有可无的提醒角标辅助

    /// <summary>展示内容：编辑过的笔记优先显示编辑后内容（存储层仍保留原行）</summary>
    public string Content => Entry.EditedContent ?? Entry.Content;

    /// <summary>面板预览：原文首行 +（如有）最近一条 AI 释义首行（等高保持，40px 内容区截断）</summary>
    public string DisplayPreview
    {
        get
        {
            if (Entry.AiFills.Count == 0) return FirstLine;
            var lastFill = Entry.AiFills[^1].Split('\n')[0].Trim();
            return $"{FirstLine}\n【AI 释义】{lastFill}";
        }
    }

    /// <summary>是否有 AI 释义（用于面板右侧 AI 徽章标识）</summary>
    public bool HasAiFills => Entry.AiFills.Count > 0;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    /// <summary>是否重复（按 Content 完全相同聚合，频次>=2 视为重复）。控制面板条目文字颜色：默认白，重复红。</summary>
    private bool _isDuplicate;
    public bool IsDuplicate
    {
        get => _isDuplicate;
        set
        {
            if (_isDuplicate == value) return;
            _isDuplicate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDuplicate)));
        }
    }

    private string _editText = string.Empty;
    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value) return;
            _editText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditText)));
        }
    }

    public void BeginEdit()
    {
        // 编辑时把 AiFills 拼接到 EditText（用户能看到+编辑全部内容）；保存时 SaveEditNote 会检测并分离
        var sb = new System.Text.StringBuilder(Entry.EditedContent ?? Entry.Content);
        if (Entry.AiFills.Count > 0)
        {
            sb.Append("\n\n—— AI 释义 ——\n");
            foreach (var fill in Entry.AiFills)
                sb.AppendLine($"【AI 释义】{fill}");
        }
        EditText = sb.ToString();
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    /// <summary>从编辑框全文分离主内容与 AI 释义块；无分隔符时全文视为内容</summary>
    public static (string Content, List<string> Fills) SplitEditText(string text)
    {
        const string separator = "\n\n—— AI 释义 ——\n";
        var idx = text.IndexOf(separator, StringComparison.Ordinal);
        if (idx < 0) return (text, new List<string>());

        var content = text[..idx].TrimEnd();
        var fills = text[(idx + separator.Length)..].Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("【AI 释义】", StringComparison.Ordinal))
            .Select(l => l.Substring("【AI 释义】".Length).Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return (content, fills);
    }

    public NoteEntryViewModel(NoteEntry entry) { Entry = entry; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class QuickViewWindow : Window
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;
    private readonly Func<SyncEngine?>? _syncEngineProvider;   // 2026-08-15：注入引擎引用（引擎可能因配置变更重建，故传 provider）
    private IChatProvider? _aiProvider;                        // v3.5：面板编辑时间识别 LLM 兜底（MainWindow 装配传入；设置变更后由 UpdateAiProvider 重建）

    /// <summary>v3.5：设置窗口保存 AI 配置后由 MainWindow 调用来重建共享 provider</summary>
    public void UpdateAiProvider(IChatProvider? p) => _aiProvider = p;
    private ExportDialog? _exportDialog;
    private List<NoteEntryViewModel> _viewModels = new();
    private DateTime _selectedDate = DateTime.Today;
    private DispatcherTimer? _selectionTimer;
    private TextBox? _activeEditBox;
    private NoteEntryViewModel? _activeEditVm;

    // ── 2026-08-15 新增：列表加载模式（单日 / 区间 / 全局查找） ──
    private NoteLoadMode _loadMode = NoteLoadMode.Date;
    private DateTime _rangeStart;
    private DateTime _rangeEnd;
    private string _searchKeyword = "";

    private enum NoteLoadMode { Date, Range, Search }

    // ── v3.5 筛选（内存层过滤，切日期/刷新后保持生效；字段驱动，非前端一次性 filter） ──
    // v2（2026-08-28）：新增第 5 档 Future（未到期=明天及以后），与其余档互斥
    // v3（2026-08-28）：来源筛选 string 单选 → HashSet 多选（空集=全部来源）
    private readonly HashSet<string> _typeFilter = new(StringComparer.Ordinal) { "All" }; // All / Note / Todo(未办) / Done(已办) / Future(未到期)，多选组合
    private readonly HashSet<string> _sourceFilter = new(StringComparer.Ordinal);         // 选中来源集合，空=全部来源
    private List<string> _availableSources = new();   // v3.6 修复：当前加载范围【筛选前】的全量来源，供来源下拉取选项，避免选中后其它来源选项消失

    // ── v3.5 编辑时间识别建议条 ──
    private NoteEntryViewModel? _suggestVm;                                                // 建议条关联的条目（保存后同步了 Content）
    private DateTime? _suggestDue;                                                         // 待设为提醒的时间
    private DispatcherTimer? _suggestTimer;                                                // 建议条 10 秒自动消失

    // ── v3.9 标题栏可组装：生成的按钮按 id 登记，动态状态（文案/可用性）全部按 id 查找，
    //    按钮被用户移除后相应状态更新自动跳过，无需散落特判 ──
    private readonly Dictionary<string, Button> _toolbarButtons = new();
    private bool _syncRunning;   // 重建工具栏时恢复「上传中/拉取中」态

    // 宽度设置边界（设置页同口径；边缘缩放回写也夹在此范围）
    public const double MinWidthLimit = 480;
    public const double MaxWidthLimit = 1280;
    private DispatcherTimer? _widthSaveTimer;   // 边缘缩放防抖落盘

    public QuickViewWindow(NoteService noteService, AppSettings settings, Func<SyncEngine?>? syncEngineProvider = null, IChatProvider? aiProvider = null)
    {
        InitializeComponent();
        _noteService = noteService;
        _settings = settings;
        _syncEngineProvider = syncEngineProvider;
        _aiProvider = aiProvider;
        Opacity = settings.QuickViewOpacity;
        Width = Math.Clamp(settings.QuickViewWidth, MinWidthLimit, MaxWidthLimit);
        Topmost = settings.QuickViewTopmost;
        // v3.9：窗口壳状态随动（最大化圆角 + 宽度记忆落盘）
        StateChanged += (_, _) => ApplyChromeState();
        SizeChanged += (_, _) => ScheduleWidthSave();
        // v3.9：标题栏按钮按设置生成（含 AI 助手名称、同步按钮可用性等初始态）
        RebuildToolbar();
        // v3.8：「恢复上次筛选」开启时跨启动还原时间筛选（默认行为是唤出时重置为当天，见 ApplySummonBehavior）
        if (settings.QuickViewRestoreLastFilter) RestoreLastFilterFromSettings();
        UpdateSyncButtonsState();   // 启动时根据引擎状态决定按钮是否可用
    }

    /// <summary>时间筛选弹层（懒创建；组件自包含，供后续可组装标题栏复用）</summary>
    private DatePickerPopup? _datePicker;

    /// <summary>从设置还原上次的时间筛选（模式 + 单日/区间），解析失败静默回退默认（今天）</summary>
    private void RestoreLastFilterFromSettings()
    {
        try
        {
            if (_settings.QuickViewLastTimeMode == "Range"
                && DateTime.TryParse(_settings.QuickViewLastRangeStart, out var rs)
                && DateTime.TryParse(_settings.QuickViewLastRangeEnd, out var re))
            {
                _rangeStart = rs;
                _rangeEnd = re > rs ? re : rs;
                _loadMode = NoteLoadMode.Range;
            }
            else if (DateTime.TryParse(_settings.QuickViewLastDate, out var d))
            {
                _selectedDate = d;
                _loadMode = NoteLoadMode.Date;
            }
        }
        catch { /* 解析失败回退默认 */ }
    }

    /// <summary>时间筛选变化后落盘（跨启动记忆 + 弹层勾选回显）</summary>
    private void SaveLastFilterToSettings()
    {
        switch (_loadMode)
        {
            case NoteLoadMode.Range:
                _settings.QuickViewLastTimeMode = "Range";
                _settings.QuickViewLastRangeStart = _rangeStart.ToString("yyyy-MM-dd");
                _settings.QuickViewLastRangeEnd = _rangeEnd.ToString("yyyy-MM-dd");
                break;
            case NoteLoadMode.Date:
                _settings.QuickViewLastTimeMode = "Date";
                _settings.QuickViewLastDate = _selectedDate.ToString("yyyy-MM-dd");
                break;
            case NoteLoadMode.Search:
                return;   // 查找模式不记忆时间筛选
        }
        _settings.Save();
    }

    /// <summary>重新加载当前选中日期的笔记（打开时与刷新按钮共用）</summary>
    public void Refresh()
    {
        ReloadNotes();
    }

    /// <summary>
    /// v3.10（2026-09-22）：外部跳转入口 —— 待办汇总面板的「跳转到灵感速览」走这里，
    /// 打开面板后定位到指定条目（开窗本身由 MainWindow 负责，面板只发请求）。
    ///
    /// 三件事必须做全，缺一条都表现为「跳过去找不到那条」：
    /// ① 退出查找/区间模式 —— 这两种模式下列表压根不是按日期加载的；
    /// ② 类型筛选重置为「全部」并清空来源筛选 —— 目标若是待办而当前筛的是「笔记」档，列表里没有它；
    /// ③ 滚动到该条目并短暂高亮（几秒后自动褪去，不留状态）。
    /// </summary>
    public void ShowAtEntry(NoteEntry target)
    {
        _loadMode = NoteLoadMode.Date;
        _selectedDate = NoteService.TodoDisplayTime(target).Date;
        _searchKeyword = "";

        _typeFilter.Clear();
        _typeFilter.Add("All");
        FilterAll.IsChecked = true;
        FilterNote.IsChecked = false;
        FilterTodo.IsChecked = false;
        FilterDone.IsChecked = false;
        FilterFuture.IsChecked = false;
        _sourceFilter.Clear();

        SaveLastFilterToSettings();
        ReloadNotes();

        // ReloadNotes 会重新解析文件（Entry 全是新对象），所以引用匹配必然落空，
        // 真正起作用的是「时间戳 + 展示内容」这条身份匹配 —— 与 NoteService 定位条目的口径一致。
        var vm = _viewModels.FirstOrDefault(v => ReferenceEquals(v.Entry, target))
                 ?? _viewModels.FirstOrDefault(v => v.Entry.Timestamp == target.Timestamp
                     && (v.Entry.EditedContent ?? v.Entry.Content) == (target.EditedContent ?? target.Content));
        if (vm == null) return;

        // 等布局跑完再滚：外部跳转时窗口可能刚从隐藏转显示，此刻容器还没生成，滚也滚不动。
        Dispatcher.BeginInvoke(new Action(() => ScrollToAndHighlight(vm)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 把某条目滚进视野并短暂高亮。
    /// ⚠ NotesList 是 ItemsControl，**没有** ScrollIntoView（那是 ListBox 才有的方法，编译直接报 CS1061）——
    /// 只能拿条目容器调 BringIntoView()，由父级 ScrollViewer（NotesScroll）响应。
    /// 高亮改的是背景色不是边框：模板里那个 Border 的 BorderThickness 是 0，临时加边框会让内容缩进一圈，看着像「跳歪了」。
    /// </summary>
    private void ScrollToAndHighlight(NoteEntryViewModel vm)
    {
        try
        {
            if (NotesList.ItemContainerGenerator.ContainerFromItem(vm) is not FrameworkElement container) return;
            container.BringIntoView();

            var border = FindVisualChild<Border>(container);
            if (border == null) return;

            var original = border.Background;
            border.Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x4A, 0x33));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, _) => { timer.Stop(); border.Background = original; };
            timer.Start();
        }
        catch { /* 定位高亮只是锦上添花，失败不影响跳转本身 */ }
    }

    /// <summary>AI 助手名称变更时同步标题栏按钮文案（设置窗口保存后调用）。按钮可能已被用户移出工具栏，按 id 查找、无则跳过。</summary>
    public void UpdateAiName(string name)
    {
        var final = string.IsNullOrWhiteSpace(name) ? "AI 问答" : name;
        if (_toolbarButtons.TryGetValue("AiAsk", out var btn)) btn.Content = final;
    }

    // ── v3.9 标题栏可组装（配置与预算见 QuickViewToolbarCatalog；设置页编辑器在 SettingsWindow 板块「灵感速览」）──

    /// <summary>按设置重建标题栏两组按钮：清空宿主 → 按有序 id 生成 → 回放动态状态。设置变更后由 ApplySettings 调用。</summary>
    public void RebuildToolbar()
    {
        ToolbarLeftHost.Children.Clear();
        ToolbarRightHost.Children.Clear();
        _toolbarButtons.Clear();

        var (left, right) = QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);
        // 清洗结果回写（只在脏时落盘一次：手改 settings.json / 升级残留的未知 id 被修正）
        if (!left.SequenceEqual(_settings.QuickViewToolbarLeft) || !right.SequenceEqual(_settings.QuickViewToolbarRight))
        {
            _settings.QuickViewToolbarLeft = left;
            _settings.QuickViewToolbarRight = right;
            _settings.Save();
        }

        foreach (var id in left) ToolbarLeftHost.Children.Add(CreateToolbarButton(id));
        foreach (var id in right) ToolbarRightHost.Children.Add(CreateToolbarButton(id));

        // 回放动态状态（按钮可能不在配置里，内部按 id 查找自动跳过）
        UpdateAiName(_settings.AiAssistantName);
        UpdateTimeButtonLabel();
        UpdateSyncButtonsState();
        SetSyncButtonsRunning(_syncRunning);
        UpdateSelectionUI();
    }

    /// <summary>Windows 自带矢量图标字体（Win10+ 随系统提供）。用于标题栏图标钮与铬区按钮，
    /// 替代此前混用的 emoji（🔍）与符号（⟳）——后者在面板所用字体里字形缺失或基线偏位，
    /// 渲染出来会缺笔画（⟳ 的箭头丢失）或明显不居中。</summary>
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    /// <summary>按功能目录生成一个标题栏按钮并挂既有 Click 处理器（功能逻辑与重构前完全一致）。</summary>
    private Button CreateToolbarButton(string id)
    {
        var def = QuickViewToolbarCatalog.Find(id)!;
        var useGlyph = !string.IsNullOrEmpty(def.Glyph);
        var btn = new Button
        {
            Content = useGlyph ? def.Glyph : def.Label,
            FontSize = def.FontSize,
            Height = 24,
            Background = Brushes.Transparent,
            Foreground = BrushFromHex(def.Foreground),
            BorderBrush = BrushFromHex(def.BorderBrush),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        if (def.IsTextButton)
        {
            btn.Padding = new Thickness(8, 0, 8, 0);
            btn.MinWidth = 60;   // 宽度随内容自适应：时间筛选档位 / AI 名称 / 「导出已选 N 条」都是动态文案
        }
        else
        {
            btn.Width = 28;
            // 图标钮必须显式清零内边距：全局 Button 样式带 Padding="8,4"，
            // 而 28px 宽减掉左右各 8 只剩 12px 内容区，图标会被挤偏甚至裁切。
            btn.Padding = new Thickness(0);
            btn.FontFamily = IconFont;
        }
        if (!string.IsNullOrEmpty(def.ToolTip)) btn.ToolTip = def.ToolTip;
        btn.Click += ToolbarButtonClick;
        _toolbarButtons[id] = btn;
        return btn;
    }

    /// <summary>#RRGGBB → 冻结 SolidColorBrush（不走 TypeConverter，兼容 PublishTrimmed）。</summary>
    private static SolidColorBrush BrushFromHex(string hex)
    {
        var s = hex.TrimStart('#');
        var brush = new SolidColorBrush(Color.FromRgb(
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[4..6], 16)));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 扩展功能的外部动作出口（由 MainWindow 构造本窗口后注入），参数为功能 id。
    /// 为什么要绕这一道：待办汇总、回收站、设置、导入、每日总结这几个窗口的依赖
    /// （热键服务、悬浮球当前坐标、导入预览构造）只有主程序持有；且待办汇总在主程序里
    /// 已有单例管理逻辑（ShowTodoSummary），若在本窗口再实现一份，两处入口迟早行为不一致。
    /// 未注入时（例如快照工具/测试直接构造本窗口）点击扩展按钮静默忽略，不抛异常。
    /// </summary>
    public Action<string>? ExternalActionRequested { get; set; }

    /// <summary>生成的按钮统一入口：按 id 分派到原有处理器（与 XAML 写死时代同一套方法）。
    /// 扩展功能见 <see cref="ExternalActionRequested"/>。</summary>
    private void ToolbarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: null } btn) return;
        var id = _toolbarButtons.FirstOrDefault(kv => ReferenceEquals(kv.Value, btn)).Key;
        switch (id)
        {
            case "Calendar": BtnCalendar_Click(sender, e); break;
            case "SyncUpload": BtnSyncUpload_Click(sender, e); break;
            case "SyncDownload": BtnSyncDownload_Click(sender, e); break;
            case "Search": BtnSearch_Click(sender, e); break;
            case "Refresh": BtnRefresh_Click(sender, e); break;
            case "AiAsk": BtnAiAsk_Click(sender, e); break;
            case "Export": BtnExport_Click(sender, e); break;
            case "GetNote": BtnGetNote_Click(sender, e); break;
            // 扩展功能：交给主程序统一开窗（2026-09-13）
            case "TodoSummary":
            case "RecycleBin":
            case "Settings":
            case "Import":
            case "DailySummary":
                ExternalActionRequested?.Invoke(id);
                break;
        }
    }

    /// <summary>设置变更后由 MainWindow 调用：宽度 / 置顶 / 标题栏按钮即时生效（面板未打开也可调用）。</summary>
    public void ApplySettings()
    {
        Topmost = _settings.QuickViewTopmost;
        if (WindowState == WindowState.Normal)
            Width = Math.Clamp(_settings.QuickViewWidth, MinWidthLimit, MaxWidthLimit);
        RebuildToolbar();
    }

    // ── v3.9 窗口铬区：最小化 / 最大化 / 状态随动 ──

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>最大化时取消圆角（否则四角露透明缺口）并切换按钮图标。</summary>
    private void ApplyChromeState()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.CornerRadius = new CornerRadius(maximized ? 0 : 8);
        // 图标必须落在图标字体里有字形的码位上：此前的 "❐" / "□" 在 Segoe MDL2 Assets 中无字形，
        // 会在切换最大化/还原时渲染成空框（2026-09-13 随铬区按钮改字体时发现的连带问题）。
        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
    }

    /// <summary>边缘缩放后把宽度回写设置（防抖落盘；悬浮球位置记忆同款先例）。</summary>
    private void ScheduleWidthSave()
    {
        if (WindowState != WindowState.Normal) return;
        var w = Math.Clamp(ActualWidth, MinWidthLimit, MaxWidthLimit);
        if (Math.Abs(_settings.QuickViewWidth - w) < 0.5) return;
        _settings.QuickViewWidth = w;
        if (_widthSaveTimer == null)
        {
            _widthSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _widthSaveTimer.Tick += WidthSaveTimer_Tick;
        }
        _widthSaveTimer.Stop();
        _widthSaveTimer.Start();
    }

    private void WidthSaveTimer_Tick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer t) t.Stop();
        _settings.Save();
    }

    private void ReloadNotes()
    {
        // v3.10（多条件筛选）：关键词/类型/来源均为独立内存筛选条件，与时间段并列叠加；
        // _loadMode 只决定时间段基础集（Date 单日 / Range 区间），不再有独占的 Search 模式
        // v2（2026-08-28）：未到期档跨日期加载全部笔记（未来待办不在任何单日列表里），内存筛选
        var entries = _typeFilter.Contains("Future")
            ? _noteService.LoadAllEntries()
            : _loadMode switch
            {
                NoteLoadMode.Range => _noteService.LoadNotesRange(_rangeStart, _rangeEnd),
                _ => _noteService.LoadNotes(_selectedDate)
            };

        // v3.6 修复：来源下拉选项取自【筛选前】的全量来源，否则选中某来源后其它来源条目被筛掉、
        // 选项随之消失、无法继续多选（用户原痛点）。先聚合全量来源供 UpdateSourceFilterOptions 使用。
        _availableSources = entries.Select(e => e.SourceWindow).Where(s => !string.IsNullOrEmpty(s))
            .Distinct().OrderBy(s => s).ToList();
        // v3.5：筛选在内存层应用（ReloadNotes 后），切日期/刷新后保持生效——不是前端一次性 filter
        var filtered = ApplyFilters(entries);
        // v3.5：已办沉底——先分桶（已办 → 桶 1 沉底，普通+未办 → 桶 0），桶内按时间倒序。
        // v3（2026-08-28 二次修复）：旧实现用 ThenBy 做沉底，但 ThenBy 只在时间相同时生效，
        // 时间不同的已办永远排不沉 → 必须把「是否已办」放主排序键。恢复待办后自动回桶 0 正常排序。
        var sorted = filtered
            .OrderBy(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Done ? 1 : 0)
            .ThenByDescending(e => NoteService.TodoDisplayTime(e));

        _viewModels = sorted.Select(e => new NoteEntryViewModel(e)).ToList();
        ApplyDuplicateMarkers();
        UpdateSourceFilterOptions();   // v3.6：来源下拉从【全量来源】聚合（_availableSources 已在筛选前算好），刷新后仍保持选中
        NotesList.ItemsSource = _viewModels;
        UpdateEmptyHint();
        UpdateSelectionUI();
        UpdateModeIndicator();
    }

    /// <summary>v3.5：类型多选 + 来源 筛选（内存过滤；“全部”时不过滤）。
    /// v2：未到期档（Future）= 明天及以后的未办待办，与类型档互斥优先。</summary>
    private IEnumerable<NoteEntry> ApplyFilters(IEnumerable<NoteEntry> entries)
    {
        // 关键词筛选（与时间段/类型/来源并列叠加）：命中 Content / EditedContent / SourceWindow 任一
        if (!string.IsNullOrEmpty(_searchKeyword))
        {
            var kw = _searchKeyword;
            entries = entries.Where(e =>
                e.Content.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(e.EditedContent) && e.EditedContent.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.SourceWindow) && e.SourceWindow.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }
        if (_typeFilter.Contains("Future"))
        {
            // 未到期：明天及以后的未办待办（今天还没到时间的归「待办」档）
            entries = entries.Where(e => e.Type == NoteType.Todo && e.TodoStatus != TodoStatus.Done
                && e.DueTime.HasValue && e.DueTime.Value.Date > DateTime.Today);
        }
        else if (!_typeFilter.Contains("All"))
        {
            var showNote = _typeFilter.Contains("Note");
            var showTodo = _typeFilter.Contains("Todo");   // 「待办」档=未办（Open+Read，不含已办）
            var showDone = _typeFilter.Contains("Done");   // 「已办」档=仅 Done
            entries = entries.Where(e =>
                (showNote && e.Type != NoteType.Todo) ||
                (showTodo && e.Type == NoteType.Todo && e.TodoStatus != TodoStatus.Done) ||
                (showDone && e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Done));
        }
        if (_sourceFilter.Count > 0)
            entries = entries.Where(e => !string.IsNullOrEmpty(e.SourceWindow) && _sourceFilter.Contains(e.SourceWindow));
        return entries;
    }

    /// <summary>v3.6：来源多选下拉——基于当前加载范围【筛选前】的全量来源重建 Popup 列表，保留 _sourceFilter 选中；同步框内文本。
    /// 修复：原从筛选后的 _viewModels 聚合来源，选中某来源后其它来源条目被筛掉、选项消失，无法多选。改为用 _availableSources。</summary>
    private void UpdateSourceFilterOptions()
    {
        var sources = _availableSources;   // 筛选前全量来源，不受当前选中状态影响

        // 剔除已失效的选中来源（当前加载范围不再出现）
        _sourceFilter.RemoveWhere(s => !sources.Contains(s));

        SourceFilterList.Children.Clear();
        foreach (var src in sources)
        {
            var selected = _sourceFilter.Contains(src);
            var btn = new Button
            {
                Content = (selected ? "✓ " : "") + src,
                Tag = src,
                Height = 26,
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = selected ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)) : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            btn.Click += (_, _) => ToggleSource(src);
            SourceFilterList.Children.Add(btn);
        }

        // 框内文本：未选=空白，已选=「已选 N 个来源」
        SourceFilterText.Text = _sourceFilter.Count == 0 ? "" : $"已选 {_sourceFilter.Count} 个来源";
        SourceFilterText.Foreground = _sourceFilter.Count == 0
            ? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
            : new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    }

    /// <summary>v3.6：来源选项点击 → 切换选中（点一下选中、再点取消）→ 重新筛选。ReloadNotes 内部 UpdateSourceFilterOptions
    /// 已用全量来源重建勾选样式，故无需单独刷新；多选并集 + 即时生效，无需确认按钮。</summary>
    private void ToggleSource(string source)
    {
        if (!_sourceFilter.Add(source)) _sourceFilter.Remove(source);
        ReloadNotes();
    }

    /// <summary>
    /// 按 Content 完全相同聚合判定重复：频次 ≥2 的 vm 标记 IsDuplicate=true，
    /// 触发 XAML 样式把字体颜色切到红色（#FF6B6B）。空内容不参与。
    /// </summary>
    private void ApplyDuplicateMarkers()
    {
        var contentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var vm in _viewModels)
        {
            var c = vm.Entry.Content?.Trim();
            if (string.IsNullOrEmpty(c)) continue;
            contentCounts[c] = contentCounts.GetValueOrDefault(c) + 1;
        }
        foreach (var vm in _viewModels)
        {
            var c = vm.Entry.Content?.Trim();
            vm.IsDuplicate = !string.IsNullOrEmpty(c) && contentCounts.GetValueOrDefault(c) >= 2;
        }
    }

    private void UpdateEmptyHint()
    {
        if (_viewModels.Count > 0) { EmptyHint.Visibility = Visibility.Collapsed; return; }

        EmptyHint.Text =
            _typeFilter.Contains("Future") ? "没有未到期的待办"
            : !string.IsNullOrEmpty(_searchKeyword) ? $"未找到包含「{_searchKeyword}」的条目"
            : _loadMode switch
            {
                NoteLoadMode.Range => $"区间 {_rangeStart:yyyy-MM-dd} ~ {_rangeEnd:yyyy-MM-dd} 内还没有笔记",
                _ => _selectedDate.Date == DateTime.Today ? "今天还没有笔记" : "这一天还没有笔记"
            };
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void UpdateModeIndicator()
    {
        // 时间段状态显示在时间按钮上（UpdateTimeButtonLabel），指示行只留查找与未到期筛选
        if (_typeFilter.Contains("Future"))
            ModeIndicator.Text = "筛选：未到期（明天及以后）";
        else if (!string.IsNullOrEmpty(_searchKeyword))
            ModeIndicator.Text = $"查找：\"{_searchKeyword}\"";
        else
            ModeIndicator.Text = "";
        // 有查找关键词或处于非单日模式时显示「返回」一键重置
        BtnReturnToDate.Visibility = (_searchKeyword != "" || _loadMode != NoteLoadMode.Date)
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateTimeButtonLabel();
    }

    /// <summary>v3.8：时间按钮直接显示当前筛选（预设名或日期区间），替代旧「单日： …」指示文案</summary>
    private void UpdateTimeButtonLabel()
    {
        QuickTimePreset preset;
        DateTime start, end;
        if (_loadMode == NoteLoadMode.Range)
        {
            start = _rangeStart;
            end = _rangeEnd;
        }
        else
        {
            start = _selectedDate;
            end = _selectedDate;
        }
        preset = DatePickerPopup.MatchPreset(start, end);
        if (_toolbarButtons.TryGetValue("Calendar", out var calendarBtn))
            calendarBtn.Content = $"📅 {DatePickerPopup.GetLabel(preset, start, end)}";
    }

    /// <summary>焦点回归自动刷新（切回面板/关闭弹窗后列表同步最新笔记）</summary>
    private void Window_Activated(object sender, EventArgs e)
    {
        try { Refresh(); }
        catch { /* 刷新失败不阻塞窗口激活 */ }
        UpdateSyncButtonsState();   // 引擎可能因配置变更被重建（MainWindow.RebuildSyncEngine），每次激活时同步按钮可用性
    }

    /// <summary>标题栏刷新按钮：立即重新加载列表</summary>
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>时间按钮（v3.8）：弹出预设 + 自定义日历弹层。弹层当前勾选跟随当前生效筛选；再点一次按钮 = 收起。</summary>
    private void BtnCalendar_Click(object sender, RoutedEventArgs e)
    {
        _datePicker ??= new DatePickerPopup(_noteService);
        _datePicker.Selected -= ApplyTimeSelection;
        _datePicker.Selected += ApplyTimeSelection;

        if (_datePicker.IsOpen)
        {
            _datePicker.Close();
            return;
        }

        var (start, end) = _loadMode == NoteLoadMode.Range
            ? (_rangeStart, _rangeEnd)
            : (_selectedDate, _selectedDate);
        if (!_toolbarButtons.TryGetValue("Calendar", out var anchorBtn)) return;   // 弹层锚定时间按钮本身；按钮已被移除则无入口
        _datePicker.Open(anchorBtn, DatePickerPopup.MatchPreset(start, end), start, end);
    }

    /// <summary>弹层选择结果 → 切换 Date/Range 模式 + 落盘 + 重载列表（单日 start==end 回 Date 模式，与旧日历同口径）</summary>
    private void ApplyTimeSelection(TimeFilterSelection selection)
    {
        if (selection.Start == selection.End)
        {
            _selectedDate = selection.Start;
            _loadMode = NoteLoadMode.Date;
        }
        else
        {
            _rangeStart = selection.Start;
            _rangeEnd = selection.End;
            _loadMode = NoteLoadMode.Range;
        }
        SaveLastFilterToSettings();
        ReloadNotes();
    }

    /// <summary>查找按钮：弹出 SearchDialog 输入关键词，确认后进入 Search 模式替换列表。</summary>
    private void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        // 搜索属**免费档**（2026-10-08 用户拍板），此处刻意不设门卫。
        // 历史上曾误登记为高级档，铺开拦截点那步已清除。
        // 关键词是独立筛选条件，不切换加载模式（与时间段叠加）：预填当前关键词，留空确认=清除
        var dlg = new SearchDialog { Owner = this, InitialKeyword = _searchKeyword };
        if (dlg.ShowDialog() != true) return;
        _searchKeyword = dlg.Keyword.Trim();
        ReloadNotes();
    }

    /// <summary>返回按钮：仅 Range/Search 模式显示。点击 → 回到 _selectedDate 单日模式。</summary>
    private void BtnReturnToDate_Click(object sender, RoutedEventArgs e)
    {
        // 一键重置加载范围层：清除关键词 + 回到今天单日（类型/来源筛选保留，由各自开关控制）
        _searchKeyword = "";
        _loadMode = NoteLoadMode.Date;
        _selectedDate = DateTime.Today;
        SaveLastFilterToSettings();
        ReloadNotes();
    }

    /// <summary>v3.8 唤出行为：默认每次唤出重置为当天笔记；设置开启「恢复上次的筛选」时保持现状（跨启动由构造函数还原）。</summary>
    private void ApplySummonBehavior()
    {
        if (_settings.QuickViewRestoreLastFilter) return;
        _selectedDate = DateTime.Today;
        if (_loadMode != NoteLoadMode.Date) _loadMode = NoteLoadMode.Date;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 编辑态优先：Esc 取消编辑，Ctrl+S 保存
        var editing = _viewModels.FirstOrDefault(vm => vm.IsEditing);
        if (editing != null)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                editing.CancelEdit();
                return;
            }
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SaveEditNote(editing);
                return;
            }
        }
        if (e.Key == Key.Escape) Hide();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 双击标题栏空白处（非按钮）→ 切换最大化/还原
        if (e.ClickCount >= 2)
        {
            if (IsSourceFromChromeButton(e.OriginalSource as DependencyObject)) return;
            BtnMaximize_Click(sender, e);
            e.Handled = true;
            return;
        }

        // 单击：普通态拖动；最大化态 → 还原为普通态并跟随鼠标拖动（系统标准交互）
        if (WindowState == WindowState.Maximized)
        {
            RestoreAndDrag(e);
            return;
        }

        try { DragMove(); } catch { /* DragMove 在窗口未显示时会抛 InvalidOperationException */ }
    }

    /// <summary>双击落在标题栏按钮（铬区最小化/最大化/关闭 + 工具栏按钮）上不触发最大化——按钮自身处理 Click。</summary>
    private static bool IsSourceFromChromeButton(DependencyObject? src)
    {
        var d = src;
        while (d != null)
        {
            if (d is Button) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>最大化态下拖动标题栏 → 还原为普通态，窗口出现在鼠标下方并跟随拖动（系统标准行为）。
    /// 最大化还原后窗口会回到上次普通态位置（可能远离鼠标），故先把窗口平移到鼠标下方再 DragMove。</summary>
    private void RestoreAndDrag(MouseButtonEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        WindowState = WindowState.Normal;
        var w = Math.Clamp(Width, MinWidthLimit, MaxWidthLimit);
        var left = p.X - w / 2;
        var top = p.Y - 20;   // 标题栏约 40px，让鼠标落在标题栏上半部
        var area = SystemParameters.WorkArea;
        left = Math.Max(area.Left, Math.Min(left, area.Right - w));
        top = Math.Max(area.Top, Math.Min(top, area.Bottom - Height));
        Left = left;
        Top = top;
        try { DragMove(); } catch { /* 还原瞬间 DragMove 偶发 InvalidOperationException，忽略即不拖 */ }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>标题栏 AI 问答入口（全局，无笔记绑定）</summary>
    private void BtnAiAsk_Click(object sender, RoutedEventArgs e)
    {
        AIDialogHelper.Open(ExplainMode.Ask);
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        // 导出**不再整体拦截**（2026-10-08 改选项级）：Markdown / 纯文本属免费档。
        // Word 那一项的置灰在 ExportDialog 内做；这里只在真要写 Word 文件时兜底 ——
        // 防止「对话框开着的时候用户去别处激活/激活失效」这类边界把免费功能也拦掉。
        try
        {
            var selected = GetSelectedEntries();
            var notes = selected.Count > 0
                ? selected
                : _viewModels.Select(vm => vm.Entry).ToList();

            if (notes.Count == 0)
            {
                var msg = _selectedDate.Date == DateTime.Today
                    ? "今天还没有笔记，快去记录灵感吧。"
                    : "这一天还没有笔记。";
                System.Windows.MessageBox.Show(msg, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _exportDialog = new ExportDialog(_settings, _noteService) { Owner = this };
            _exportDialog.ShowDialog();

            if (!_exportDialog.Confirmed) return;

            var svc = new NoteExportService();
            var config = _exportDialog.Config;
            var folder = _exportDialog.FolderPath;
            var ext = svc.GetFileExtension(config.Format);
            var baseName = selected.Count > 0
                ? $"灵感_已选{selected.Count}条_{_selectedDate:yyyy-MM-dd}"
                : $"灵感_{_selectedDate:yyyy-MM-dd}";
            var fileName = NoteExportService.SanitizeFileName(baseName) + ext;
            var filePath = NoteExportService.GetUniquePath(Path.Combine(folder, fileName));

            Directory.CreateDirectory(folder);

            if (config.Format == ExportFormat.Word)
            {
                // 兜底（正常路径下 ExportDialog 已把该项置灰，走不到这里）：
                // 万一被绕过或状态在对话框开着时发生变化，也不该静默产出 Word 文件。
                if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureExportWord, "Word 导出", this)) return;

                var bytes = svc.BuildWord(notes, config);
                File.WriteAllBytes(filePath, bytes);
            }
            else
            {
                var content = svc.BuildExport(notes, config);
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }

            var successDialog = new SuccessDialog(filePath) { Owner = this };
            successDialog.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导出失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportDialog = null;
        }
    }

    /// <summary>
    /// 笔记行的鼠标手势（2026-09-18 语义变更，用户拍板）：**只保留「双击 → 进入编辑」**。
    /// 原先单击即复制内容到剪贴板，导致双击的第一下（ClickCount==1）必然先写一次剪贴板 ——
    /// 同步 OLE 写入阻塞 UI 线程、被其他程序占用时还要退避重试，双击进编辑因此明显变慢。
    /// 复制改由右键菜单「复制」承担（CtxCopy_Click，仍走 SafeClipboard 容错）。
    /// </summary>
    private void NoteItem_Click(object sender, MouseButtonEventArgs e)
    {
        // 防止点击 CheckBox 区域时误触发（勾选框有自己的单击/双击语义）
        if (IsClickFromCheckBox(e.OriginalSource as DependencyObject)) return;

        if (sender is Border { DataContext: NoteEntryViewModel vm })
        {
            if (vm.IsEditing) return;              // 编辑态内不响应，避免重入打断框内选词
            if (e.ClickCount == 2) BeginEditNote(vm);
        }
    }

    // ── 同步（2026-08-15 灵感速览同步入口：上传=全量推；下载=只 pull 不 push） ──

    /// <summary>根据当前引擎/解锁状态刷新同步按钮可用性 + ToolTip（窗口激活时由 UpdateSyncButtonsState 触发）。
    /// 按钮可能已被用户移出工具栏，按 id 查找、无则跳过。</summary>
    private void UpdateSyncButtonsState()
    {
        var engine = _syncEngineProvider?.Invoke();
        var enabled = engine != null && engine.IsMasterPasswordSet;
        var tip = enabled ? null : "云同步未配置或未解锁，请到设置页连接";
        ApplySyncButton("SyncUpload", "上传笔记到云端（沿用全量同步机制）", enabled, tip);
        ApplySyncButton("SyncDownload", "从云端拉取笔记到本地（仅拉不推）", enabled, tip);
    }

    /// <summary>同步进行中状态使用的图标（环形箭头）。必须是图标字体里有字形的码位。</summary>
    private const string SyncRunningGlyph = "\uE72C";

    /// <summary>
    /// 刷新单个同步按钮的状态。
    /// 图标改为统一从功能目录取（2026-09-13 修正）：此前这里硬编码 "↑" / "↓" 文本字符，
    /// 而按钮的 FontFamily 已换成 Segoe MDL2 Assets —— 该字体没有这两个字形，按钮会渲染成空框。
    /// 同理「上传中…」这类中文在图标字体下同样没有字形，且图标钮仅 28px 宽也放不下中文，
    /// 故进行中状态改用图标表示，文字状态交给 ToolTip（信息不丢失）。
    /// </summary>
    private void ApplySyncButton(string id, string idleTip, bool enabled, string? disabledTip)
    {
        if (!_toolbarButtons.TryGetValue(id, out var btn)) return;
        var idleGlyph = QuickViewToolbarCatalog.Find(id)?.Glyph;
        btn.IsEnabled = enabled && !_syncRunning;
        if (_syncRunning)
        {
            btn.Content = SyncRunningGlyph;
            btn.ToolTip = id == "SyncUpload" ? "上传中…" : "拉取中…";
        }
        else
        {
            if (!string.IsNullOrEmpty(idleGlyph)) btn.Content = idleGlyph;
            btn.ToolTip = enabled ? idleTip : disabledTip;
        }
    }

    /// <summary>正在同步：禁用两个按钮，避免并发同步（SyncEngine 自身也有 SemaphoreSlim 闸）</summary>
    private void SetSyncButtonsRunning(bool running)
    {
        _syncRunning = running;
        UpdateSyncButtonsState();
    }

    private void ShowSyncStatus(string text, bool error = false)
    {
        SyncStatusText.Text = text;
        SyncStatusText.Foreground = new SolidColorBrush(Color.FromRgb(
            error ? (byte)0xE5 : (byte)0x88,
            error ? (byte)0x73 : (byte)0xCC,
            error ? (byte)0x73 : (byte)0xCC));
    }

    /// <summary>标题栏 ↑ 按钮：触发一次全量推（与设置面板"立即同步"语义一致，机制不变）</summary>
    private async void BtnSyncUpload_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            ShowSyncStatus("云同步未配置：请到 设置 → 云同步 填写 WebDAV 与授权码", error: true);
            return;
        }
        // 2026-10-08 门卫：未解锁时不进后台流程，直接给文字反馈。
        // 这里刻意用 IsAllowed 而**不弹模态窗** —— 标题栏已有 ShowSyncStatus 文字位，
        // 快速操作场景弹窗是打扰（用户手已经在标题栏上了，视线不该被拽走）。
        if (!LicenseGate.IsAllowed(LicenseGate.FeatureSync))
        {
            ShowSyncStatus("云同步是高级功能：请在 设置 → 激活码 输入激活码", error: true);
            return;
        }
        SetSyncButtonsRunning(true);
        ShowSyncStatus("正在上传到云端（全量推）…");
        try
        {
            var result = await engine.SyncNowAsync(auto: false);
            ShowSyncStatus(result.Success ? "✓ 上传成功" : "✗ 上传失败：" + result.Error, error: !result.Success);
        }
        catch (Exception ex)
        {
            ShowSyncStatus("✗ 上传失败：" + ex.Message, error: true);
        }
        finally
        {
            SetSyncButtonsRunning(false);
        }
    }

    /// <summary>标题栏 ↓ 按钮：触发一次只 pull 不 push（避免把本机未确认内容顺手推上云端）</summary>
    private async void BtnSyncDownload_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            ShowSyncStatus("云同步未配置：请到 设置 → 云同步 填写 WebDAV 与授权码", error: true);
            return;
        }
        // 门卫同 ↑ 按钮：文字反馈，不弹窗。
        if (!LicenseGate.IsAllowed(LicenseGate.FeatureSync))
        {
            ShowSyncStatus("云同步是高级功能：请在 设置 → 激活码 输入激活码", error: true);
            return;
        }
        SetSyncButtonsRunning(true);
        ShowSyncStatus("正在从云端拉取…");
        try
        {
            var result = await engine.PullOnlyAsync();
            ShowSyncStatus(result.Success ? "✓ 拉取完成" : "✗ 拉取失败：" + result.Error, error: !result.Success);
        }
        catch (Exception ex)
        {
            ShowSyncStatus("✗ 拉取失败：" + ex.Message, error: true);
        }
        finally
        {
            SetSyncButtonsRunning(false);
        }
    }

    // ── 条目右键菜单 ──

    /// <summary>标题栏「存」按钮：勾选条目优先推送，未勾选推送当前显示列表。去重/确认/重试在 GetNotePushService 内。</summary>
    private async void BtnGetNote_Click(object sender, RoutedEventArgs e)
    {
        // 2026-10-08 门卫（得到大脑归「云同步」档）：拦在取数之前 ——
        // 未解锁时若先走「当前没有可上传的笔记」分支，用户会以为是空数据问题，
        // 实际上是没激活，误导。这里弹激活窗（UI 线程，且是用户主动点的按钮，弹窗不算打扰）。
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureSync, "得到大脑", this)) return;

        var selected = GetSelectedEntries();
        var fromSelection = selected.Count > 0;
        var entries = fromSelection ? selected : _viewModels.Select(vm => vm.Entry).ToList();
        if (entries.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "当前没有可上传的笔记。", "存到得到大脑",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pushService = new GetNotePushService(_settings, new GetNoteDestination(_settings), new GetNoteSyncState())
        {
            ConfirmHandler = desc => Task.FromResult(System.Windows.MessageBox.Show(
                this, desc, "存到得到大脑",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK),
            // 检测到已推送条目 → 三按钮弹窗问一次（整批生效，2026-10-01 拍板；替代旧「直接跳过」）
            DuplicateHandler = dups => Task.FromResult(ShowDuplicateDialog(dups)),
        };

        try
        {
            var outcome = await pushService.PushAsync(entries, fromSelection);
            AppLog.Info("GetNote", $"按钮推送结束：{outcome.Message}");
            if (outcome.Cancelled)
                return;   // 用户主动取消：不打扰（2026-10-01 拍板，连结果框也不弹）
            System.Windows.MessageBox.Show(this, outcome.Message, "存到得到大脑",
                MessageBoxButton.OK, outcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("GetNote", "按钮推送异常", ex);
            System.Windows.MessageBox.Show(this, $"推送出错：{ex.Message}", "存到得到大脑",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>重复询问弹窗（批量一次）。整批共用一个处置：仍要推送 / 跳过 / 取消。</summary>
    private GetNotePushService.DuplicateChoice ShowDuplicateDialog(IReadOnlyList<GetNotePushService.DuplicateInfo> duplicates)
    {
        var dlg = new GetNoteDuplicateDialog(duplicates) { Owner = this };
        dlg.ShowDialog();
        return dlg.Choice;
    }

    private void NoteContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // 记录右键目标条目，供各菜单项点击时使用
        if (sender is ContextMenu menu && menu.PlacementTarget is FrameworkElement target)
        {
            _contextTarget = target.DataContext as NoteEntryViewModel;
        }
        // v3.5：「设置提醒/取消提醒」仅待办条目可用（普通笔记置灰）。
        // 注意：MenuItem 在 ContextMenu 自己的 namescope 内，x:Name 不会提升为窗口字段，
        // 这里通过 cm.Items 遍历（按 Header 识别），避免直接字段访问。
        var isTodo = _contextTarget?.IsTodo == true;
        if (sender is ContextMenu cm)
        {
            foreach (var item in cm.Items.OfType<MenuItem>())
            {
                if (item.Header is string h && (h == "设置提醒…" || h == "取消提醒"))
                    item.IsEnabled = isTodo;
            }
        }
    }

    private NoteEntryViewModel? _contextTarget;

    private NoteEntryViewModel? GetContextTarget(object sender)
        => _contextTarget;

    private void CtxAiTranslate_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        // 传整条笔记作为 selectedText，让 OpenSession 走 selectedText 路径自动发起第一轮（修复：之前漏传，对话框空白）
        if (vm != null) AIDialogHelper.Open(ExplainMode.Translate, vm.Entry, vm.Content);
    }

    private void CtxAiSearch_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm != null) AIDialogHelper.Open(ExplainMode.Search, vm.Entry, vm.Content);
    }

    private void CtxCopy_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null) return;
        if (string.IsNullOrEmpty(vm.Content)) return;

        ClipboardHookService.MarkSelfCopy();
        // 双程加固写入（OLE 失败 → Win32 固化退避重试，2026-10-01）；失败只提示，不抛到全局异常弹模态框
        if (!SafeClipboard.TrySetTextHardened(vm.Content, WpfClipboard.SetText, Win32.WriteClipboardText))
            ShowSyncStatus("复制失败：剪贴板被其他程序占用，请稍后重试", error: true);
    }

    private void CtxEdit_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm != null) BeginEditNote(vm);
    }

    private void CtxDelete_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确认删除这条笔记？\n\n时间：{vm.Timestamp:HH:mm}\n内容：{vm.FirstLine}\n\n" +
            "（将移入回收站，30 天内可恢复）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_noteService.DeleteNote(vm.Entry))
        {
            System.Windows.MessageBox.Show("删除失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        CancelEditState(vm);
        Refresh();   // 2026-08-15 修复：原 _viewModels.Remove + Items.Refresh() 对 ItemsSource=List 不触发 UI 重绘（List 无集合通知且 Items.Refresh 对 ItemsSource 模式无效），单条删除后不实时刷新；统一走 ReloadNotes 全量重载（与批量删除路径一致）
    }

    // ── v3.5 右键提醒（仅待办显示） ──

    /// <summary>右键「设置提醒」：弹出 yyyy-MM-dd HH:mm 输入对话框 → UpdateTodo 原地设提醒时间（正文带当前展示内容，防覆盖编辑）</summary>
    private void CtxSetDue_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null || !vm.IsTodo) return;
        var dlg = new DueTimeDialog(vm.Entry.DueTime) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.DueTime.HasValue) return;
        if (_noteService.UpdateTodo(vm.Entry,
                newContent: vm.Entry.EditedContent ?? vm.Entry.Content,
                dueTime: dlg.DueTime.Value))
            Refresh();
        else
            System.Windows.MessageBox.Show("设置提醒失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>右键「取消提醒」：UpdateTodo 清除提醒时间（正文带当前展示内容，防覆盖编辑）</summary>
    private void CtxClearDue_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null || !vm.IsTodo) return;
        if (_noteService.UpdateTodo(vm.Entry,
                newContent: vm.Entry.EditedContent ?? vm.Entry.Content,
                clearDue: true))
            Refresh();
        else
            System.Windows.MessageBox.Show("取消提醒失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// v3.5：待办徽标点击 → 已办（原地改行 + 落盘）。e.Handled 吞事件防冒泡触发条目复制。
    /// v3（2026-08-28）：可撤销——再点「已办」→ 恢复为「待办」（Open）。兜底场景：用户标记完成后又觉得该事
    /// 只是暂时没时间做、仍可优化，恢复后继续保留在待办列表。
    /// </summary>
    private void TodoBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is NoteEntryViewModel vm)
        {
            e.Handled = true;
            if (!vm.IsTodo) return;
            var next = vm.IsDone ? TodoStatus.Open : TodoStatus.Done;
            if (_noteService.UpdateTodo(vm.Entry,
                    newContent: vm.Entry.EditedContent ?? vm.Entry.Content,
                    status: next))
                Refresh();
        }
    }

    // ── v3.5 筛选（类型多选 + 来源） ──

    private void TypeFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn || btn.Tag is not string tag) return;

        // 点「全部」：清空其余档位，只留 All
        if (tag == "All")
        {
            _typeFilter.Clear();
            _typeFilter.Add("All");
            FilterAll.IsChecked = true;
            FilterNote.IsChecked = false;
            FilterTodo.IsChecked = false;
            FilterDone.IsChecked = false;
            FilterFuture.IsChecked = false;
        }
        // 点「未到期」：与其余档互斥，只留 Future
        else if (tag == "Future")
        {
            _typeFilter.Clear();
            if (btn.IsChecked == true) _typeFilter.Add("Future");
            else _typeFilter.Add("All");
            FilterAll.IsChecked = !_typeFilter.Contains("Future");
            FilterNote.IsChecked = false;
            FilterTodo.IsChecked = false;
            FilterDone.IsChecked = false;
            if (!_typeFilter.Contains("Future")) FilterFuture.IsChecked = false;
        }
        else
        {
            // 点类型档：移除未到期档
            if (_typeFilter.Remove("Future")) FilterFuture.IsChecked = false;
            _typeFilter.Remove("All");
            if (btn.IsChecked == true) _typeFilter.Add(tag);
            else _typeFilter.Remove(tag);
            // 四档全取消 → 自动回「全部」
            if (_typeFilter.Count == 0)
            {
                _typeFilter.Add("All");
                FilterAll.IsChecked = true;
            }
            else
            {
                FilterAll.IsChecked = false;
            }
        }
        ReloadNotes();
    }

    // ── v3.6：来源多选 popup 外部点击关闭（Popup 默认 StaysOpen=True 后手动维护可见性） ──
    /// <summary>v3.6：来源筛选框点击 → 开/合下拉。开 popup 时注册窗口级外击监听；关时清理。</summary>
    private void SourceFilterBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFilterPopup.IsOpen)
            CloseSourcePopup();
        else
        {
            UpdateSourceFilterOptions();
            SourceFilterPopup.IsOpen = true;
            Mouse.AddMouseDownHandler(this, OnWindowMouseDown_OutsideSourcePopup);
        }
    }

    private void CloseSourcePopup()
    {
        SourceFilterPopup.IsOpen = false;
        Mouse.RemoveMouseDownHandler(this, OnWindowMouseDown_OutsideSourcePopup);
    }

    /// <summary>点 popup 外（不在按钮也不在 popup 子树内）→ 关 popup。OriginalSource 判断所属控件路径。</summary>
    private void OnWindowMouseDown_OutsideSourcePopup(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src)
        {
            if (IsDescendantOf(src, SourceFilterBtn) || IsDescendantOf(src, SourceFilterPopup.Child))
                return;
        }
        CloseSourcePopup();
    }

    /// <summary>沿 VisualTree 向上查找 ancestor 中是否包含 target。</summary>
    private static bool IsDescendantOf(DependencyObject node, DependencyObject? ancestor)
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

    // ── v3.5 编辑时间识别建议条 ──

    /// <summary>显示建议条（编辑框区域上方浮出，10 秒自动消失）。文案用 TimeParser.FormatNaturalTime 生成。</summary>
    private void ShowSuggestBar(DateTime due)
    {
        SuggestText.Text = $"检测到{TimeParser.FormatNaturalTime(due)}，设为提醒？";
        // 定位：建议条浮在当前编辑条目容器上方（Canvas 覆盖层坐标）
        if (_suggestVm != null && NotesList.ItemContainerGenerator.ContainerFromItem(_suggestVm) is FrameworkElement container)
        {
            try
            {
                var p = container.TransformToVisual(FloatToolbarCanvas).Transform(new Point(0, 0));
                Canvas.SetLeft(SuggestBar, Math.Max(0, Math.Min(p.X, FloatToolbarCanvas.ActualWidth - SuggestBar.ActualWidth - 4)));
                Canvas.SetTop(SuggestBar, Math.Max(0, p.Y - SuggestBar.ActualHeight - 4));
            }
            catch { Canvas.SetLeft(SuggestBar, 10); Canvas.SetTop(SuggestBar, 10); }
        }
        else { Canvas.SetLeft(SuggestBar, 10); Canvas.SetTop(SuggestBar, 10); }

        SuggestBar.Visibility = Visibility.Visible;
        _suggestTimer?.Stop();
        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); HideSuggestBar(); };
        _suggestTimer.Start();
    }

    private void HideSuggestBar()
    {
        _suggestTimer?.Stop();
        SuggestBar.Visibility = Visibility.Collapsed;
        _suggestVm = null;
        _suggestDue = null;
    }

    /// <summary>设为提醒：UpdateTodo(newContent: vm.Content(已同步新正文), dueTime) 原地改行</summary>
    private void SuggestDueSet_Click(object sender, RoutedEventArgs e)
    {
        var vm = _suggestVm;
        var due = _suggestDue;
        HideSuggestBar();
        if (vm == null || !due.HasValue) return;
        if (_noteService.UpdateTodo(vm.Entry, newContent: vm.Entry.Content, dueTime: due.Value))
            Refresh();
    }

    /// <summary>忽略：仅收起建议条，编辑内容保留</summary>
    private void SuggestDueIgnore_Click(object sender, RoutedEventArgs e)
    {
        HideSuggestBar();
    }

    /// <summary>双击进入编辑态：沉浸式锁定时弹窗拦截</summary>
    private double _scrollOffsetBeforeEdit; // 进入编辑前的列表滚动位置，编辑后恢复

    // 进入编辑时 SelectAll 会触发一次 SelectionChanged，用此标志抑制（全选不弹浮动工具条，用户主动拖选才弹）
    private bool _suppressToolbarOnSelectAll;

    private void BeginEditNote(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 同一时间只允许编辑一条
        foreach (var other in _viewModels.Where(v => v.IsEditing && !ReferenceEquals(v, vm)))
            other.CancelEdit();
        CancelEditState(vm);

        // 记录编辑前 ScrollViewer 滚动偏移，编辑后恢复（ScrollViewer 是 NotesList 的父级，不是后代）
        _scrollOffsetBeforeEdit = NotesScroll?.VerticalOffset ?? 0;

        vm.BeginEdit();
        _activeEditVm = vm;
        // 等模板切换完成后聚焦编辑框。
        // 优先级用 Loaded 而非原先的 Background（2026-09-18）：Dispatcher 优先级由高到低为
        // DataBind(9) > Render(8) > Loaded(7) > Input(6) > Background(5)。模板切换由绑定（DataBind）
        // 与布局渲染（Render）驱动，两者仍都早于 Loaded，所以不会拿到旧模板；
        // 而 Background 表示"等所有任务都空闲"，列表刷新/输入排队时聚焦会明显延后。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (NotesList.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement container)
            {
                var box = FindVisualChild<TextBox>(container);
                AttachEditBox(vm, box);
                _suppressToolbarOnSelectAll = true; // 全选触发 SelectionChanged 时不弹浮动工具条
                box?.Focus();
                box?.SelectAll();
                // 修复：长文本全选后 TextBox 自动滚动到选区末尾，内容"被拉到下面看不见"——拉回开头
                box?.ScrollToHome();

                // 恢复列表滚动位置到编辑前（Focus 触发的 ScrollIntoView 会让编辑项滚到视口边缘，后面笔记被挤出去）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NotesScroll?.ScrollToVerticalOffset(_scrollOffsetBeforeEdit);
                }), DispatcherPriority.Background);
            }
        }), DispatcherPriority.Loaded);
    }

    private void BtnEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteEntryViewModel vm) SaveEditNote(vm);
    }

    private void BtnEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteEntryViewModel vm)
        {
            CancelEditState(vm);
            vm.CancelEdit();
        }
    }

    /// <summary>全屏编辑：独立 Window，与行内编辑共享 EditText（双向同步）</summary>
    private void BtnFullEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoteEntryViewModel vm }) OpenFullEdit(vm);
    }

    private void OpenFullEdit(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 若未在行内编辑，先同步 EditText = 原始内容；若在行内编辑过，保留行内最新内容
        if (!vm.IsEditing)
            vm.EditText = vm.Content;

        // 关闭行内编辑态（EditText 值保留给全屏窗口）
        CancelEditState(vm);
        vm.CancelEdit();

        var win = new NoteEditWindow(_noteService, vm,
            $"编辑笔记 · {vm.Entry.Timestamp:yyyy-MM-dd HH:mm}", _aiProvider, _settings)
        { Owner = this };

        if (win.ShowDialog() == true)
            Refresh();
    }

    /// <summary>
    /// 保存编辑（v5 2026-09-20）：笔记/待办统一走 TodoEditService.SaveEdited **原地替换**改行
    /// （笔记此前走 AppendEdit 追加【编辑】行，因跨端同步双条/孤儿卡问题经用户拍板改为替换）。
    /// 保存成功后对待办做时间识别——规则优先（TimeParser），
    /// 规则未命中才调 LLM 兜底（DetectDueAsync），识别到未来时间弹建议条；未识别到时间不自动清除原提醒。
    /// async void：LLM 调用放后台线程（DetectDueAsync 内部），禁止 UI 线程同步阻塞等 LLM。
    /// </summary>
    private async void SaveEditNote(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            vm.CancelEdit();
            return;
        }

        // 分离 AI 释义块（编辑时拼接显示，保存时剥离开来保持"MD 只增不减"结构）：
        // 主内容保存；释义行只能由 AI 对话框追加，编辑框内改动不写回存储
        var (contentToSave, _) = NoteEntryViewModel.SplitEditText(vm.EditText?.Trim() ?? "");
        if (string.IsNullOrEmpty(contentToSave))
        {
            System.Windows.MessageBox.Show("内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 内容未变化：不改行（替换语义下无变化即无操作）
        var displayBase = vm.Entry.EditedContent ?? vm.Entry.Content;
        if (contentToSave != displayBase)
        {
            if (!TodoEditService.SaveEdited(_noteService, vm.Entry, contentToSave))
            {
                System.Windows.MessageBox.Show("保存失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                vm.CancelEdit();
                return;
            }
            // 存储层状态同步（v5 2026-09-20：笔记编辑=原地替换，同待办）：
            // UpdateNote/UpdateTodo 内部已回写 entry.Content；笔记的旧 EditedContent（历史【编辑】
            // 痕迹的合并结果）必须清掉——痕迹行已随替换清理，留着会用旧文本盖住新正文
            vm.Entry.Content = contentToSave;
            if (vm.Entry.Type != NoteType.Todo)
                vm.Entry.EditedContent = null;
        }

        CancelEditState(vm);
        Refresh();

        // v3.5：待办时间识别建议（规则优先；规则未命中才调 LLM 兜底；未配 Key/异常 → 优雅降级不弹建议条不崩）
        // v2：ResolveDueAsync 统一处理——裸时钟已过弹三选一、纯日期弹"问几点"、未来时间直接返回
        if (vm.Entry.Type == NoteType.Todo)
        {
            var due = await TodoEditService.ResolveDueAsync(this, contentToSave, _aiProvider);
            if (due.HasValue)
            {
                _suggestVm = vm;
                _suggestDue = due.Value;
                ShowSuggestBar(due.Value);
            }
        }
    }

    // ── 编辑态浮动工具条 ──

    /// <summary>绑定当前编辑框的选中/失焦事件；切换编辑目标时解绑旧的</summary>
    private void AttachEditBox(NoteEntryViewModel vm, TextBox? box)
    {
        if (_activeEditBox != null)
        {
            _activeEditBox.SelectionChanged -= EditBox_SelectionChanged;
            _activeEditBox.LostFocus -= EditBox_LostFocus;
        }
        _activeEditBox = box;
        if (box != null)
        {
            box.SelectionChanged += EditBox_SelectionChanged;
            box.LostFocus += EditBox_LostFocus;
        }
        HideFloatToolbar();
    }

    /// <summary>取消编辑状态：解绑编辑框、清计时器、隐藏工具条</summary>
    private void CancelEditState(NoteEntryViewModel? vm)
    {
        if (vm != null && _activeEditVm != null && !ReferenceEquals(vm, _activeEditVm)) return;

        if (_activeEditBox != null)
        {
            _activeEditBox.SelectionChanged -= EditBox_SelectionChanged;
            _activeEditBox.LostFocus -= EditBox_LostFocus;
        }
        _activeEditBox = null;
        _activeEditVm = null;
        _selectionTimer?.Stop();
        _suppressToolbarOnSelectAll = false;
        HideFloatToolbar();
    }

    private void EditBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var selected = box.SelectionLength > 0 && !string.IsNullOrEmpty(box.SelectedText);
        _selectionTimer?.Stop();

        // 进入编辑时 SelectAll 触发的全选：不弹工具条（用户主动拖选才弹）
        if (_suppressToolbarOnSelectAll)
        {
            _suppressToolbarOnSelectAll = false;
            HideFloatToolbar();
            return;
        }

        if (selected && box.IsKeyboardFocused)
        {
            _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _selectionTimer.Tick += (_, _) =>
            {
                _selectionTimer.Stop();
                try { ShowFloatToolbar(); }
                catch { HideFloatToolbar(); } // 编辑框已失效等异常，兜底隐藏不崩
            };
            _selectionTimer.Start();
        }
        else
        {
            HideFloatToolbar();
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _selectionTimer?.Stop();
        // 延迟：避免点击浮动工具条/编辑态内按钮时 TextBox 失焦导致按钮 IsHitTestVisible=false（Collapsed）收不到 Click
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 焦点仍在编辑态 UI（EditBox/全屏/保存/取消，DataContext 都是当前 vm）或浮动工具条内 → 不自动保存退出
            if (!FloatToolbar.IsKeyboardFocusWithin && !IsFocusInEditingArea())
            {
                // 焦点真正离开编辑区：自动保存退出（用户期望：点击被编辑笔记以外的任何位置都默认保存退出）
                if (_activeEditVm != null && _activeEditVm.IsEditing)
                {
                    try { SaveEditNote(_activeEditVm); }
                    catch { /* 已被外部流程处理（如删除/刷新）则忽略 */ }
                }
                HideFloatToolbar();
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>焦点是否落在当前编辑态 UI 内（EditBox / 全屏 / 保存 / 取消，DataContext 均指向当前 vm）</summary>
    private bool IsFocusInEditingArea()
    {
        if (_activeEditVm == null) return false;
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused != null)
        {
            if (focused is FrameworkElement fe && ReferenceEquals(fe.DataContext, _activeEditVm))
                return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    /// <summary>在选中文字附近显示浮动工具条（Canvas 浮层定位，不参与布局；超界时靠边）</summary>
    private void ShowFloatToolbar()
    {
        var box = _activeEditBox;
        if (box == null || _activeEditVm == null) return;
        if (box.SelectionLength <= 0) return;

        try
        {
            // 选中起点相对覆盖层 Canvas 的坐标
            // 注意：必须用 TransformToVisual（不要求祖先关系）——FloatToolbarCanvas 是 ScrollViewer 的兄弟，不是 EditBox 的祖先，
            // 用 TransformToAncestor 会抛 InvalidOperationException 导致闪退
            var rect = box.GetRectFromCharacterIndex(box.SelectionStart, false);
            var point = box.TransformToVisual(FloatToolbarCanvas).Transform(new Point(rect.Left, rect.Bottom));

            FloatToolbar.Visibility = Visibility.Visible;
            FloatToolbar.UpdateLayout();

            var left = Math.Min(Math.Max(0, point.X), FloatToolbarCanvas.ActualWidth - FloatToolbar.ActualWidth - 4);
            var top = Math.Min(Math.Max(0, point.Y + 4), FloatToolbarCanvas.ActualHeight - FloatToolbar.ActualHeight - 4);
            Canvas.SetLeft(FloatToolbar, left);
            Canvas.SetTop(FloatToolbar, top);
        }
        catch
        {
            // 编辑框可能已从可视树移除（退出编辑/删除等），任何布局异常都不崩，直接隐藏
            HideFloatToolbar();
        }
    }

    private void HideFloatToolbar()
    {
        FloatToolbar.Visibility = Visibility.Collapsed;
    }

    /// <summary>工具条拖动手柄：Thumb 拖动时限制在 Canvas 范围内</summary>
    private void ToolbarDragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Canvas.GetLeft(FloatToolbar) + e.HorizontalChange;
        var top = Canvas.GetTop(FloatToolbar) + e.VerticalChange;
        left = Math.Max(0, Math.Min(left, FloatToolbarCanvas.ActualWidth - FloatToolbar.ActualWidth));
        top = Math.Max(0, Math.Min(top, FloatToolbarCanvas.ActualHeight - FloatToolbar.ActualHeight));
        Canvas.SetLeft(FloatToolbar, left);
        Canvas.SetTop(FloatToolbar, top);
    }

    private void FloatToolbar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button { Tag: string mode }) return;
            var vm = _activeEditVm;
            // 优先选中文字；若点击瞬间选区丢失（失焦等），兜底用编辑框全文——保证点翻译/搜索一定有内容可发
            var selected = _activeEditBox?.SelectedText?.Trim();
            if (string.IsNullOrEmpty(selected))
                selected = vm?.EditText?.Trim();
            if (vm == null || string.IsNullOrEmpty(selected)) { HideFloatToolbar(); return; }

            var explainMode = mode switch
            {
                "Translate" => ExplainMode.Translate,
                "Search" => ExplainMode.Search,
                _ => ExplainMode.Ask,
            };

            HideFloatToolbar();
            AIDialogHelper.Open(explainMode, vm.Entry, selected);
        }
        catch
        {
            // 任何异常都不崩，静默隐藏工具条
            HideFloatToolbar();
        }
    }

    /// <summary>在可视树中查找指定类型的第一个后代</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>从原始点击源向上回溯，判断是否落在 CheckBox 上</summary>
    private static bool IsClickFromCheckBox(DependencyObject? src)
    {
        var d = src;
        while (d != null)
        {
            if (d is CheckBox) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ── 多选 / 删除 ──

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _viewModels) vm.IsSelected = true;
        UpdateSelectionUI();
    }

    private void BtnInvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _viewModels) vm.IsSelected = !vm.IsSelected;
        UpdateSelectionUI();
    }

    // ── v3.11（2026-09-26）：行尾「AI 整理」按钮 ──

    /// <summary>同一时间只允许整理一条（按钮禁用 + 本标志双保险）—— 避免连点发出多个请求、回来时列表已被重建。</summary>
    private bool _tidyRunning;

    /// <summary>
    /// 行尾「AI」按钮：把这条内容交给大模型整理，先出预览对照窗再决定改不改。
    /// 行内编辑态下整理的是**编辑框里正在改的内容**（不是已落盘的旧文本），否则用户会看到"我改了它却按旧内容整理"。
    /// 落库口径（替换原文 / 另存新笔记 / 复制）全部收在 AiTidyFlow，与本面板之外的另两个入口共用一份，防行为漂移。
    /// </summary>
    private async void BtnAiTidy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NoteEntryViewModel vm) return;
        if (_tidyRunning) return;

        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可整理", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = vm.IsEditing ? vm.EditText : vm.Content;

        _tidyRunning = true;
        var originalContent = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "…";
        try
        {
            // 预览窗非模态（2026-09-27）：窗口打开后本方法即返回；落库发生在关窗时，经回调刷新列表。
            // 预览窗开着时面板保持可操作 —— 用户可翻列表、编辑别的条目，甚至再开一个预览窗。
            // 只有真的改了数据（替换原文 / 另存新笔记 / 创建待办）才刷新；复制不改数据，刷新反而会打断正在进行的编辑
            await AiTidyFlow.RunAsync(this, _noteService, _settings, _aiProvider, vm.Entry, text,
                result => { if (result?.DataChanged == true) Refresh(); });
        }
        finally
        {
            _tidyRunning = false;
            btn.IsEnabled = true;
            btn.Content = originalContent;   // 列表可能已被 Refresh 重建，此时改的是废弃实例，无害
        }
    }

    private void BtnDeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteEntryViewModel vm)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"确认删除这条笔记？\n\n时间：{vm.Timestamp:HH:mm}\n内容：{vm.FirstLine}\n\n" +
                "（将移入回收站，30 天内可恢复）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            if (!_noteService.DeleteNote(vm.Entry))
            {
                System.Windows.MessageBox.Show("删除失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Refresh();   // 2026-08-15 修复：同上（单条 × 删除后实时刷新，原 Remove+Items.Refresh 对 ItemsSource=List 无效）
        }
    }

    private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确认删除选中的 {selected.Count} 条笔记？\n\n" +
            "（将移入回收站，30 天内可恢复）",
            "批量删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var failed = 0;
        foreach (var entry in selected)
        {
            if (!_noteService.DeleteNote(entry)) failed++;
        }
        Refresh(); // 重载后已删除项自然消失，失败项保留
        if (failed > 0)
        {
            System.Windows.MessageBox.Show($"{failed} 条笔记删除失败（未在笔记文件中找到，可能已被外部修改）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>任一勾选即显示"删除已选"按钮（勾选/取消由 NoteCheckBox_Click 同步触发）</summary>
    private void UpdateSelectionUI()
    {
        var count = _viewModels.Count(vm => vm.IsSelected);
        if (count > 0)
        {
            SelectedCountText.Text = $"已选 {count} 条";
            SelectedCountText.Visibility = Visibility.Visible;
            BtnDeleteSelected.Visibility = Visibility.Visible;
            SetToolbarButtonContent("Export", $"▼ 导出已选 {count} 条");
        }
        else
        {
            SelectedCountText.Visibility = Visibility.Collapsed;
            BtnDeleteSelected.Visibility = Visibility.Collapsed;
            SetToolbarButtonContent("Export", "▼ 导出");
        }
    }

    /// <summary>按 id 改标题栏按钮文案（按钮可能已被移出工具栏，无则跳过）。</summary>
    private void SetToolbarButtonContent(string id, string content)
    {
        if (_toolbarButtons.TryGetValue(id, out var btn)) btn.Content = content;
    }

    /// <summary>CheckBox 勾选/取消 → 同步选中状态并更新删除按钮可见性</summary>
    private void NoteCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: NoteEntryViewModel vm } cb)
            vm.IsSelected = cb.IsChecked == true;
        UpdateSelectionUI();
    }

    private List<NoteEntry> GetSelectedEntries()
        => _viewModels.Where(vm => vm.IsSelected).Select(vm => vm.Entry).ToList();

    public new void Show()
    {
        ApplySummonBehavior();
        Refresh();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;   // v3.9：最小化中热键唤出 → 先恢复正常态
        base.Show();
        Activate();
        Focus();
    }

    public new void Hide()
    {
        _datePicker?.Close();
        if (_exportDialog != null && _exportDialog.IsVisible)
        {
            _exportDialog.Close();
            _exportDialog = null;
        }
        base.Hide();
    }
}
