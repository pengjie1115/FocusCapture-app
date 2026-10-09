using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Destinations;
using FocusCapture.Services.Files;
using FocusCapture.Services.Sync;
using FocusCapture.Windows;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace FocusCapture;

public partial class MainWindow : Window
{
    private readonly Models.AppSettings _settings;
    private HotkeyService? _hotkeyService;
    private NoteService? _noteService;
    private IChatProvider? _aiProvider;              // v3.5：共享 AI provider（面板编辑时间识别 LLM 兜底 + AI 对话框同源配置），设置变更后重建
    private SyncEngine? _syncEngine;            // 云端同步引擎（可插拔 Provider，配置完整才创建）
    private ChatSyncEngine? _chatSyncEngine;    // 2026-09：AI 会话同步引擎（搭 _syncEngine 周期，共享闸；随 CreateSyncEngine 一并创建/重建）
    private FileStoreSync? _fileStoreSync;      // 2026-09-16：网盘文件清单同步（同周期搭车；只同步几 KB 元数据，文件本体按需取回）
    private FloatBall? _floatBall;
    private InputWindow? _inputWindow;
    private QuickViewWindow? _quickViewWindow;
    private VoiceInputWindow? _voiceWindow;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIconHandle;   // 托盘图标的 GDI 句柄（它自己拥有，重建/退出时必须 Dispose）
    private ClipboardHookService? _clipboardHook;
    private ReminderService? _reminderService;                    // v3.5 Phase3：提醒定时器/弹窗调度/角标
    private ReminderPopupWindow? _reminderPopup;                  // v3.5 Phase3：单条/多条到点弹窗
    private DailySummaryWindow? _dailySummary;                    // v3.5 Phase3：每日汇总弹窗
    private TodoSummaryWindow? _todoSummaryWindow;                // v3.8：待办汇总面板（热键可唤出/收起，故需持实例）
    private IntPtr _hwnd; // 保存窗口句柄供剪贴板监听和热键切换使用
    private SettingsWindow? _settingsWindow;   // v3.10：设置窗口单例 —— 设置改非模态后没人拦重入了，必须自己管实例（见 OpenSettings）

    public MainWindow()
    {
        InitializeComponent();
        _settings = Models.AppSettings.Load();

        // 应用图标（2026-09-19）：注册全局窗口钩子 + 载入自定义图标。
        // 位置很要紧 —— 钩子必须赶在**任何窗口 Show 之前**注册，否则先显示的那个窗口
        // （主窗口自己、悬浮球）会漏掉，任务栏上继续显示 exe 编译期图标。
        // 钩子一处注册覆盖全部窗口（含以后新增的），这也是不用逐个窗口去设的理由。
        AppIconService.HookWindowIcon();
        AppIconService.Reload(_settings.CustomIconPath);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // 硬规则：系统关机/注销时无法弹窗拦截 → 静默尽力一传（带超时），失败由下次启动对账补传
        SystemEvents.SessionEnding += OnSessionEnding;

        // 关键：创建悬浮球放到最前面，托盘失败不影响核心 UI
        try
        {
            CreateFloatBall();
        }
        catch (Exception ex)
        {
            LogStartupError("CreateFloatBall", ex);
        }

        try
        {
            _noteService = new NoteService(_settings);
            AIDialogHelper.Initialize(_noteService, _settings, this);

            // 文件仓库装配（2026-09-16）：接云仓库 + 起后台队列 + 跑一轮保养。
            // 全部放后台线程，失败静默 —— 网盘不可用绝不能拖慢或影响启动。
            try { InitializeFileStore(); }
            catch (Exception ex) { AppLog.Error("Files", "文件仓库装配失败", ex); }
            _hotkeyService = new HotkeyService(_hwnd, _settings);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
            _hotkeyService.RegisterAll();

            // 云端同步引擎（本机变更 → 合并窗口推送；自动同步开 → 启动自动解锁 + 30min 轮询）
            _noteService.NotesChanged += OnNotesChanged;
            _syncEngine = CreateSyncEngine();
            if (_syncEngine != null && _syncEngine.TryUnlockWithStoredToken())
            {
                if (_settings.Sync.AutoSyncEnabled)
                    _syncEngine.StartAutoSync();                      // 内部含首次同步
                else
                    _ = Task.Run(() => _syncEngine.SyncNowAsync());   // 硬规则：启动无条件后台首拉（先拉后推），不阻塞主窗口
            }

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            _inputWindow = new InputWindow(_noteService, _settings);
            _inputWindow.NoteSaved += () => _floatBall?.FlashGreen();
            // v3.5：共享 AI provider（与 AI 对话框同源配置；面板编辑待办时间识别 LLM 兜底用，设置变更后由 OpenSettings 回调重建）
            // 2026-09-23 多供应商改造：改走统一解析入口（配置 → provider 的唯一映射点）
            _aiProvider = AiModelResolver.CreateProvider(_settings);
            _quickViewWindow = new QuickViewWindow(_noteService, _settings, () => _syncEngine, _aiProvider);
            // v3.9：灵感速览标题栏的扩展功能（待办汇总/回收站/设置/导入/每日总结）统一由主程序开窗 ——
            // 这些窗口的依赖（热键服务、悬浮球锚点、导入预览构造）只有这里持有，
            // 且待办汇总在本类已有单例管理逻辑，面板若就地实现会造成两套入口行为漂移。
            _quickViewWindow.ExternalActionRequested = OnQuickViewExternalAction;
            _voiceWindow = new VoiceInputWindow(_settings);

            // v3.5（Phase 3）：待办提醒服务装配 ——
            // 到点 → ReminderPopupWindow；每日汇总 → DailySummaryWindow；角标 → _floatBall.SetBadge；
            // 角标点击 → TodoSummaryWindow（v3 起每次点击新建实例，见 BadgeClicked）。
            // 启动后立即 Refresh() 一次（角标初始化，验收 9）；NotesChanged → Refresh 保持角标/下次检查同步。
            _reminderPopup = new ReminderPopupWindow(_noteService, _settings);
            _dailySummary = new DailySummaryWindow(_noteService, _settings);
            _reminderService = new ReminderService(_noteService, _settings,
                showDuePopups: items =>
                {
                    // v3.6：悬浮球处于收起态（靠边隐藏）时提醒触发 → 先自动展开，弹窗锚定展开后的主区域；
                    // 未收起（用户在用）则保持现状逻辑不变
                    if (_floatBall?.IsCollapsed == true) _floatBall.ExpandBall();
                    var (lx, ty) = GetBallAnchor();
                    _reminderPopup.ShowPopups(items, lx, ty);
                },
                showDailySummary: () =>
                {
                    var (lx, ty) = GetBallAnchor();
                    _dailySummary.ShowSummary(_noteService.LoadAllEntries(), lx, ty);
                },
                updateBadge: (count, hasRead) => Dispatcher.Invoke(() => _floatBall?.SetBadge(count, hasRead)));
            if (_floatBall != null)
                _floatBall.BadgeClicked += () => Dispatcher.Invoke(() =>
                {
                    // 角标点击语义 = 唤出（不改）：已打开则刷新数据并前置，未打开则新建。
                    // v3.8：实例生命周期改由 ShowTodoSummary 统一管理（关闭事件置 null），
                    // 根上仍是「已关闭的窗口绝不再 Show」；热键入口复用同一方法，杜绝两份逻辑漂移。
                    ShowTodoSummary();
                });
            _noteService.NotesChanged += () => _reminderService?.Refresh();
            // 2026-09-09：云同步拉取落地 → 刷新灵感速览（若开着）+ 待办角标。独立事件，不走 NotesChanged（防反向推回云端）
            _noteService.CloudDataLanded += () => Dispatcher.Invoke(() =>
            {
                try { _quickViewWindow?.Refresh(); _reminderService?.Refresh(); }
                catch { /* 刷新失败不影响同步 */ }
            });
            _reminderService.Start();

            // 剪贴板自动捕获
            _clipboardHook = new ClipboardHookService(_noteService, () =>
                Dispatcher.Invoke(() => _floatBall?.FlashGreen()));
            if (_settings.ClipboardCaptureEnabled)
            {
                _clipboardHook.Install(_hwnd);
                _floatBall?.SetCaptureActive(true);
            }
        }
        catch (Exception ex)
        {
            LogStartupError("InitServices", ex);
        }

        // 托盘最后创建，失败也不影响悬浮球和热键
        try
        {
            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            LogStartupError("CreateTrayIcon", ex);
        }
    }

