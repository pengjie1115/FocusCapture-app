using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Destinations;
using FocusCapture.Services.Destinations.GetNote;
using FocusCapture.Services.Files;
using FocusCapture.Services.Skills;
using FocusCapture.Services.Sync;
using Microsoft.Win32;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FocusCapture.Windows;

public partial class SettingsWindow : Window
{
    private Models.AppSettings _settings = null!;
    private readonly HotkeyService? _hotkeyService;
    private readonly Action? _onChanged;
    private readonly NoteService? _noteService;
    private readonly Func<SyncEngine?>? _syncEngineProvider;   // 实时取 MainWindow 当前引擎（配置保存后由 MainWindow 重建）
    private readonly Func<ChatSyncEngine?>? _chatSyncEngineProvider; // 实时取当前会话同步引擎（状态实时刷新 + 重新拉取入口）
    private readonly Action? _onSyncConfigChanged;              // 保存 WebDAV 配置后通知 MainWindow 重建引擎
    private bool _capturing;
    private Action<Models.HotkeyBinding>? _onCaptureDone;
    private bool _suppressEvents = true; // 抑制 InitializeComponent 期间的 ValueChanged 事件

    public SettingsWindow(Models.AppSettings s, HotkeyService? hk = null, Action? onChanged = null,
        NoteService? noteService = null, Func<SyncEngine?>? syncEngineProvider = null, Action? onSyncConfigChanged = null,
        Func<ChatSyncEngine?>? chatSyncEngineProvider = null)
    {
        _settings = s; _hotkeyService = hk; _onChanged = onChanged; _noteService = noteService;
        _syncEngineProvider = syncEngineProvider; _onSyncConfigChanged = onSyncConfigChanged;
        _chatSyncEngineProvider = chatSyncEngineProvider;
        InitializeComponent();
        _suppressEvents = false; // 初始化完成，允许事件处理
        BuildSearchIndex();
        ShowSection(0);
        LoadSettings(); KeyDown += OnKeyDown;
        // v3.8：录制热键期间全局热键被临时注销（见 StartCapture）——录制中途直接关窗时补回注册，
        // 否则用户的热键会整体失效且毫无提示（三个出口：正常录完 / Esc 取消 / 关窗，缺一不可）
        Closed += (_, _) => { if (_capturing) _hotkeyService?.RegisterAll(); };
        SubscribeChatSyncStatus();
    }

    /// <summary>订阅会话同步状态变化：设置页打开期间「AI 问答记录」状态实时刷新（2026-09-09 修复：
    /// 原先只在打开瞬间读一次留痕，会话同步跑的几分钟里界面一直停在旧文案，看起来像卡死）。</summary>
    private void SubscribeChatSyncStatus()
    {
        var chat = _chatSyncEngineProvider?.Invoke();
        if (chat == null) return;
        chat.StatusChanged += msg => Dispatcher.Invoke(() =>
        {
            if (!IsLoaded) return;
            SyncStatusText.Text = SyncStatusText.Text.Split('\n')[0] + $"\nAI 问答记录：{msg}";
        });
    }

    // ═══════ 板块导航 + 搜索（v3.7 设置大改版） ═══════

    /// <summary>搜索索引条目（由 BuildSearchIndex 自动扫描生成）</summary>
    private class SettingEntry
    {
        public required string Section { get; init; }
        public required int SectionIndex { get; init; }
        public required string Title { get; init; }
        public string Hint { get; set; } = "";
        public required FrameworkElement Target { get; init; }
        public string Subtitle => string.IsNullOrEmpty(Hint) ? Section : $"{Section} · {Hint}";
    }

    private readonly List<SettingEntry> _searchIndex = new();
    private readonly string[] _sectionNames = { "热键", "AI 模型", "AI 功能", "整理规则", "外观", "显示", "灵感速览", "输入框", "云同步", "待办与提醒", "文件与网盘", "通用", "激活码" };
    private bool _navSuppress; // 程序化切换导航选中项时抑制事件

    /// <summary>板块面板列表，顺序与 _sectionNames / 左侧导航一一对应</summary>
    private StackPanel[] SectionPanels() => new[]
    {
        PanelHotkey, PanelAiModel, PanelAi, PanelTidyRules, PanelAppearance, PanelDisplay, PanelQuickView,
        PanelInput, PanelSync, PanelTodo, PanelFiles, PanelGeneral, PanelLicense
    };

    /// <summary>外部跳转到指定板块（按导航名，如「整理规则」）。找不到就不动 —— 调用方不必先判存在。</summary>
    public void SelectSection(string title)
    {
        for (int i = 0; i < NavList.Items.Count; i++)
        {
            if ((NavList.Items[i] as ListBoxItem)?.Content as string == title)
            {
                NavList.SelectedIndex = i;   // 触发 NavList_SelectionChanged → ShowSection(i)
                return;
            }
        }
    }

    /*
     * ══ 新增设置项的写死规则（自动扫描约定）══
     * 1. 每个板块面板（PanelXxx）的【直接子元素】视为一行设置项；
     *    紧跟在某行后面、且颜色为 #999999 的 TextBlock 视为该行的「说明文字」。
     * 2. 设置项名称取行内第一个【非灰色、非空】的 TextBlock 文字；
     *    独立摆放的非灰色 TextBlock 归为【下一行】的名称（如「笔记存储路径」）；
     *    都没有时回退取第一个 Button 的 Content 文字。
     * 3. 跳转/高亮目标取行内第一个可交互控件（Button/TextBox/PasswordBox/ComboBox/Slider/CheckBox/RadioButton）。
     * 4. 名称、说明、板块名都参与搜索匹配。
     * ⇒ 以后新增设置项只需把控件按上述结构加进对应板块面板的 XAML，搜索索引自动生效，无需改 C#。
     * ⇒ 新增板块才需要动三处：导航 ListBox 加一项、_sectionNames 加名字、SectionPanels() 加面板。
     */
    private void BuildSearchIndex()
    {
        _searchIndex.Clear();
        var panels = SectionPanels();
        for (int i = 0; i < panels.Length; i++)
        {
            SettingEntry? last = null;
            string? pendingTitle = null; // 板块内独立摆放的非灰色标签，归为下一行的名称（如「笔记存储路径」）
            foreach (var child in panels[i].Children.OfType<FrameworkElement>())
            {
                // 板块内独立的灰色小字 = 上一行的说明
                if (IsHintTextBlock(child, out var loneHint))
                {
                    if (last != null) last.Hint = loneHint;
                    continue;
                }
                // 板块内独立的非灰色标签 = 下一行的名称
                if (child is TextBlock label && !string.IsNullOrWhiteSpace(label.Text))
                {
                    pendingTitle = label.Text.Trim();
                    continue;
                }

                var descendants = Walk(child).OfType<FrameworkElement>().ToList();
                var target = descendants.FirstOrDefault(IsInteractiveControl);
                if (target == null) continue; // 纯装饰行（分隔线等）不入索引

                var title = pendingTitle
                    ?? descendants.OfType<TextBlock>()
                        .Where(t => !IsGray(t) && !string.IsNullOrWhiteSpace(t.Text))
                        .Select(t => t.Text.Trim())
                        .FirstOrDefault()
                    ?? (target is Button b ? b.Content?.ToString() ?? "" : "").Trim();
                if (title.Length == 0 && target is CheckBox cb) title = cb.Content?.ToString() ?? "";
                if (title.Length == 0) continue;
                pendingTitle = null;

                // 行内嵌的灰色小字也可作说明
                var inlineHint = descendants.OfType<TextBlock>()
                    .Where(t => IsGray(t) && !string.IsNullOrWhiteSpace(t.Text))
                    .Select(t => t.Text.Trim())
                    .FirstOrDefault() ?? "";

                last = new SettingEntry
                {
                    Section = _sectionNames[i], SectionIndex = i,
                    Title = title, Hint = inlineHint, Target = target
                };
                _searchIndex.Add(last);
            }
        }
    }

    private static bool IsInteractiveControl(FrameworkElement fe) => fe is Button or TextBox
        or PasswordBox or ComboBox or Slider or CheckBox or RadioButton;

    private static bool IsGray(TextBlock t) =>
        t.Foreground is SolidColorBrush b && b.Color == Color.FromRgb(0x99, 0x99, 0x99);

    private static bool IsHintTextBlock(FrameworkElement fe, out string text)
    {
        if (fe is TextBlock t && IsGray(t) && !string.IsNullOrWhiteSpace(t.Text))
        { text = t.Text.Trim(); return true; }
        text = ""; return false;
    }