    private static void LogStartupError(string stage, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusCapture");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}\n{ex}\n\n");
        }
        catch { /* 日志写不进也别崩 */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY) { _hotkeyService?.HandleHotkey(wParam.ToInt32()); handled = true; }
        else if (msg == ClipboardHookService.WM_CLIPBOARDUPDATE)
        {
            _clipboardHook?.OnClipboardUpdate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHotkeyPressed(int id) => Dispatcher.Invoke(() =>
    {
        switch (id)
        {
            case HotkeyService.ID_SUMMON: _inputWindow?.Show(); break;
            case HotkeyService.ID_CLIPBOARD_TOGGLE: ToggleClipboardCapture(); break;
            case HotkeyService.ID_QUICK_VIEW: ShowQuickView(); break;
            case HotkeyService.ID_VOICE_INPUT: ShowVoiceInput(); break;
            case HotkeyService.ID_TODO_SWITCH: _inputWindow?.ToggleType(); break;   // v3.5：全局切换笔记/待办类型
            case HotkeyService.ID_SETTINGS: OpenSettings(); break;                  // v3.7：唤出设置面板
            case HotkeyService.ID_AI_ASK: AIDialogHelper.Open(ExplainMode.Ask); break;   // v3.8：唤起 AI 问答（复用单例窗口，每次开新会话）
            case HotkeyService.ID_TODO_SUMMARY: ToggleTodoSummary(); break;              // v3.8：待办汇总面板（唤出/收起）
        }
    });

    /// <summary>v3.8：待办汇总面板——显示（已打开则刷新数据并前置）。角标点击与热键唤出共用。
    /// 生命周期：单例 + Closed 置 null，已关闭的窗口绝不再 Show（复用前提是窗口无状态，每次 RefreshAll 全量重载）。</summary>
    private void ShowTodoSummary()
    {
        if (_noteService == null) return;
        try
        {
            if (_todoSummaryWindow == null)
            {
                _todoSummaryWindow = new TodoSummaryWindow(_noteService, _settings, _aiProvider);
                _todoSummaryWindow.Closed += (_, _) => _todoSummaryWindow = null;
                // v3.10：「跳转到灵感速览」——面板只发请求，开窗+定位收在这里（与热键/托盘同一批方法）
                _todoSummaryWindow.JumpToQuickViewRequested += entry => Dispatcher.Invoke(() => JumpToQuickView(entry));
            }
            _todoSummaryWindow.RefreshAll(_noteService.LoadAllEntries());
            _todoSummaryWindow.Show();
            if (_todoSummaryWindow.WindowState == WindowState.Minimized)
                _todoSummaryWindow.WindowState = WindowState.Normal;
            _todoSummaryWindow.Activate();
        }
        catch (Exception ex)
        {
            _todoSummaryWindow = null;   // 异常实例不复用，下次唤出重建
            LogStartupError("OpenTodoSummary", ex);
        }
    }

    /// <summary>v3.8：热键入口——按一次唤出，再按一次收起（与灵感速览/输入框一致的语义）。</summary>
    private void ToggleTodoSummary()
    {
        if (_todoSummaryWindow?.IsVisible == true) { _todoSummaryWindow.Hide(); return; }
        ShowTodoSummary();
    }

    private void ToggleClipboardCapture()
    {
        _settings.ClipboardCaptureEnabled = !_settings.ClipboardCaptureEnabled;
        _settings.Save();

        if (_settings.ClipboardCaptureEnabled)
        {
            _clipboardHook?.Install(_hwnd);
            _floatBall?.SetCaptureActive(true);
        }
        else
        {
            _clipboardHook?.Uninstall();
            _floatBall?.SetCaptureActive(false);
        }
    }

    private void ShowQuickView()
    {
        if (_quickViewWindow?.IsVisible == true) _quickViewWindow.Hide(); else _quickViewWindow?.Show();
    }

    /// <summary>v3.5（Phase 3）：悬浮球的中心坐标，供提醒/汇总弹窗定位在球上方。</summary>
    private (double left, double top) GetBallAnchor()
    {
        if (_floatBall != null && _floatBall.IsVisible)
            return (_floatBall.Left + _floatBall.Width / 2, _floatBall.Top + _floatBall.Height / 2);
        var wa = SystemParameters.WorkArea;
        return (wa.Right - 80, wa.Bottom - 200); // 兜底：默认球位置
    }

    private void ShowVoiceInput()
    {
        // 语音输入（沉浸记录）属**免费档**（2026-10-08 用户拍板），此处刻意不设门卫。
        // 历史上曾误登记为高级档，铺开拦截点那步已清除。
        if (_voiceWindow?.IsVisible == true) _voiceWindow.Hide(); else _voiceWindow?.Show();
    }

    private void CreateFloatBall()
    {
        _floatBall = new FloatBall();
        _floatBall.AiAssistantName = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName;
        _floatBall.SetOpacity(_settings.FloatBallOpacity);
        _floatBall.ApplyPosition(_settings.BallLeft, _settings.BallTop);
        _floatBall.InputRequested += () => Dispatcher.Invoke(() => _inputWindow?.Show());
        _floatBall.QuickViewRequested += () => Dispatcher.Invoke(ShowQuickView);
        _floatBall.SettingsRequested += () => Dispatcher.Invoke(OpenSettings);
        _floatBall.VoiceInputRequested += () => Dispatcher.Invoke(ShowVoiceInput);
        _floatBall.AiAskRequested += () => Dispatcher.Invoke(() => AIDialogHelper.Open(ExplainMode.Ask));
        _floatBall.ExitRequested += () => Dispatcher.Invoke(ExitApp);

        // 拖放保存（2026-09-16）：总开关决定 AllowDrop，关闭时球收不到任何拖放事件
        _floatBall.SetDragToSaveEnabled(_settings.DragToSaveEnabled);
        _floatBall.DropReceived += payload => Dispatcher.Invoke(() => HandleBallDrop(payload));

        _floatBall.Show();
    }

    // ══════════════════ 悬浮球拖放保存（2026-09-16）══════════════════
    //
    // 三条最容易在维护中被破坏的纪律，先写在这里：
    //
    //  ① **拖入不做任何落地**：文件拖进来"什么都不做"是正确行为，没有暂存区、不复制、不入仓。
    //     「不点任何选项 → 零副作用」是这块设计的底线。
    //  ② **不可撤回的动作只能由显式点击触发**：绝不用"悬停 N 秒"这类时间阈值触发上传。
    //  ③ **浮层必须是独立窗口**：球窗口的透明区穿透，画在球里的按钮既收不到拖放也点不到。
    //
    // 外发链路不新增机制：「得到大脑」直接调适配器（用户已拍板），与 B-8 按钮直传同一条。
    // 以后新增渠道 = 卡片选项加一行 + 适配器注册一行，**卡片不硬编码渠道名**。

    private DropActionStrip? _dropStrip;
    private DropActionCard? _dropCard;

    private void HandleBallDrop(DragPayload payload)
    {
        // 双保险：开关关闭时球根本收不到拖放，这里再挡一次（设置项刚关、事件还在飞的情况）
        if (!_settings.DragToSaveEnabled) return;

        switch (payload.Kind)
        {
            case DragPayloadKind.Text: HandleTextDrop(payload); break;
            case DragPayloadKind.Files: HandleFileDrop(payload); break;
        }
    }

    /// <summary>文字拖入：立即存本地笔记 + 球闪绿（球在 FloatBall 里已闪）+ 浮出竖向小条。</summary>
    private void HandleTextDrop(DragPayload payload)
    {
        var text = (payload.Text ?? "").Trim();
        if (text.Length == 0) return;

        // ① 立即存为本地笔记。这是本地动作，误存只是脏数据（能删），所以可以自动发生；
        //    「上传」那类收不回的动作一律留给用户点（见上面纪律 ②）。
        try
        {
            _noteService?.SaveNote(text, payload.SourceApp);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Drag", "拖入文字存笔记失败：" + ex.Message);
        }

        // ② 小条的两个出口
        CloseDropOverlays();
        var strip = new DropActionStrip(
            _settings.DropActionStripSeconds, HasGetNoteCredential(), _settings.DropActionOpacity);
        strip.AiAskRequested += () => AIDialogHelper.Open(ExplainMode.Ask, null, text);
        strip.GetNoteRequested += () => PushTextToGetNote(text);
        _dropStrip = strip;
        strip.Closed += (_, _) => { if (ReferenceEquals(_dropStrip, strip)) _dropStrip = null; };
        ShowOverlayNearBall(strip);
    }

    /// <summary>
    /// 文件拖入：**什么都不做**（不复制、不入仓、不上传），只弹紧凑卡片。
    /// 看到这里想"顺手复制一份保命"的话，先看清禁区清单（REGRESSION B-15 段的「改这块前必读」）—— "新增暂存区"那条已经被否掉了。
    /// </summary>
    private void HandleFileDrop(DragPayload payload)
    {
        var paths = payload.Paths.ToList();
        if (paths.Count == 0) return;

        var (title, subtitle) = DragDropSaveService.BuildCardHeader(paths, payload.SourceApp);
        var showGetNote = DragDropSaveService.HasAnyTextFile(paths) || DragDropSaveService.HasAnyImageFile(paths);

        CloseDropOverlays();
        var card = new DropActionCard(title, subtitle, showGetNote,
            HasGetNoteCredential(), _settings.DropActionOpacity);
        card.AiAskRequested += () => OpenAiWithFiles(paths);
        card.SaveToCloudRequested += () => SaveFilesToCloud(paths);
        card.GetNoteRequested += () => PushFilesToGetNote(paths);
        _dropCard = card;
        card.Closed += (_, _) => { if (ReferenceEquals(_dropCard, card)) _dropCard = null; };
        ShowOverlayNearBall(card);
    }

    /// <summary>贴着球浮出（球在屏幕左半 → 弹右侧；右半 → 弹左侧）。几何一律取球的**实时**尺寸：
    /// 吸附态是 8×36、展开后是 56×56（2026-09-19 由 48×48 改），写死会算错位置。</summary>
    private void ShowOverlayNearBall(Window overlay)
    {
        if (_floatBall == null || !_floatBall.IsVisible)
        {
            try { overlay.Close(); } catch { /* 球都不在了，浮层没意义 */ }
            return;
        }

        var (left, top) = _floatBall.GetPosition();
        var w = _floatBall.Width;
        var h = _floatBall.Height;

        switch (overlay)
        {
            case DropActionStrip s: s.ShowNear(left, top, w, h); break;
            case DropActionCard c:  c.ShowNear(left, top, w, h); break;
        }
    }

    /// <summary>同一时刻只留一个浮层（连拖两次不叠一堆）。</summary>
    private void CloseDropOverlays()
    {
        try { _dropStrip?.Close(); } catch { /* 已关闭 */ }
        try { _dropCard?.Close(); } catch { /* 已关闭 */ }
        _dropStrip = null;
        _dropCard = null;
    }

    /// <summary>得到大脑凭证是否已配置（未配置时卡片/小条上那一项置灰）。</summary>
    private bool HasGetNoteCredential() =>
        !string.IsNullOrWhiteSpace(_settings.GetNoteApiKey)
        && !string.IsNullOrWhiteSpace(_settings.GetNoteClientId);

    /// <summary>卡片「用 AI 问答打开」：把文件带进对话（Open 扩展了附件参数）。</summary>
    private void OpenAiWithFiles(List<string> paths)
    {
        var usable = DragDropSaveService.ExistingFiles(paths);
        var missing = paths.Where(p => !usable.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

        if (usable.Count == 0)
        {
            WarnMissingFiles(missing);
            return;
        }

        AIDialogHelper.Open(ExplainMode.Ask, null, null, usable);

        // 部分文件已不在：对话照样打开（能用的先用上），但要如实说少了什么
        if (missing.Count > 0) WarnMissingFiles(missing);
    }

    /// <summary>卡片「存到网盘」：现有链路原样 —— RegisterLocalFile 入仓 + UploadQueue.Kick。</summary>
    private void SaveFilesToCloud(List<string> paths)
    {
        var usable = DragDropSaveService.ExistingFiles(paths);
        var missing = paths.Where(p => !usable.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

        if (usable.Count == 0)
        {
            WarnMissingFiles(missing);
            return;
        }

        var ok = 0;
        var failures = new List<string>();

        foreach (var path in usable)
        {
            var display = DragDropSaveService.BuildDisplayName(path);
            try
            {
                var (meta, error) = FileRepository.RegisterLocalFile(
                    path, FileTypes.Upload, display, false, null);
                if (meta == null) failures.Add($"{display}：{error}");
                else ok++;
            }
            catch (Exception ex)
            {
                failures.Add($"{display}：{ex.Message}");
            }
        }

        if (ok > 0) UploadQueue.Kick();

        // ⚠ 措辞纪律（2026-09-16 事故教训）：登记成功 **不等于** 已经到网盘。
        //   内部状态显示什么不算数，用户能在网盘上看见才算。所以这里只说"已加入上传队列"。
        var msg = new StringBuilder();
        if (ok > 0)
            msg.Append($"{ok} 个文件已登记到本机文件区，并加入上传队列。\n" +
                       "稍后可在「设置 → 文件与网盘」看到，上传完成后百度网盘上才有。");
        if (failures.Count > 0)
            msg.Append((msg.Length > 0 ? "\n\n" : "") + "这些没成功：\n" + string.Join("\n", failures.Take(6)));
        if (missing.Count > 0)
            msg.Append((msg.Length > 0 ? "\n\n" : "") + DragDropSaveService.BuildMissingFileMessage(missing));

        if (msg.Length > 0) ShowDropResult(msg.ToString(), failures.Count > 0 || missing.Count > 0);
    }

    /// <summary>小条「得到大脑」：文字直传（**绕过 AI 直接调适配器**，用户已拍板）。</summary>
    private void PushTextToGetNote(string text)
    {
        // 2026-10-08 门卫（得到大脑归「云同步」档）：拦在**入口**而非收口方法上 ——
        // 若拦在 ExecuteGetNoteSaveAsync，混合拖放（图片 + 文本）会连着弹两次激活窗
        // （图片收口一次、文本收口一次），用户取消后又被问一遍。一个入口只该问一次。
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureSync, "得到大脑", this)) return;

        var title = DragDropSaveService.MakeGetNoteTitle(text);
        if (string.IsNullOrWhiteSpace(title)) title = "拖入的内容";
        _ = ExecuteGetNoteSaveAsync(title, text);
    }

    /// <summary>卡片「发到得到大脑」：文本类文件读出来直传（该行只对文本类显示）。</summary>
    private void PushFilesToGetNote(List<string> paths)
    {
        // 门卫同 PushTextToGetNote：拦在入口，一次拖放无论含几张图 / 几个文本只问一次。
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureSync, "得到大脑", this)) return;

        var existing = DragDropSaveService.ExistingFiles(paths);
        var images = existing.Where(DragDropSaveService.IsImageFile).ToList();
        var texts = existing.Where(DragDropSaveService.IsTextFile).ToList();

        if (images.Count == 0 && texts.Count == 0)
        {
            ShowDropResult("这些文件里没有可直传的内容（图片 .jpg/.png/.gif/.webp，或文本类 .md/.txt/.csv/.json/代码）。", true);
            return;
        }

        // 图片（2026-10-01 用户拍板）：每张一篇图片笔记，标题用显示名去扩展名，逐张直传
        foreach (var image in images)
        {
            var displayName = DragDropSaveService.BuildDisplayName(image);
            _ = ExecuteGetNoteImageSaveAsync(image, Path.GetFileNameWithoutExtension(displayName));
        }

        // 文本类：维持原路径（读出合并成一篇）
        if (texts.Count == 0) return;

        var usable = texts;
        var sb = new StringBuilder();
        var errors = new List<string>();

        foreach (var path in usable)
        {
            var (content, error) = DragDropSaveService.ReadTextFileStrict(path);
            if (error != null)
            {
                errors.Add($"{Path.GetFileName(path)}：{error}");
                continue;
            }
            if (usable.Count > 1) sb.Append("## ").Append(Path.GetFileName(path)).Append('\n');
            sb.Append(content).Append('\n');
        }

        if (sb.Length == 0)
        {
            ShowDropResult("没有读到可用内容：\n" + string.Join("\n", errors), true);
            return;
        }

        // 标题策略：单文件用文件名（去掉扩展名）—— 比"首行前 30 字"更符合"我把这个文件发上去"的预期
        var title = usable.Count == 1
            ? Path.GetFileNameWithoutExtension(usable[0])
            : $"{usable.Count} 个文本文件";

        if (errors.Count > 0)
            ShowDropResult("部分文件没读成功，已先传读到的内容：\n" + string.Join("\n", errors), true);

        _ = ExecuteGetNoteSaveAsync(title, sb.ToString());
    }

    /// <summary>调得到大脑适配器保存一篇笔记。这是**绕过 AI** 的直传路径（架构纪律）。</summary>
    private async Task ExecuteGetNoteSaveAsync(string title, string content)
    {
        // 门卫不在这里：拦在入口（PushTextToGetNote / PushFilesToGetNote），否则混合拖放会重复弹窗。
        try
        {
            var json = BuildGetNoteSaveArgs(title, content);
            var destination = new GetNoteDestination(_settings);
            var result = await destination.ExecuteAsync("getnote_save_note", json, System.Threading.CancellationToken.None);
            ShowDropResult(result.Message, !result.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Drag", "拖放直传得到大脑异常：" + ex.Message);
            ShowDropResult("上传得到大脑时出错：" + ex.Message, true);
        }
    }

    /// <summary>
    /// 拖入的图片直传得到大脑（2026-10-01 用户拍板）：走 getnote_save_image
    /// （取上传令牌 → 图片直传 OSS → 存 img_text 图片笔记），**绕过 AI** 的直传路径（架构纪律同上）。
    /// AskAllowDir 不注入：路径来自用户亲手拖入，不经过模型，无需 FsGuard。
    /// </summary>
    private async Task ExecuteGetNoteImageSaveAsync(string imagePath, string title)
    {
        // 门卫不在这里：同 ExecuteGetNoteSaveAsync，拦在入口（PushFilesToGetNote）。
        try
        {
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["image_path"] = imagePath,
                ["title"] = title,
            };
            // 默认知识库：与文本笔记同一口径（设置面板里选的那个）
            if (!string.IsNullOrWhiteSpace(_settings.GetNoteDefaultTopicId))
                body["topic_id"] = _settings.GetNoteDefaultTopicId;

            var destination = new GetNoteDestination(_settings);
            var result = await destination.ExecuteAsync("getnote_save_image", body.ToJsonString(),
                System.Threading.CancellationToken.None);
            ShowDropResult(result.Message, !result.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Drag", "图片直传得到大脑异常：" + ex.Message);
            ShowDropResult("上传图片到得到大脑时出错：" + ex.Message, true);
        }
    }

    private string BuildGetNoteSaveArgs(string title, string content)
    {
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = title,
            ["content"] = content,
        };
        // 默认知识库：与设置面板里选的一致；没选就让服务端用账号默认库
        if (!string.IsNullOrWhiteSpace(_settings.GetNoteDefaultTopicId))
            body["topic_id"] = _settings.GetNoteDefaultTopicId;
        return body.ToJsonString();
    }

    /// <summary>文件已不在的提示（微信的 temp 图片会被清掉，必须说清楚而不是静默失败）。</summary>
    private void WarnMissingFiles(List<string> missing)
    {
        if (missing.Count == 0) return;
        ShowDropResult(DragDropSaveService.BuildMissingFileMessage(missing), true);
    }

    private void ShowDropResult(string message, bool isWarning)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        System.Windows.MessageBox.Show(this, message, isWarning ? "拖放保存" : "拖放保存",
            MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }


    /// <summary>
    /// 「AI 整理预览窗 → 自定义」入口（2026-09-27）：打开设置并定位到「整理规则」板块。
    /// 设置窗本就是非模态单例（OpenSettings），预览窗保持打开，两边可同时操作。
    /// </summary>
    public void OpenSettingsToTidyRules()
    {
        OpenSettings();
        if (_settingsWindow != null)
        {
            EnsureWindowVisible(_settingsWindow);
            _settingsWindow.SelectSection("整理规则");
        }
    }

    private void OpenSettings()
    {
        // 已存在 → 直接前置（非模态后必须自己挡重入：连按热键会开出好几个设置窗口，用户改哪个都不知道）
        if (_settingsWindow != null) { EnsureWindowVisible(_settingsWindow); return; }
        try
        {
            var sw = new SettingsWindow(_settings, _hotkeyService, () =>
            {
                _hotkeyService?.RegisterAll();
                // v3.5：AI 配置可能变更 → 重建共享 provider 并同步给面板（编辑待办时间识别 LLM 兜底用当前配置）
                _aiProvider = AiModelResolver.CreateProvider(_settings);
                _quickViewWindow?.UpdateAiProvider(_aiProvider);
                _todoSummaryWindow?.UpdateAiProvider(_aiProvider);   // v3.10：待办汇总的时间识别也吃同一个 provider
                _inputWindow?.SetOpacity(_settings.InputOpacity);
                _inputWindow?.SetCornerRadius(_settings.InputBorderRadius);
                _floatBall?.SetOpacity(_settings.FloatBallOpacity);
                if (_quickViewWindow != null) _quickViewWindow.Opacity = _settings.QuickViewOpacity;
                _quickViewWindow?.ApplySettings();   // v3.9：宽度/置顶/标题栏按钮即时生效
                // 应用图标（2026-09-19）：换/清自定义图标 → 任务栏窗口图标与托盘图标同时换。
                // 顺序要紧：先换图标源（Reload）→ 再重建托盘（吃新源，在下面那行里）→ 最后刷新已打开窗口。
                // 三步都跑完两处才同源；全在同一个事件里完成，所以不必重启应用。
                AppIconService.Reload(_settings.CustomIconPath);
                ApplyAssistantNameToAllEntries();
                // AI 问答界面（2026-09-26）：昵称 / 自定义欢迎语 / 用户头像改完即时生效 ——
                // 否则用户在设置里改完欢迎语，起手页那句话要等重开窗口才变（看着像没生效）。
                AIDialogHelper.NotifyChatUiSettingsChanged();
                AppIconService.RefreshOpenWindows();

                // 拖放保存（2026-09-16）：开关关掉要立刻收回球上的 AllowDrop，并收掉已经浮着的浮层；
                // 透明度要**实时**作用到已打开的小条/卡片（验收清单里就有这一条）。
                _floatBall?.SetDragToSaveEnabled(_settings.DragToSaveEnabled);
                if (!_settings.DragToSaveEnabled) CloseDropOverlays();
                _dropStrip?.SetOpacity(_settings.DropActionOpacity);
                _dropCard?.SetOpacity(_settings.DropActionOpacity);
            }, _noteService, () => _syncEngine, RebuildSyncEngine, () => _chatSyncEngine);
            sw.Owner = this;
            sw.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow = sw;

            var beforeState = $"{sw.WindowState}/{sw.IsVisible}";
            EnsureWindowVisible(sw);
            // 取证（2026-09-22）：用户实测过「按热键后设置只在任务栏出现、还得手点任务栏图标」，
            // 根因尚未 100% 坐实（候选：宿主主窗口被最小化 → owned 窗口跟随；或抢前台失败）。
            // 把唤起前后的两窗状态落进日志，下次复现直接看日志定因，不用再猜。
            AppLog.Info("Settings", $"唤出设置｜宿主(state={WindowState},visible={IsVisible})" +
                $"｜设置窗(前={beforeState} 后={sw.WindowState}/{sw.IsVisible})");
        }
        catch (Exception ex)
        {
            _settingsWindow = null;   // 异常实例不复用，下次唤出重建
            LogStartupError("OpenSettings", ex);
        }
    }

    /// <summary>
    /// 统一的「把窗口唤到用户面前」流程（2026-09-22）：最小化归位 → Show → Activate → Focus，
    /// 必要时借 Topmost 闪一次强制置顶。
    ///
    /// 起因：6 条唤出路径里**只有设置窗口**没有归位/前置这一环，用户实测出现过「按了热键
    /// 只在任务栏出现、要手动点任务栏图标」。修复取的是**覆盖两种候选根因**的防御性做法
    /// （① 宿主主窗口被最小化时 owned 窗口跟着隐藏/最小化；② 前台窗口锁定导致没抢到前台）。
    ///
    /// ⚠ 灵感速览 / 待办汇总在最小化态按热键会走 Hide 分支（IsVisible 仍为 true）—— 那是**已知并
    /// 有意保留**的现状（用户 2026-09-22 拍板不动），别顺手"统一"过来。
    /// 新加的面板一律走这个方法。
    /// </summary>
    private static void EnsureWindowVisible(Window w)
    {
        var wasOffScreen = w.WindowState == WindowState.Minimized || !w.IsVisible;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (!w.IsVisible) w.Show();
        w.Activate();
        w.Focus();

        // 抢前台兜底：只在「本来就没在屏幕上」时才用 Topmost 闪一下 —— 这种情况单靠 Activate()
        // 经常不生效；闪完立刻恢复原值，不会篡改用户自己的置顶设置。
        if (wasOffScreen)
        {
            var wasTopmost = w.Topmost;
            w.Topmost = true;
            w.Topmost = wasTopmost;
        }
    }

    /// <summary>待办汇总面板的「跳转到灵感速览」：确保面板可见 → 按该条目定位。
    /// 开窗统一走 ShowQuickView 这条既有入口，不另开一条（免得两套开窗行为各自漂移）。</summary>
    private void JumpToQuickView(Models.NoteEntry entry)
    {
        if (_quickViewWindow == null) return;
        try
        {
            if (!_quickViewWindow.IsVisible) _quickViewWindow.Show();
            EnsureWindowVisible(_quickViewWindow);
            // 等布局跑完再定位：刚 Show 出来的窗口此刻还没完成布局，直接 ScrollIntoView 滚不动。
            var win = _quickViewWindow;
            win.Dispatcher.BeginInvoke(new Action(() => win.ShowAtEntry(entry)), DispatcherPriority.Loaded);
        }
        catch (Exception ex) { LogStartupError("JumpToQuickView", ex); }
    }

    // ── v3.9：灵感速览标题栏扩展功能的统一出口 ──
    // 面板只发 id，开窗逻辑全部收在这里：与托盘菜单、全局热键复用同一批方法，
    // 保证"面板入口"和"热键/托盘入口"永远走同一段代码，不会各改各的。

    /// <summary>面板扩展按钮的分派表。</summary>
    private void OnQuickViewExternalAction(string id)
    {
        switch (id)
        {
            case "TodoSummary":  ToggleTodoSummary(); break;   // 与全局热键同语义：唤出/收起
            case "RecycleBin":   OpenRecycleBin();    break;
            case "Settings":     OpenSettings();      break;
            case "Import":       OpenImport();        break;
            case "DailySummary": OpenDailySummary();  break;
        }
    }

    /// <summary>回收站：模态打开（同设置窗口内的入口），关闭后刷新面板——恢复的笔记要立刻可见。</summary>
    private void OpenRecycleBin()
    {
        if (_noteService == null) return;
        try
        {
            var win = new RecycleBinWindow(_noteService, _noteService.RecycleBin, _syncEngine) { Owner = this };
            win.ShowDialog();
            _quickViewWindow?.Refresh();
        }
        catch (Exception ex) { LogStartupError("OpenRecycleBin", ex); }
    }

    /// <summary>导入笔记：复用 ImportFlow（与导出对话框里的导入按钮同一实现）。</summary>
    private void OpenImport()
    {
        if (_noteService == null) return;
        try
        {
            if (ImportFlow.Run(_quickViewWindow, _noteService, _settings))
                _quickViewWindow?.Refresh();
        }
        catch (Exception ex) { LogStartupError("OpenImport", ex); }
    }

    /// <summary>每日总结：与 ReminderService 的到点汇总走同一个窗口实例（锚点同样取自悬浮球）。</summary>
    private void OpenDailySummary()
    {
        if (_noteService == null || _dailySummary == null) return;
        try
        {
            var (lx, ty) = GetBallAnchor();
            _dailySummary.ShowSummary(_noteService.LoadAllEntries(), lx, ty);
        }
        catch (Exception ex) { LogStartupError("OpenDailySummary", ex); }
    }

    // ── 同步引擎生命周期 ──

    // ── 文件仓库生命周期（2026-09-16） ──

    /// <summary>
    /// 装配文件仓库：
    /// ① 接上云仓库（百度网盘；凭据不全 / 未授权时 <see cref="ICloudStorage.IsReady"/> 为 false，
    ///    整个文件仓库自动降级为「只存本地」，不抛异常也不骚扰用户）
    /// ② 把设置里的淘汰参数灌进淘汰器
    /// ③ 起后台上传队列，并跑一轮保养（到期清理 → 淘汰 → 补传）
    /// </summary>
    private void InitializeFileStore()
    {
        FileRepository.Cloud = new BaiduCloudStorage(_settings.BaiduNetRoot);
        FileRepository.AttachmentRetentionDays = Math.Clamp(_settings.BaiduAttachmentRetentionDays, 0, 3650);
        // 与云端核对（2026-09-27）：注入设置，用于跨会话冷却期 + 记录「最后核对时刻」。
        // 必须在装配处给（而不是等用户打开设置窗口）—— 否则设置页会一直显示「尚未核对过」。
        CloudVerifyService.Settings = _settings;

        CacheEvictor.Enabled = _settings.LocalCacheEvictEnabled;
        CacheEvictor.MaxBytes = (long)(Math.Clamp(_settings.LocalCacheMaxGb, 0.5, 1024) * 1024 * 1024 * 1024);
        CacheEvictor.IdleDays = Math.Clamp(_settings.LocalCacheIdleDays, 1, 3650);

        UploadQueue.Start();

        // 订阅前一律先解绑：本方法在引擎重建 / 设置变更时会再跑，重复 += 会导致同一动作执行多次。
        ChatAttachmentService.Stored -= OnAttachmentStored;
        ChatAttachmentService.Stored += OnAttachmentStored;
        FileRepository.MetadataChanged -= OnFileMetadataChanged;
        FileRepository.MetadataChanged += OnFileMetadataChanged;

        _ = Task.Run(async () =>
        {
            try
            {
                await AttachmentExpiryService.RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { AppLog.Warn("Files", "附件到期清理失败：" + ex.Message); }

            try { CacheEvictor.Run(); }
            catch (Exception ex) { AppLog.Warn("Files", "缓存淘汰失败：" + ex.Message); }

            UploadQueue.Kick();   // 启动时把上次没传完的补上
        });
    }

    /// <summary>
    /// 附件落盘后的登记（2026-09-16）。**开关默认关**：不开就不登记、不上传，
    /// 与改造前「附件只存本机」的行为完全一致。
    ///
    /// 注意这里只管「自动」：开关关着时用户显式说「把这张图存上去」，仍会由 Agent 工具走显式路径上传
    /// （显式意图优先）。
    /// </summary>
    private void OnAttachmentStored(ChatAttachment attachment)
    {
        try
        {
            if (!_settings.BaiduAttachmentUploadEnabled) return;

            var path = ChatAttachmentService.ResolvePath(attachment);
            if (!File.Exists(path)) return;

            var (meta, error) = FileRepository.RegisterLocalFile(
                path, FileTypes.Attachment, attachment.FileName, attachExisting: true);
            if (meta == null)
            {
                AppLog.Warn("Files", $"附件登记失败：{error}");
                return;
            }
            UploadQueue.Kick();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", "附件登记异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 文件登记/彻底删除后踢一脚：① 让新文件立刻进上传队列 ② 让元数据改动尽快同步出去。
    /// 不这么做的话，「这台存完、那台要等半小时才看见」会让人以为功能坏了。
    /// </summary>
    private void OnFileMetadataChanged()
    {
        UploadQueue.Kick();
        _syncEngine?.NotifyLocalChange();
    }

    /// <summary>本机笔记变更 → 合并窗口推送（订阅一次，_syncEngine 字段实时指向当前引擎）。</summary>
    private void OnNotesChanged() => _syncEngine?.NotifyLocalChange();

    /// <summary>会话本地保存（ChatSessionService.Save 静态事件）→ 会话同步防抖窗口。
    /// 静态事件订阅以 -=/+= 防引擎重建后重复触发。</summary>
    private void OnChatSessionChanged() => _chatSyncEngine?.NotifyLocalChange();

    /// <summary>分组清单落盘（ChatGroupStore.GroupsChanged）→ 会话上传防抖：新建的空分组借道上传，换机器不用等下次同步周期。
    /// 实例方法 + 读字段，与 OnChatSessionChanged 同风格：引擎重建后自动指向新实例；静态事件 -=/+= 防重复订阅。</summary>
    private void OnChatGroupsChanged() => _chatSyncEngine?.NotifyLocalChange();

    /// <summary>按当前配置创建引擎；配置不完整（无 Provider/无授权码）返回 null（本地功能不受影响）。
    /// ChatSyncEngine 随之一并创建：共享笔记引擎并发闸、订阅 CycleCompleted 搭车 hook，互不影响笔记同步。</summary>
    private SyncEngine? CreateSyncEngine()
    {
        if (_noteService == null) return null;
        var sync = _settings.Sync;
        if (sync.ProviderName != "WebDAV") return null;
        var token = Models.SyncSettings.UnprotectToken(sync.WebDavToken);
        if (string.IsNullOrEmpty(sync.WebDavUser) || string.IsNullOrEmpty(token)) return null;
        var provider = new WebDAVProvider(sync.WebDavUrl, sync.WebDavUser, token);
        var engine = new SyncEngine(_settings, _noteService, provider);
        _chatSyncEngine = new ChatSyncEngine(_settings, provider, engine.Gate);
        engine.CycleCompleted += () => _chatSyncEngine.RunOnceAsync().ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() => AIDialogHelper.RefreshOpenDrawer())));  // 会话同步完成后刷新展开中的抽屉（启动首拉/轮询/flush 全覆盖）

        // 文件清单同步（2026-09-16）：同样搭车周期钩子 + 共享并发闸，只同步几 KB 元数据。
        // 注意它**不是** SyncEngine 的桶：文件元数据是另一种结构、另一套合并规则，只共用通道与节拍。
        _fileStoreSync = new FileStoreSync(_settings, provider, engine.Gate);
        FileStoreSync.ResetInitialPull();   // 新引擎要重新拉一轮清单，这期间不做「文件不存在」的判断
        engine.CycleCompleted += () => _fileStoreSync.RunOnceAsync().ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(AIDialogHelper.NotifyFileListChanged)));
        AIDialogHelper.SessionDeleted = id => _chatSyncEngine?.MarkDeleted(id);  // AI 对话删除 UI → MarkDeleted 闭环①（lambda 读字段，引擎重建后自动指向新实例）
        _chatSyncEngine.ConflictResolutionRequested += OnChatConflictResolution;  // 阶段二：Rev 冲突弹窗裁决（true=本地覆盖上传）
        ChatSessionService.SessionChanged -= OnChatSessionChanged;
        ChatSessionService.SessionChanged += OnChatSessionChanged;
        ChatGroupStore.GroupsChanged -= OnChatGroupsChanged;    // 批 0 分组落盘钩子接线：订阅只许在唯一创建点，
        ChatGroupStore.GroupsChanged += OnChatGroupsChanged;    // 与 RebuildSyncEngine/退出路径的 Dispose 成对（构造函数禁止订阅）
        return engine;
    }

    /// <summary>会话保存冲突裁决（ChatSyncEngine 后台线程回调）：弹窗问用户——
    /// 「是」= 覆盖：用本机这版覆盖云端；「否」= 保留他端：本机这版暂不上传，留痕待下次保存再提示。</summary>
    private Task<bool> OnChatConflictResolution(string sessionId)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var answer = System.Windows.MessageBox.Show(this,
                    $"会话在另一设备有更新（{sessionId[..Math.Min(8, sessionId.Length)]}…）。\n\n" +
                    "【是】覆盖：用本机这版覆盖云端（他端更新会被覆盖）\n" +
                    "【否】保留他端：本机这版暂不上传（下次保存会再次提示）",
                    "会话同步冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                tcs.TrySetResult(answer == MessageBoxResult.Yes);
            }
            catch { tcs.TrySetResult(false); }  // 弹窗失败按"保留他端"处理，绝不静默覆盖
        }));
        return tcs.Task;
    }

    /// <summary>设置页保存 WebDAV 配置后重建引擎（新配置立即生效，自动同步轮询延续）。</summary>
    private void RebuildSyncEngine()
    {
        _syncEngine?.StopAutoSync();
        _chatSyncEngine?.Dispose();   // 旧会话引擎先停用（停防抖 timer）：重建后旧实例绝不被 GroupsChanged 唤醒写真实目录
        _syncEngine = CreateSyncEngine();
        if (_syncEngine != null && _syncEngine.TryUnlockWithStoredToken())
        {
            if (_settings.Sync.AutoSyncEnabled)
                _syncEngine.StartAutoSync();
            else
                _ = Task.Run(() => _syncEngine.SyncNowAsync());
        }
        _syncEngine?.RefreshMergeWindow();   // 合并间隔若被调整，重排等待中的合并窗口立即生效
    }

    /// <summary>AI 助手名称同步到三处入口：面板标题栏按钮 / 悬浮球右键菜单 / 托盘菜单（含图标重建）</summary>
    private void ApplyAssistantNameToAllEntries()
    {
        var name = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName;
        if (_floatBall != null) _floatBall.AiAssistantName = name;
        _quickViewWindow?.UpdateAiName(name);
        try { CreateTrayIcon(); } catch { /* 托盘重建失败不阻塞设置窗口 */ }
    }

    /// <summary>
    /// 托盘图标：与任务栏窗口图标**同源** —— 两处都取自 <see cref="AppIconService"/> 载入的自定义图标
    /// （%AppData%\FocusCapture\custom_icon.png），没有自定义时回退默认深灰方块（v0.1 兜底逻辑保留）。
    /// </summary>
    private void CreateTrayIcon()
    {
        // 重建时释放旧实例（设置变更后即时刷新托盘）
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        // 图标句柄归它自己所有，重建时必须显式释放。
        // 旧写法是「交给托盘之后立刻 DestroyIcon」——托盘长期握着已失效的句柄（use-after-free），
        // 图标可能变空白或画错却不报错，属很难当场发现的隐患，已随本次改造去掉。
        _trayIconHandle?.Dispose();
        _trayIconHandle = null;

        var icon = AppIconService.BuildTrayIcon(_settings.CustomIconPath, System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A));
        _trayIconHandle = icon;

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        { Icon = icon, Visible = true, Text = "FocusCapture - 专注力捕捉" };

        var cm = new System.Windows.Forms.ContextMenuStrip();
        cm.Items.Add("显示设置", null, (_, _) => OpenSettings());
        cm.Items.Add("灵感速览", null, (_, _) => ShowQuickView());
        var aiName = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName;
        cm.Items.Add(aiName, null, (_, _) => AIDialogHelper.Open(ExplainMode.Ask));
        cm.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        cm.Items.Add("退出", null, (_, _) => ExitApp());
        _notifyIcon.ContextMenuStrip = cm;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    /// <summary>系统关机/注销：无法弹窗拦截，静默尽力一传（带超时，会话同步搭车收尾），失败由下次启动对账补传。</summary>
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        try
        {
            if (_syncEngine == null) return;
            var work = Task.Run(() => _syncEngine.SyncNowAsync(auto: true, includeChat: false));   // 关机只抢救笔记（快）；会话下次启动补传
            work.Wait(TimeSpan.FromSeconds(4));   // 阻塞宽限：关机路径弹窗无意义，只争取 4s
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 退出：彻底退出路径（托盘/悬浮球退出）先做同步 flush（硬规则，带 4s 超时）：
    /// SyncNowAsync 笔记部分 + 会话同步搭车 hook 一起收尾；失败弹窗【重试/仍要退出】，
    /// 选"仍要退出"不丢数据（本地文件是事实源，下次启动对账自动补传）。
    /// </summary>
    private async void ExitApp()
    {
        try { await FlushBeforeExitAsync(); }
        catch { /* flush 流程自身异常不阻塞退出 */ }

        _reminderService?.Stop();   // v3.5（Phase 3）：退出前停掉提醒定时器与弹窗调度
        _chatSyncEngine?.Dispose();   // 会话引擎停用（幂等）：flush 已完成，停防抖 timer，退出后不再写任何目录
        _clipboardHook?.Dispose();
        _hotkeyService?.Dispose();
        if (_floatBall != null) { var (l, t) = _floatBall.GetPosition(); _settings.BallLeft = l; _settings.BallTop = t; _settings.Save(); }
        AIDialogHelper.CloseAll();
        _floatBall?.Close(); _inputWindow?.Close(); _quickViewWindow?.Close(); _voiceWindow?.Close(); _notifyIcon?.Dispose();
        CloseDropOverlays();   // 拖放浮层是 Topmost 无边框窗口，退出时必须显式关，否则可能残留在屏幕上
        _todoSummaryWindow?.Close();   // v3.8：待办汇总改为实例常驻，退出时一并关闭
        WpfApp.Current.Shutdown();
    }

    /// <summary>退出前 flush：带超时跑一轮【仅笔记】同步（2026-09-09：includeChat=false——会话对账要拉云端文件，
    /// 4 秒预算等不起，必然超时弹窗；会话未推送内容本地是事实源，下次启动自动补传）。失败循环弹【重试/仍要退出】。关机路径（SessionEnding）不走此方法。</summary>
    private async Task FlushBeforeExitAsync()
    {
        if (_syncEngine == null || !_syncEngine.IsMasterPasswordSet) return;   // 未配置同步：无可 flush
        while (true)
        {
            var work = Task.Run(() => _syncEngine!.SyncNowAsync(auto: false, includeChat: false));
            var completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false);
            if (completed == work && (await work.ConfigureAwait(false)).Success) return;   // 收尾成功

            var reason = completed != work ? "同步超时（4 秒）" : (await work.ConfigureAwait(false)).Error ?? "未知原因";
            var choice = System.Windows.MessageBox.Show(
                $"有记录未上传成功（原因：{reason}）。\n\n要重试上传吗？\n\n选「否」将直接退出；未上传的内容留在本机，下次启动同步时自动补传。",
                "退出前同步失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;   // 仍要退出：对账补传兜底，无需额外脏标记
        }
    }

    protected override void OnClosed(EventArgs e) { _chatSyncEngine?.Dispose(); _hotkeyService?.Dispose(); _notifyIcon?.Dispose(); _trayIconHandle?.Dispose(); base.OnClosed(e); }
}