    /// <summary>先序遍历可视化树（含自身）</summary>
    private static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(node, i)))
                yield return d;
    }

    /// <summary>切换到指定板块（右侧只显示对应面板）</summary>
    private void ShowSection(int index)
    {
        var panels = SectionPanels();
        for (int i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = _sectionNames[index];
        ContentScroller.ScrollToTop();
        _navSuppress = true;
        NavList.SelectedIndex = index;
        _navSuppress = false;

        // 打开「AI 模型」板块时自动探一次供应商状态（2026-09-23）。
        // 用**面板引用**判断而不是索引数字：索引写死的话，往导航里插一个板块就会
        // 静默探到隔壁板块去（不报错，只是行为不对）—— 快照工具刚因为硬编码索引踩过一次。
        if (ReferenceEquals(panels[index], PanelAiModel)) _ = ProbeAiProvidersAsync();

        // 打开「激活码」板块时把状态区刷成最新（2026-10-08）。
        // 同样用面板引用而非索引 —— 状态可能因为用户刚在别处激活/跨天启动而变化。
        if (ReferenceEquals(panels[index], PanelLicense)) RefreshLicenseSection();
    }

    /// <summary>探测进行中（防重入：用户在导航里来回点时不要叠出多批请求）。</summary>
    private bool _aiProbing;

    /// <summary>
    /// 并发探测全部已配置供应商并把结论写回状态点。
    ///
    /// <para><b>触发时机＝打开板块时</b>，刻意不做启动探测、也不做后台定时（用户拍板，设计稿 §5.1）：
    /// 用户需要状态点准确的时刻就是他打开面板看的那一刻。</para>
    ///
    /// <para>不弹任何模态框、失败不改配置；探测最长 5 秒，所以先给卡片降透明度做「检测中」的中间态，
    /// 否则点下去像卡住。</para>
    /// </summary>
    private async Task ProbeAiProvidersAsync()
    {
        if (_aiProbing || _settings.AiModelProviders.Count == 0) return;
        _aiProbing = true;
        try
        {
            foreach (var card in AiProviderList.Children.OfType<FrameworkElement>())
                card.Opacity = 0.6;

            // 取快照再探：探测期间用户可能已经删掉或新加了供应商（5 秒足够干这些事）
            var providers = _settings.AiModelProviders.ToList();
            var results = await AiProviderHealthService.ProbeAllAsync(providers);

            foreach (var p in providers)
                if (results.TryGetValue(p.Id, out var verdict))
                    AiProviderHealthService.Apply(p, verdict);

            _settings.Save();          // 落盘才有时戳可留到下次打开
            RebuildAiProviderList();
        }
        catch
        {
            // 兜底：探测本身不抛，这里防的是「写盘 / 重建界面」这类本地意外。
            // 绝不能让它冒到 ShowSection 上，否则打开设置面板会跟着出问题。
        }
        finally
        {
            _aiProbing = false;
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSuppress || NavList.SelectedIndex < 0) return;
        ShowSection(NavList.SelectedIndex);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchWatermark.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnClearSearch.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (q.Length == 0)
        {
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            NavList.Visibility = Visibility.Visible;
            return;
        }
        var matches = _searchIndex
            .Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || x.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || x.Section.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SearchCountText.Text = matches.Count > 0 ? $"{matches.Count} 个匹配项" : "无匹配项";
        SearchResults.ItemsSource = matches;
        NavList.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Visible;
    }

    private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        Keyboard.Focus(SearchBox);
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SettingEntry entry) return;
        SearchResults.SelectedIndex = -1; // 允许重复点击同一结果
        ShowSection(entry.SectionIndex);
        HighlightEntry(entry);
    }

    /// <summary>跳转到设置项：滚动定位 + 所在行短暂绿色高亮</summary>
    private void HighlightEntry(SettingEntry entry)
    {
        var target = entry.Target;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            target.BringIntoView();

            // 向上找到「板块面板的直接子元素」作为整行高亮宿主
            DependencyObject node = target;
            FrameworkElement? row = null;
            while (node != null)
            {
                if (node is FrameworkElement fe && VisualTreeHelper.GetParent(fe) is StackPanel sp
                    && sp.Name.StartsWith("Panel", StringComparison.Ordinal))
                { row = fe; break; }
                node = VisualTreeHelper.GetParent(node);
            }
            if (row is not Panel p) return;

            var brush = new SolidColorBrush(Color.FromArgb(0x55, 0x4C, 0xAF, 0x50));
            p.Background = brush;
            var anim = new ColorAnimation(
                Color.FromArgb(0x55, 0x4C, 0xAF, 0x50), Colors.Transparent,
                new Duration(TimeSpan.FromSeconds(1.5)))
            { FillBehavior = FillBehavior.Stop };
            anim.Completed += (_, _) => p.Background = null;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LoadSettings()
    {
        _suppressEvents = true;
        BtnSummonHotkey.Content = Win32.HotkeyToString(_settings.SummonHotkey);
        BtnClipboardHotkey.Content = Win32.HotkeyToString(_settings.ClipboardToggleHotkey);
        BtnQuickViewHotkey.Content = Win32.HotkeyToString(_settings.QuickViewHotkey);
        BtnVoiceInputHotkey.Content = Win32.HotkeyToString(_settings.VoiceInputHotkey);
        BtnSettingsHotkey.Content = Win32.HotkeyToString(_settings.SettingsHotkey);
        BtnAiAskHotkey.Content = Win32.HotkeyToString(_settings.AiAskHotkey);
        BtnTodoSummaryHotkey.Content = Win32.HotkeyToString(_settings.TodoSummaryHotkey);
        InputOpacitySlider.Value = _settings.InputOpacity;
        BallOpacitySlider.Value = _settings.FloatBallOpacity;
        QuickViewOpacitySlider.Value = _settings.QuickViewOpacity;
        InputOpacityLabel.Text = $"{(int)(_settings.InputOpacity * 100)}%";
        BallOpacityLabel.Text = $"{(int)(_settings.FloatBallOpacity * 100)}%";
        QuickViewOpacityLabel.Text = $"{(int)(_settings.QuickViewOpacity * 100)}%";
        InputBorderRadiusSlider.Value = _settings.InputBorderRadius;
        InputBorderRadiusLabel.Text = $"{(int)Math.Round(_settings.InputBorderRadius)}";
        // AI 问答界面明暗（2026-09-27）：滑块回显 + 标签定性
        ChatShadeSlider.Value = _settings.ChatShade;
        ChatShadeLabel.Text = _settings.ChatShade >= 0.75 ? "深色" : _settings.ChatShade <= 0.25 ? "浅色" : $"{(int)Math.Round(_settings.ChatShade * 100)}%";
        // 悬浮球拖放保存（2026-09-16）：开关 + 浮层不透明度 + 小条停留秒数
        DragToSaveCheck.IsChecked = _settings.DragToSaveEnabled;
        DropActionOpacitySlider.Value = Math.Clamp(_settings.DropActionOpacity, 0.3, 1.0);
        DropActionOpacityLabel.Text = $"{(int)(_settings.DropActionOpacity * 100)}%";
        DropActionStripSlider.Value = Math.Clamp(_settings.DropActionStripSeconds, 2, 10);
        DropActionStripLabel.Text = $"{_settings.DropActionStripSeconds} 秒";
        NotesPathText.Text = _settings.NotesPath;
        AutoStartCheck.IsChecked = _settings.AutoStart;
        // v3.8：灵感速览唤出行为下拉回显（v3.9 起在「灵感速览」板块）
        foreach (ComboBoxItem it in QuickViewSummonCombo.Items)
            if ((string)it.Tag == (_settings.QuickViewRestoreLastFilter ? "Restore" : "Today"))
                QuickViewSummonCombo.SelectedItem = it;
        // v3.9：灵感速览面板宽度 / 置顶 / 标题栏自定义编辑器回显
        QuickViewWidthInput.Text = ((int)Math.Clamp(_settings.QuickViewWidth,
            QuickViewWindow.MinWidthLimit, QuickViewWindow.MaxWidthLimit)).ToString();
        QuickViewTopmostCheck.IsChecked = _settings.QuickViewTopmost;
        RebuildToolbarEditor();
        // 图片清晰度档位：索引即档位值（0 省流 / 1 标准 / 2 高清），与 ChatAttachmentService.QualitySpec 对应
        AiImageQualityCombo.Items.Clear();
        AiImageQualityCombo.Items.Add(new ComboBoxItem { Content = "省流（长边 768）" });
        AiImageQualityCombo.Items.Add(new ComboBoxItem { Content = "标准（长边 1568）" });
        AiImageQualityCombo.Items.Add(new ComboBoxItem { Content = "高清（长边 2048）" });

        RebuildAiProviderList();          // 多供应商卡片列表（2026-09-23）
        AiAssistantNameInput.Text = _settings.AiAssistantName;
        AgentEnabledCheck.IsChecked = _settings.AgentEnabled;
        AgentWriteConfirmCheck.IsChecked = _settings.AgentWriteConfirmPopup;
        AiFsToolsEnabledCheck.IsChecked = _settings.AiFsToolsEnabled;
        RebuildAiAllowedDirs();
        AgentMaxToolRoundsInput.Text = _settings.AgentMaxToolRounds.ToString();
        AiToolResultLimitInput.Text = _settings.AiToolResultLimit.ToString();
        AiImageQualityCombo.SelectedIndex = Math.Clamp(_settings.AiImageQualityLevel, 0, 2);
        AiVisionEnabledCheck.IsChecked = _settings.AiVisionEnabled;
        GetNoteKeyInput.Password = _settings.GetNoteApiKey;
        GetNoteClientIdInput.Text = _settings.GetNoteClientId;
        GetNoteTestResult.Text = "";
        LogRetentionInput.Text = _settings.LogRetentionDays.ToString();

        UpdateIconUI();
        // AI 问答界面（2026-09-24）：昵称 / 自定义欢迎语 / 侧边栏默认展开 / 图片资源状态回显
        ChatNicknameInput.Text = _settings.ChatUserNickname ?? "";
        ChatWelcomeInput.Text = _settings.ChatWelcomeText ?? "";
        // 起手页快捷问法胶囊（2026-09-27）：每行一条回显
        ChatQuickPromptsInput.Text = string.Join(Environment.NewLine, _settings.ChatQuickPrompts ?? new List<string>());
        SidebarDefaultExpandedCheck.IsChecked = _settings.ChatSidebarDefaultExpanded;
        UpdateChatAssetsUI();
        LoadSyncSettings();
        LoadFileSettings();
        LoadTodoSettings();
        LoadInputSettings();
        LoadTidyRules();                  // 整理规则板块（2026-09-27）：规则列表 + 默认规则 + 自定义入口开关
        _suppressEvents = false;
        RefreshHotkeyWarning();           // v3.8：回显上次注册失败的键位（多为被其他程序占用）
        _ = LoadGetNoteTopicsQuietly();   // 凭证已配置时后台拉取知识库列表（不阻塞设置打开）
        RefreshSkillSection();            // Skill 分区（2026-09-20）：目录 / 运行时状态 / 已授权列表
    }

    /// <summary>禁用按钮的文字色 —— 窗口样式只有 hover 态，禁用态得自己上色，否则"不能点"看不出来</summary>
    private static readonly Brush DisabledTextBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    /// <summary>外部依赖面板正在刷新（防并发：两次刷新交错会插出两遍行）</summary>
    private bool _depsBuilding;

    // ══════════════════ Skill 扩展（2026-09-20） ══════════════════
    //
    // 这里只负责「让 Skill 可见、可控」，刻意**不做**装 / 删 / 停用 —— 那会让用户多学一套概念。
    //   装 = 把文件夹拷进目录；授权 = 首次执行脚本时弹一次窗；撤销 = 下面这个列表。
    // 目录扫描与运行时探测复用 Services/Skills 里的同一套组件，与 AI 问答走的是同一份逻辑，
    // 不另起一份"设置页专用"的实现（两份实现必然漂移）。

    private void RefreshSkillSection()
    {
        try
        {
            var catalog = new SkillCatalog(FocusCapturePaths.Combine("Skills"),
                                           msg => AppLog.Warn("Skill", msg));
            var runtime = new SkillRuntime(AppContext.BaseDirectory);
            var skills = catalog.GetSkills(forceRescan: true);
            var executable = skills.Count(s => !s.HasScripts || runtime.IsPresent);

            SkillSummaryText.Text = skills.Count == 0
                ? $"还没装 Skill。（目录：{catalog.Root}）"
                : $"已装 {skills.Count} 个，其中 {executable} 个可执行。（目录：{catalog.Root}）";

            SkillRuntimeText.Text = File.Exists(runtime.PythonPath)
                ? "内置 Python 运行时：已就位。"
                : "内置 Python 运行时：未就位 —— 含脚本的 Skill 暂时跑不了。"
                  + "点下面的按钮由本应用下载（约 11MB），不用你自己装 Python，也不用敲任何命令。";

            // 缺运行时给下载入口（2026-09-21，按需分发）：包默认不带它，用户点了才下。
            // 文案保留"应用自身组件缺失"的口径 —— 这是应用该补的东西，不是使用者的操作问题。
            var pyRecipe = RuntimeRecipes.For(RuntimeRecipes.PythonId);
            PythonDownloadPanel.Visibility = !runtime.IsPresent && pyRecipe != null
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (runtime.IsPresent) PythonDownloadText.Text = "";


            RebuildTrustedSkillList();

            // 「恢复内置技能」的可用性：应用里压根没有内置技能时（精简包 / 开发机没带），
            // 按钮点了只会弹一句"没有可恢复的"—— 不如直接禁用，并把禁用态涂出来（只看 hover 的样式分不出能不能点）。
            var hasBuiltin = BuiltinSkills.ListBuiltin(
                BuiltinSkills.SourceRoot(AppContext.BaseDirectory)).Count > 0;
            BtnSkillRestoreBuiltin.IsEnabled = hasBuiltin;
            if (hasBuiltin) BtnSkillRestoreBuiltin.ClearValue(Control.ForegroundProperty);
            else BtnSkillRestoreBuiltin.Foreground = DisabledTextBrush;

            _ = RefreshSkillDepsAsync();      // 外部依赖面板（异步探测，不阻塞设置打开）
        }
        catch (Exception ex)
        {
            AppLog.Warn("Skill", $"设置页刷新 Skill 分区失败：{ex.Message}");
            SkillSummaryText.Text = "读取 Skill 信息失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 外部依赖面板（2026-09-20）。
    ///
    /// <para>
    /// 存在的理由：Skill 依赖的外部 CLI 需要"先登录"，而阶段一交付时这块是空白的 ——
    /// 结果是模型让用户去终端敲命令。这里给出**应用内唯一的授权入口**：
    /// 看到状态、点一下、扫一次码，其余全由宿主完成。同时提供"退出登录"（撤销入口）——
    /// 只能授权不能撤销的安全机制是残缺的。
    /// </para>
    /// </summary>
    private async Task RefreshSkillDepsAsync()
    {
        // 并发保护 + **原子替换**（两条都要，缺一条就会出两遍行）：
        //   ① 这个方法被多处触发（打开设置 / 切换板块 / 授权回来 / 退出登录 / 快照入口）；
        //   ② 更隐蔽的一条：不能"先 Clear → await 探测 → 再 Add" —— 那样另一条路径的清空与插入
        //      可以落在中间，结果就是同一个依赖显示两行（快照里抓到过）。
        // 所以先算好整份行、再一次性替换。
        if (_depsBuilding) return;
        _depsBuilding = true;
        try
        {
            var rows = new List<UIElement>();
            try
            {
                foreach (var dep in SkillDependencies.All(AppContext.BaseDirectory))
                {
                    var status = await dep.ProbeAsync().ConfigureAwait(true);
                    rows.Add(BuildDependencyRow(dep, status));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Skill", $"刷新外部依赖面板失败：{ex.Message}");
                rows.Add(new TextBlock
                {
                    Text = "读取外部依赖状态失败：" + ex.Message,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x9A)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            SkillDepsPanel.Children.Clear();
            foreach (var row in rows) SkillDepsPanel.Children.Add(row);
        }
        finally
        {
            _depsBuilding = false;
        }
    }

    /// <summary>一个依赖一行：状态（按登录态着色）+ 位置 + 授权 / 退出登录</summary>
    private UIElement BuildDependencyRow(SkillDependency dep, DependencyStatus status)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        // 位置三态与候选顺序对齐（自带 → 数据目录 → 系统 PATH，2026-09-21）：
        // 按需下载的那一份必须能一眼看出来是「已下载」——否则使用者会以为它来自系统，
        // 将来排查"到底跑的是哪一份"时无从下手。
        // 找不到时**不再缀一个（未找到）**：Detail 里已经说清了，重复一遍只是噪声（快照里看到的）。
        var location = !status.Resolved ? "" : dep.SourceOf(status.ExePath) switch
        {
            DependencySource.Bundled => "应用自带",
            DependencySource.DataDir => "已下载",
            DependencySource.SystemPath => "系统 PATH",
            _ => "位置未知",
        };
        var locationSuffix = location.Length > 0 ? $"（{location}）" : "";

        var color = status.Auth switch
        {
            DependencyAuth.Ready => Color.FromRgb(0x8F, 0xD1, 0x8F),
            // 「还没配应用凭据」是第一次用的正常状态，不是故障 —— 用提示色而不是报错色
            DependencyAuth.Unknown => Color.FromRgb(0xE0, 0xC0, 0x8F),
            DependencyAuth.NotConfigured => Color.FromRgb(0xE0, 0xC0, 0x8F),
            _ => Color.FromRgb(0xE0, 0x9A, 0x9A),
        };

        panel.Children.Add(new TextBlock
        {
            Text = $"{dep.DisplayName}：{status.Detail}{locationSuffix}",
            Foreground = new SolidColorBrush(color),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        // 没有授权协议的依赖（"在位即可用"那类）不该给可点的授权按钮 —— 点了也没有流程可走
        var canAuthorize = status.CanAuthorize && dep.Flow != null;
        // 没配应用凭据时，"扫一次码"这个说法是误导的（那时连码都出不来）——
        // 按钮文案要如实说清第一步是创建应用（2026-09-21）
        var authButtonText = status.Auth switch
        {
            DependencyAuth.Ready => "重新授权",
            DependencyAuth.NotConfigured => "创建应用并授权",
            _ => "授权（扫一次码）",
        };
        var authButton = new Button
        {
            Content = authButtonText,
            Width = 132,
            Height = 28,
            IsEnabled = canAuthorize,
        };
        // 禁用态必须**看得出来**：SettingsWindow 的按钮样式只有 hover 态，
        // 不动前景色的话"不能点"和"能点"长得一样，用户会以为按钮坏了（快照对比中发现，2026-09-20）。
        if (!canAuthorize) authButton.Foreground = DisabledTextBrush;
        authButton.Click += async (_, _) => await RunDependencyAuthAsync(dep, status).ConfigureAwait(true);
        row.Children.Add(authButton);

        // 没有登录态可撤的依赖（无协议 / 未登录）不给可点的退出按钮，避免出现"点了没反应"
        var canLogout = dep.Flow != null && status.Auth == DependencyAuth.Ready;
        var logoutButton = new Button
        {
            Content = "退出登录",
            Width = 96,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = canLogout,
        };
        if (!canLogout) logoutButton.Foreground = DisabledTextBrush;
        logoutButton.Click += async (_, _) =>
        {
            logoutButton.IsEnabled = false;
            logoutButton.Foreground = DisabledTextBrush;
            var (ok, message) = await RunLogoutAsync(dep).ConfigureAwait(true);
            AppLog.Info("Skill", $"依赖退出登录（{dep.Id}）：ok={ok} {message}");
            await RefreshSkillDepsAsync().ConfigureAwait(true);
        };
        row.Children.Add(logoutButton);

        panel.Children.Add(row);

        // 组件不在位（应用没自带、也没下载过）→ 给一个下载入口（2026-09-21，按需分发）。
        // 这一格才是"按需下载"真正被用到的地方：包默认不带 runtime，用户第一次用才下。
        if (!status.Resolved && RuntimeRecipes.For(dep.Id) != null)
        {
            var missingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

            var dlButton = new Button { Content = "下载组件", Width = 120, Height = 28 };
            var dlBar = new ProgressBar
            {
                Width = 140, Height = 8, Margin = new Thickness(8, 0, 0, 0),
                Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xD1, 0x8F)),
                BorderThickness = new Thickness(0),
            };
            var dlText = new TextBlock
            {
                Text = "应用自带没有这一份，也没有下载过。点这里由本应用下载安装。",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            dlButton.Click += async (_, _) =>
                await RunRuntimeInstallAsync(dep.Id, dlButton, dlText, dlBar).ConfigureAwait(true);

            missingRow.Children.Add(dlButton);
            missingRow.Children.Add(dlBar);
            missingRow.Children.Add(dlText);
            panel.Children.Add(missingRow);
        }

        return panel;
    }

    /// <summary>内置 Python 的下载按钮（与依赖行共用同一个安装方法）</summary>
    private async void BtnDownloadPython_Click(object sender, RoutedEventArgs e)
    {
        await RunRuntimeInstallAsync(
            RuntimeRecipes.PythonId, BtnDownloadPython, PythonDownloadText, PythonDownloadBar)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// 按需下载一个运行时组件 —— 设置页里的两个入口（外部依赖行 / 内置 Python 行）共用它。
    ///
    /// <para>
    /// 进度、按钮禁用、失败如实回报都在这里做一次，**不各写一套**（两份实现必然漂移）。
    /// </para>
    /// <para>
    /// ⚠ 这是在 UI 事件处理器里做外部 IO，**必须自己兜住异常**：冒泡到全局处理器会弹模态框，
    /// 而模态框会吃掉后续点击 —— 用户看到的是"点了没反应"。
    /// </para>
    /// </summary>
    private async Task RunRuntimeInstallAsync(string id, Button button, TextBlock statusText, ProgressBar? bar)
    {
        var recipe = RuntimeRecipes.For(id);
        if (recipe == null)
        {
            statusText.Text = "这个组件没有已知的下载来源，暂时装不了。";
            return;
        }

        button.IsEnabled = false;
        if (bar != null) { bar.Value = 0; bar.Visibility = Visibility.Visible; }

        try
        {
            var downloader = new RuntimeDownloader { OnLog = m => AppLog.Info("Skill", m) };
            // Progress 在 UI 线程上创建 → 回调自动回到 UI 线程（读流线程上碰控件会抛）
            var progress = new Progress<RuntimeProgress>(p =>
            {
                statusText.Text = p.Describe();
                if (bar != null && p.Percent is int pct) bar.Value = pct;
            });

            var result = await downloader.InstallAsync(recipe, progress).ConfigureAwait(true);
            if (!result.Ok)
            {
                statusText.Text = "没装成：" + result.Detail;
                button.IsEnabled = true;
                if (bar != null) bar.Visibility = Visibility.Collapsed;
                AppLog.Warn("Skill", $"运行时组件安装失败（{id}）：{result.Detail}");
                return;
            }

            statusText.Text = result.AlreadyPresent ? "已就绪。" : "安装完成，可以用了。";
            if (bar != null) bar.Visibility = Visibility.Collapsed;
            AppLog.Info("Skill", $"{recipe.DisplayName} 安装完成：{result.TargetDir}");

            // 重扫一遍：依赖状态、Python 状态、列表都要跟着变
            RefreshSkillSection();
        }
        catch (Exception ex)
        {
            AppLog.Error("Skill", $"下载运行时组件出错（{id}）", ex);
            statusText.Text = "下载出错：" + ex.Message;
            button.IsEnabled = true;
            if (bar != null) bar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 撤销登录态 —— 走协议（2026-09-21 起协议由依赖自己提供）。
    /// 没有协议的依赖本来就不该有登录态，这里兜住并如实说明，**不抛**。
    /// </summary>
    private static async Task<(bool Ok, string Message)> RunLogoutAsync(SkillDependency dep)
    {
        var flow = dep.Flow;
        if (flow == null) return (false, $"{dep.DisplayName} 没有可撤销的登录状态。");
        return await flow.LogoutAsync().ConfigureAwait(false);
    }

    /// <summary>从设置页发起授权 —— 走的是和 AI 问答里同一个窗口，不另起一套</summary>
    private async Task RunDependencyAuthAsync(SkillDependency dep, DependencyStatus status)
    {
        var window = new SkillAuthWindow(dep, status) { Owner = this };
        var done = window.ShowDialog() == true;
        AppLog.Info("Skill", $"设置页发起依赖授权（{dep.Id}）：{(done ? "完成" : "未完成")}");
        await RefreshSkillDepsAsync().ConfigureAwait(true);
    }

    private void RebuildTrustedSkillList()
    {
        SkillTrustedPanel.Children.Clear();

        var names = _settings.SkillTrusted.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (names.Count == 0)
        {
            SkillTrustedPanel.Children.Add(new TextBlock
            {
                Text = "（还没有授权任何 Skill）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        foreach (var name in names)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            // 不用固定 Height：窗口隐式按钮样式 Padding=12,6 + 13px 字，固定 24 会把文字下半截掉（2026-10-01 实测）
            var revoke = new Button { Content = "撤销", Width = 56, MinHeight = 24, Tag = name };
            revoke.Click += BtnSkillRevoke_Click;
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);

            row.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });

            SkillTrustedPanel.Children.Add(row);
        }
    }

    private void BtnSkillOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = FocusCapturePaths.Combine("Skills");
            Directory.CreateDirectory(dir);   // 没有就先建好，用户直接往里拷
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打开资源管理器失败不该弹模态框（UI 事件里的外部操作一律自己兜住）
            AppLog.Warn("Skill", $"打开 Skill 目录失败：{ex.Message}");
        }
    }

    private void BtnSkillRescan_Click(object sender, RoutedEventArgs e)
    {
        // RefreshSkillSection 内部就是 forceRescan: true —— 用户刚拷完 Skill，用这个按钮立刻认出来
        RefreshSkillSection();
    }

    /// <summary>
    /// 恢复内置技能（2026-09-21，授权闭环步骤 4）。
    ///
    /// <para>
    /// 存在理由：内置技能是「目标不存在才复制」，落地之后就归用户 —— 他改了、改坏了，
    /// 应用不会再去覆盖它。那就必须给一个**显式的后悔药**，否则"改坏 = 永久坏"。
    /// </para>
    /// <para>
    /// 这是破坏性动作（覆盖用户改动），所以：由用户点击发起 + 二次确认里写清会覆盖什么、
    /// 备份到哪 + 覆盖前先把现存版本整体挪走（不删除）。<c>AGENTS.md</c> 红线 7。
    /// </para>
    /// </summary>
    private void BtnSkillRestoreBuiltin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sourceRoot = BuiltinSkills.SourceRoot(AppContext.BaseDirectory);
            var names = BuiltinSkills.ListBuiltin(sourceRoot);
            var skillsRoot = FocusCapturePaths.Combine("Skills");

            if (names.Count == 0)
            {
                MessageBox.Show(this,
                    "应用里没有可恢复的内置技能。\n\n" +
                    "（如果你用的是精简包，内置技能可能没有随包分发。）",
                    "恢复内置技能", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var backupRoot = Path.GetDirectoryName(
                skillsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var confirm = new StringBuilder();
            confirm.AppendLine("将用应用自带的版本覆盖这些技能：");
            confirm.Append("  ").AppendLine(string.Join("、", names));
            confirm.AppendLine();
            confirm.AppendLine("你自己改过的内容会被覆盖。覆盖之前，现在这份会先整体备份到：");
            confirm.Append("  ").Append(backupRoot).AppendLine("\\Skills_backup\\<时间戳>\\");
            confirm.AppendLine();
            confirm.Append("要继续吗？（备份只挪位置、不删除，随时可以自己搬回来）");

            if (MessageBox.Show(this, confirm.ToString(), "恢复内置技能",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            var okList = new List<string>();
            var failList = new List<string>();
            var lastBackup = "";

            foreach (var name in names)
            {
                var r = BuiltinSkills.Restore(sourceRoot, skillsRoot, name);
                if (r.Ok) { okList.Add(name); if (r.BackupPath.Length > 0) lastBackup = r.BackupPath; }
                else failList.Add($"{name}（{r.Detail}）");
            }

            AppLog.Info("Skill", $"恢复内置技能：成功 {okList.Count} 个，失败 {failList.Count} 个");

            var report = new StringBuilder();
            if (okList.Count > 0) report.Append("已恢复：").Append(string.Join("、", okList)).AppendLine();
            if (lastBackup.Length > 0) report.Append("覆盖前的版本备份在：\n  ").Append(lastBackup).AppendLine();
            if (failList.Count > 0) report.Append("没成功：").Append(string.Join("；", failList)).AppendLine();

            MessageBox.Show(this, report.ToString(),
                failList.Count == 0 ? "恢复完成" : "部分没成功",
                MessageBoxButton.OK,
                failList.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            RefreshSkillSection();
        }
        catch (Exception ex)
        {
            // UI 事件里的文件操作一律自己兜住：异常冒泡到全局处理器会弹模态框，模态框吃掉后续点击
            AppLog.Error("Skill", "恢复内置技能失败", ex);
            MessageBox.Show(this, "恢复内置技能时出错：" + ex.Message,
                "恢复内置技能", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSkillRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;

        // 撤销 = 从配置里删掉。只是一个名字，删掉不涉及任何文件；下次执行会重新弹窗询问。
        _settings.SkillTrusted.RemoveAll(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        AppLog.Info("Skill", $"已撤销执行授权：{name}");
        RebuildTrustedSkillList();
    }

    /// <summary>v3.8：注册失败提示——键位被其他程序占用时 RegisterHotKey 会失败，
    /// 用户表现为「按了没反应」。此处把 HotkeyService 留痕的失败项显式列在热键板块。</summary>
    private void RefreshHotkeyWarning()
    {
        var fails = _hotkeyService?.LastFailures;
        if (fails == null || fails.Count == 0)
        {
            HotkeyWarningBorder.Visibility = Visibility.Collapsed;
            return;
        }
        HotkeyWarningText.Text = "以下快捷键注册失败（可能已被其他程序占用），请换一组："
            + string.Join("、", fails.Select(f => $"{f.Name}（{f.Keys}）"));
        HotkeyWarningBorder.Visibility = Visibility.Visible;
    }

    /// <summary>v3.6 输入框设置回填（自动隐藏模式/秒数 + 位置记忆）</summary>
    private void LoadInputSettings()
    {
        AlwaysVisibleRadio.IsChecked = _settings.InputAlwaysVisible;
        CustomHideRadio.IsChecked = !_settings.InputAlwaysVisible;
        AutoHideSecondsInput.Text = _settings.InputAutoHideSeconds.ToString();
        AutoHideSecondsInput.IsEnabled = !_settings.InputAlwaysVisible;
        RememberPositionCheck.IsChecked = _settings.InputRememberPosition;
    }

    // ── v3.6 输入框：自动隐藏与位置记忆（改即保存） ──

    private void AutoHideMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InputAlwaysVisible = AlwaysVisibleRadio.IsChecked == true;
        AutoHideSecondsInput.IsEnabled = !_settings.InputAlwaysVisible;
        _settings.Save();
    }

    /// <summary>自定义秒数：输入过程中合法即保存（≥3 整数，上不封顶）；失焦时非法才回退原值，避免打断多位数输入</summary>
    private void AutoHideSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = AutoHideSecondsInput.Text.Trim();
        if (int.TryParse(v, out var n) && n >= 3) { _settings.InputAutoHideSeconds = n; _settings.Save(); }
    }

    private void AutoHideSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = AutoHideSecondsInput.Text.Trim();
        if (!int.TryParse(v, out var n) || n < 3)
            AutoHideSecondsInput.Text = _settings.InputAutoHideSeconds.ToString();
    }

    private void RememberPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InputRememberPosition = RememberPositionCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>v3.5 待办与提醒设置回填</summary>
    private void LoadTodoSettings()
    {
        DefaultTypeCombo.SelectedIndex = _settings.InputDefaultType == "Todo" ? 1 : 0;
        BtnTodoSwitchHotkey.Content = Win32.HotkeyToString(_settings.TodoSwitchHotkey);
        DailySummaryCheck.IsChecked = _settings.DailySummaryEnabled;
        DailySummaryEmptyCheck.IsChecked = _settings.DailySummaryEmptyPopup;
        DailySummaryTimeInput.Text = _settings.DailySummaryTime;
        SnoozeMinutesInput.Text = _settings.SnoozeMinutes.ToString();
        PopupCloseSecondsInput.Text = _settings.PopupAutoCloseSeconds.ToString();
        AskTimeCheck.IsChecked = _settings.AskTimeForDateOnly;
    }

    /// <summary>云同步设置回填（方案A 2026-08-15：授权码自动解锁，无主密码/恢复码）</summary>
    private void LoadSyncSettings()
    {
        SyncUrlInput.Text = _settings.Sync.WebDavUrl;
        SyncUserInput.Text = _settings.Sync.WebDavUser;
        AutoSyncCheck.IsChecked = _settings.Sync.AutoSyncEnabled;
        MergeWindowInput.Text = _settings.Sync.MergeWindowSeconds.ToString();
        ChatSyncCheck.IsChecked = _settings.Sync.ChatSyncEnabled;
        var engine = _syncEngineProvider?.Invoke();
        var unlocked = engine?.IsMasterPasswordSet == true;
        SyncStatusText.Text = unlocked
            ? $"已解锁自动同步 · 上次同步：{_settings.Sync.LastSyncAt} {_settings.Sync.LastSyncResult}"
            : engine?.IsLegacyMasterPasswordMode == true
                ? "检测到旧版主密码配置，点击『保存并连接』一键升级（无需原主密码）"
                : string.IsNullOrEmpty(_settings.Sync.E2eeSalt)
                    ? "未配置云同步（首次点击『保存并连接』即完成配置）"
                    : "授权码已保存，应用启动后自动解锁（重新填写授权码可更换）";
        // 会话同步失败留痕持续可见（不允许无声丢失；成功/等待首配等状态也在此展示）
        if (!string.IsNullOrEmpty(_settings.Sync.ChatSyncResult))
            SyncStatusText.Text += $"\nAI 问答记录：{_settings.Sync.ChatSyncResult}";

        ChatFilesCheck.IsChecked = _settings.Sync.FileSyncEnabled;
        if (!string.IsNullOrEmpty(_settings.Sync.FileSyncResult))
            SyncStatusText.Text += $"\n网盘文件清单：{_settings.Sync.FileSyncResult}";
    }

    private void StartCapture(Button btn, Action<Models.HotkeyBinding> done)
    {
        if (_capturing) return;
        _capturing = true; _onCaptureDone = done;
        // v3.8：录制期间临时注销全部全局热键——否则按下已注册的组合键会当场触发它（例如录 Ctrl+Alt+V 就弹速览）。
        // 恢复点共三个：录完走 _onChanged → RegisterAll、Esc 走 CancelCapture、关窗走 Closed（都要有）。
        _hotkeyService?.UnregisterAll();
        btn.Content = "按下新快捷键…";
        btn.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        Keyboard.Focus(btn);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        var m = Keyboard.Modifiers;
        if (m == ModifierKeys.None && e.Key != Key.Escape) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { CancelCapture(); return; }
        if (e.Key is Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var hk = new Models.HotkeyBinding
        {
            Modifiers = (int)m,
            Key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key)
        };
        _onCaptureDone?.Invoke(hk); _capturing = false; _onCaptureDone = null;
        _onChanged?.Invoke();
        RefreshHotkeyWarning();   // v3.8：注册在 _onChanged 内完成，之后才能读到本次失败清单
    }

    private void CancelCapture()
    {
        _capturing = false; _onCaptureDone = null;
        _hotkeyService?.RegisterAll();   // v3.8：Esc 取消必须补回注册（StartCapture 已临时注销），否则所有热键失效
        LoadSettings();
    }

    private void DoneCapture(Button btn, Models.HotkeyBinding hk)
    {
        btn.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        _settings.Save();
    }

    private void BtnSummon_Click(object sender, RoutedEventArgs e) => StartCapture(BtnSummonHotkey, hk =>
    { _settings.SummonHotkey = hk; BtnSummonHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnSummonHotkey, hk); });
    private void BtnClipboard_Click(object sender, RoutedEventArgs e) => StartCapture(BtnClipboardHotkey, hk =>
    { _settings.ClipboardToggleHotkey = hk; BtnClipboardHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnClipboardHotkey, hk); });
    private void BtnQuickView_Click(object sender, RoutedEventArgs e) => StartCapture(BtnQuickViewHotkey, hk =>
    { _settings.QuickViewHotkey = hk; BtnQuickViewHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnQuickViewHotkey, hk); });
    private void BtnVoiceInput_Click(object sender, RoutedEventArgs e) => StartCapture(BtnVoiceInputHotkey, hk =>
    { _settings.VoiceInputHotkey = hk; BtnVoiceInputHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnVoiceInputHotkey, hk); });
    private void BtnSettings_Click(object sender, RoutedEventArgs e) => StartCapture(BtnSettingsHotkey, hk =>
    { _settings.SettingsHotkey = hk; BtnSettingsHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnSettingsHotkey, hk); });
    private void BtnAiAsk_Click(object sender, RoutedEventArgs e) => StartCapture(BtnAiAskHotkey, hk =>
    { _settings.AiAskHotkey = hk; BtnAiAskHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnAiAskHotkey, hk); });
    private void BtnTodoSummary_Click(object sender, RoutedEventArgs e) => StartCapture(BtnTodoSummaryHotkey, hk =>
    { _settings.TodoSummaryHotkey = hk; BtnTodoSummaryHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnTodoSummaryHotkey, hk); });

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.SummonHotkey = new() { Modifiers = 1, Key = 0x20 };
        _settings.ClipboardToggleHotkey = new() { Modifiers = 3, Key = 0x70 };
        _settings.QuickViewHotkey = new() { Modifiers = 3, Key = 0x56 };
        _settings.VoiceInputHotkey = new() { Modifiers = 3, Key = 0x52 };
        _settings.SettingsHotkey = new() { Modifiers = 3, Key = 0x53 };
        _settings.AiAskHotkey = new() { Modifiers = 3, Key = 0x41 };
        _settings.TodoSummaryHotkey = new() { Modifiers = 3, Key = 0x54 };
        _settings.Save(); LoadSettings(); _onChanged?.Invoke(); RefreshHotkeyWarning();
    }

    // ── v3.5 待办与提醒（改即保存 + 即时校验） ──

    private void DefaultType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InputDefaultType = DefaultTypeCombo.SelectedIndex == 1 ? "Todo" : "Note";
        _settings.Save();
    }

    private void BtnTodoSwitch_Click(object sender, RoutedEventArgs e) => StartCapture(BtnTodoSwitchHotkey, hk =>
    { _settings.TodoSwitchHotkey = hk; BtnTodoSwitchHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnTodoSwitchHotkey, hk); });

    private void DailySummary_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DailySummaryEnabled = DailySummaryCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>v3.7：当天无待办是否仍弹每日汇总弹窗（默认弹）</summary>
    private void DailySummaryEmpty_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DailySummaryEmptyPopup = DailySummaryEmptyCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>纯日期是否弹窗问几点（取消=默认当天 09:00）</summary>
    private void AskTime_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AskTimeForDateOnly = AskTimeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>汇总时间：输入过程中合法（HH:mm，00:00~23:59）即保存；失焦时非法才回退原值，避免打断输入</summary>
    private void DailySummaryTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = DailySummaryTimeInput.Text.Trim();
        if (IsValidDailyTime(v)) { _settings.DailySummaryTime = v; _settings.Save(); }
    }

    private void DailySummaryTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsValidDailyTime(DailySummaryTimeInput.Text.Trim()))
            DailySummaryTimeInput.Text = _settings.DailySummaryTime;
    }

    private static bool IsValidDailyTime(string v)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,2}:\d{2}$")) return false;
        var parts = v.Split(':');
        return int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h >= 0 && h <= 23 && m >= 0 && m <= 59;
    }

    /// <summary>稍后提醒分钟数：输入过程中合法（正整数）即保存；失焦时非法才回退原值</summary>
    private void SnoozeMinutes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = SnoozeMinutesInput.Text.Trim();
        if (int.TryParse(v, out var n) && n > 0) { _settings.SnoozeMinutes = n; _settings.Save(); }
    }

    private void SnoozeMinutes_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = SnoozeMinutesInput.Text.Trim();
        if (!int.TryParse(v, out var n) || n <= 0)
            SnoozeMinutesInput.Text = _settings.SnoozeMinutes.ToString();
    }

    private void PopupCloseSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = PopupCloseSecondsInput.Text.Trim();
        if (int.TryParse(v, out var n) && n > 0) { _settings.PopupAutoCloseSeconds = n; _settings.Save(); }
    }

    private void PopupCloseSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = PopupCloseSecondsInput.Text.Trim();
        if (!int.TryParse(v, out var n) || n <= 0)
            PopupCloseSecondsInput.Text = _settings.PopupAutoCloseSeconds.ToString();
    }

    private void InputOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.InputOpacity = e.NewValue; InputOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void BallOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.FloatBallOpacity = e.NewValue; BallOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void QuickViewOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.QuickViewOpacity = e.NewValue; QuickViewOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void InputBorderRadius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.InputBorderRadius = e.NewValue; InputBorderRadiusLabel.Text = $"{(int)Math.Round(e.NewValue)}"; _settings.Save(); _onChanged?.Invoke(); }

    // AI 问答界面明暗（2026-09-27）：实时刷应用级角色色（DynamicResource 全窗跟随）+ 保存。
    // 标签只报两端的定性名，中间值报百分比 —— 拖动时可预期。
    private void ChatShade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _settings.ChatShade = ChatThemeService.ClampShade(e.NewValue);
        ChatShadeLabel.Text = _settings.ChatShade switch
        {
            <= 0.25 => "浅色",
            >= 0.75 => "深色",
            < 0.5 => $"偏浅 {(int)Math.Round(_settings.ChatShade * 100)}%",
            _ => $"偏深 {(int)Math.Round(_settings.ChatShade * 100)}%",
        };
        ChatThemeService.Apply(_settings.ChatShade);
        _settings.Save();
    }

    // 悬浮球拖放保存（2026-09-16）。_onChanged 里会让已打开的小条/卡片实时跟手，
    // 否则「拖完再调滑块」看不到变化，会被当成功能坏了。
    private void DragToSave_Changed(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.DragToSaveEnabled = DragToSaveCheck.IsChecked == true; _settings.Save(); _onChanged?.Invoke(); }
    private void DropActionOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.DropActionOpacity = e.NewValue; DropActionOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void DropActionStripSeconds_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.DropActionStripSeconds = (int)Math.Round(e.NewValue); DropActionStripLabel.Text = $"{_settings.DropActionStripSeconds} 秒"; _settings.Save(); _onChanged?.Invoke(); }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.AutoStart = AutoStartCheck.IsChecked == true; SetAutoStart(_settings.AutoStart); _settings.Save(); }

    // v3.8：灵感速览唤出行为（当天 / 恢复上次筛选），QuickViewWindow.Show 时按此项决定是否重置
    private void QuickViewSummon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (QuickViewSummonCombo.SelectedItem is ComboBoxItem it)
        {
            _settings.QuickViewRestoreLastFilter = (string)it.Tag == "Restore";
            _settings.Save();
        }
    }

    // ── v3.9 灵感速览板块：面板宽度 / 置顶 / 标题栏自定义编辑器 ──
    // 布局预算与配置清洗都在 QuickViewToolbarCatalog（纯逻辑，tests 快层直链覆盖）。
    // 每次变更：写设置 → 落盘 → 刷新编辑器 → _onChanged（MainWindow → QuickViewWindow.ApplySettings 即时生效）。

    private void QuickViewWidth_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!int.TryParse(QuickViewWidthInput.Text.Trim(), out var w)) return;   // 输入中途不处理，失焦回退
        var clamped = (int)Math.Clamp(w, QuickViewWindow.MinWidthLimit, QuickViewWindow.MaxWidthLimit);
        if (_settings.QuickViewWidth != clamped)
        {
            _settings.QuickViewWidth = clamped;
            _settings.Save();
            _onChanged?.Invoke();
        }
        RefreshToolbarBudget();   // 宽度变了，标题栏预算随之变化
    }

    private void QuickViewWidth_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(QuickViewWidthInput.Text.Trim(), out var w)
            || w < (int)QuickViewWindow.MinWidthLimit || w > (int)QuickViewWindow.MaxWidthLimit)
            QuickViewWidthInput.Text = ((int)Math.Clamp(_settings.QuickViewWidth,
                QuickViewWindow.MinWidthLimit, QuickViewWindow.MaxWidthLimit)).ToString();
    }

    private void QuickViewTopmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.QuickViewTopmost = QuickViewTopmostCheck.IsChecked == true;
        _settings.Save();
        _onChanged?.Invoke();
    }

    /// <summary>重建标题栏自定义编辑器：两个已启用列表 + 两个「可用功能」下拉 + 预算文字。</summary>
    private void RebuildToolbarEditor()
    {
        var (left, right) = QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);

        FillToolbarList(ToolbarLeftList, left);
        FillToolbarList(ToolbarRightList, right);

        FillToolbarCombo(ToolbarLeftAddCombo, left, right, isLeft: true);
        FillToolbarCombo(ToolbarRightAddCombo, left, right, isLeft: false);
        RefreshToolbarBudget();
    }

    private void FillToolbarList(ListBox list, List<string> ids)
    {
        var selectedId = (list.SelectedItem as ListBoxItem)?.Tag as string;
        list.Items.Clear();
        for (var i = 0; i < ids.Count; i++)
        {
            var def = QuickViewToolbarCatalog.Find(ids[i]);
            if (def == null) continue;
            list.Items.Add(new ListBoxItem
            {
                Content = $"{i + 1}. {def.Label}",
                Tag = def.Id,
                IsSelected = def.Id == selectedId,
                Padding = new Thickness(6, 3, 6, 3),
            });
        }
    }

    /// <summary>「可用功能」下拉：只列两侧都未启用的功能；预算放不下的项禁用并注明。
    /// 下拉为空是**正常情况**（功能池已全部启用完），此时显示提示并把添加按钮/下拉一并置灰 ——
    /// 否则用户点了发现毫无反应，只会以为界面坏了（2026-09-13 实际反馈）。</summary>
    private void FillToolbarCombo(ComboBox combo, List<string> left, List<string> right, bool isLeft)
    {
        combo.Items.Clear();
        foreach (var def in QuickViewToolbarCatalog.All)
        {
            if (left.Contains(def.Id) || right.Contains(def.Id)) continue;
            var canAdd = QuickViewToolbarCatalog.CanAdd(_settings.QuickViewWidth, left, right, def.Id);
            var item = new ComboBoxItem
            {
                Content = canAdd ? def.Label : $"{def.Label}（空间不足）",
                Tag = def.Id,
                IsEnabled = canAdd,
            };
            combo.Items.Add(item);
        }

        var hasAvailable = combo.Items.Count > 0;
        combo.SelectedIndex = hasAvailable ? 0 : -1;
        combo.IsEnabled = hasAvailable;
        (isLeft ? ToolbarLeftEmptyHint : ToolbarRightEmptyHint).Visibility =
            hasAvailable ? Visibility.Collapsed : Visibility.Visible;
        (isLeft ? ToolbarLeftAddBtn : ToolbarRightAddBtn).IsEnabled = hasAvailable;
    }

    /// <summary>预算文字：已用 / 可用像素；超出预算红字提示（宽度过低 + 按钮过多会标题栏重叠）。</summary>
    private void RefreshToolbarBudget()
    {
        var (left, right) = QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);
        var used = QuickViewToolbarCatalog.UsedBudget(left, right);
        var avail = QuickViewToolbarCatalog.AvailableBudget(_settings.QuickViewWidth);
        ToolbarBudgetText.Text = $"已用 {used:0} / 可用 {avail:0} px"
            + (used > avail + 0.01 ? "（空间不足：标题栏按钮可能重叠，建议加宽面板或移除按钮）" : "");
        ToolbarBudgetText.Foreground = used > avail + 0.01
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))
            : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }

    /// <summary>编辑动作统一出口：写回设置并落盘、刷新编辑器、通知 MainWindow 让面板即时重建。</summary>
    private void CommitToolbarConfig()
    {
        _settings.Save();
        RebuildToolbarEditor();
        _onChanged?.Invoke();
    }

    private static List<string> SideList(List<string> left, List<string> right, bool isLeft) => isLeft ? left : right;

    private (List<string> left, List<string> right) WorkingLists()
        => QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);

    private void ToolbarAdd_Click(bool isLeft)
    {
        var combo = isLeft ? ToolbarLeftAddCombo : ToolbarRightAddCombo;
        if (combo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        var (left, right) = WorkingLists();
        if (!QuickViewToolbarCatalog.CanAdd(_settings.QuickViewWidth, left, right, id)) return;
        SideList(left, right, isLeft).Add(id);
        _settings.QuickViewToolbarLeft = left;
        _settings.QuickViewToolbarRight = right;
        CommitToolbarConfig();
    }

    private void ToolbarMove_Click(bool isLeft, int delta)
    {
        var list = isLeft ? ToolbarLeftList : ToolbarRightList;
        if (list.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var (left, right) = WorkingLists();
        var ids = SideList(left, right, isLeft);
        var idx = ids.IndexOf(id);
        var next = idx + delta;
        if (idx < 0 || next < 0 || next >= ids.Count) return;
        (ids[idx], ids[next]) = (ids[next], ids[idx]);
        _settings.QuickViewToolbarLeft = left;
        _settings.QuickViewToolbarRight = right;
        CommitToolbarConfig();
        // 重建后按 id 恢复选中，支持连续点击上移/下移
        SelectInList(isLeft ? ToolbarLeftList : ToolbarRightList, id);
    }

    private void ToolbarRemove_Click(bool isLeft)
    {
        var list = isLeft ? ToolbarLeftList : ToolbarRightList;
        if (list.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var (left, right) = WorkingLists();
        SideList(left, right, isLeft).Remove(id);
        _settings.QuickViewToolbarLeft = left;
        _settings.QuickViewToolbarRight = right;
        CommitToolbarConfig();
    }

    private static void SelectInList(ListBox list, string id)
    {
        foreach (ListBoxItem item in list.Items)
            if ((string)item.Tag == id) { item.IsSelected = true; return; }
    }

    private void ToolbarLeftAdd_Click(object sender, RoutedEventArgs e) => ToolbarAdd_Click(isLeft: true);
    private void ToolbarLeftUp_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: true, delta: -1);
    private void ToolbarLeftDown_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: true, delta: +1);
    private void ToolbarLeftRemove_Click(object sender, RoutedEventArgs e) => ToolbarRemove_Click(isLeft: true);
    private void ToolbarRightAdd_Click(object sender, RoutedEventArgs e) => ToolbarAdd_Click(isLeft: false);
    private void ToolbarRightUp_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: false, delta: -1);
    private void ToolbarRightDown_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: false, delta: +1);
    private void ToolbarRightRemove_Click(object sender, RoutedEventArgs e) => ToolbarRemove_Click(isLeft: false);

    private void BtnToolbarReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.QuickViewToolbarLeft = QuickViewToolbarCatalog.DefaultLeft.ToList();
        _settings.QuickViewToolbarRight = QuickViewToolbarCatalog.DefaultRight.ToList();
        CommitToolbarConfig();
    }

    private void AgentEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AgentEnabled = AgentEnabledCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>图片清晰度档位（下拉索引即档位值）</summary>
    private void AiImageQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var idx = AiImageQualityCombo.SelectedIndex;
        if (idx < 0) return;
        _settings.AiImageQualityLevel = idx;
        _settings.Save();
    }

    /// <summary>允许发送图片总开关（关掉后问答窗口会拦下粘贴的图片并提示）</summary>
    private void AiVisionEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiVisionEnabled = AiVisionEnabledCheck.IsChecked == true;
        _settings.Save();
    }

    private void AgentWriteConfirm_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AgentWriteConfirmPopup = AgentWriteConfirmCheck.IsChecked == true;
        _settings.Save();
    }

    // ── AI 本地文件工具（2026-10-01，授权目录制） ──

    private void AiFsTools_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiFsToolsEnabled = AiFsToolsEnabledCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>白名单列表（样式与 RebuildTrustedSkillList 同款：一行一路径 + 右侧移除按钮）</summary>
    private void RebuildAiAllowedDirs()
    {
        AiAllowedDirsPanel.Children.Clear();

        var dirs = _settings.AiAllowedDirs.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (dirs.Count == 0)
        {
            AiAllowedDirsPanel.Children.Add(new TextBlock
            {
                Text = "（还没有授权任何目录 —— AI 无法读写任何本地文件）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        foreach (var dir in dirs)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var remove = new Button { Content = "移除", Width = 56, MinHeight = 24, Tag = dir };
            remove.Click += BtnAiFsRemoveDir_Click;
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            row.Children.Add(new TextBlock
            {
                Text = dir,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });

            AiAllowedDirsPanel.Children.Add(row);
        }
    }

    private void BtnAiFsAddDir_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择允许 AI 读写的目录（删除始终进 Windows 回收站）",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        FocusCapture.Services.Agent.FsGuard.AllowDir(dlg.SelectedPath, _settings);   // 规范化 + 去重 + 落盘
        RebuildAiAllowedDirs();
    }

    private void BtnAiFsRemoveDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string dir }) return;
        _settings.AiAllowedDirs.Remove(dir);
        _settings.Save();
        AppLog.Info("Settings", $"AI 文件访问白名单已移除目录：{dir}");
        RebuildAiAllowedDirs();
    }

    private void BtnAiFsReload_Click(object sender, RoutedEventArgs e) => RebuildAiAllowedDirs();

    // ── 得到大脑凭证（按钮「存到得到大脑」与对话推送共用，改即保存） ──

    private void GetNoteKey_PasswordChanged(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.GetNoteApiKey = GetNoteKeyInput.Password; _settings.Save(); }

    private void GetNoteClientId_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.GetNoteClientId = GetNoteClientIdInput.Text.Trim(); _settings.Save(); }

    private async void BtnGetNoteTest_Click(object sender, RoutedEventArgs e)
    {
        _settings.GetNoteApiKey = GetNoteKeyInput.Password;   // 兜底落盘（正常已由输入事件保存）
        _settings.GetNoteClientId = GetNoteClientIdInput.Text.Trim();
        _settings.Save();

        BtnGetNoteTest.IsEnabled = false;
        GetNoteTestResult.Text = "正在连接…";
        try
        {
            var (ok, msg, topics) = await new GetNoteDestination(_settings).TestConnectionAsync();
            GetNoteTestResult.Text = msg;
            GetNoteTestResult.Foreground = new SolidColorBrush(
                ok ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xEF, 0x53, 0x50));
            if (ok) FillTopicCombo(topics);
        }
        catch (Exception ex)
        {
            GetNoteTestResult.Text = $"连接出错：{ex.Message}";
            GetNoteTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
        }
        finally
        {
            BtnGetNoteTest.IsEnabled = true;
        }
    }

    /// <summary>回填知识库下拉：首项=账号默认库，其余来自 knowledge/list；命中已存默认库则选中</summary>
    private void FillTopicCombo(List<GetNoteDestination.GetNoteTopic> topics)
    {
        var prev = _suppressEvents;
        _suppressEvents = true;
        try
        {
            CmbGetNoteTopic.Items.Clear();
            CmbGetNoteTopic.Items.Add(new ComboBoxItem { Content = "（账号默认库）", Tag = "" });
            foreach (var t in topics)
                CmbGetNoteTopic.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });

            var idx = 0;
            for (var i = 1; i < CmbGetNoteTopic.Items.Count; i++)
                if ((CmbGetNoteTopic.Items[i] as ComboBoxItem)?.Tag as string == _settings.GetNoteDefaultTopicId)
                { idx = i; break; }
            CmbGetNoteTopic.SelectedIndex = idx;
        }
        finally { _suppressEvents = prev; }
    }

    private void CmbGetNoteTopic_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var item = CmbGetNoteTopic.SelectedItem as ComboBoxItem;
        _settings.GetNoteDefaultTopicId = item?.Tag as string ?? "";
        _settings.GetNoteDefaultTopicName = item is { Tag: string id } && id.Length > 0
            ? item.Content as string ?? ""
            : "";
        _settings.Save();
    }

    /// <summary>凭证已配置时打开设置后台拉取知识库列表（失败静默：下拉保持已存默认库，不打断设置使用）</summary>
    private async Task LoadGetNoteTopicsQuietly()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) ||
                string.IsNullOrWhiteSpace(_settings.GetNoteClientId)) return;
            var (ok, _, topics) = await new GetNoteDestination(_settings).TestConnectionAsync();
            if (ok && topics.Count > 0)
                await Dispatcher.InvokeAsync(() => FillTopicCombo(topics));
        }
        catch { /* 静默失败 */ }
    }

    // ── 运行日志（通用板块） ──

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppLog.EnsureLogDir(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings", "打开日志文件夹失败", ex);
            MessageBox.Show(this, $"打开日志文件夹失败：{ex.Message}\n\n可手动打开：{AppLog.EnsureLogDir()}",
                "FocusCapture 提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LogRetention_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!int.TryParse(LogRetentionInput.Text.Trim(), out var days)) return; // 输入中途的空/非数字不处理，失焦或下次输入生效
        var clamped = Math.Clamp(days, 1, 365);
        if (clamped != days)
        {
            _suppressEvents = true;
            LogRetentionInput.Text = clamped.ToString();
            _suppressEvents = false;
        }
        _settings.LogRetentionDays = clamped;
        _settings.Save();
        AppLog.ApplyRetention(clamped); // 立即生效并清理过期日志
    }

    // ══════════════ 模型供应商列表 / 编辑（2026-09-23 多供应商改造） ══════════════

    /// <summary>
    /// 界面快照专用（与其它 Seed*ForSnapshot 同套做法）：造两家供应商再刷列表。
    ///
    /// <para>为什么必须塞数据：快照跑在隔离沙箱里，settings.json 是全新的、一个供应商都没有，
    /// 不塞的话这张图只有一行空态提示 —— 而卡片布局（长地址省略、按钮对齐、**状态点五种配色**）
    /// 全都看不到。出图就是为了看这些。</para>
    ///
    /// <para>刻意不调 Save()：快照只渲染，不写盘（硬规则 0）。</para>
    /// </summary>
    internal void SeedAiProvidersForSnapshot()
    {
        _settings.AiModelProviders.Clear();
        _settings.AiModelProviders.Add(new AiProviderEntry
        {
            Id = "snap1", Name = "Agnes 中国站", BaseUrl = "https://api.agnes-ai.cn/v1",
            ApiKey = "sk-snapshot-not-a-real-key",
            LastTestedAt = "2026-09-23T20:40:00", LastTestStatus = "Ok",
            Models = { new AiModelEntry { Id = "agnes-3.0-flash", DisplayName = "Agnes 3.0 Flash" } },
        });
        _settings.AiModelProviders.Add(new AiProviderEntry
        {
            Id = "snap2", Name = "siliconflow", BaseUrl = "https://api.siliconflow.cn/v1",
            ApiKey = "sk-snapshot-not-a-real-key",
            LastTestedAt = "2026-09-23T20:41:00", LastTestStatus = "Account",
            Models =
            {
                new AiModelEntry
                {
                    Id = "deepseek-ai/DeepSeek-V4-Flash", DisplayName = "DeepSeek V4 Flash",
                    ContextWindow = 1048576, MaxOutputTokens = 393216,
                },
            },
        });
        _settings.ActiveModelKey = "snap1/agnes-3.0-flash";
        _settings.DefaultModelKey = "snap1/agnes-3.0-flash";
        RebuildAiProviderList();
    }

    /// <summary>
    /// 重建供应商卡片列表。**整表重建在这里是安全的**：卡片上没有正在编辑的中间态 ——
    /// 用户真正在编辑的东西在独立的编辑窗口里，改完才回来刷这张列表。
    /// </summary>
    private void RebuildAiProviderList()
    {
        AiProviderList.Children.Clear();
        foreach (var p in _settings.AiModelProviders)
            AiProviderList.Children.Add(BuildProviderCard(p));

        AiProviderEmptyText.Visibility = _settings.AiModelProviders.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        AiDefaultModelText.Text = BuildDefaultModelText();
        AiTidyModelText.Text = BuildTidyModelText();
    }

    /// <summary>
    /// 「默认模型」那一行显示什么（2026-09-26）。
    /// <list type="bullet">
    /// <item>用户设的那个键<b>精确命中</b> → 显示模型名（以用户自己填的显示名为准，跨供应商重名时才补「（供应商）」）。</item>
    /// <item>没设过 / 键已失效 → 说明白<b>新会话实际会用哪个</b>。写「（未设置）」会让刚配好模型的用户
    /// 以为哪里坏了 —— 其实解析链会回退到「上次使用」或第一个可用模型，AI 照常能用。</item>
    /// </list>
    /// </summary>
    private string BuildDefaultModelText()
    {
        var exact = AiModelResolver.TryResolveExact(_settings, _settings.DefaultModelKey);
        if (exact != null) return AiModelResolver.DisplayNameFor(_settings, exact);

        var fallback = AiModelResolver.ResolveForNewSession(_settings);
        return fallback == null
            ? "（未配置）"
            : $"（未设置 · 新会话用 {AiModelResolver.DisplayNameFor(_settings, fallback)}）";
    }

    /// <summary>点「默认模型」那一行 → 弹已配置模型列表，点中即生效（用户拍板：不弹确认框）。
    /// 与 AI 问答里那个会话级下拉同一套展示口径（名字不带供应商前缀，重名才补）。</summary>
    private void DefaultModelRow_Click(object sender, MouseButtonEventArgs e)
    {
        // 刻意用 new ContextMenu() 不用初始化器：自带 Style 会顶掉 App.xaml 深色模板出白条（项目已踩过）
        var menu = new ContextMenu();
        foreach (var p in _settings.AiModelProviders)
        {
            foreach (var m in p.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                var key = p.Id + "/" + m.Id;
                var resolved = AiModelResolver.TryResolveExact(_settings, key);
                var label = resolved != null
                    ? AiModelResolver.DisplayNameFor(_settings, resolved)
                    : (string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName);
                var item = new MenuItem { Header = label, IsChecked = _settings.DefaultModelKey == key };
                var captured = key;
                item.Click += (_, _) => ApplyDefaultModel(captured);
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "（还没有配置任何模型）", IsEnabled = false });

        menu.PlacementTarget = DefaultModelRow;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>设置默认模型：写 <see cref="AppSettings.DefaultModelKey"/>，
    /// <b>同时</b>把「上次使用」一起覆盖成同一个键 —— 否则用户刚改完默认模型，新会话仍被"上次使用"盖住，
    /// 看起来就是"设置了没反应"（2026-09-26 用户明确要求改完即生效）。</summary>
    private void ApplyDefaultModel(string key)
    {
        _settings.DefaultModelKey = key;
        _settings.ActiveModelKey = key;
        _settings.Save();
        RebuildAiProviderList();
        _onChanged?.Invoke();
    }

    /// <summary>
    /// 「AI 整理模型」那一行显示什么（2026-09-26）。
    /// <list type="bullet">
    /// <item>用户选过且键<b>精确命中</b> → 显示那个模型名。</item>
    /// <item>没选过 / 键已失效 → 说明白"跟随当前模型"，并把实际会用的那个模型名写出来 ——
    /// 只写「跟随当前模型」用户仍不知道到底是哪个模型在干活。</item>
    /// </list>
    /// </summary>
    private string BuildTidyModelText()
    {
        var exact = AiModelResolver.TryResolveExact(_settings, _settings.AiTidyModelKey);
        if (exact != null) return AiModelResolver.DisplayNameFor(_settings, exact);

        var fallback = AiModelResolver.ResolveActive(_settings);
        return fallback == null
            ? "跟随当前模型（当前没有可用模型）"
            : $"跟随当前模型（{AiModelResolver.DisplayNameFor(_settings, fallback)}）";
    }

    /// <summary>点「AI 整理模型」那一行 → 弹已配置模型列表，点中即生效（用户拍板：不弹确认框）。
    /// 第一项固定是「跟随当前模型」（= 不写这份配置，与聊天共用），与 AI 问答的模型下拉同一套展示口径。</summary>
    private void TidyModelRow_Click(object sender, MouseButtonEventArgs e)
    {
        // 同 DefaultModelRow_Click：刻意用 new ContextMenu() 不用初始化器（自带 Style 会顶掉 App.xaml 深色模板出白条）
        var menu = new ContextMenu();

        var follow = new MenuItem
        {
            Header = "跟随当前模型",
            IsChecked = string.IsNullOrWhiteSpace(_settings.AiTidyModelKey),
        };
        follow.Click += (_, _) => ApplyTidyModel("");
        menu.Items.Add(follow);
        menu.Items.Add(new Separator());

        var count = 0;
        foreach (var p in _settings.AiModelProviders)
        {
            foreach (var m in p.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                var key = p.Id + "/" + m.Id;
                var resolved = AiModelResolver.TryResolveExact(_settings, key);
                var label = resolved != null
                    ? AiModelResolver.DisplayNameFor(_settings, resolved)
                    : (string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName);
                var item = new MenuItem { Header = label, IsChecked = _settings.AiTidyModelKey == key };
                var captured = key;
                item.Click += (_, _) => ApplyTidyModel(captured);
                menu.Items.Add(item);
                count++;
            }
        }

        if (count == 0)
            menu.Items.Add(new MenuItem { Header = "（还没有配置任何模型）", IsEnabled = false });

        menu.PlacementTarget = TidyModelRow;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>设置整理模型：写 <see cref="AppSettings.AiTidyModelKey"/> 并落盘。空 = 跟随当前活跃模型。
    /// 刻意不像「默认模型」那样连带改 ActiveModelKey —— 那会把聊天正在用的模型一起改掉（用户没要求）。</summary>
    private void ApplyTidyModel(string key)
    {
        _settings.AiTidyModelKey = key ?? "";
        _settings.Save();
        RebuildAiProviderList();
        _onChanged?.Invoke();
    }

    /// <summary>建一张供应商卡片：状态点 + 名称/摘要 + 编辑/删除。</summary>
    private FrameworkElement BuildProviderCard(AiProviderEntry provider)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Child = grid;

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Fill = new SolidColorBrush(ProviderStatusColor(provider.LastTestStatus)),
            ToolTip = ProviderStatusTooltip(provider),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(provider.Name) ? "（未命名）" : provider.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var preset = AiProviders.MatchByUrl(provider.BaseUrl);
        text.Children.Add(new TextBlock
        {
            Text = $"{provider.Models.Count} 个模型 · {preset?.Name ?? AiProviders.Custom} · {provider.BaseUrl}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editBtn = new Button { Content = "编辑", Width = 64, Margin = new Thickness(8, 0, 0, 0) };
        editBtn.Click += (_, _) => EditProvider(provider);
        actions.Children.Add(editBtn);
        var delBtn = new Button { Content = "删除", Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        delBtn.Click += (_, _) => DeleteProvider(provider);
        actions.Children.Add(delBtn);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return card;
    }

    /// <summary>状态点配色。分类型是因为**断网时所有供应商会一起失败**，若不分类用户会以为 Key 全坏了。</summary>
    private static Color ProviderStatusColor(string status) => status switch
    {
        "Ok" => Color.FromRgb(0x4C, 0xAF, 0x50),       // 绿：上次检测通过
        "Account" => Color.FromRgb(0xE5, 0xA3, 0x35),  // 橙：余额/套餐问题 —— 重试无用，得去充值
        "Key" => Color.FromRgb(0xE5, 0x39, 0x35),      // 红：Key 无效
        "Server" => Color.FromRgb(0xE5, 0x39, 0x35),   // 红：供应商侧问题
        "Network" => Color.FromRgb(0x88, 0x88, 0x88),  // 灰：本机网络不通，与配置无关
        _ => Color.FromRgb(0x66, 0x66, 0x66),          // 深灰：尚未检测
    };

    /// <summary>状态点的悬停文案。措辞刻意**不写「可用」** —— 它只代表「上次检测的结果 + 时间」。</summary>
    private static string ProviderStatusTooltip(AiProviderEntry provider)
    {
        var when = TryFormatTestTime(provider.LastTestedAt);
        return provider.LastTestStatus switch
        {
            "Ok" => $"上次检测：通过（{when}）",
            "Account" => $"上次检测：账户余额不足或套餐到期（{when}）",
            "Key" => $"上次检测：API Key 未通过验证（{when}）",
            "Server" => $"上次检测：供应商暂时不可用（{when}）",
            "Network" => $"上次检测：本机网络不可用，与配置无关（{when}）",
            _ => "尚未检测过 —— 打开编辑页点「测试连接」即可检测",
        };
    }

    private static string TryFormatTestTime(string? iso)
        => DateTime.TryParse(iso ?? "", out var t) ? t.ToString("yyyy-MM-dd HH:mm") : "时间未知";

    /// <summary>
    /// 「添加提供方」→ 就地弹出预置列表（2026-09-23 按用户反馈调整）。
    ///
    /// <para>改前是「外层先选下拉、再点按钮」，读起来是两步互不相干的动作；
    /// 现在把选择收进按钮里 —— 点它才出现选项，与原来「点模型供应商即弹出预置」的手感一致。</para>
    ///
    /// <para>用 <c>new ContextMenu()</c> <b>不带对象初始化器</b>：自带 Style/模板会顶掉 App.xaml 的深色模板，
    /// 结果是一条浅色白条（项目在待办汇总右键菜单上实际踩过这个坑）。</para>
    /// </summary>
    private void BtnAddPresetProvider_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var preset in AiProviders.Presets)
        {
            var item = new MenuItem { Header = preset.Name };
            var picked = preset;   // 显式局部变量：避免闭包捕获的歧义读法
            item.Click += (_, _) => AddProviderFromPreset(picked);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = BtnAddPresetProvider;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddProviderFromPreset(AiProviderPreset preset)
        => OpenProviderEditor(new AiProviderEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = preset.Name,
            BaseUrl = preset.BaseUrl,
        }, isNew: true);

    private void BtnAddCustomProvider_Click(object sender, RoutedEventArgs e)
        => OpenProviderEditor(new AiProviderEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = AiProviders.Custom,
            BaseUrl = "",
        }, isNew: true);

    private void EditProvider(AiProviderEntry provider) => OpenProviderEditor(provider, isNew: false);

    /// <summary>
    /// 打开编辑窗口。**编辑的是草稿副本** —— 点「取消」或直接关窗，原配置一个字都不动。
    /// 保存后才回写设置并通知主程序重建 provider（改完即生效，不用重启应用）。
    /// </summary>
    private void OpenProviderEditor(AiProviderEntry source, bool isNew)
    {
        var win = new AiProviderEditWindow(source.Clone()) { Owner = this };
        if (win.ShowDialog() != true || win.Result is null) return;

        var result = win.Result;
        // 保存**之前**先记下"此前能不能用" —— 这是判断"用户第一次把模型配起来"的唯一依据。
        // 写进去之后再问 IsConfigured 必然为真，弹窗就永远问得着（每次都弹 = 打扰）。
        var wasConfigured = AiModelResolver.IsConfigured(_settings);

        var idx = _settings.AiModelProviders.FindIndex(p => p.Id == source.Id);
        if (idx >= 0) _settings.AiModelProviders[idx] = result;
        else _settings.AiModelProviders.Add(result);

        _settings.Save();
        RebuildAiProviderList();

        // 首次配置成功 → 问一句要不要设为默认模型（2026-09-26 用户拍板）。
        // 这里原来是"静默把第一个模型设为当前使用"（旧语义＝全局默认模型，本期已退场）：
        // 现在改成显式征询并落 DefaultModelKey，用户选"否"也不影响可用性（解析链会回退到第一个可用模型）。
        if (!wasConfigured) PromptSetDefaultModel(result);

        RebuildAiProviderList();
        _onChanged?.Invoke();
    }

    /// <summary>首次配置成功时的询问（2026-09-26 用户拍板）：要不要把这个模型设为默认模型。
    /// 这是全 App 唯一一处主动弹窗问模型 —— 其余场合都靠"上次使用"记忆与解析回退静默处理。</summary>
    private void PromptSetDefaultModel(AiProviderEntry provider)
    {
        var first = provider.Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Id));
        if (first == null) return;
        var name = string.IsNullOrWhiteSpace(first.DisplayName) ? first.Id : first.DisplayName;

        var answer = MessageBox.Show(this,
            $"是否把「{name}」设为默认模型？\n\n" +
            "默认模型只在你还用不上「上次使用的模型」时起作用；以后新会话默认用你上次用过的那个。\n" +
            "之后可以随时在本页「默认模型」那一行更换。",
            "设置默认模型", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var key = provider.Id + "/" + first.Id;
        _settings.DefaultModelKey = key;
        _settings.ActiveModelKey = key;   // 同值：用户此刻的意图就是"以后就用它"，别让"上次使用"再盖回去
        _settings.Save();
    }

    private void DeleteProvider(AiProviderEntry provider)
    {
        var name = string.IsNullOrWhiteSpace(provider.Name) ? "（未命名）" : provider.Name;
        var answer = System.Windows.MessageBox.Show(this,
            $"确定删除供应商「{name}」吗？\n\n它的 API Key 与 {provider.Models.Count} 个模型配置会一起消失，且无法撤销。",
            "删除供应商", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _settings.AiModelProviders.RemoveAll(p => p.Id == provider.Id);

        // 删掉的正好是「上次使用」或「默认模型」指向的那家 → 两个键都别留成指向空气的（2026-09-26：
        // 以前只顾了 ActiveModelKey；现在设置页会显示默认模型，留着失效键会显示成一句看不懂的话）。
        // 回退到删完之后第一个「配置完整」的模型；一个都没有就置空，解析层还有旧扁平字段兜底。
        var prefix = provider.Id + "/";
        var fallback = "";
        foreach (var p in _settings.AiModelProviders)
        {
            var m = p.Models.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Id));
            if (m == null || string.IsNullOrWhiteSpace(p.BaseUrl)) continue;
            fallback = p.Id + "/" + m.Id;
            break;
        }
        if ((_settings.ActiveModelKey ?? "").StartsWith(prefix, StringComparison.Ordinal))
            _settings.ActiveModelKey = fallback;
        if ((_settings.DefaultModelKey ?? "").StartsWith(prefix, StringComparison.Ordinal))
            _settings.DefaultModelKey = fallback;

        _settings.Save();
        RebuildAiProviderList();
        _onChanged?.Invoke();
    }

    private void AiAssistantName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiAssistantName = AiAssistantNameInput.Text.Trim();
        _settings.Save();
        _onChanged?.Invoke();
    }

    /// <summary>Agent 工具轮数上限：1~50 合法才落盘</summary>
    private void AgentMaxToolRounds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (int.TryParse(AgentMaxToolRoundsInput.Text.Trim(), out var v) && v is >= 1 and <= 50)
        { _settings.AgentMaxToolRounds = v; _settings.Save(); }
    }

    /// <summary>工具结果截断阈值（字符数）：500~65536 合法才落盘</summary>
    private void AiToolResultLimit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (int.TryParse(AiToolResultLimitInput.Text.Trim(), out var v) && v is >= 500 and <= 65536)
        { _settings.AiToolResultLimit = v; _settings.Save(); }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (rk == null) return;
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            if (enable) rk.SetValue("FocusCapture", $"\"{exe}\"");
            else rk.DeleteValue("FocusCapture", false);
        }
        catch { }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        { Description = "选择笔记存储目录", SelectedPath = _settings.NotesPath };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        { _settings.NotesPath = dlg.SelectedPath; NotesPathText.Text = dlg.SelectedPath; _settings.Save(); }
    }

    // ── 外观：自定义托盘图标 ──

    /// <summary>自定义图标落盘位置：与 AppIconService 同源（托盘 + 任务栏窗口图标共用这一张图）。</summary>
    private static string CustomIconPath => AppIconService.DefaultIconPath;

    private void UpdateIconUI()
    {
        var hasCustom = !string.IsNullOrEmpty(_settings.CustomIconPath) && File.Exists(_settings.CustomIconPath);
        BtnResetIcon.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;
        IconStatusText.Text = hasCustom ? "已使用自定义图标" : "";
    }

    private void BtnChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择任务栏/托盘图标（png/jpg，≤1MB）",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var file = new FileInfo(dlg.FileName);
        var ext = file.Extension.ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
        {
            IconStatusText.Text = "仅支持 png/jpg 图片";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            return;
        }
        if (file.Length > 1024 * 1024)
        {
            IconStatusText.Text = "图片超过 1MB，请换一张更小的";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            return;
        }

        // 复制到 %AppData%\FocusCapture\custom_icon.png 并持久化路径
        try
        {
            var dir = Path.GetDirectoryName(CustomIconPath)!;
            Directory.CreateDirectory(dir);
            File.Copy(dlg.FileName, CustomIconPath, true);
            _settings.CustomIconPath = CustomIconPath;
            _settings.Save();
            IconStatusText.Text = "已保存，任务栏与托盘图标立即生效";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            UpdateIconUI();
            _onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            IconStatusText.Text = $"保存失败：{ex.Message}";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        }
    }

    private void BtnResetIcon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(CustomIconPath)) File.Delete(CustomIconPath);
            _settings.CustomIconPath = "";
            _settings.Save();
            IconStatusText.Text = "已恢复默认图标";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            UpdateIconUI();
            _onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            IconStatusText.Text = $"恢复失败：{ex.Message}";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        }
    }

    // ── AI 问答界面（2026-09-24）：侧边栏 / 昵称 / 自定义欢迎语 / 用户头像 ──
    // 图片走 ChatAssetsService（不持久化绝对路径，判据 = Has*）；昵称 / 欢迎语 / 侧边栏默认展开走 AppSettings。
    // 与上面「自定义托盘图标」是两套独立资源，互不串味（见 ChatAssetsService 头注释）。
    // 2026-09-26：「欢迎语图标」整块按用户要求删除（起手页不再显示图标，此处入口与 ChatAssetsService 的 4 个 API 一并移除；
    // 已落盘的 chat_welcome_icon.* 保留不删 —— 删除类动作只给用户，且已无代码读取它）。
    private void UpdateChatAssetsUI()
    {
        var hasAvatar = ChatAssetsService.HasUserAvatar;
        AvatarStatus.Text = hasAvatar ? "已设置用户头像" : "未设置（昵称首字色块）";
        AvatarStatus.Foreground = hasAvatar
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void SidebarDefaultExpanded_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.ChatSidebarDefaultExpanded = SidebarDefaultExpandedCheck.IsChecked == true;
        _settings.Save();
        _onChanged?.Invoke();
    }

    private void ChatNickname_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.ChatUserNickname = ChatNicknameInput.Text;
        _settings.Save();
        _onChanged?.Invoke();
    }

    /// <summary>自定义欢迎语（2026-09-26 新增）：存整句，留空 = 回到默认句式「{昵称}，我帮你」。
    /// 改完即时生效 —— _onChanged 会让 AI 问答窗口重刷起手页（下次打开/切回起手态就能看到）。</summary>
    private void ChatWelcome_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.ChatWelcomeText = ChatWelcomeInput.Text;
        _settings.Save();
        _onChanged?.Invoke();
    }

    // 起手页快捷问法胶囊（2026-09-27）：每行一条；去空白行、截前 8 条。
    // 窗口内解析统一走 AIDialogWindow.UpdateWelcomeContent（trim + 跳空行），这里只存原文行。
    private void ChatQuickPrompts_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.ChatQuickPrompts = ChatQuickPromptsInput.Text
            .Split(['\r', '\n'])
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(8)
            .ToList();
        _settings.Save();
        _onChanged?.Invoke();
    }

    private void BtnAvatarPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择用户头像（图片，≤10MB）",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        if (ChatAssetsService.ImportUserAvatar(dlg.FileName) == null)
        {
            AvatarStatus.Text = "导入失败：格式不支持 / 文件过大（≤10MB）/ 文件不可读";
            AvatarStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            return;
        }
        UpdateChatAssetsUI();
        _onChanged?.Invoke();
    }

    private void BtnAvatarReset_Click(object sender, RoutedEventArgs e)
    {
        ChatAssetsService.ResetUserAvatar();
        UpdateChatAssetsUI();
        _onChanged?.Invoke();
    }

    private void BtnRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (_noteService == null) return;
        var bin = new RecycleBinService(_settings.NotesPath);
        var win = new RecycleBinWindow(_noteService, bin, _syncEngineProvider?.Invoke()) { Owner = this };
        win.ShowDialog();
    }

    // ── 云同步（WebDAV 配置 / E2EE 主密码 / 同步控制 / 重置） ──

    /// <summary>保存 WebDAV 配置 + 解锁/迁移 + 立即同步一次（授权码即钥匙：填一次永久有效，方案A 2026-08-15）。</summary>
    private async void BtnSyncConnect_Click(object sender, RoutedEventArgs e)
    {
        var url = SyncUrlInput.Text.Trim();
        var user = SyncUserInput.Text.Trim();
        var token = SyncTokenInput.Password;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user))
        {
            SyncStatusText.Text = "请填写服务器地址、坚果云账号";
            return;
        }
        if (string.IsNullOrEmpty(token))
        {
            // 授权码留空 = 沿用已保存的（DPAPI 密文解出；应用重启后依然有效）
            var saved = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
            if (string.IsNullOrEmpty(saved))
            {
                SyncStatusText.Text = "请填写坚果云授权码（坚果云客户端『设置 → 第三方应用管理』生成的应用密码）";
                return;
            }
            token = saved;
        }

        _settings.Sync.ProviderName = "WebDAV";
        _settings.Sync.WebDavUrl = url;
        _settings.Sync.WebDavUser = user;
        _settings.Sync.WebDavToken = Models.SyncSettings.ProtectToken(token);
        _settings.Save();
        _onSyncConfigChanged?.Invoke();   // MainWindow 用新配置重建引擎

        var engine = _syncEngineProvider?.Invoke();
        if (engine == null)
        {
            SyncStatusText.Text = "同步引擎不可用，请检查配置";
            return;
        }

        SyncStatusText.Text = engine.IsLegacyMasterPasswordMode
            ? "检测到旧版主密码配置，正在一键升级（本地明文重新加密上传）…"
            : "正在连接并同步…";
        try
        {
            SyncResult result;
            if (engine.IsLegacyMasterPasswordMode)
                result = await engine.MigrateFromLegacyAsync();          // 旧版：无需原主密码，自动升级
            else
            {
                await engine.SetTokenKeyAsync(token);
                result = await engine.SyncNowAsync(auto: false);
            }
            SyncStatusText.Text = result.Success
                ? $"连接成功，已同步（{_settings.Sync.LastSyncAt}）。建议勾选『自动同步』"
                : "连接失败：" + result.Error;
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "连接失败：" + ex.Message;
        }
    }

    private async void BtnSyncNow_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            SyncStatusText.Text = "请先『保存并连接』（未解锁或未配置）";
            return;
        }
        SyncStatusText.Text = "正在同步…";
        var result = await engine.SyncNowAsync(auto: false);
        SyncStatusText.Text = result.Success
            ? $"同步完成（{_settings.Sync.LastSyncAt}）"
            : "同步失败：" + result.Error;
    }

    /// <summary>从云端重新拉取（2026-09-09）：清空同步进度 → 只拉取。修「状态成功但本地一条没收到」的自助入口；
    /// 本地数据不动、不上传，已有行幂等跳过，缺的补上。</summary>
    private async void BtnRepull_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            SyncStatusText.Text = "请先『保存并连接』（未解锁或未配置）";
            return;
        }
        var choice = System.Windows.MessageBox.Show(
            "将清空同步进度，把云端全部笔记重新对一遍：\n\n· 本地已有的内容不会重复、不会改动\n· 缺失的内容会补回来\n· 本机任何数据都不会被上传或覆盖\n\n继续吗？",
            "从云端重新拉取", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        BtnRepull.IsEnabled = false;
        SyncStatusText.Text = "正在从云端重新拉取…";
        try
        {
            var result = await engine.RepullFromCloudAsync();
            SyncStatusText.Text = result.Success
                ? $"重新拉取完成（{_settings.Sync.LastSyncAt}）"
                : "重新拉取失败：" + result.Error;
        }
        catch (Exception ex) { SyncStatusText.Text = "重新拉取失败：" + ex.Message; }
        finally { BtnRepull.IsEnabled = true; }
    }

    private void AutoSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.AutoSyncEnabled = AutoSyncCheck.IsChecked == true;
        _settings.Save();
        var engine = _syncEngineProvider?.Invoke();
        if (_settings.Sync.AutoSyncEnabled) engine?.StartAutoSync();
        else engine?.StopAutoSync();
    }

    /// <summary>上传合并间隔（笔记+会话共用）：失焦保存，非数字/低于 30 按 30 兜底（坚果云红线）；重排合并窗口立即生效。</summary>
    private void MergeWindow_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var ok = int.TryParse(MergeWindowInput.Text.Trim(), out var seconds);
        var value = ok ? Math.Max(30, seconds) : 30;
        if (_settings.Sync.MergeWindowSeconds == value) { MergeWindowInput.Text = value.ToString(); return; }
        _settings.Sync.MergeWindowSeconds = value;
        _settings.Save();
        MergeWindowInput.Text = value.ToString();
        _syncEngineProvider?.Invoke()?.RefreshMergeWindow();
    }

    /// <summary>同步 AI 问答记录开关：关 = ChatSyncEngine 完全不跑（笔记同步不受影响）。</summary>
    private void ChatSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.ChatSyncEnabled = ChatSyncCheck.IsChecked == true;
        _settings.Save();
    }

    private async void BtnResetSync_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "重置同步状态将清空云端全部桶并全量重新上传。\n确认继续？",
            "重置同步状态", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null)
        {
            SyncStatusText.Text = "同步引擎不可用";
            return;
        }
        SyncStatusText.Text = "正在重置并全量重传…";
        var result = await engine.ResetSyncAsync();
        SyncStatusText.Text = result.Success ? "已重置并全量重传" : "重置失败：" + result.Error;
    }

    // ══════════════════ 文件与网盘（2026-09-16） ══════════════════

    private CancellationTokenSource? _baiduAuthCts;

    /// <summary>加载文件/网盘板块。</summary>
    private void LoadFileSettings()
    {
        var creds = BaiduCredentialStore.LoadCredentials();
        BaiduAppKeyInput.Text = creds?.AppKey ?? "";
        // SecretKey 刻意不回显：留在输入框里会跟着截图/录屏一起泄露。留空 = 沿用已保存的那份。
        BaiduSecretInput.Password = "";
        BaiduNetRootInput.Text = _settings.BaiduNetRoot;

        CacheMaxGbInput.Text = _settings.LocalCacheMaxGb.ToString("0.#");
        CacheIdleDaysInput.Text = _settings.LocalCacheIdleDays.ToString();
        CacheEvictCheck.IsChecked = _settings.LocalCacheEvictEnabled;

        AttachmentUploadCheck.IsChecked = _settings.BaiduAttachmentUploadEnabled;
        AttachmentRetentionInput.Text = _settings.BaiduAttachmentRetentionDays.ToString();

        DataRootText.Text = FocusCapturePaths.Root;
        DataRootStatusText.Text = FocusCapturePaths.IsCustomRootInEffect
            ? "当前使用自定义目录；改回默认目录时把指针文件 data_root.txt 删掉即可。"
            : "当前使用默认目录（%AppData%\\FocusCapture）。";

        CloudVerifyService.Settings = _settings;   // 核对完成后要记「最后核对时刻」
        RefreshBaiduStatus();
        RefreshCacheStatus();
        RefreshCloudVerifyStatus();
    }

    /// <summary>
    /// 与云端核对（2026-09-27）：拉云端实际清单 → 与本地记录比对 → 出报告让用户决定。
    ///
    /// 全程**只读**：云端不写、本地在本步也不写；写回标注发生在报告窗口里、由用户点。
    /// 前置条件（未授权 / 清单还在同步 / 冷却期内）先问 <see cref="CloudVerifyService.CanRun"/>，
    /// 直接把原因显示出来 —— 否则用户看到的就是「点了没反应」。
    /// </summary>
    private async void BtnCloudVerify_Click(object sender, RoutedEventArgs e)
    {
        // 2026-10-08 门卫（云端核对归「云同步」档）：必须排在 CanRun() **之前** ——
        // CanRun() 会先报「未授权 / 清单还在同步 / 冷却期内」，未解锁时那些原因全是误导，
        // 用户会去反复重连 WebDAV，实际只是没激活。
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureSync, "与云端核对", this)) return;

        var blocked = CloudVerifyService.CanRun();
        if (blocked != null)
        {
            CloudVerifyStatusText.Text = blocked;
            return;
        }

        BtnCloudVerify.IsEnabled = false;
        try
        {
            var (report, error) = await CloudVerifyService.RunAsync(
                new Progress<string>(s => CloudVerifyStatusText.Text = s));

            RefreshCloudVerifyStatus();

            if (report == null)
            {
                CloudVerifyStatusText.Text = error ?? "核对未能完成。";
                return;
            }

            // 真对话框：用户看完、点完就关（与"长驻面板必须用 Show()"那类不是一回事）
            var win = new CloudVerifyWindow(report) { Owner = this };
            win.ShowDialog();
            RefreshCacheStatus();   // 标注可能改变了列表状态
        }
        catch (Exception ex)
        {
            // UI 事件里的外部资源操作自己 try 住、不弹模态框（界面铁律：模态框会吃掉后续点击）
            CloudVerifyStatusText.Text = "核对失败：" + ex.Message;
        }
        finally
        {
            BtnCloudVerify.IsEnabled = true;
        }
    }

    /// <summary>刷新核对状态文案（显示最后核对时刻 —— 结论会过期，新鲜度必须可见）。</summary>
    private void RefreshCloudVerifyStatus()
    {
        var at = CloudVerifyService.LastRunAt();
        CloudVerifyStatusText.Text = at.HasValue
            ? $"上次核对：{at.Value:yyyy-MM-dd HH:mm}"
            : "尚未核对过";
    }

    private void RefreshBaiduStatus()
    {
        var creds = BaiduCredentialStore.LoadCredentials();
        if (creds?.IsComplete != true)
        {
            BaiduStatusText.Text = "尚未配置应用凭据：请填写 AppKey 与 SecretKey（在百度网盘开放平台创建「个人使用」应用后获得）。";
            return;
        }

        var token = BaiduCredentialStore.LoadToken();
        var head = $"已配置应用（AppKey {BaiduCredentialStore.Mask(creds.AppKey)}）";
        BaiduStatusText.Text = token == null
            ? head + "，尚未授权。点「开始授权」按提示用手机扫码或输入授权码即可。"
            : head + $"；授权令牌{(token.IsValid ? "有效" : "已过期，下次使用时会自动刷新")}。";
    }

    private void RefreshCacheStatus()
    {
        var (count, bytes, protectedCount) = CacheEvictor.Measure();
        var text = count == 0
            ? "本机暂无缓存的网盘文件"
            : $"本机缓存 {count} 个文件，共 {RootMigrationService.FormatSize(bytes)}";
        if (protectedCount > 0)
            text += $"；其中 {protectedCount} 个尚未上传完成，不会被自动清理";

        // 「一直传不上去」必须看得见：否则用户以为存成功了，直到某天想取回才发现云端根本没有
        var stuck = FileRepository.StuckUploadCount(UploadQueue.MaxRetry);
        if (stuck > 0)
            text += $"；⚠ 有 {stuck} 个文件多次上传失败（多半是授权或网盘目录没配好。" +
                    "修好后点上面的「测试连接」，通过时会自动重试）";

        // 「云端还有东西没清掉」同样必须看得见（2026-09-16）：否则用户以为清理功能坏了，
        // 或者更糟 —— 以为已经清干净了。清理失败的记录会在设置页一直挂着，直到真的清掉。
        var pendingCleanup = FileRepository.CleanupPendingCount();
        if (pendingCleanup > 0)
            text += $"；⚠ 有 {pendingCleanup} 个文件的云端副本尚未清理（附件到期或删除失败）。" +
                    "点「重试云端清理」；若仍失败，可用「复制网盘路径」到网盘里手动删除";

        CacheStatusText.Text = text + "。";
    }

    // ── 应用凭据 ──

    private void BaiduAppKey_LostFocus(object sender, RoutedEventArgs e) => SaveBaiduCredentials();

    private void BaiduSecret_LostFocus(object sender, RoutedEventArgs e) => SaveBaiduCredentials();

    /// <summary>
    /// 保存应用凭据。两个输入框是配合关系（AppKey 明文可回显、SecretKey 一律不回显），
    /// 所以只在「两个都填了」或「AppKey 变了且已存有 SecretKey」时才落盘，避免半截覆盖。
    /// </summary>
    private void SaveBaiduCredentials()
    {
        var appKey = BaiduAppKeyInput.Text.Trim();
        var secret = BaiduSecretInput.Password.Trim();
        var saved = BaiduCredentialStore.LoadCredentials();

        if (appKey.Length == 0 && secret.Length == 0) return;
        if (secret.Length == 0)
        {
            if (saved?.IsComplete == true && appKey.Length > 0 && appKey != saved.AppKey)
            {
                // 只改了 AppKey：保留原 SecretKey（用户没重填就代表沿用）
                if (!BaiduCredentialStore.SaveCredentials(appKey, saved.SecretKey))
                    AppLog.Warn("Settings", "应用凭据保存失败");
                BaiduSecretInput.Password = "";
                RefreshBaiduStatus();
            }
            return;
        }
        if (appKey.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "请同时填写 AppKey。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (appKey == saved?.AppKey && secret == saved.SecretKey) return;

        if (!BaiduCredentialStore.SaveCredentials(appKey, secret))
            System.Windows.MessageBox.Show(this, "凭据保存失败，请检查磁盘权限。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        BaiduSecretInput.Password = "";   // 存完就清空输入框，屏幕上不留明文
        RefreshBaiduStatus();
    }

    private void BaiduNetRoot_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = BaiduNetdiskClient.NormalizeNetRoot(BaiduNetRootInput.Text);
        BaiduNetRootInput.Text = v;
        if (v == _settings.BaiduNetRoot) return;
        _settings.BaiduNetRoot = v;
        _settings.Save();
        FileRepository.Cloud = new BaiduCloudStorage(v);   // 立即生效，不必重启
    }

    // ── 授权 ──

    private async void BtnBaiduAuthorize_Click(object sender, RoutedEventArgs e)
    {
        if (_baiduAuthCts != null)
        {
            _baiduAuthCts.Cancel();
            _baiduAuthCts = null;
            return;   // 再点一次 = 取消正在进行的授权
        }

        SaveBaiduCredentials();
        var creds = BaiduCredentialStore.LoadCredentials();
        if (creds?.IsComplete != true)
        {
            System.Windows.MessageBox.Show(this,
                "请先填写并保存 AppKey 与 SecretKey。\n\n" +
                "获取方式：pan.baidu.com/union → 申请接入 → 实名认证 → 创建应用（用途选「个人使用」，类型选「软件」）。",
                "缺少应用凭据", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cts = new CancellationTokenSource();
        _baiduAuthCts = cts;
        BtnBaiduAuthorize.Content = "取消授权中…";
        try
        {
            BaiduStatusText.Text = "正在获取授权码…";
            var code = await BaiduNetdiskClient.RequestDeviceCodeAsync(creds.AppKey, cts.Token);

            BaiduStatusText.Text =
                $"已获取授权码：{code.UserCode}\n" +
                $"请在浏览器打开 {code.VerificationUrl} 并输入上面的授权码（已尝试自动打开浏览器）。\n" +
                "等待授权中…";

            try { Process.Start(new ProcessStartInfo(code.VerificationUrl) { UseShellExecute = true }); }
            catch { /* 打不开浏览器也不影响：用户可自行复制地址 */ }

            var token = await BaiduNetdiskClient.PollDeviceTokenAsync(creds.AppKey, creds.SecretKey, code,
                msg => Dispatcher.Invoke(() => BaiduStatusText.Text = "等待授权… " + msg), cts.Token);

            // 授权令牌必须立刻落盘：它是本机专属凭据，丢了要重新走一遍流程
            if (!BaiduCredentialStore.SaveToken(token))
                AppLog.Warn("Settings", "授权令牌保存失败");

            BaiduStatusText.Text = "授权成功。文件现在可以存到网盘了。";
            FileRepository.Cloud = new BaiduCloudStorage(_settings.BaiduNetRoot);

            // 「未授权」是失败重试的主要成因之一。授权刚成功，之前撞上限卡住的文件
            // 正是最该立刻重传的，顺手放回队列（否则它们会永远躺在失败态）。
            var requeued = FileRepository.ResetFailedUploads();
            if (requeued > 0)
                BaiduStatusText.Text += $"\n已把 {requeued} 个此前失败的文件放回上传队列，正在重传。";

            UploadQueue.Kick();   // 把之前因未授权而滞留的待传文件补上
            RefreshCacheStatus();
        }
        catch (OperationCanceledException)
        {
            BaiduStatusText.Text = "已取消授权。";
        }
        catch (Exception ex)
        {
            BaiduStatusText.Text = "授权失败：" + ex.Message;
        }
        finally
        {
            BtnBaiduAuthorize.Content = "开始授权";
            _baiduAuthCts = null;
            cts.Dispose();
        }
    }

    private async void BtnBaiduTest_Click(object sender, RoutedEventArgs e)
    {
        BtnBaiduTest.IsEnabled = false;
        BaiduStatusText.Text = "正在连接…";
        try
        {
            var (ok, message) = await BaiduCloudStorage.TestAsync(_settings.BaiduNetRoot, CancellationToken.None);

            // 测试通过 = 配置确实修好了 → 顺手把之前撞到重试上限、永久卡住的失败文件放回队列。
            // 「重试有上限」是为了不无限骚扰平台，但如果没有这个出口，用户改对了配置也永远等不到重传。
            if (ok)
            {
                var requeued = FileRepository.ResetFailedUploads();
                if (requeued > 0)
                {
                    UploadQueue.Kick();
                    message += $"\n已把 {requeued} 个此前失败的文件放回上传队列，正在重传。";
                }
                RefreshCacheStatus();
            }

            BaiduStatusText.Text = (ok ? "✓ " : "✗ ") + message;
        }
        catch (Exception ex)
        {
            BaiduStatusText.Text = "✗ 连接失败：" + ex.Message;
        }
        finally
        {
            BtnBaiduTest.IsEnabled = true;
        }
    }

    private void BtnBaiduRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,
                "取消授权？\n\n本机保存的授权令牌会被清除，需要重新授权才能继续存取网盘文件。\n" +
                "（网盘上已有的文件不受影响；应用凭据会保留）",
                "取消授权", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        BaiduCredentialStore.ClearToken();
        RefreshBaiduStatus();
    }

    // ── 本地缓存 ──

    private void CacheMaxGb_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CacheMaxGbInput.Text.Trim(), out var gb) && gb >= 0.5 && gb <= 1024)
        {
            _settings.LocalCacheMaxGb = gb;
            _settings.Save();
            CacheEvictor.MaxBytes = (long)(gb * 1024 * 1024 * 1024);
        }
        CacheMaxGbInput.Text = _settings.LocalCacheMaxGb.ToString("0.#");
    }

    private void CacheIdleDays_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(CacheIdleDaysInput.Text.Trim(), out var days) && days is >= 1 and <= 3650)
        {
            _settings.LocalCacheIdleDays = days;
            _settings.Save();
            CacheEvictor.IdleDays = days;
        }
        CacheIdleDaysInput.Text = _settings.LocalCacheIdleDays.ToString();
    }

    private void CacheEvict_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.LocalCacheEvictEnabled = CacheEvictCheck.IsChecked == true;
        _settings.Save();
        CacheEvictor.Enabled = _settings.LocalCacheEvictEnabled;
    }

    private void BtnCacheClean_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = CacheEvictor.Run(force: true);
            var tail = report.Removed == 0
                ? "没有符合清理条件的本地副本（未上传完成的文件不会被清理）。"
                : $"已释放 {report.Removed} 个本地副本，回收 {RootMigrationService.FormatSize(report.FreedBytes)}。";
            RefreshCacheStatus();
            CacheStatusText.Text = tail + " " + CacheStatusText.Text;
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = "清理失败：" + ex.Message;
        }
    }

    private void BtnOpenFilesDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FileRepository.FilesDir);
            Process.Start(new ProcessStartInfo(FileRepository.FilesDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 重试云端清理（2026-09-16）：把「云端待清理」的记录再删一轮。
    /// 这是用户把网络/授权修好之后**唯一**的出口 —— 否则记录会永远躺在「待清理」不动，
    /// 而"永久卡死且没有出口"比"反复重试"更难排查（同款出口在上传那边叫「测试连接」）。
    /// </summary>
    private async void BtnRetryCloudCleanup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (done, left) = await AttachmentExpiryService.RetryPendingAsync();
            var tail = done == 0 && left == 0
                ? "没有需要清理的云端文件。"
                : left == 0
                    ? $"已清理 {done} 个云端文件。"
                    : $"已清理 {done} 个，仍有 {left} 个未清掉（可稍后再试，或用「复制网盘路径」到网盘手动删除）。";
            RefreshCacheStatus();
            CacheStatusText.Text = tail + " " + CacheStatusText.Text;
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = "重试云端清理失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 复制网盘附件目录路径（2026-09-16）。人工兜底路径必须是"最低成本动作"：
    /// 附件按月分子目录，用户复制路径后进网盘删掉月份文件夹就完事，不必逐文件勾选。
    /// </summary>
    private void BtnCopyCloudPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FileRepository.NetAttachmentsDir;
            // 双程加固写入（OLE 失败 → Win32 固化退避重试，2026-10-01）
            if (!SafeClipboard.TrySetTextHardened(path, System.Windows.Clipboard.SetText, Win32.WriteClipboardText))
            {
                System.Windows.MessageBox.Show(this, "复制失败：剪贴板被其他程序占用，请稍后重试", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            System.Windows.MessageBox.Show(this,
                $"已复制网盘路径：\n{path}\n\n在百度网盘里进入这个目录即可统一清理" +
                "（附件按月分子目录，删掉对应月份文件夹就算清完）。",
                "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "复制失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 附件上传 ──

    private void AttachmentUpload_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.BaiduAttachmentUploadEnabled = AttachmentUploadCheck.IsChecked == true;
        _settings.Save();
    }

    private void AttachmentRetention_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(AttachmentRetentionInput.Text.Trim(), out var days) && days is >= 0 and <= 3650)
        {
            _settings.BaiduAttachmentRetentionDays = days;
            _settings.Save();
            FileRepository.AttachmentRetentionDays = days;
        }
        AttachmentRetentionInput.Text = _settings.BaiduAttachmentRetentionDays.ToString();
    }

    private void ChatFiles_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.FileSyncEnabled = ChatFilesCheck.IsChecked == true;
        _settings.Save();
    }

    // ── 数据目录迁移 ──

    private void BtnOpenDataRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FocusCapturePaths.Root);
            Process.Start(new ProcessStartInfo(FocusCapturePaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnChangeRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择新的数据目录（建议新建一个空的、好找的文件夹）",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        var target = dlg.FolderName;
        var source = FocusCapturePaths.Root;

        var validation = RootMigrationService.ValidateTarget(target, source);
        if (!validation.Ok)
        {
            System.Windows.MessageBox.Show(this, validation.Message, "该位置不可用",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var copyData = validation.State != RootTargetState.NonEmpty;

        if (!copyData)
        {
            if (System.Windows.MessageBox.Show(this,
                    "这个目录里已经有内容。\n\n" +
                    "为避免冲掉现有数据，切换后将**直接使用该目录里的现有内容**，不再复制当前数据。\n" +
                    "如果你想搬一份过去，请选一个空的文件夹。\n\n继续切换？",
                    "目录已有内容", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        else
        {
            var (files, bytes) = RootMigrationService.Measure(source, applyExcludes: true);
            if (System.Windows.MessageBox.Show(this,
                    $"确定把数据目录改到：\n{target}\n\n" +
                    $"现有数据会完整复制过去（{files} 个文件，{RootMigrationService.FormatSize(bytes)}）。\n" +
                    "原目录不会被修改也不会被删除，随时可以改回来。\n" +
                    "复制完成并校验通过后才会生效，改完需要重启程序。\n\n继续？",
                    "更改数据目录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        BtnChangeRoot.IsEnabled = false;
        DataRootStatusText.Text = "正在处理…";
        try
        {
            var result = await Task.Run(() => RootMigrationService.Migrate(
                source, target, copyData,
                msg => Dispatcher.Invoke(() => DataRootStatusText.Text = msg)));

            DataRootStatusText.Text = result.Message + (result.Ok ? "  请重启程序以使用新目录。" : "");
            DataRootText.Text = FocusCapturePaths.Root;

            if (result.Ok)
                System.Windows.MessageBox.Show(this,
                    "数据目录已切换。\n\n请关闭并重新打开 FocusCapture 让新目录生效。\n" +
                    "原目录里的内容仍然保留，确认新位置一切正常后可以自行删除。",
                    "需要重启", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show(this, result.Message, "迁移未完成",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DataRootStatusText.Text = "迁移失败：" + ex.Message;
            System.Windows.MessageBox.Show(this, "迁移失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnChangeRoot.IsEnabled = true;
        }
    }

    /// <summary>界面快照专用（诊断工具用，正常流程不调用）：滚到内容底部再出图。</summary>
    internal void ScrollToEndForSnapshot()
    {
        try { ContentScroller.ScrollToEnd(); } catch { /* 快照辅助失败不影响主流程 */ }
    }

    /// <summary>
    /// 界面快照专用：**同步**把外部依赖行建出来，然后滚到底。
    ///
    /// <para>
    /// 为什么需要它：依赖行是异步探测后插入的（探针要起子进程），而快照的"滚到底"发生在插入之前 ——
    /// 结果是图里只剩一个区块标题、看不见状态行与按钮（实测踩到，2026-09-20）。
    /// 同步阻塞 UI 线程在这里**不会死锁**：<c>ProbeAsync</c> 内部的 await 全部走
    /// <c>ConfigureAwait(false)</c>，续体在线程池上跑，不需要 UI 线程回信。
    /// </para>
    /// </summary>
    internal void PrepareSkillDepsForSnapshot()
    {
        try
        {
            SkillDepsPanel.Children.Clear();
            foreach (var dep in SkillDependencies.All(AppContext.BaseDirectory))
                SkillDepsPanel.Children.Add(BuildDependencyRow(dep, dep.ProbeAsync().GetAwaiter().GetResult()));
            UpdateLayout();
            ContentScroller.ScrollToEnd();
        }
        catch { /* 快照辅助失败不影响主流程 */ }
    }

    // ═══════════════ 激活码板块（2026-10-08 激活码与试用期） ═══════════════

    /// <summary>
    /// 刷新「激活码」板块的状态区。
    /// 每次进入板块时调用 —— 状态一律从 <see cref="LicenseGate"/> 现算，不缓存结论。
    /// </summary>
    private void RefreshLicenseSection()
    {
        try
        {
            var status = LicenseGate.GetStatus();

            if (status.Activated)
            {
                LicenseStatusText.Text = "已激活（永久有效）";
                LicenseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                // ⚠ 口径（2026-10-08 用户纠正）：不提「不绑设备 / 任意电脑」—— 见 LicenseWindow 同处注释。
                LicenseStatusDetail.Text = "感谢支持，全部高级功能已解锁。请妥善保管激活码，重装或更换设备时需要重新输入。";
                LicenseCodeInput.Text = status.Code ?? "";
            }
            else if (status.Expired)
            {
                LicenseStatusText.Text = "免费体验已结束";
                LicenseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x7B, 0x39));
                LicenseStatusDetail.Text = $"试用天数已用满 {status.TrialDays} 天。高级功能（AI 问答、AI 整理、Word 导出、云同步等）已暂停，输入激活码后可继续使用。";
            }
            else
            {
                LicenseStatusText.Text = "免费体验中";
                LicenseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                LicenseStatusDetail.Text = $"已使用 {status.UsedDays} 天，剩余 {status.RemainingDays} 天（共 {status.TrialDays} 天）。到期后输入激活码即可继续使用高级功能。";
            }

            BtnActivateLicense.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LicenseStatusText.Text = "状态读取失败";
            LicenseStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x7B, 0x39));
            LicenseStatusDetail.Text = "读取授权状态时出错，请查看运行日志。";
            AppLog.Warn("License", $"刷新激活码板块失败：{ex.Message}");
        }
    }

    /// <summary>输入框内容变化时清掉上一条提示，避免"改了码还显示旧错误"。</summary>
    private void LicenseCodeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LicenseMessageText != null)
        {
            LicenseMessageText.Text = "";
            LicenseMessageText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        }
    }

    /// <summary>点「激活」：验签通过才写盘（失败不落状态文件），结果就地反馈。</summary>
    private void BtnActivateLicense_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = (LicenseCodeInput.Text ?? "").Trim();
            if (code.Length == 0)
            {
                SetLicenseMessage("请先粘贴激活码。", warning: true);
                return;
            }

            if (LicenseGate.TryActivate(code, out var error))
            {
                SetLicenseMessage("激活成功，高级功能已解锁。", warning: false);
                RefreshLicenseSection();

                // 与激活窗同一套提醒（口径：只说永久有效 + 请妥善保存，不提跨设备）
                System.Windows.MessageBox.Show(
                    this,
                    "激活成功，全部高级功能已解锁。\n\n" +
                    "建议把这串激活码保存到安全的地方（例如微信收藏、备忘录或密码管理器）。\n" +
                    "激活码永久有效，以后重装或更换设备时还要用它。",
                    "激活成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                SetLicenseMessage(error, warning: true);
            }
        }
        catch (Exception ex)
        {
            // 界面事件里的外部操作自己 try 住，绝不把异常抛到 UI 线程
            SetLicenseMessage("激活时发生异常，请查看运行日志。", warning: true);
            AppLog.Warn("License", $"激活失败：{ex.Message}");
        }
    }

    private void SetLicenseMessage(string text, bool warning)
    {
        LicenseMessageText.Text = text;
        LicenseMessageText.Foreground = new SolidColorBrush(
            warning ? Color.FromRgb(0xE0, 0x7B, 0x39) : Color.FromRgb(0x4C, 0xAF, 0x50));
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ═══════════════ 整理规则（2026-09-27 AI 整理规则化） ═══════════════

    /// <summary>正在编辑的自定义规则 Id（null = 编辑器里是"新增"）。空串 = 编辑器关闭。</summary>
    private string? _tidyRuleEditingId;

    /// <summary>重建规则列表行 + 默认规则下拉。增删 / 排序 / 显隐后都走这里，一次全刷。</summary>
    private void LoadTidyRules()
    {
        // 默认规则下拉：列出全部规则（含隐藏的 —— 默认规则与显隐互不干涉）
        _suppressEvents = true;
        CmbTidyDefault.Items.Clear();
        foreach (var r in _settings.TidyRules)
            CmbTidyDefault.Items.Add(r.Name + (r.Visible ? "" : "（已隐藏）"));
        var di = _settings.TidyRules.FindIndex(r => r.Id == _settings.TidyDefaultRuleId);
        CmbTidyDefault.SelectedIndex = di >= 0 ? di : 0;
        TidyShowCustomEntryCheck.IsChecked = _settings.TidyShowCustomEntry;
        _suppressEvents = false;

        TidyRulesHost.Children.Clear();
        var rules = _settings.TidyRules;
        for (int i = 0; i < rules.Count; i++)
        {
            var idx = i;
            var r = rules[i];

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = r.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tag = new TextBlock
            {
                Text = r.IsBuiltin ? "内置" : "自定义",
                Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                FontSize = 10,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            namePanel.Children.Add(name);
            namePanel.Children.Add(tag);
            if (!r.Visible)
                namePanel.Children.Add(new TextBlock
                {
                    Text = "（不显示在预览窗）",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                    FontSize = 10,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            Grid.SetColumn(namePanel, 0);
            // 悬停显示适用场景说明（2026-09-27 phase2）：预置带出代码里的说明；自定义没填就提示去哪补
            var ruleDesc = !string.IsNullOrWhiteSpace(r.Description)
                ? r.Description
                : r.IsBuiltin ? null : "未填写适用场景说明 —— 点「编辑」补一句，预览窗悬停规则按钮时即可见";
            if (ruleDesc != null) namePanel.ToolTip = $"{r.Name}：{ruleDesc}";
            row.Children.Add(namePanel);

            var showCheck = new CheckBox
            {
                IsChecked = r.Visible,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "是否显示在预览窗底部规则条（隐藏不影响它作为默认规则运行）",
            };
            showCheck.Checked += (_, _) => { if (!_suppressEvents) { r.Visible = true; _settings.Save(); LoadTidyRules(); } };
            showCheck.Unchecked += (_, _) => { if (!_suppressEvents) { r.Visible = false; _settings.Save(); LoadTidyRules(); } };
            var showPanel = new StackPanel { Orientation = Orientation.Horizontal };
            showPanel.Children.Add(new TextBlock
            {
                Text = "显示",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            showPanel.Children.Add(showCheck);
            Grid.SetColumn(showPanel, 1);
            row.Children.Add(showPanel);

            var ops = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            Button MakeOp(string content, RoutedEventHandler onClick, string tip)
            {
                var b = new Button
                {
                    Content = content, FontSize = 11, Height = 24, Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), BorderThickness = new Thickness(1),
                    ToolTip = tip,
                };
                b.Click += onClick;
                return b;
            }
            if (idx > 0)
                ops.Children.Add(MakeOp("↑", (_, _) => MoveTidyRule(idx, -1), "上移（预览窗底栏顺序）"));
            if (idx < rules.Count - 1)
                ops.Children.Add(MakeOp("↓", (_, _) => MoveTidyRule(idx, 1), "下移（预览窗底栏顺序）"));
            if (r.IsBuiltin)
            {
                ops.Children.Add(MakeOp("复制", (_, _) => OpenTidyRuleEditor(null, r.Name + "（副本）", TidyRuleCatalog.Find(r.Id)?.Instruction ?? "", TidyRuleCatalog.Find(r.Id)?.Description ?? ""), "复制这条预置规则作为自定义规则的底稿（可改提示词）"));
            }
            else
            {
                ops.Children.Add(MakeOp("编辑", (_, _) => OpenTidyRuleEditor(r.Id, r.Name, r.Prompt, r.Description), "修改这条自定义规则的名称、适用场景说明与提示词"));
                ops.Children.Add(MakeOp("删除", (_, _) => DeleteTidyRule(r.Id), "删除这条自定义规则（预置规则不可删）"));
            }
            Grid.SetColumn(ops, 2);
            row.Children.Add(ops);
            TidyRulesHost.Children.Add(row);
        }

        if (rules.Count == 0)
            TidyRulesHost.Children.Add(new TextBlock
            {
                Text = "（规则列表是空的 —— 保存一次设置后会自动补回出厂预置规则）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4),
            });
    }

    private void MoveTidyRule(int index, int delta)
    {
        var rules = _settings.TidyRules;
        (rules[index], rules[index + delta]) = (rules[index + delta], rules[index]);
        _settings.Save();
        LoadTidyRules();
    }

    /// <summary>打开行内编辑器。id=null 表示新增；baseName/basePrompt/baseDesc 是初稿（复制预置时带出指令正文与适用场景说明）。</summary>
    private void OpenTidyRuleEditor(string? id, string baseName, string basePrompt, string baseDesc = "")
    {
        _tidyRuleEditingId = id;
        _suppressEvents = true;
        TidyRuleNameInput.Text = baseName;
        TidyRulePromptInput.Text = basePrompt;
        TidyRuleDescInput.Text = baseDesc;
        _suppressEvents = false;
        TidyRuleEditorTag.Text = id == null ? "新增自定义规则" : "正在编辑这条自定义规则";
        UpdateTidyRuleEditorHint();
        TidyRuleEditor.Visibility = Visibility.Visible;
        TidyRuleNameInput.Focus();
    }

    private void BtnTidyRuleAdd_Click(object sender, RoutedEventArgs e) => OpenTidyRuleEditor(null, "", "");

    private void BtnTidyRuleCancel_Click(object sender, RoutedEventArgs e)
    {
        _tidyRuleEditingId = null;
        TidyRuleEditor.Visibility = Visibility.Collapsed;
    }

    private void BtnTidyRuleSave_Click(object sender, RoutedEventArgs e)
    {
        var name = TidyRuleNameInput.Text.Trim();
        var prompt = TidyRulePromptInput.Text.Trim();
        var desc = TidyRuleDescInput.Text.Trim();
        if (name.Length == 0) { TidyRuleEditorHint.Text = "规则名称不能为空。"; return; }
        if (prompt.Length == 0) { TidyRuleEditorHint.Text = "提示词不能为空 —— 这段话就是模型收到的全部指令。"; return; }
        if (_settings.TidyRules.Any(r => !r.IsBuiltin && r.Id != _tidyRuleEditingId && r.Name == name))
        { TidyRuleEditorHint.Text = "已有同名的自定义规则。"; return; }

        if (_tidyRuleEditingId is { } editing && _settings.TidyRules.FirstOrDefault(r => r.Id == editing) is { } exist)
        {
            exist.Name = name;
            exist.Prompt = prompt;
            exist.Description = desc;
        }
        else
        {
            _settings.TidyRules.Add(new TidyRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Prompt = prompt,
                Description = desc,
                IsBuiltin = false,
                Visible = true,
            });
        }
        _settings.Save();
        _tidyRuleEditingId = null;
        TidyRuleEditor.Visibility = Visibility.Collapsed;
        LoadTidyRules();
    }

    private void DeleteTidyRule(string id)
    {
        var rule = _settings.TidyRules.FirstOrDefault(r => r.Id == id);
        if (rule == null) return;
        var confirm = System.Windows.MessageBox.Show(this, $"确认删除自定义规则「{rule.Name}」？", "删除确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        _settings.TidyRules.Remove(rule);
        if (_settings.TidyDefaultRuleId == id) _settings.TidyDefaultRuleId = TidyRuleCatalog.TidyId;   // 默认规则被删 → 回落逻辑梳理
        _settings.Save();
        LoadTidyRules();
    }

    private void CmbTidyDefault_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var i = CmbTidyDefault.SelectedIndex;
        if (i < 0 || i >= _settings.TidyRules.Count) return;
        _settings.TidyDefaultRuleId = _settings.TidyRules[i].Id;
        _settings.Save();
    }

    private void TidyShowCustomEntry_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.TidyShowCustomEntry = TidyShowCustomEntryCheck.IsChecked == true;
        _settings.Save();
    }

    private void TidyRulePrompt_TextChanged(object sender, TextChangedEventArgs e) => UpdateTidyRuleEditorHint();

    private void UpdateTidyRuleEditorHint()
    {
        if (TidyRuleEditorHint == null) return;
        TidyRuleEditorHint.Text = $"{TidyRulePromptInput.Text.Length} 字。写清三点最有效：要做什么 / 输出什么格式 / 不要什么。";
    }
}
