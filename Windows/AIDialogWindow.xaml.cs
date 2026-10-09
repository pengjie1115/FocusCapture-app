using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Agent;
using FocusCapture.Services.Destinations;
using FocusCapture.Services.Files;
using FocusCapture.Services.Skills;
using FocusCapture.Windows.Controls;

namespace FocusCapture.Windows;

/// <summary>
/// 气泡里的单个附件展示项（2026-09-14）。
/// 与输入区用同一种卡片（图标前缀 + 截断文件名），悬停出预览、双击看大图/打开原文件；
/// 本体不在本机（跨端拉来的会话）时降级配色并标注，不显示裂图。
/// </summary>
public class ChatAttachmentViewModel
{
    /// <summary>对应的数据模型（预览 / 打开时用）</summary>
    public ChatAttachment Model { get; }

    /// <summary>卡片文案："图片 文件名" / "文件 文件名"</summary>
    public string ChipText { get; }

    public bool IsImage { get; }

    /// <summary>本体是否不在本机（跨端会话的附件）</summary>
    public bool IsMissing { get; }

    public ChatAttachmentViewModel(ChatAttachment model)
    {
        Model = model;
        ChipText = ChipLabel(model);
        IsImage = model.Kind == ChatAttachmentKind.Image;

        var path = ChatAttachmentService.ResolvePath(model);
        IsMissing = !File.Exists(path);
    }

    /// <summary>
    /// 卡片文案的唯一实现：输入区卡片与气泡卡片都调这里，避免两处文案漂移。
    /// 文件名截断到 16 字——输入框一行装不下长文件名，会把行撑得很难看。
    /// </summary>
    public static string ChipLabel(ChatAttachment model)
    {
        var name = model.FileName.Length > 16 ? model.FileName[..16] + "…" : model.FileName;
        return (model.Kind == ChatAttachmentKind.Image ? "图片 " : "文件 ") + name;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}

/// <summary>
/// 对话里的云文件卡片（2026-09-16）。
/// 与附件卡片是两套东西：附件是「要发给模型看的内容」，这里是「从网盘取回到本机的文件」，
/// 所以独立成一个 VM，避免把两种语义混进同一个模板里。
/// </summary>
public class CloudFileCardViewModel
{
    public FileMetadata Model { get; }

    /// <summary>卡片文案：云文件 名称 · 大小</summary>
    public string Label { get; }

    /// <summary>本机路径（取回后必有；为空表示还没落到本机）。</summary>
    public string? LocalPath { get; }

    public CloudFileCardViewModel(FileMetadata model)
    {
        Model = model;
        var entry = FileRepository.FindCache(model.Id);
        LocalPath = entry != null && File.Exists(entry.LocalPath) ? entry.LocalPath : null;

        var name = model.Name.Length > 20 ? model.Name[..20] + "…" : model.Name;
        Label = $"云文件 {name} · {ChatAttachmentViewModel.FormatSize(model.Size)}";
    }
}

/// <summary>对话框消息气泡 ViewModel</summary>
public class ChatBubbleViewModel : INotifyPropertyChanged
{
    public bool IsUser { get; }

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            FirePropertyChanged(nameof(Content));
            // 显示文本跟着刷：流式期间把 [[OPTIONS:...]] 标记（含半截）藏掉，正文缓冲区不动
            FirePropertyChanged(nameof(DisplayContent));
        }
    }

    /// <summary>气泡正文展示文本 = Content 剥除快捷选项标记（含尾部半截）后的结果。
    /// BubbleText 绑这个而不是 Content —— 会话历史保留原文（模型下轮还能看到自己给过什么选项），
    /// 剥除只发生在显示层（2026-09-27 快捷选项协议，见 QuickOptions）。</summary>
    public string DisplayContent => IsUser ? _content : QuickOptions.StripForDisplay(_content);

    /// <summary>快捷选项按钮（AI 气泡用；null = 不显示按钮行）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<QuickActionViewModel>? QuickActions { get; private set; }

    public bool HasQuickActions => QuickActions is { Count: > 0 };

    /// <summary>回答结束后调用：解析 Content 里的 [[OPTIONS:...]] 标记 → 剥除 + 生成按钮。
    /// 用户气泡跳过（协议只对 AI 回复生效）。</summary>
    public void FinalizeQuickOptions()
    {
        if (IsUser) return;
        var options = QuickOptions.Parse(_content, out var clean);
        Content = clean;   // setter 会同步刷 DisplayContent（此时无标记，等价 clean）
        if (options.Count > 0)
        {
            QuickActions = new System.Collections.ObjectModel.ObservableCollection<QuickActionViewModel>(
                options.Select(o => new QuickActionViewModel(o)));
        }
        FirePropertyChanged(nameof(QuickActions));
        FirePropertyChanged(nameof(HasQuickActions));
    }

    /// <summary>挂命令型按钮（不走模型、点击执行本地动作，如错误后的「重试」）。</summary>
    public void SetCommandActions(params QuickActionViewModel[] actions)
    {
        if (IsUser) return;
        QuickActions = new System.Collections.ObjectModel.ObservableCollection<QuickActionViewModel>(actions);
        FirePropertyChanged(nameof(QuickActions));
        FirePropertyChanged(nameof(HasQuickActions));
    }

    public bool IsFillable { get; }

    private bool _isFilled;
    public bool IsFilled
    {
        get => _isFilled;
        set
        {
            if (_isFilled == value) return;
            _isFilled = value;
            FirePropertyChanged(nameof(IsFilled));
        }
    }

    private string _reasoningText = "";
    /// <summary>思考过程文本（仅思考型模型产生；流式追加）</summary>
    public string ReasoningText
    {
        get => _reasoningText;
        set
        {
            if (_reasoningText == value) return;
            _reasoningText = value;
            FirePropertyChanged(nameof(ReasoningText));
            FirePropertyChanged(nameof(HasReasoning));
        }
    }

    public bool HasReasoning => _reasoningText.Length > 0;

    private bool _isReasoningOpen;
    /// <summary>思考过程区展开状态：流式期间自动展开，回答结束收起</summary>
    public bool IsReasoningOpen
    {
        get => _isReasoningOpen;
        set
        {
            if (_isReasoningOpen == value) return;
            _isReasoningOpen = value;
            FirePropertyChanged(nameof(IsReasoningOpen));
        }
    }

    private string _toolSteps = "";
    /// <summary>Agent 工具调用步骤（每步一行，回答完成后保留为过程记录）</summary>
    public string ToolSteps
    {
        get => _toolSteps;
        set
        {
            if (_toolSteps == value) return;
            _toolSteps = value;
            FirePropertyChanged(nameof(ToolSteps));
            FirePropertyChanged(nameof(HasToolSteps));
        }
    }

    public bool HasToolSteps => _toolSteps.Length > 0;

    /// <summary>消息携带的附件（仅用户消息会出现；无附件为 null）</summary>
    public IReadOnlyList<ChatAttachmentViewModel>? Attachments { get; }

    public bool HasAttachments => Attachments is { Count: > 0 };

    /// <summary>从网盘取回并交付给用户的文件卡片（工具执行过程中动态追加）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<CloudFileCardViewModel> CloudFiles { get; } = new();

    public bool HasCloudFiles => CloudFiles.Count > 0;

    /// <summary>追加一张云文件卡片（在 UI 线程调用）。</summary>
    public void AddCloudFile(FileMetadata meta)
    {
        if (CloudFiles.Any(c => c.Model.Id == meta.Id)) return;   // 同一文件只给一张卡
        CloudFiles.Add(new CloudFileCardViewModel(meta));
        FirePropertyChanged(nameof(HasCloudFiles));
    }

    /// <summary>卡片被移除后刷新「是否有云文件卡片」（添加走 AddCloudFile，移除走这里）。</summary>
    public void NotifyCloudFilesChanged() => FirePropertyChanged(nameof(HasCloudFiles));

    public ChatBubbleViewModel(bool isUser, string content, bool isFillable = false,
        IReadOnlyList<ChatAttachmentViewModel>? attachments = null)
    {
        IsUser = isUser;
        Content = content;
        IsFillable = isFillable;
        Attachments = attachments;
        // 历史会话回看：加载时内容即定稿，直接解析一次（流式气泡此刻内容为空，Finalize 无操作）
        if (!isUser) FinalizeQuickOptions();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void FirePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>气泡下方的快捷按钮（2026-09-27）：
/// 发送型（IsCommand=false，模型 [[OPTIONS:...]] 协议产出 / 起手页胶囊）点击 = 整句直接发送；
/// 命令型（IsCommand=true）点击 = 执行本地动作（如错误后的「重试」），不走模型。</summary>
public sealed class QuickActionViewModel
{
    public string Text { get; }
    public bool IsCommand { get; }
    public QuickActionViewModel(string text, bool isCommand = false)
    {
        Text = text;
        IsCommand = isCommand;
    }
}

/// <summary>三板块 AI 对话框：翻译 / 搜索 / 问答。连续对话 + 真流式（含思考过程）+ 发送/停止 + 历史会话抽屉 + 回填</summary>
public partial class AIDialogWindow : Window
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;
    private readonly OpenAICompatibleProvider _provider;
    // 会话级模型 provider 缓存（2026-09-24 任务5）：按会话 ModelKey 解析，同 key 复用同一 provider 实例
    // —— 否则 LastTrimHint 会因每次 new 新 provider 而丢失（发送设的裁剪提示取不回来）。
    private readonly Dictionary<string, OpenAICompatibleProvider> _sessionProviders = new(StringComparer.Ordinal);
    private bool _closed;
    private bool _drawerOpen;               // 历史抽屉展开状态
    private AgentToolRegistry? _registry; // Agent 工具注册表（AgentEnabled 时懒构建）
    private SkillCatalog? _skillCatalog;  // Skill 目录扫描（2026-09-20；带目录时间戳缓存，装完不必重启）
    private SkillRuntime? _skillRuntime;  // 内置 Python 运行时（只读状态，探测带缓存）
    private IReadOnlyList<SkillDependency>? _skillDeps; // 外部依赖表（2026-09-20；授权走应用内，不外包给用户）

    // ── 多会话并行（2026-09-21）：每个会话独立运行态，切会话不中断后台回答，后台流继续往各自 Bubbles 写 ──
    private ConversationRuntime? _active;   // 当前活跃（前台显示）的会话运行态
    private readonly Dictionary<string, ConversationRuntime> _runtimes = new(StringComparer.Ordinal);  // 按 SessionId 索引；后台正在回答的会话也常驻于此

    /// <summary>单个会话的运行态：把原先散在窗口级的会话状态收拢成每会话一个实例，使多会话可并行。
    /// 切会话 = 切 _active + 把 MessagesList.ItemsSource 指向目标 Bubbles；后台流照常往各自 Bubbles 写，切回即见。</summary>
    private sealed class ConversationRuntime
    {
        public ObservableCollection<ChatBubbleViewModel> Bubbles { get; } = new();
        public ChatSessionService Session { get; }
        public CancellationTokenSource? Cts;        // 当前回答的取消源；停止按钮 / 关窗时 Cancel
        public bool IsStreaming;                    // 该会话是否正在生成回答
        public ExplainMode Mode { get; }
        public NoteEntry? TargetNote { get; }        // 关联的目标笔记（翻译/搜索来源）
        public bool AgentRulesAdded;                // Agent 系统规则每会话只注入一次
        public string? DraftText;                   // 未发送草稿（切会话各自保留；关窗时落盘、重开回填）
        public bool Deleted;                        // 已删标记：其流跑完的 finally 不得再 Save，否则会复活已删文件

        // 本会话用户选过的文件牌号（2026-09-21，用户拍板）：
        // 卡片显示与对模型的注入都只认本会话的牌号 —— 切到别的会话看不见、模型也操作不了；
        // 切回本会话卡片恢复显示、AI 仍可操作。牌号本体仍留在 FileHandleStore（24h 过期不变），
        // TryResolve 保持全局：后台回答中的会话只要注入时见过牌号，工具调用就还能解析。
        public List<string> HandleIds { get; } = new();

        public ConversationRuntime(ChatSessionService session, ExplainMode mode, NoteEntry? targetNote)
        {
            Session = session;
            Mode = mode;
            TargetNote = targetNote;
        }
    }

    /// <summary>附件悬停预览 + 双击大图（输入区卡片与气泡卡片共用一份实例）</summary>
    private readonly AttachmentPreviewHost _preview = new();

    /// <summary>剪贴板诊断日志的节流：同一轮粘贴不刷屏</summary>
    private DateTime _lastClipboardLog = DateTime.MinValue;

    /// <summary>侧边栏刷新是否已排队：把同一批数据变更事件合并成一次重扫（见 OnChatDataChanged）</summary>
    private bool _drawerRefreshPending;

    public AIDialogWindow(NoteService noteService, AppSettings settings)
    {
        _noteService = noteService;
        _settings = settings;
        // 2026-09-23 多供应商改造：改走统一解析入口（配置 → provider 的唯一映射点）
        _provider = AiModelResolver.CreateProvider(settings);
        InitializeComponent();
        // 明暗主题（2026-09-27）：XAML 色值已全部资源化，这里按设置的 shade 灌一次角色色
        // （shade=1 = 现状深色；设置窗口滑块变化时也会实时调 Apply，窗口开着立即跟随）
        ChatThemeService.Apply(_settings.ChatShade);
        DarkTitleBar.Enable(this, _settings.ChatShade > 0.5);   // 浅色端还原系统浅色标题栏（句柄建好后生效）
        SearchPanel.OwnerWindow = this;   // 搜索面板 XAML 常驻实例化（默认构造），owner 在此注入
        DarkTitleBar.Enable(this);   // 2026-09-21：主动申请深色原生标题栏（WPF 默认白底，不申请就靠系统心情）
        ApplyHeaderLayout(false);    // 标题栏起始态 = 收起（构造函数里还没开侧边栏；默认展开走下面的 Loaded）
        ApplyAssistantName();        // 标题跟随设置里的 AI 助手名称（不等 Activate —— 空白窗口期也不该闪默认名）
        // MessagesList.ItemsSource 在 Activate() 时按活跃会话绑定（多会话并行：切会话即切 Bubbles 源）
        InitInputArea();
        // 预览浮层预热：Popup 首次显示要创建宿主窗口（低配机上可感知），
        // 放到窗口加载完成后的空闲时机先开合一次，把这份开销挪到用户看不见的地方
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(_preview.Prewarm), DispatcherPriority.Background);
        // 侧边栏默认展开（2026-09-23）：设置里可改，默认收起。
        // 放在 Loaded 而不是构造函数 —— 展开要走宽度动画，窗口还没渲染时触发会停在 Width=0 的中间态。
        Loaded += (_, _) => { if (_settings.ChatSidebarDefaultExpanded) OpenDrawer(true); };
        // 侧边栏（2026-09-23 取代历史抽屉）。与旧抽屉同一条纪律：控件只抛事件，业务一律宿主执行。
        // 注意批量操作（多选）随旧抽屉一起退场了 —— 新侧边栏的会话菜单是单条操作。
        Sidebar.SessionSelected += item => Dispatcher.BeginInvoke(new Action(() => LoadHistorySession(item.FilePath)));
        Sidebar.SessionAction += (item, action, context) => Dispatcher.BeginInvoke(new Action(() => HandleItemAction(item, action, context)));
        Sidebar.GroupSelected += group => Dispatcher.BeginInvoke(new Action(() => HandleGroupSelected(group)));
        Sidebar.GroupAction += (group, action) => Dispatcher.BeginInvoke(new Action(() => HandleGroupAction(group, action)));
        Sidebar.NewChatRequested += () => Dispatcher.BeginInvoke(new Action(() => { CloseGroupView(); StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote); }));
        Sidebar.NewGroupRequested += () => Dispatcher.BeginInvoke(new Action(HandleNewGroup));
        Sidebar.RecycleBinRequested += () => Dispatcher.BeginInvoke(new Action(HandleRecycleBin));
        // 批量管理（2026-09-24 找回；2026-09-26 文案从「批量操作」统一为「批量管理」）：
        // 入口在会话三点菜单，动作在侧边栏底部操作条。与分组视图的批量多选互斥（两个多选态同屏没意义）。
        Sidebar.BatchModeRequested += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            ExitGroupBatchMode();
            Sidebar.EnterBatchMode();
        }));
        Sidebar.BatchDeleteRequested += items => Dispatcher.BeginInvoke(new Action(() =>
        {
            // 确认框点「否」→ 不删也不退批量（勾选保留）；真删了才退出多选并刷新
            if (!DeleteSessions(items)) return;
            Sidebar.ExitBatchMode();
            RefreshDrawer();
        }));
        Sidebar.BatchGroupRequested += (items, groupId) => Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var item in items)
                ApplySessionMeta(item, s => s.GroupId = groupId);
            RefreshDrawer();
        }));
        Deactivated += (_, _) => _preview.HoverLeave();   // 窗口失焦时鼠标可能已不在卡片上，预览要跟着收
        Closed += OnWindowClosed;

        // 会话 / 分组数据变更 → 侧边栏实时刷新（2026-10-01 用户实测：以前新建或删除会话后，
        // 列表要"关掉侧边栏再打开"才更新）。这两个静态事件同样必须成对解绑（见 OnWindowClosed）——
        // 窗口会被 AIDialogHelper 重建、快照链路每场景一个窗口，不退订就是实例泄漏 + 串场景刷新。
        ChatSessionService.SessionChanged += OnChatDataChanged;
        ChatGroupStore.GroupsChanged += OnChatDataChanged;

        // 云文件交付（2026-09-16）：工具在后台线程取回文件后，把卡片挂到当前气泡上。
        // 静态事件必须成对解绑（窗口会被 AIDialogHelper 重建），否则旧窗口实例泄漏。
        FileDeliveryHub.Delivered += OnFileDelivered;
        FileDeliveryHub.OpenRequested += OnFileOpenRequested;
        FileDeliveryHub.LocateRequested += OnFileLocateRequested;

        RefreshHandleChips();   // 句柄是进程级的，重开窗口时把仍有效的牌号摆回来
    }

    /// <summary>
    /// 以新模式/新目标开启一轮会话。selectedText 非空时自动发起第一轮。
    /// 全局问答入口（无 targetNote、无 selectedText）重复打开时保留原会话。
    /// </summary>
    public void OpenSession(ExplainMode mode, NoteEntry? targetNote = null, string? selectedText = null)
    {
        var isGlobalAskReopen = mode == ExplainMode.Ask
                                && targetNote == null
                                && string.IsNullOrEmpty(selectedText)
                                && _active != null
                                && _active.Mode == ExplainMode.Ask;

        if (!isGlobalAskReopen)
        {
            StartNewSession(mode, targetNote);

            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                var firstMessage = mode == ExplainMode.Translate
                    ? PromptBuilder.BuildTranslatePrompt(selectedText.Trim())
                    : selectedText.Trim();
                // 用户消息统一由 SendAsync 加入会话与 UI，避免两条路径重复添加
                Dispatcher.BeginInvoke(new Action(() => SendAsync(firstMessage)));
            }
        }
        else
        {
            // 复用原会话：把前台切回当前活跃 runtime（可能正后台回答中）
            if (_active != null) Activate(_active);
        }

        // 注：空会话不在此 Save（阶段1 守卫：仅有 system 消息不落盘，避免打开窗口即生成空对话历史）
    }

    /// <summary>新建会话：建一个新 runtime 并切为活跃。**不中断当前活跃会话的后台回答**（多会话并行）。
    /// 当前活跃会话若仍空（未对话）且模式/目标笔记/**分组归属**一致 → 复用，避免空 runtime 堆积。
    /// <paramref name="groupId"/> 非空 = 直接建在该分组里（2026-09-24 修：此前根本不传，
    /// "在分组里发消息"建的会话全是未分组 —— 用户报的"分组里创建的会话没有归类"就是这个）。</summary>
    private void StartNewSession(ExplainMode mode, NoteEntry? targetNote, string groupId = "")
    {
        // 复用仍空的当前会话：模式、目标笔记、**分组归属**全一致才接管 ——
        // 少了归属这一条，在分组里新建会话会错误复用一个未分组的空会话，归组再次落空
        if (_active != null
            && !HasConversation(_active)
            && _active.Mode == mode
            && SameTargetNote(_active.TargetNote, targetNote)
            && string.Equals(_active.Session.GroupId, groupId, StringComparison.Ordinal))
        {
            _active.AgentRulesAdded = false;
            Activate(_active);
            return;
        }

        string? noteContext = null;
        string? noteContent = null;
        if (targetNote != null)
        {
            noteContext = targetNote.Timestamp.ToString("yyyy-MM-dd HH:mm");
            noteContent = targetNote.Content;
        }

        var session = new ChatSessionService(mode, noteContext, noteContent, _settings.AiToolResultLimit)
        {
            GroupId = groupId,   // 空 = 未分组（原行为不变）
            // 2026-09-26 用户拍板的优先级链：上次使用 → 默认模型 → 第一个可用（详见 AiModelResolver.ResolveForNewSession）。
            // 与旧写法的差别：以前这里恒取 AppSettings.ActiveModelKey（当时语义是"全局默认"），
            // 而用户在会话里切模型从不回写它 —— 于是「分组里先选好模型再发言」会被这一步用旧值覆盖，
            // 就是用户报的「分组里选的模型不生效」。现在会话里的每次切换都会回写"上次使用"（见 ApplySessionModel）。
            ModelKey = AiModelResolver.ResolveForNewSession(_settings)?.Key ?? "",
        };
        var runtime = new ConversationRuntime(session, mode, targetNote);
        _runtimes[session.SessionId] = runtime;
        Activate(runtime);
    }

    /// <summary>把指定 runtime 切为活跃：消息列表指向其 Bubbles、按其状态刷新按钮与标题。</summary>
    private void Activate(ConversationRuntime runtime)
    {
        SaveActiveDraft();        // 切走前把当前输入框草稿存到旧 runtime（每会话各自保留）
        PersistActiveSession();   // 切走前把旧会话落盘，使它进历史列表、用户可点它切回看答案
        _active = runtime;
        CloseFindBar();           // 会话内查找的命中按会话算的，换会话即失效（顺手收起）
        MessagesList.ItemsSource = runtime.Bubbles;
        ApplyAssistantName();   // 2026-09-26：标题一律跟随设置里的 AI 助手名称（不再显示模式名）
        SetBusyUi(runtime.IsStreaming);
        RefreshHandleChips();     // 牌号卡片跟会话走（2026-09-21）：切到哪个会话就摆哪个会话选的文件
        RestoreDraft(runtime);   // 回填目标 runtime 的草稿
        FocusInput();
        UpdateSessionModelButton();   // 会话级模型下拉显示跟会话走（2026-09-24 任务5）
    }

    // ── 会话级模型下拉（2026-09-24 任务5；2026-09-26 改为"上次使用"记忆）──
    // 新会话的初始模型由 StartNewSession 按「上次使用 → 默认模型 → 第一个可用」写入（会话从第一句起固定）。
    // 这里给用户改本会话模型的入口：点开弹所有可用模型，选中即改本会话 ModelKey，
    // 同时把它记为"上次使用"，使下一个新会话（含在分组里新建）跟它走。
    // 「跟随全局默认」那一项已于 2026-09-26 删除 —— "全局默认"这个概念退场了，
    // 现在唯一的模型配置是设置里的「默认模型」，而它只在用户还没有使用记录时顶用。
    private void BtnSessionModel_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        // 刻意用 new ContextMenu() 不用初始化器：自带 Style 会顶掉 App.xaml 深色模板出白条（项目已踩过）
        var menu = new ContextMenu();

        foreach (var p in _settings.AiModelProviders)
        {
            foreach (var m in p.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                var key = p.Id + "/" + m.Id;
                var resolved = AiModelResolver.TryResolveExact(_settings, key);
                // 显示名以用户自己填的为准、不带供应商前缀；只有跨供应商同名时才自动补「（供应商）」
                var label = resolved != null
                    ? AiModelResolver.DisplayNameFor(_settings, resolved)
                    : (string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName);
                var item = new MenuItem { Header = label, IsChecked = _active.Session.ModelKey == key };
                var captured = key;
                item.Click += (_, _) => ApplySessionModel(captured);
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "（还没有配置任何模型）", IsEnabled = false });

        menu.PlacementTarget = BtnSessionModel;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>切换当前会话的模型：改本会话 ModelKey + <b>回写「上次使用」</b> + 清 provider 缓存 + 刷新下拉。
    ///
    /// <para>回写这一步是 2026-09-26 修「分组里选的模型不生效」的关键：用户在分组视图里选好模型后一发言，
    /// SendCurrentInput 会先 StartNewSession 建新会话，新会话的初始模型取自"上次使用" ——
    /// 不回写的话它拿到的还是旧值，用户的选择当场被覆盖。回写后无论直接新建对话还是在分组里新建都一致。</para>
    ///
    /// <para>落盘失败不抛（best effort，沿用本项目通则）：设置没存下去最多是下次启动回到旧值，
    /// 不该让"切个模型"把界面弄崩。</para>
    /// </summary>
    private void ApplySessionModel(string key)
    {
        if (_active == null) return;
        _active.Session.ModelKey = key;
        _settings.ActiveModelKey = key;
        try { _settings.Save(); } catch { /* best effort */ }
        _sessionProviders.Clear();
        UpdateSessionModelButton();
    }

    /// <summary>下拉按钮显示：解析出的模型名（不带供应商前缀）+ ▾。
    /// 键为空/失效时 Resolve 有一路回退，所以正常人看到的永远是"实际会用的那个模型"，不会空白。</summary>
    private void UpdateSessionModelButton()
    {
        if (_active == null || BtnSessionModel == null) return;
        var key = _active.Session.ModelKey ?? "";
        var resolved = AiModelResolver.Resolve(_settings, key);
        if (resolved == null)
        {
            BtnSessionModel.Content = "模型 ▾";
            BtnSessionModel.ToolTip = "还没有配置可用的模型（去「设置 → AI 模型」添加）";
            return;
        }
        var label = AiModelResolver.DisplayNameFor(_settings, resolved);
        BtnSessionModel.Content = label + " ▾";
        BtnSessionModel.ToolTip = "本会话使用：" + label + "（点开切换）";
    }

    /// <summary>把当前输入框的纯文本草稿存到活跃会话（切会话/关窗前调用）。</summary>
    private void SaveActiveDraft()
    {
        if (_active == null) return;
        var (text, _) = ExtractInput();   // 草稿只留纯文本；附件不跨会话保留（方案已确认）
        _active.DraftText = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>切走/关窗前把活跃会话落盘（有对话才写；空会话不落盘由 ChatSessionService 守卫）。
    /// 目的：让仍在后台回答的会话进入历史列表，用户可点它切回、复用内存 runtime 看最新内容。</summary>
    private void PersistActiveSession()
    {
        if (_active == null || _active.Deleted) return;
        if (!HasConversation(_active)) return;
        try { _active.Session.Save(); } catch { /* best effort，落盘失败不阻断切换 */ }
    }

    /// <summary>把目标会话的草稿回填到输入框；无草稿则复位为空。</summary>
    private void RestoreDraft(ConversationRuntime runtime)
    {
        if (string.IsNullOrEmpty(runtime.DraftText))
        {
            ResetInput();
            return;
        }
        InsertPlainText(runtime.DraftText);
    }

    /// <summary>清空输入区并插入一段纯文本草稿（多行用 LineBreak 还原，与 ExtractInput 的换行语义对齐）。</summary>
    private void InsertPlainText(string text)
    {
        ResetInput();
        var doc = InputBox.Document;
        if (doc.Blocks.FirstBlock is not Paragraph p) return;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]));
        }
        InputBox.CaretPosition = doc.ContentEnd;
        UpdatePlaceholder();
    }

    /// <summary>草稿文件路径：放数据根下（不进 chat_history，因此不被会话同步引擎扫描/上传；红线12 数据只留本机）。</summary>
    private static string DraftFilePath => FocusCapturePaths.Combine("ai_chat_draft.txt");

    /// <summary>把草稿落到本机文件；草稿为空则清掉文件，避免残留误回填。</summary>
    private static void SaveDraftToDisk(string? text)
    {
        try
        {
            var path = DraftFilePath;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex) { AppLog.Warn("AI", "草稿落盘失败：" + ex.Message); }
    }

    /// <summary>重开窗口时把上次关窗落盘的草稿回填到当前会话输入框，随后清盘（只回填一次）。</summary>
    internal void RestoreDraftFromDisk()
    {
        try
        {
            var path = DraftFilePath;
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            File.Delete(path);   // 无论是否回填都清盘，避免下次重复回填
            if (string.IsNullOrWhiteSpace(text) || _active == null) return;

            var (cur, _) = ExtractInput();
            if (!string.IsNullOrEmpty(cur)) return;   // 输入框已有内容 → 不覆盖用户正在打的字

            _active.DraftText = text;
            InsertPlainText(text);
            FocusInput();
        }
        catch (Exception ex) { AppLog.Warn("AI", "草稿回填失败：" + ex.Message); }
    }

    /// <summary>runtime 是否已有实质对话（非 system 消息）</summary>
    private static bool HasConversation(ConversationRuntime runtime)
        => runtime.Session.Messages.Any(m => m.Role != ChatRoles.System);

    /// <summary>两个目标笔记是否同一（按时间戳判定；都为 null 视为一致）</summary>
    private static bool SameTargetNote(NoteEntry? a, NoteEntry? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.Timestamp == b.Timestamp;
    }

    private void AddBubble(ConversationRuntime runtime, bool isUser, string content, bool isFillable = false,
        IReadOnlyList<ChatAttachmentViewModel>? attachments = null)
    {
        runtime.Bubbles.Add(new ChatBubbleViewModel(isUser, content, isFillable, attachments));
        ScrollAfterDelay(runtime);
    }

    private void ScrollAfterDelay(ConversationRuntime? runtime)
    {
        // 仅当前活跃会话滚动：后台 runtime 的回调不抢前台滚动位置（内容照常写入各自 Bubbles，切回即见）
        if (runtime == null || _active != runtime) return;
        Dispatcher.BeginInvoke(new Action(ScrollToBottom), DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        MessagesScroll.ScrollToEnd();
    }

    // ══════════════════ 输入区：富文本 + 附件混排（2026-09-14） ══════════════════
    // 架构要点：输入区是 RichTextBox，附件是内联的原子块（InlineUIContainer）。
    // 顺序信息存在每个附件的 InsertOffset 上，上行请求时据此把正文切开、还原"文字-图-文字"的原序。

    /// <summary>
    /// 输入区初始化：空段落 + 接管粘贴/复制 + 接管拖拽 + 占位提示。
    ///
    /// 拖拽为什么要在输入框上再挂一遍：RichTextBox 内置了文本拖放处理，会先把 DragOver/Drop
    /// 标记成"已处理"，外层容器的处理器根本收不到。表现就是"鼠标停在输入框正上方放不进去、
    /// 偏上偏下（落在容器的内边距上）才能放" —— handledEventsToo: true 才能在它处理之后接管。
    /// </summary>
    private void InitInputArea()
    {
        ResetInput();
        DataObject.AddPastingHandler(InputBox, OnInputPaste);
        DataObject.AddCopyingHandler(InputBox, OnInputCopying);

        InputBox.AddHandler(UIElement.DragOverEvent, new DragEventHandler(InputArea_DragOver), true);
        InputBox.AddHandler(UIElement.DropEvent, new DragEventHandler(InputArea_Drop), true);

        InputBox.TextChanged += (_, _) => UpdatePlaceholder();
        UpdatePlaceholder();
    }

    /// <summary>清空输入区并恢复一个空段落（RichTextBox 至少要有一个 Block，否则无法输入）</summary>
    private void ResetInput()
    {
        _preview.HoverLeave();   // 卡片会被一起清掉，鼠标不会再触发 MouseLeave，就地收掉预览
        var doc = InputBox.Document;
        doc.Blocks.Clear();
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        InputBox.CaretPosition = doc.ContentStart;
        UpdatePlaceholder();
    }

    /// <summary>占位提示只在"既没文字也没附件"时显示。
    /// 顺带同步「输入区居中 / 沉底」与欢迎语 —— 发消息、切会话、切分组都会经过这里，
    /// 挂在它上面就不必去十几个调用点各插一次。</summary>
    private void UpdatePlaceholder()
    {
        var text = new TextRange(InputBox.Document.ContentStart, InputBox.Document.ContentEnd).Text;
        var empty = string.IsNullOrWhiteSpace(text) && AttachmentCountInInput() == 0;
        InputPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RefreshPickGroupLabel();   // 「选择分组」文案跟着状态走（发送/切会话/进退分组都经过这里）
        RefreshComposerLayout();
    }

    /// <summary>按当前会话状态决定输入区在中间还是底部。
    /// 分组视图也沉底 —— 它不是起手页（空态提示由分组视图自己的"说第一句"承担）。</summary>
    private void RefreshComposerLayout()
        => ApplyComposerLayout(atBottom: _active is { Bubbles.Count: > 0 } || _activeGroupId.Length > 0);

    /// <summary>
    /// 输入区布局（2026-09-23，用户要求）：
    /// - **起手态**（空会话且不在分组视图里）：输入框垂直居中，上方显示欢迎语
    /// - **对话态**：沉到底部，但**留 16px 空隙**（原话"不要完全触底"）
    ///
    /// 位置切换本身没法做动画（改的是 Grid.Row / VerticalAlignment），所以靠 Transparency 感的
    /// 短淡入让跳变不刺眼 —— 不做位移补间是因为那要引入 Canvas 定位，把整个布局关系搞脆。
    /// </summary>
    private void ApplyComposerLayout(bool atBottom)
    {
        var showingWelcome = !atBottom && _activeGroupId.Length == 0;

        WelcomePanel.Visibility = showingWelcome ? Visibility.Visible : Visibility.Collapsed;
        if (showingWelcome) UpdateWelcomeContent();

        var targetRow = atBottom ? 1 : 0;
        var targetAlignment = atBottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        var targetMargin = atBottom ? new Thickness(0, 0, 0, 16) : new Thickness(28, 0, 28, 0);
        // 沉底时左下角要不要切平，取决于侧边栏开合（沿用原有规则，别改出分叉）；居中态四角都是圆的
        var targetCorner = atBottom
            ? (_drawerOpen ? new CornerRadius(0, 0, 0, 6) : new CornerRadius(0, 0, 6, 6))
            : new CornerRadius(6);

        // 布局没变就什么都不做 —— UpdatePlaceholder 每次打字都会路过这里（TextChanged → RefreshComposerLayout），
        // 下面那段淡入动画若无条件重播，就是"敲一个键输入板闪一次"的根因（2026-09-27 用户报告）。
        if (Grid.GetRow(InputArea) == targetRow
            && InputArea.VerticalAlignment == targetAlignment
            && InputArea.Margin == targetMargin
            && InputArea.CornerRadius == targetCorner)
        {
            return;
        }

        Grid.SetRow(InputArea, targetRow);
        InputArea.VerticalAlignment = targetAlignment;
        InputArea.Margin = targetMargin;
        InputArea.CornerRadius = targetCorner;

        // 短淡入：位置跳变时给一帧过渡（快照/无动画环境下 BeginAnimation 不影响终值）
        var fade = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(140));
        InputArea.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 欢迎语内容（2026-09-26 改版）。
    /// - 设置里填了「自定义欢迎语」→ 整句照用，其中 <c>{昵称}</c> 会替换成称呼（不填占位符就原样显示）；
    /// - 没填 → 默认句式「{昵称}，我帮你」；
    /// - 称呼来源：设置里的昵称优先，留空取 Windows 登录名，再取不到（异常 / 空）就只显示「我帮你」。
    /// 2026-09-26 起**不再有欢迎语图标**（用户拍板删除，起手页只显示文字）。
    /// </summary>
    private void UpdateWelcomeContent()
    {
        var nickname = ResolveWelcomeNickname();
        var custom = (_settings.ChatWelcomeText ?? "").Trim();
        WelcomeText.Text = custom.Length > 0
            ? custom.Replace("{昵称}", nickname)
            : nickname.Length > 0 ? $"{nickname}，我帮你" : "我帮你";

        // 起手页快捷问法胶囊（2026-09-27）：点击 = 整句直接发送；空列表/全空白 = 整行不显示。
        // 只在内容真变时才重赋 ItemsSource：赋新实例 = 全部胶囊容器销毁重建，重建那一帧内点击会落空
        // （本方法每次起手态布局都会路过，2026-09-27 诊断实测旧按钮 DataContext 变 {DisconnectedItem}）
        var prompts = (_settings.ChatQuickPrompts ?? new List<string>())
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var current = WelcomePrompts.ItemsSource as IReadOnlyList<string>;
        if (current == null || current.Count != prompts.Count
            || !current.Zip(prompts, (a, b) => a == b).All(same => same))
        {
            WelcomePrompts.ItemsSource = prompts;
        }
        WelcomePrompts.Visibility = prompts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>欢迎语里的称呼：设置里的昵称优先，留空取 Windows 登录名（取不到返回空串，绝不抛）。</summary>
    private string ResolveWelcomeNickname()
    {
        var nickname = (_settings.ChatUserNickname ?? "").Trim();
        if (nickname.Length > 0) return nickname;
        try { return (Environment.UserName ?? "").Trim(); }
        catch { return ""; }
    }

    /// <summary>设置里改了昵称 / 自定义欢迎语 / 头像 / AI 助手名称后重刷界面（由 <see cref="AIDialogHelper.NotifyChatUiSettingsChanged"/> 调用）。
    /// 起手页那句话、侧边栏底部用户区、窗口标题都跟着设置走；窗口没开着就不会走到这里。</summary>
    internal void RefreshChatUiFromSettings()
    {
        ApplyAssistantName();
        UpdateWelcomeContent();
        Sidebar.SetUser(_settings.ChatUserNickname, ChatAssetsService.LoadUserAvatar());
        // 明暗滑块（2026-09-27）：窗口开着时拖滑块 → 资源色重灌（幂等）+ 标题栏深浅实时切
        ChatThemeService.Apply(_settings.ChatShade);
        DarkTitleBar.Apply(this, _settings.ChatShade > 0.5);
    }

    /// <summary>把键盘焦点落到输入框。窗口是非模态弹出的，WPF 不会自动聚焦任何控件，必须显式调</summary>
    internal void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    /// <summary>
    /// 从输入区取出（正文, 附件有序列表）。
    /// 每个附件的 InsertOffset = 它插在正文第几个字符之后，这是"混排"顺序的唯一来源。
    /// </summary>
    private (string Text, List<ChatAttachment> Attachments) ExtractInput()
    {
        var attachments = new List<ChatAttachment>();
        var sb = new StringBuilder();

        foreach (var block in InputBox.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                switch (inline)
                {
                    case Run run:
                        sb.Append(run.Text);
                        break;
                    case LineBreak:
                        sb.Append('\n');
                        break;
                    case InlineUIContainer uc when uc.Tag is ChatAttachment a:
                        a.InsertOffset = sb.Length;
                        attachments.Add(a);
                        break;
                }
            }
            sb.Append('\n');   // 段落之间补换行
        }

        var raw = sb.ToString();
        var trimmed = raw.Trim();

        // 正文首尾空白被剪掉后要同步修正附件偏移，否则图会落到错误位置
        var startCut = raw.Length - raw.TrimStart().Length;
        if (startCut > 0)
            foreach (var a in attachments)
                a.InsertOffset = Math.Max(0, a.InsertOffset - startCut);

        return (trimmed, attachments);
    }

    private int AttachmentCountInInput() => InputBox.Document.Blocks
        .OfType<Paragraph>()
        .Sum(p => p.Inlines.OfType<InlineUIContainer>().Count());

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C / Ctrl+X：自己接管（含附件卡片的选区降级纯文本；纯文本选区走双程加固写入），
        // 不放行 WPF 内置 Copy/Cut —— 内置命令在 OleSetClipboard 成功后还要 Clipboard.Flush() 固化，
        // 远程工具占用剪贴板时 Flush 抛 CLIPBRD_E_CANT_OPEN，直接弹「界面错误」crash 框（2026-10-01 实测）。
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.C || e.Key == Key.X))
        {
            if (HandleSelectionCopy(e.Key == Key.X)) e.Handled = true;
            return;
        }

        // Ctrl+V：剪贴板是"资源管理器复制的文件"时自己接管。RichTextBox 的内置粘贴命令
        // 只认 Xaml/Rtf/UnicodeText/Bitmap 等格式，对纯 FileDrop 判 CanExecute=false，
        // 粘贴事件（OnInputPaste）根本不会触发 —— 表现就是"复制文件后粘贴毫无反应"，
        // 连诊断日志都没有。文件优先于文本，与 OnInputPaste 的判据顺序保持一致。
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            try
            {
                if (Clipboard.GetDataObject() is { } data
                    && TryGetClipboardFiles(data) is { Length: > 0 } files)
                {
                    e.Handled = true;
                    _ = AddFilesSafelyAsync(files);
                    return;
                }
            }
            catch (Exception ex)
            {
                // 剪贴板被其他程序占用等：放行默认粘贴，不让粘贴整体失效
                AppLog.Warn("AI", "Ctrl+V 检查文件剪贴板失败：" + ex.Message);
            }
        }

        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) return;   // Shift+回车 = 换行（保留默认行为）
        e.Handled = true;
        SendCurrentInput();
    }

    // ══════════════════ 复制 / 粘贴（2026-09-14 二版加固） ══════════════════

    /// <summary>
    /// 输入框复制/剪切统一走这里（Ctrl+C/X 在 PreviewKeyDown 接管）：选区含附件卡片时降级"只放纯文本"，
    /// 纯文本选区也走双程加固写入。空选区返回 false 放行（默认命令对空选区本就无操作）。
    ///
    /// 为什么纯文本也不放行：卡片是 UIElement，默认复制会把整棵可视化树塞进剪贴板的富文本格式
    /// （实测选区里的卡片在纯文本里只留两个空格，图片本身跨应用传不出去）；而 WPF 内置 Copy/Cut 在
    /// OleSetClipboard 成功后还要 Clipboard.Flush() 固化，剪贴板被远程工具占用时 Flush 抛
    /// CLIPBRD_E_CANT_OPEN（2026-10-01 实测 crash 弹窗）。自己走 hardened 写入：有退避重试、失败可见、不抛。
    /// </summary>
    private bool HandleSelectionCopy(bool cut)
    {
        var hasAttachment = SelectionHasAttachment();
        if (!hasAttachment && string.IsNullOrEmpty(InputBox.Selection.Text)) return false;   // 空选区放行

        var text = InputBox.Selection.Text;
        if (cut) InputBox.Selection.Text = string.Empty;   // 剪切语义：原文变空

        try
        {
            // 剪贴板被其他程序占用时统一走双程加固写入（OLE 失败 → Win32 固化退避重试，失败不抛）
            if (!SafeClipboard.TrySetTextHardened(text, WpfClipboard.SetText, Win32.WriteClipboardText))
            {
                AppLog.Error("AI", "复制选区失败：内容为空或剪贴板被持续占用（OLE + Win32 双程均已退避重试）");
                System.Windows.MessageBox.Show(this,
                    "复制失败：剪贴板可能正被其他程序占用（如远程工具）。\n请稍后重试；若持续失败，可暂时关闭远程工具的剪贴板同步。",
                    "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ClipboardHookService.MarkSelfCopy();   // 别让"剪贴板监控自动存笔记"把这次当成用户复制
                if (hasAttachment)
                    AppLog.Info("AI", $"复制选区（含附件卡片）：已降级为纯文本，{text.Length} 字");
                else
                    AppLog.Info("AI", $"复制选区：{text.Length} 字");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("AI", "复制选区失败", ex);
        }

        UpdatePlaceholder();
        return true;
    }

    /// <summary>兜底：复制命令若从别的路径进来（右键菜单等），也把含卡片的选区降级成纯文本</summary>
    private void OnInputCopying(object sender, DataObjectCopyingEventArgs e)
    {
        if (e.IsDragDrop) return;              // 拖拽走自己的语义，不干预
        if (!SelectionHasAttachment()) return;

        e.CancelCommand();
        try
        {
            if (SafeClipboard.TrySetTextHardened(InputBox.Selection.Text, WpfClipboard.SetText, Win32.WriteClipboardText))
                ClipboardHookService.MarkSelfCopy();
            else
            {
                AppLog.Error("AI", "复制兜底处理失败：内容为空或剪贴板被持续占用（OLE + Win32 双程均已退避重试）");
                System.Windows.MessageBox.Show(this,
                    "复制失败：剪贴板可能正被其他程序占用（如远程工具）。\n请稍后重试；若持续失败，可暂时关闭远程工具的剪贴板同步。",
                    "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("AI", "复制兜底处理失败", ex);
        }
    }

    /// <summary>选区里是否包含至少一个附件卡片</summary>
    private bool SelectionHasAttachment()
    {
        var selStart = InputBox.Selection.Start;
        var selEnd = InputBox.Selection.End;
        if (selStart.CompareTo(selEnd) == 0) return false;

        foreach (var block in InputBox.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var c in p.Inlines.OfType<InlineUIContainer>())
            {
                var start = c.ElementStart;
                var end = c.ElementEnd;
                if (start == null || end == null) continue;
                if (start.CompareTo(selStart) >= 0 && end.CompareTo(selEnd) <= 0) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 粘贴拦截：剪贴板有文件或图片时接管为附件，其余情况放行走默认文本粘贴。
    ///
    /// 判据顺序（顺序本身就是规则）：
    /// ① 资源管理器复制的文件（含图片文件）→ 一定是附件
    /// ② 带实义文字 → 用户多半想粘文字（Word / 网页复制文本时剪贴板里也带位图），放行
    /// ③ 只剩位图 → 截图粘贴，接管
    ///
    /// 第 ③ 步取图不再只看 DataFormats.Bitmap：系统截图工具主要写的是 CF_DIB / PNG 格式，
    /// 只看 Bitmap 会漏掉，命中不到就什么都不做、也不提示 —— 那正是"粘不进去"的成因。
    /// 现在统一走系统剪贴板取图，并在取不到时留下可追查的日志。
    /// </summary>
    private void OnInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        try
        {
            LogClipboardDiagnostics(e.DataObject);

            // ① 资源管理器里复制的文件
            if (TryGetClipboardFiles(e.DataObject) is { Length: > 0 } files)
            {
                e.CancelCommand();
                _ = AddFilesSafelyAsync(files);
                return;
            }

            // ② 带实义文字 → 放行默认文本粘贴
            if (TryGetClipboardText(e.DataObject) is { Length: > 0 })
                return;

            // ③ 只剩位图（截图）→ 当附件处理
            var image = TryGetClipboardImage(e.DataObject);
            if (image != null)
            {
                e.CancelCommand();
                _ = AddBitmapSafelyAsync(image);
                return;
            }

            // ④ 既没文字也没图：交回默认处理（可能是 RTF 等富文本），只留一行日志备查
            AppLog.Info("AI", "粘贴：未识别为附件，交回默认处理，格式=" + string.Join(",", SafeFormats(e.DataObject)));
        }
        catch (Exception ex)
        {
            AppLog.Error("AI", "粘贴处理异常", ex);
            e.CancelCommand();
            System.Windows.MessageBox.Show(this, "粘贴失败：" + ex.Message, "粘贴出错",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 把剪贴板里可见的格式写进日志。
    /// 粘贴类问题只能靠"真实剪贴板里到底有什么"来定位，光看代码永远猜不准；
    /// 同一轮里做节流，避免连续粘贴刷爆日志。
    /// </summary>
    private void LogClipboardDiagnostics(IDataObject? data)
    {
        if ((DateTime.Now - _lastClipboardLog).TotalSeconds < 1.5) return;
        _lastClipboardLog = DateTime.Now;
        try
        {
            var formats = SafeFormats(data);
            var textLen = TryGetClipboardText(data)?.Length ?? 0;
            var fileCount = TryGetClipboardFiles(data)?.Length ?? 0;
            AppLog.Info("AI", $"粘贴诊断：格式=[{string.Join(",", formats)}] 文本长度={textLen} 文件数={fileCount}");
        }
        catch
        {
            // 诊断本身不该影响粘贴
        }
    }

    private static string[] SafeFormats(IDataObject? data)
    {
        try { return data?.GetFormats() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string[]? TryGetClipboardFiles(IDataObject? data)
    {
        try
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch { return null; }
    }

    private static string? TryGetClipboardText(IDataObject? data)
    {
        try
        {
            if (data == null || !data.GetDataPresent(DataFormats.UnicodeText)) return null;
            var s = data.GetData(DataFormats.UnicodeText) as string;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从剪贴板取位图。两条路：
    /// 1) 粘贴事件自带的数据对象（Bitmap 格式，或只写 PNG 自定义格式的程序）—— 不碰系统剪贴板，无锁竞争
    /// 2) 系统剪贴板接口兜底 —— 覆盖只写 CF_DIB 的程序（系统截图工具就是这类）
    /// </summary>
    private static BitmapSource? TryGetClipboardImage(IDataObject? data)
    {
        try
        {
            if (data != null)
            {
                if (data.GetDataPresent(DataFormats.Bitmap)
                    && data.GetData(DataFormats.Bitmap) is BitmapSource bs
                    && EnsureFrozen(bs) is { } frozen)
                    return EnsureOpaqueIfBlankAlpha(frozen);

                var pngName = DataFormats.GetDataFormat("PNG").Name;
                if (data.GetDataPresent(pngName)
                    && data.GetData(pngName) is Stream pngStream
                    && DecodeStream(pngStream) is { } decoded)
                    return decoded;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "从粘贴数据取图失败：" + ex.Message);
        }

        try
        {
            return WpfClipboard.GetImage() is { } sysImg ? EnsureOpaqueIfBlankAlpha(sysImg) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "从系统剪贴板取图失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// alpha 体检：微信截图等工具写进剪贴板的位图 alpha 通道无效（全 0），WPF 解出来
    /// 是一张全透明图，预览白底上就是"白板"（PrintScreen / 企业微信截图不带这种坏 alpha，正常）。
    /// 整图扫一遍 alpha：全 0 则判定宿主根本没写 alpha，强制转不透明；只要有一个非 0 像素
    /// 就原样返回，不碰正常的半透明截图。
    /// </summary>
    private static BitmapSource EnsureOpaqueIfBlankAlpha(BitmapSource src)
    {
        try
        {
            // 无 alpha 通道的格式（Bgr32 等）不存在这个问题，零成本放行
            if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32) return src;

            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0) continue;
                hasAlpha = true;
                break;
            }
            if (hasAlpha) return src;

            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var opaque = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, pixels, w * 4);
            opaque.Freeze();
            AppLog.Info("AI", $"粘贴图片：位图 alpha 全 0（微信截图等工具的写法），已强制不透明，{w}×{h}");
            return opaque;
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "alpha 体检失败，按原图处理：" + ex.Message);
            return src;
        }
    }

    private static BitmapSource? EnsureFrozen(BitmapSource src)
    {
        try
        {
            if (src.IsFrozen) return src;
            var clone = src.Clone();
            clone.Freeze();
            return clone;
        }
        catch { return null; }
    }

    private static BitmapSource? DecodeStream(Stream stream)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>fire-and-forget 包装：异常必须落到日志与提示，不能被静默吞掉（"点了没反应"的经典成因）</summary>
    private async Task AddFilesSafelyAsync(string[] files)
    {
        try { await AddFilesAsync(files); }
        catch (Exception ex)
        {
            AppLog.Error("AI", "添加文件附件失败", ex);
            System.Windows.MessageBox.Show(this, "添加附件失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task AddBitmapSafelyAsync(BitmapSource source)
    {
        try { await AddBitmapAsync(source); }
        catch (Exception ex)
        {
            AppLog.Error("AI", "添加图片附件失败", ex);
            System.Windows.MessageBox.Show(this, "添加图片失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>发送/停止一体按钮：回答中点击 = 停止生成，空闲时点击 = 发送</summary>
    private void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null && _active.IsStreaming)
        {
            StopStreaming();
            return;
        }
        SendCurrentInput();
    }

    private void SendCurrentInput()
    {
        if (_active != null && _active.IsStreaming) return; // 回答中 Enter 不发送也不清空输入框，防误触丢字

        var (text, attachments) = ExtractInput();
        if (string.IsNullOrEmpty(text) && attachments.Count == 0) return;

        // 分组视图下输入 = 在该分组里开一个**新会话**（用户 2026-09-23 定："开对话之前先选好分组"）。
        // 不切走的话消息会发进 _active（多半是别的会话），归组落空 —— 这正是 2026-09-24 修的根因之一。
        // 发送即退出分组视图，回到正常对话界面（2026-09-24 拍板）；
        // 2026-09-26 改版：**不再顺手收起侧边栏** —— 进分组默认不收起的新规一并管到发消息这一刻
        //（用户拍板：退出分组视图，但侧边栏保持展开）。
        if (_activeGroupId.Length > 0)
        {
            var groupId = _activeGroupId;
            CloseGroupView();
            StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote, groupId);
        }
        // 起手态选了「选择分组」→ 第一句话就会话建在该分组（WorkBuddy"选择工作空间"语义）
        else if (_pendingGroupId.Length > 0)
        {
            StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote, _pendingGroupId);
            _pendingGroupId = "";
        }

        // 双保险：设置里关了图片发送时，即使图片已贴在输入区也不上行。
        // 提示后保留输入区内容，不擅自丢弃用户已经准备好的东西。
        if (!_settings.AiVisionEnabled && attachments.Any(a => a.Kind == ChatAttachmentKind.Image))
        {
            ShowVisionDisabledTip();
            return;
        }

        ResetInput();
        if (_active != null) _active.DraftText = null;   // 发送成功即清该会话草稿
        SendAsync(text, attachments);
    }

    /// <summary>气泡下方快捷按钮点击（2026-09-27）：发送型 = 整句直接发送（不用打字）；
    /// 命令型 = 本地动作（目前只有错误后的「重试」）。分组落位与 SendCurrentInput 同口径。</summary>
    private void QuickOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QuickActionViewModel action }) return;
        if (action.IsCommand)
        {
            RetryLastUserMessage();
            return;
        }
        SendFromUiButton(action.Text);
    }

    /// <summary>起手页快捷问法胶囊点击（2026-09-27）：整句直接发送，第一句话就带着设置里的问法出去。</summary>
    private void WelcomePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string prompt } || string.IsNullOrWhiteSpace(prompt)) return;
        SendFromUiButton(prompt);
    }

    /// <summary>界面按钮（快捷选项/起手胶囊）共用的发送入口：回答中不给点；
    /// 分组落位与 SendCurrentInput 同口径（分组视图/待选分组 = 先落到目标分组的新会话再发）。</summary>
    private void SendFromUiButton(string text)
    {
        if (_active is { IsStreaming: true }) return;
        if (_activeGroupId.Length > 0)
        {
            var groupId = _activeGroupId;
            CloseGroupView();
            StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote, groupId);
        }
        else if (_pendingGroupId.Length > 0)
        {
            StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote, _pendingGroupId);
            _pendingGroupId = "";
        }
        SendAsync(text);
    }

    /// <summary>错误气泡的「重试」：把最后一条无附件的用户消息原样重发。
    /// 带附件的重发要重新挂附件块，按钮在错误路径里就不给（协议见 SendAsync 的 catch）。</summary>
    private void RetryLastUserMessage()
    {
        var runtime = _active;
        if (runtime == null || runtime.IsStreaming) return;
        var lastUser = runtime.Session.Messages.LastOrDefault(m => m.Role == ChatRoles.User);
        if (lastUser == null || lastUser.Attachments is { Count: > 0 }) return;
        SendAsync(lastUser.Content);
    }

    // ── 附件添加入口：加号 / 粘贴 / 拖拽 三处共用 ──

    /// <summary>
    /// 输入区左侧那个唯一的加号（2026-09-23）：点开弹两项 —— 用户要求"打开时只出现一个加号"，
    /// 原来并排的第二个按钮（引用文件）收进这里。
    ///
    /// 两项语义仍然严格区分，不要合并实现：
    /// · 添加附件 = 把文件内容发给模型看（占上下文）
    /// · 引用文件 = 只给 AI 一个可操作的牌号，文件本身不发模型（红线：本机路径永不进模型）
    /// </summary>
    private void BtnAttach_Click(object sender, RoutedEventArgs e)
    {
        // 刻意用 new ContextMenu() 而不是对象初始化器：自带 Style 会顶掉 App.xaml 的深色模板、弹出层变白条
        var menu = new ContextMenu();

        var attach = new MenuItem { Header = "添加附件" };
        attach.Click += (_, _) => PickAttachments();
        menu.Items.Add(attach);

        var reference = new MenuItem { Header = "引用文件" };
        reference.Click += (_, _) => PickFileForHandle();
        menu.Items.Add(reference);

        menu.PlacementTarget = BtnAttach;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PickAttachments()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片或文档",
            Filter = ChatAttachmentService.FileDialogFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        _ = AddFilesAsync(dlg.FileNames);
    }

    // ── 选择文件：只签发牌号，不复制、不发给模型（2026-09-16） ──

    /// <summary>
    /// 「选择文件」入口。**只做一件事：给用户点过的文件签发一个牌号。**
    /// 与旁边的「+」是两种语义：「+」是把文件当附件发给模型看，这里是让模型能对它动手（存网盘等）。
    /// 由于牌号只在本机生成、模型无法编造，AI 的可达范围就被严格限定在用户亲手点过的文件上。
    /// </summary>
    private void PickFileForHandle()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要交给 AI 操作的文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var path in dlg.FileNames)
        {
            try { FileHandleStore.Register(path); }
            catch (Exception ex) { AppLog.Warn("AI", "登记文件句柄失败：" + ex.Message); }
        }
        RefreshHandleChips();
        FocusInput();
    }

    /// <summary>刷新已选文件卡片区（无内容时整块收起，不占高度）。</summary>
    private void RefreshHandleChips()
    {
        var items = FileHandleStore.Snapshot();
        HandleChips.ItemsSource = items;
        HandleChips.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HandleChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 双击 = 用系统默认程序打开（确认选对了文件）。单击不做事，避免误触。
        if (e.ClickCount < 2) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not FileHandleInfo info) return;
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(info.Path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HandleChipRemove_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            FileHandleStore.Remove(id);
            _active?.HandleIds.Remove(id);   // 卡片跟会话走：从当前会话摘掉
            RefreshHandleChips();
        }
    }

    // ── 云文件卡片的三个交付动作（2026-09-16） ──

    private void CloudFile_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CloudFileCardViewModel card) OpenCloudFile(card);
    }

    private void CloudFile_Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CloudFileCardViewModel card) LocateCloudFile(card);
    }

    private void CloudFile_SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CloudFileCardViewModel card) SaveCloudFileAs(card);
    }

    /// <summary>
    /// 云文件卡片的第四个动作：彻底删除（2026-09-16）。
    ///
    /// <b>刻意只给用户、不给 AI</b>：破坏性动作不上工具，这是项目红线。理由很具体，不是洁癖 ——
    /// AI 手上只有牌号、看不见文件内容也看不见你的上下文，"把上周那三个没用的删了"这种话它只会自己挑，
    /// 而你无从察觉挑错；网盘删除又只进网盘回收站，恢复得自己去网盘客户端。
    /// AI 可以**引导**用户点这个按钮（说「请点卡片上的彻底删除」），但不能自己执行。
    /// </summary>
    private async void CloudFile_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CloudFileCardViewModel card) return;

        var confirm = System.Windows.MessageBox.Show(this,
            $"彻底删除「{card.Model.Name}」？\n\n" +
            "· 本机这份会被删除，这张卡片随即失效\n" +
            "· 网盘上那份会一并删除（网盘回收站可找回）\n" +
            "· 在本应用内不可撤销",
            "彻底删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var (ok, message) = await FileRepository.DeletePermanentlyAsync(card.Model.Id);
            // 云端删不掉时 message 里已如实写明（含去哪补删），照原样交给用户，不做美化
            System.Windows.MessageBox.Show(this, message, ok ? "彻底删除" : "删除失败",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok) RemoveCloudFileCard(card);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "删除失败：" + ex.Message, "彻底删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>摘掉同一文件的所有卡片（同 id 可能挂在多个气泡上），并刷新「是否有云文件卡片」。</summary>
    private void RemoveCloudFileCard(CloudFileCardViewModel card)
    {
        // 云文件卡片可能挂在任一会话（含后台回答中）的气泡上，遍历全部 runtime 摘卡
        foreach (var b in _runtimes.Values.SelectMany(r => r.Bubbles).ToList())
        {
            var hit = b.CloudFiles.FirstOrDefault(c => c.Model.Id == card.Model.Id);
            if (hit == null) continue;
            b.CloudFiles.Remove(hit);
            b.NotifyCloudFilesChanged();
        }
    }

    private void OpenCloudFile(CloudFileCardViewModel card)
    {
        if (!EnsureCloudFileLocal(card, out var path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LocateCloudFile(CloudFileCardViewModel card)
    {
        if (!EnsureCloudFileLocal(card, out var path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "定位失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveCloudFileAs(CloudFileCardViewModel card)
    {
        if (!EnsureCloudFileLocal(card, out var path)) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            FileName = card.Model.Name,
            Filter = "所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.Copy(path, dlg.FileName, overwrite: true);
            System.Windows.MessageBox.Show(this, "已保存到：\n" + dlg.FileName, "另存为",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "保存失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool EnsureCloudFileLocal(CloudFileCardViewModel card, out string path)
    {
        path = card.LocalPath ?? "";
        if (path.Length > 0 && File.Exists(path)) return true;
        System.Windows.MessageBox.Show(this,
            "这个文件还没取回到本机。\n\n可以让 AI 再取一次（说「把 XX 取回来」），或在「设置 → 文件与网盘」里查看记录。",
            "文件不在本机", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // ── 工具交付回调（后台线程 → UI） ──

    private void OnFileDelivered(FileMetadata meta) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        var runtime = _active;
        if (runtime == null) return;
        var bubble = runtime.Bubbles.LastOrDefault(b => !b.IsUser) ?? runtime.Bubbles.LastOrDefault();
        bubble?.AddCloudFile(meta);
        ScrollAfterDelay(runtime);
    }));

    private void OnFileOpenRequested(FileMetadata meta) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        var card = new CloudFileCardViewModel(meta);
        if (card.LocalPath != null) OpenCloudFile(card);
    }));

    private void OnFileLocateRequested(FileMetadata meta) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        var card = new CloudFileCardViewModel(meta);
        if (card.LocalPath != null) LocateCloudFile(card);
    }));

    /// <summary>
    /// 界面快照专用（诊断工具用，正常流程不调用）：造几张「已选择文件」卡片，
    /// 让句柄卡片区在快照里能渲染出来。正常流程下这块只在用户真点过「选择文件」时才出现，
    /// 而它正是本次红线（AI 只能引用牌号）的界面落点，值得一张图。
    /// </summary>
    internal void SeedHandleChipsForSnapshot()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-snapshot-handles");
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "季度报告.docx", "截图-20260916.png", "数据表.xlsx" })
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) File.WriteAllText(path, "snapshot");
                FileHandleStore.Register(path);
            }
            RefreshHandleChips();
        }
        catch { /* 快照辅助失败不影响主流程 */ }
    }

    /// <summary>网盘清单同步完成后由 MainWindow 调用：刷新句柄卡片（可能有已过期的）。</summary>
    internal void RefreshFileViews()
    {
        try { RefreshHandleChips(); } catch { /* 刷新失败不打断对话 */ }
    }

    /// <summary>
    /// 界面快照专用（诊断工具用，正常流程不调用）：造两张「云文件」卡片。
    ///
    /// 云文件卡片只在 AI 真把文件取回并交付时才渲染，其它快照场景覆盖不到它。
    /// 2026-09-16 给卡片加了第四个按钮（彻底删除）—— 一行四个按钮正是"被控件挤出可视区"的高危形态，
    /// 静态读 XAML 看不出来，只能出图。这里刻意不走 OnFileDelivered（它经 Dispatcher 异步排队，
    /// 快照可能在渲染前就完成了），直接同步挂卡片。
    /// </summary>
    /// <summary>快照专用：无活跃会话时建一个空的，避免快照方法访问 _active 落空。</summary>
    private void EnsureActiveForSnapshot()
    {
        if (_active == null) StartNewSession(ExplainMode.Ask, null);
    }

    internal void SeedCloudFileCardsForSnapshot()
    {
        try
        {
            EnsureActiveForSnapshot();
            var runtime = _active;
            if (runtime == null) return;
            AddBubble(runtime, false, "两份文件都取回来了：可以直接打开、在文件夹中定位、另存，确认不要了也能彻底删除。");
            var bubble = runtime.Bubbles.LastOrDefault(b => !b.IsUser);
            if (bubble == null) return;

            foreach (var (name, size) in new[]
                     {
                         ("2026中国OPC白皮书.pdf", 16103185L),
                         ("灵感_2026-09-16.md", 1737L),
                     })
            {
                bubble.AddCloudFile(new FileMetadata
                {
                    Id = "snap-cloud-" + name,
                    Name = name,
                    NetPath = "/apps/FocusCapture/files/" + name,
                    Size = size,
                    Type = FileTypes.Upload,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                });
            }
        }
        catch { /* 快照辅助失败不影响主流程 */ }
    }

    /// <summary>当前会话出现过的全部附件（链路 B 的输入：工具只能引用其中之一，不能凭空指定路径）。</summary>
    private IReadOnlyList<ChatAttachment> CurrentSessionAttachments()
    {
        var session = _active?.Session;
        if (session == null) return Array.Empty<ChatAttachment>();
        return session.Messages
            .Where(m => m.Attachments is { Count: > 0 })
            .SelectMany(m => m.Attachments!)
            .ToList();
    }

    /// <summary>
    /// 把一批文件**直接落进输入区**（悬浮球拖放保存用，2026-09-16）。
    /// 与「加号 / 粘贴 / 拖到输入框」三条入口的差别：这条是"从外面带着文件来的"，
    /// 所以先清空输入区再落 —— 复用单例窗口时，上一轮没发出去的残留附件会跟新附件堆在一起，
    /// 用户看到的是"怎么自己多出两个文件"。
    /// 调用时机硬要求：必须等窗口 Show() 之后再调（AddFilesAsync 失败要弹 MessageBox，owner 尚未显示会出问题）。
    /// </summary>
    public void AddAttachmentPaths(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return;
        ResetInput();
        _ = AddFilesAsync(paths);
    }

    /// <summary>把一批本地文件加进输入区；不支持的格式逐个收集原因，最后一次性告知</summary>
    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        var accepted = 0;

        foreach (var path in paths)
        {
            if (AttachmentCountInInput() >= ChatAttachmentService.MaxAttachmentsPerMessage)
            {
                errors.Add($"单条消息最多 {ChatAttachmentService.MaxAttachmentsPerMessage} 个附件");
                break;
            }
            if (!ChatAttachmentService.IsSupported(path))
            {
                errors.Add($"{Path.GetFileName(path)}：不支持的格式");
                continue;
            }
            if (ChatAttachmentService.IsSupportedImage(path) && !EnsureVisionAllowed()) return;

            var (att, error) = await ChatAttachmentService.CreateFromFileAsync(path, _settings.AiImageQualityLevel);
            if (att == null)
            {
                errors.Add($"{Path.GetFileName(path)}：{error}");
                continue;
            }
            InsertAttachmentChip(att);
            accepted++;
        }

        if (accepted > 0) FocusInput();
        if (errors.Count > 0)
            System.Windows.MessageBox.Show(this, string.Join("\n", errors), "部分附件未添加",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>把剪贴板位图加进输入区（截图粘贴）</summary>
    private async Task AddBitmapAsync(BitmapSource source)
    {
        if (!EnsureVisionAllowed()) return;
        if (AttachmentCountInInput() >= ChatAttachmentService.MaxAttachmentsPerMessage)
        {
            System.Windows.MessageBox.Show(this, $"单条消息最多 {ChatAttachmentService.MaxAttachmentsPerMessage} 个附件", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        var att = await ChatAttachmentService.CreateFromBitmapAsync(source, name, _settings.AiImageQualityLevel);
        if (att == null)
        {
            System.Windows.MessageBox.Show(this, "图片处理失败，请重试", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        InsertAttachmentChip(att);
        FocusInput();
    }

    private void InputArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void InputArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            e.Handled = true;
            _ = AddFilesAsync(files);
        }
    }

    private bool EnsureVisionAllowed()
    {
        if (_settings.AiVisionEnabled) return true;
        ShowVisionDisabledTip();
        return false;
    }

    private void ShowVisionDisabledTip()
    {
        System.Windows.MessageBox.Show(this,
            "当前已关闭「允许发送图片」，图片不会被添加或发送。\n\n" +
            "如果所用模型支持图片输入，请在「设置 → AI 功能」中打开该开关。",
            "图片发送已关闭", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 附件块（InlineUIContainer）：原子节点，整块删除、光标跨不过去 ──

    /// <summary>
    /// 在光标处插入附件卡片。
    /// 选富文本方案（而非"文字框 + 下方附件条"）的核心收益就在这里：
    /// 附件是文档里的原子节点，删不掉一半、复制不乱序。
    /// </summary>
    private void InsertAttachmentChip(ChatAttachment att)
    {
        var pos = InputBox.CaretPosition;
        var insertAt = pos.GetInsertionPosition(LogicalDirection.Forward) ?? pos;

        var container = new InlineUIContainer(BuildAttachmentChip(att, removable: true), insertAt)
        {
            Tag = att,
            // 卡片比一行文字高，默认的基线对齐会让卡片悬在半空、文字沉到下面（看着就是"没调平"）。
            // 改成行内垂直居中，文字与卡片的中线才在一条线上。
            BaselineAlignment = BaselineAlignment.Center,
        };

        // 插完把光标移到块之后：否则光标可能仍停在块前，用户接着打字会插到附件之前
        var after = container.ElementEnd?.GetInsertionPosition(LogicalDirection.Forward);
        if (after != null) InputBox.CaretPosition = after;
        InputBox.Focus();
        UpdatePlaceholder();
    }

    /// <summary>
    /// 构造附件卡片。输入区（removable=true，带移除叉）与气泡（removable=false）共用同一套视觉 ——
    /// 用户在输入框里看到什么形态，发出去就该是什么形态。
    /// </summary>
    private FrameworkElement BuildAttachmentChip(ChatAttachment att, bool removable)
    {
        var isImage = att.Kind == ChatAttachmentKind.Image;

        var text = new TextBlock
        {
            Text = ChatAttachmentViewModel.ChipLabel(att),
            FontSize = 12,
            Foreground = ChatThemeService.Brush("Chat_CCCCCC", _settings.ChatShade),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(text);

        if (removable)
        {
            var remove = new TextBlock
            {
                Text = "×",
                FontSize = 13,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = ChatThemeService.Brush("Chat_999999", _settings.ChatShade),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "移除该附件",
            };
            // 必须用 Down：chip 在 RichTextBox 里，左键按下即被文本编辑器捕获鼠标，
            // MouseLeftButtonUp 会被重定向、永远到不了小叉上（表现为"点了没反应"）；
            // 同卡"双击打开"用 MouseLeftButtonDown 是同一原因。Handled 防止按下触发选区变化。
            remove.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                RemoveAttachmentChip(att);
            };
            panel.Children.Add(remove);
        }

        var chip = new Border
        {
            Background = ChatThemeService.Brush("Chat_2D2D2D", _settings.ChatShade),
            BorderBrush = ChatThemeService.Brush(isImage ? "Chat_378ADD" : "Chat_888780", _settings.ChatShade),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = panel,
        };

        WireAttachmentChip(chip, att);
        return chip;
    }

    /// <summary>给卡片接上悬停预览与双击（输入区卡片与气泡卡片共用同一套行为）</summary>
    private void WireAttachmentChip(FrameworkElement chip, ChatAttachment att)
    {
        chip.MouseEnter += (_, _) => _preview.HoverEnter(att);
        chip.MouseLeave += (_, _) => _preview.HoverLeave();
        chip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            OpenAttachment(att);
        };
    }

    /// <summary>双击附件：图片开大图窗，文档交给系统默认程序</summary>
    private void OpenAttachment(ChatAttachment att)
    {
        var path = ChatAttachmentService.ResolvePath(att);
        if (!File.Exists(path))
        {
            System.Windows.MessageBox.Show(this, "这个附件只存在原设备上，本机没有拷贝。", "附件不在本机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (att.Kind == ChatAttachmentKind.Image)
        {
            var src = AttachmentPreviewHost.LoadFullImage(path);
            if (src == null)
            {
                System.Windows.MessageBox.Show(this, "图片读取失败，无法放大显示。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 先收掉悬停预览：它是独立顶层窗口，不收会浮在大图窗上面
            _preview.HoverLeave();
            new ImagePreviewWindow(att.FileName, src) { Owner = this }.ShowDialog();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 气泡卡片的三个事件（挂载在气泡模板里） ──

    private void AttachmentChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatAttachmentViewModel vm)
            _preview.HoverEnter(vm.Model);
    }

    private void AttachmentChip_MouseLeave(object sender, MouseEventArgs e) => _preview.HoverLeave();

    private void AttachmentChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatAttachmentViewModel vm) return;
        if (e.ClickCount < 2) return;
        e.Handled = true;
        OpenAttachment(vm.Model);
    }

    private void RemoveAttachmentChip(ChatAttachment att)
    {
        _preview.HoverLeave();   // 卡片马上消失，鼠标不会再触发 MouseLeave
        foreach (var block in InputBox.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            var target = p.Inlines.OfType<InlineUIContainer>().FirstOrDefault(c => ReferenceEquals(c.Tag, att));
            if (target != null)
            {
                p.Inlines.Remove(target);
                break;
            }
        }
        FocusInput();
        UpdatePlaceholder();
    }

    /// <summary>
    /// 仅供 --snapshot 界面自检（2026-09-14）：在输入区放一个附件 chip、在消息区放一条带图的用户消息，
    /// 用于核验这两处新界面的真实渲染 —— 空会话快照走不到这两个分支。
    /// 正常启动路径（不带 --snapshot）永远不会调用它。
    /// </summary>
    internal void SeedAttachmentsForSnapshot()
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2D, 0x6E, 0xB4)), null, new Rect(0, 0, 512, 288));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)), null, new Rect(32, 32, 448, 64));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), null, new Rect(32, 128, 200, 120));
            }
            var bmp = new RenderTargetBitmap(512, 288, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();

            var att = ChatAttachmentService
                .CreateFromBitmapAsync(bmp, "快照示例.png", 1)
                .GetAwaiter().GetResult();
            if (att == null) return;

            // 输入区专门摆成"文字 + 图片卡片 + 文字 + 文档卡片"：
            // 快照要看的就是卡片与同排文字到底对没对齐，以及两种卡片的配色差异。
            // InsertTextInRun 不移动插入点，写完文字要手动推到文末，否则卡片会插到文字前面。
            ResetInput();
            var seedText = "看看这张图：";
            InputBox.CaretPosition = InputBox.Document.ContentStart;
            InputBox.CaretPosition.InsertTextInRun(seedText);
            InputBox.CaretPosition = InputBox.CaretPosition.GetPositionAtOffset(seedText.Length, LogicalDirection.Forward)
                                     ?? InputBox.CaretPosition;
            InsertAttachmentChip(att);

            var docAtt = CreateSnapshotDocument();
            if (docAtt != null)
            {
                var seedText2 = " 顺便看下这份文档：";
                InputBox.CaretPosition.InsertTextInRun(seedText2);
                InputBox.CaretPosition = InputBox.CaretPosition.GetPositionAtOffset(seedText2.Length, LogicalDirection.Forward)
                                         ?? InputBox.CaretPosition;
                InsertAttachmentChip(docAtt);
            }

            var bubbleItems = new List<ChatAttachment> { att };
            if (docAtt != null) bubbleItems.Add(docAtt);
            EnsureActiveForSnapshot();
            _active!.Bubbles.Add(new ChatBubbleViewModel(true, "这是带附件的消息示例", false,
                BuildAttachmentVms(bubbleItems)));
        }
        catch
        {
            // 快照是辅助手段，任一环节失败都不该阻断整轮快照
        }
    }

    /// <summary>快照专用：造一份真实文档附件，用于核验文档卡片（灰色边框）的渲染</summary>
    private static ChatAttachment? CreateSnapshotDocument()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "fc-snapshot-sample.md");
            File.WriteAllText(path, "# 示例文档\n这是一份用于界面自检的示例文档，用来核验长文件名截断与文档卡片配色。");
            var (doc, _) = ChatAttachmentService.CreateFromFileAsync(path, 1).GetAwaiter().GetResult();
            return doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把消息附件转成气泡展示项（无附件返回 null，模板据此隐藏整块）</summary>
    private static IReadOnlyList<ChatAttachmentViewModel>? BuildAttachmentVms(IReadOnlyList<ChatAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 }) return null;
        return attachments.Select(a => new ChatAttachmentViewModel(a)).ToList();
    }

    private void StopStreaming()
    {
        // 停止当前活跃会话的回答（发送/停止按钮用）。后台会话不被打扰，切回时再停。
        try { _active?.Cts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放即已结束 */ }
    }

    /// <summary>发送一条消息（可带附件）并流式接收回复（普通/Agent 两路径统一：真流式 + 思考过程 + 可停止）</summary>
    private async void SendAsync(string text, List<ChatAttachment>? attachments = null)
    {
        var runtime = _active;
        if (runtime == null || runtime.IsStreaming) return;
        if (string.IsNullOrWhiteSpace(text) && attachments is not { Count: > 0 }) return;

        var cts = new CancellationTokenSource();
        runtime.Cts = cts;
        runtime.IsStreaming = true;
        SetBusyUi(true);

        try
        {
            AddBubble(runtime, true, text, attachments: BuildAttachmentVms(attachments));
            AddBubble(runtime, false, "", runtime.TargetNote != null && runtime.Mode != ExplainMode.Ask);
            // 2026-09-26 修「发首条消息后输入框不下沉」：SendCurrentInput 里的 ResetInput() 先跑，
            // 那一刻 Bubbles 还是 0 → RefreshComposerLayout 判定为起手态，输入区保持居中；
            // 气泡加进来之后若不在这里重算一次，布局就永远停在中间（用户实测截图）。
            // 位置必须在第一个 await 之前 —— AddBubble 与本行都还在 UI 线程上。
            RefreshComposerLayout();
            var current = runtime.Bubbles[runtime.Bubbles.Count - 1];
            current.Content = "思考中…"; // 首包到达前的等待占位

            if (_settings.AgentEnabled && runtime.Mode == ExplainMode.Ask)
            {
                // Agent 路径：function calling 流式循环（RunAsync 内部负责把用户消息写入会话）
                await SendViaAgentAsync(runtime, text, attachments, current, cts);
            }
            else
            {
                // 普通问答路径：用户消息入会话 + 事件流式接收（正文/思考）
                // （AddUser 不可省：请求体 messages 无 user 会导致 Agnes 400 "No user query" / DeepSeek 自说自话）
                runtime.Session.AddUser(text, attachments);
                await StreamPlainReplyAsync(runtime, current, cts);
            }

            // 上下文裁剪是隐形的（设计稿 §6 坑⑤）：本轮真丢了消息就如实告诉用户。
            // 只写进气泡的**显示内容**，不进会话历史 —— 这是我们给的提示，不是模型说的话。
            // 放在两条路径的共同收尾处而不是各路径内部：写两处必然漏一处。
            var trimHint = ResolveProviderForSession(runtime).LastTrimHint;
            if (!string.IsNullOrWhiteSpace(trimHint))
                current.Content = (current.Content ?? "") + "\n\n（" + trimHint + "）";

            // 快捷选项收尾（2026-09-27）：普通/Agent 两路径统一在这里解析 [[OPTIONS:...]] 标记
            // → 剥除 + 渲染胶囊按钮。停止生成（取消）路径的 MarkStopped 不给按钮。
            current.FinalizeQuickOptions();
        }
        catch (Exception ex)
        {
            var last = runtime.Bubbles.Count > 0 ? runtime.Bubbles[runtime.Bubbles.Count - 1] : null;
            var errorText = $"（错误：{ex.Message}）";
            ChatBubbleViewModel? errorBubble;
            if (last != null && !last.IsUser)
            {
                last.Content += string.IsNullOrEmpty(last.Content) ? errorText : $"\n\n{errorText}";
                errorBubble = last;
            }
            else
            {
                errorBubble = new ChatBubbleViewModel(false, errorText);
                runtime.Bubbles.Add(errorBubble);
            }
            // 命令型「重试」：把最后一条无附件的用户消息原样重发（带附件的重发要重挂附件，先不覆盖）
            var lastUser = runtime.Session.Messages.LastOrDefault(m => m.Role == ChatRoles.User);
            if (lastUser != null && (lastUser.Attachments is not { Count: > 0 }))
                errorBubble.SetCommandActions(new QuickActionViewModel("重试", isCommand: true));
            ScrollAfterDelay(runtime);
        }
        finally
        {
            runtime.IsStreaming = false;
            if (ReferenceEquals(runtime.Cts, cts)) runtime.Cts = null;
            cts.Dispose();
            // 仅当该 runtime 仍是前台活跃会话时才复位发送按钮；后台完成的 runtime 不打扰前台按钮状态
            if (_active == runtime) SetBusyUi(false);
            if (!runtime.Deleted) runtime.Session.Save();   // 已删会话不复活；多会话并行各自落盘（阶段1 空会话守卫仍生效）
            // 后台 runtime 跑完时新会话已进历史列表，刷新展开的抽屉让用户看见
            if (_active != runtime) _ = Dispatcher.BeginInvoke(new Action(RefreshDrawerIfOpen));
        }
    }

    /// <summary>分组指令的注入文本（现读 + 带来源与从属标注）。
    /// 实现搬到 <see cref="ChatGroupService.BuildInstructionContext"/> —— 放服务层才守得住
    /// 「必须显式标注从属关系，不得覆盖系统红线」这条安全要求（窗口的私有方法测不到）。</summary>
    private static string BuildGroupInstructionContext(ChatSessionService session)
        => ChatGroupService.BuildInstructionContext(session.GroupId);

    /// <summary>
    /// 普通问答路径的请求消息 = 会话历史 +（可选）一条**临时附加**的分组指令 system 消息。
    ///
    /// 为什么不复用 Agent 路径的 ExtraSystemContext：那条通道挂在 AgentRunService 上，
    /// 不开 Agent 工具时根本不走。这里手动加一份不写回会话历史的副本，让两条路径行为一致。
    /// </summary>
    private static List<ChatMessage> BuildPlainRequestMessages(ConversationRuntime runtime)
    {
        var groupInstruction = BuildGroupInstructionContext(runtime.Session);
        var messages = runtime.Session.Messages.ToList();
        if (groupInstruction.Length > 0)
            messages.Add(new ChatMessage(ChatRoles.System, groupInstruction));
        return messages;
    }

    /// <summary>按会话级 ModelKey 解析 provider（2026-09-24 任务5）。
    /// 会话 ModelKey 空 = 跟随全局 _provider；非空则按 key 调 AiModelResolver.Resolve 解析，失效回退全局。
    /// 同 key 复用缓存实例 —— LastTrimHint 等 provider 侧状态才能跨发送/取提示正确传递。</summary>
    private OpenAICompatibleProvider ResolveProviderForSession(ConversationRuntime runtime)
    {
        var key = runtime.Session.ModelKey ?? "";
        if (string.IsNullOrWhiteSpace(key)) return _provider;
        if (_sessionProviders.TryGetValue(key, out var cached)) return cached;
        var resolved = AiModelResolver.Resolve(_settings, key);
        var p = resolved != null
            ? new OpenAICompatibleProvider(resolved.BaseUrl, resolved.ApiKey, resolved.ModelId, resolved.MaxOutputTokens, resolved.ContextWindow)
            : _provider;
        _sessionProviders[key] = p;
        return p;
    }

    /// <summary>普通问答路径：消费 StreamChatWithToolsAsync 事件流（无 tools），正文打字机 + 思考过程展示。
    /// 用户停止时已生成的部分内容照常写入会话历史。</summary>
    private async Task StreamPlainReplyAsync(ConversationRuntime runtime, ChatBubbleViewModel current, CancellationTokenSource cts)
    {
        var sb = new StringBuilder();
        try
        {
            await foreach (var ev in ResolveProviderForSession(runtime).StreamChatWithToolsAsync(BuildPlainRequestMessages(runtime), tools: null, cts.Token))
            {
                // 多会话并行：不再因切会话丢弃旧流；取消由 cts.Token 触发 OperationCanceledException
                switch (ev)
                {
                    case StreamChatEvent.ReasoningDelta reasoning:
                        AppendReasoning(current, reasoning.Text);
                        break;
                    case StreamChatEvent.ContentDelta delta:
                        sb.Append(delta.Text);
                        current.Content = sb.ToString();
                        ScrollAfterDelay(runtime);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止/关窗取消：已生成部分写入会话历史
            MarkStopped(current, sb.ToString());
            if (!string.IsNullOrWhiteSpace(sb.ToString()))
                runtime.Session.AddAssistant(sb.ToString());
            return;
        }

        CollapseReasoning(current);
        var full = sb.ToString();
        if (string.IsNullOrWhiteSpace(full))
        {
            current.Content = "（模型未返回内容）";
        }
        else
        {
            runtime.Session.AddAssistant(full);
        }
    }

    /// <summary>
    /// Agent 路径：function calling 流式循环。工具调用步骤实时追加到气泡；正文/思考增量打字机展示；
    /// 写操作（非只读工具）执行前经 ConfirmHandler 弹窗确认。用户停止时不把部分内容写入会话
    /// （中断可能落在 assistant(tool_calls) 与 tool 结果配对之间，写入不完整配对会让后续请求 400）。
    /// </summary>
    private async Task SendViaAgentAsync(ConversationRuntime runtime, string text, List<ChatAttachment>? attachments,
        ChatBubbleViewModel current, CancellationTokenSource cts)
    {
        EnsureAgentRegistry();
        AppendAgentRulesOnce(runtime);
        var agent = new AgentRunService(ResolveProviderForSession(runtime), _registry!, runtime.Session, _settings.AgentMaxToolRounds)
        {
            // 必须经 UiThread 封送：工具跑在线程池线程上，直接 MessageBox.Show(this, …) 会因
            // 跨线程访问窗口对象而抛「调用线程无法访问此对象」（2026-09-20 实测，详见 UiThread 注释）。
            ConfirmHandler = desc => UiThread.AskAsync(Dispatcher, () => System.Windows.MessageBox.Show(
                this,
                $"AI 请求执行以下操作：\n\n{desc}\n\n确认执行？",
                "AI 操作确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK,
                msg => AppLog.Warn("Agent", msg)),
            WriteConfirmEnabled = _settings.AgentWriteConfirmPopup,
            // 每轮把「当前时间」+「用户当前选中的文件牌号」告诉模型。放这里而不是会话历史里：
            // 它们是随手会变的短期状态，写进历史既污染持久化数据，也会让翻旧会话时看到过期值。
            // ⚠️ 时间**绝不能**改放系统提示词 —— 那个会被持久化进会话文件（ChatSessionService.Save），
            // 跨天之后里面的日期就是错的，比不告诉模型更糟（2026-09-17）。
            ExtraSystemContext = () =>
            {
                var sb = new StringBuilder();
                sb.Append("当前时间：").Append(PromptBuilder.DescribeNow(DateTime.Now))
                  .Append("。用户说「今天/明天/上周/这个月」等相对时间时，一律以这个时间为基准。");

                var handleText = FileHandleStore.DescribeForModel(runtime.HandleIds);   // 按会话注入（2026-09-21）
                if (handleText.Length > 0) sb.Append('\n').Append(handleText);

                // Skill 清单（2026-09-20）：与「当前时间」「文件牌号」同类 —— 随手会变（装/删 Skill），
                // 但没必要写进会话历史。走这条既有通道，主循环一行都不用改。
                var skills = _skillCatalog?.GetSkills();
                if (skills is { Count: > 0 })
                    sb.Append('\n').Append(SkillManifest.Build(skills, _skillRuntime?.IsPresent ?? false));

                // 分组指令（2026-09-23）：与「当前时间」「文件牌号」同类的短期状态 —— 现读、随时可变、不进历史。
                // 刻意走这条通道而不是 AppendSystemRules：那条会把文本写进会话文件，
                // 于是用户改了指令之后，旧会话里存的还是上一版，而模型每轮又都看得见它（对不上账）。
                var groupInstruction = BuildGroupInstructionContext(runtime.Session);
                if (groupInstruction.Length > 0) sb.Append('\n').Append(groupInstruction);

                return sb.ToString();
            },
        };

        var gate = new object(); // agentSb / finished 由事件线程与 UI 线程共同访问
        var agentSb = new StringBuilder();
        var finished = false;

        agent.StatusCallback = msg =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                current.ToolSteps = string.IsNullOrEmpty(current.ToolSteps) ? "· " + msg : current.ToolSteps + "\n· " + msg;
            }));
        };
        agent.ContentDelta += t =>
        {
            lock (gate)
            {
                if (finished) return;
                agentSb.Append(t);
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (gate)
                {
                    if (finished) return;
                    current.Content = agentSb.ToString();
                }
                ScrollAfterDelay(runtime);
            }));
        };
        agent.ReasoningDelta += t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref finished)) return;
                AppendReasoning(current, t);
            }));
        };

        try
        {
            var reply = await agent.RunAsync(text, attachments, cts.Token);
            lock (gate) finished = true;
            CollapseReasoning(current);
            current.Content = string.IsNullOrWhiteSpace(reply) ? "（模型未返回内容）" : reply;
        }
        catch (OperationCanceledException)
        {
            lock (gate) finished = true;
            CollapseReasoning(current);
            string partial;
            lock (gate) partial = agentSb.ToString();
            MarkStopped(current, partial);
        }
    }

    /// <summary>思考过程追加：首次出现时自动展开折叠区</summary>
    private static void AppendReasoning(ChatBubbleViewModel bubble, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (string.IsNullOrEmpty(bubble.ReasoningText)) bubble.IsReasoningOpen = true;
        bubble.ReasoningText += text;
    }

    /// <summary>回答结束收起思考过程区（内容保留可再展开）</summary>
    private static void CollapseReasoning(ChatBubbleViewModel bubble)
    {
        if (bubble.HasReasoning) bubble.IsReasoningOpen = false;
    }

    /// <summary>回答被用户停止：已生成部分保留，气泡标注（已停止）</summary>
    private static void MarkStopped(ChatBubbleViewModel bubble, string partial)
    {
        bubble.Content = string.IsNullOrWhiteSpace(partial) || bubble.Content == "思考中…"
            ? "（已停止）"
            : partial + "\n\n（已停止）";
    }

    /// <summary>发送/停止按钮状态机（2026-09-26 改版）：回答中 = 蓝圆 + 中心白色圆角方块（用户给的"铜钱"样式），
    /// 空闲 = 绿圆 + 向上箭头。圆底色与图标都在这里一起切 —— 原先只切图标、圆底恒为绿色，与用户要的样子不符。</summary>
    private void SetBusyUi(bool busy)
    {
        if (_closed) return;   // 窗口已关闭不刷按钮（关窗后后台 runtime 完成的回调不应再动 UI）
        if (busy)
        {
            SendIcon.Visibility = Visibility.Collapsed;
            StopIcon.Visibility = Visibility.Visible;
            SendCircle.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            BtnSend.ToolTip = "停止生成";
        }
        else
        {
            SendIcon.Visibility = Visibility.Visible;
            StopIcon.Visibility = Visibility.Collapsed;
            SendCircle.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            BtnSend.ToolTip = null;
            InputBox.Focus();
        }
        // 输入框回答期间保持可用（可预输入下一条），发送动作由 _active.IsStreaming 守卫拦截
    }

    /// <summary>侧边栏开关（标题栏里两个位置的按钮共用：收起态的 ≡ 与展开态的 ‹）。
    /// 开关逻辑与旧 BtnHistory_Click 一致：收起态先刷新再展开，展开态直接收起。</summary>
    private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (!_drawerOpen)
            RefreshDrawer();
        OpenDrawer(!_drawerOpen);
    }

    /// <summary>收起态标题栏「＋ 新建会话」（2026-09-25 用户要求）：对话进行中也能立即开新会话。
    /// 与侧边栏「新对话」同一链路：退查找条 → 退分组视图 → StartNewSession；
    /// 当前会话由 Activate/Persist 落盘，回答中的会话转后台继续，可从侧边栏切回。</summary>
    private void BtnHeaderNewChat_Click(object sender, RoutedEventArgs e)
    {
        CloseFindBar();
        CloseGroupView();
        StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote);
    }

    // ── 4b：全局搜索覆盖层（2026-09-25，原独立窗体 ChatSearchWindow 改造）──
    // 面板（ChatSearchPanel）常驻本窗可视树，用 Visibility 开关；主内容整体挂 BlurEffect 做毛玻璃，
    // 关闭即移除，避免常驻渲染开销。关闭途径：点面板外遮罩 / Esc（Window_PreviewKeyDown）/ 面板右上×。

    /// <summary>1 号搜索（展开态侧边栏标题栏）：恒开全局搜索覆盖层（2026-09-25 用户拍板不调整）。
    /// 不能与收起态的 BtnGlobalSearch_Click 共用 —— 那颗已按状态分流。</summary>
    private void BtnSidebarHeaderSearch_Click(object sender, RoutedEventArgs e) => OpenSearchOverlay();

    /// <summary>收起态标题栏 🔍（3号/4号同体，2026-09-25 用户拍板按状态分流）：
    /// 对话打开（有内容气泡且不在分组视图）= 向右展开**会话内查找条**；
    /// 起手页 / 分组视图 = 全局搜索覆盖层（原 4 号行为，不调整）。</summary>
    private void BtnGlobalSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_active is { Bubbles.Count: > 0 } && GroupViewPanel.Visibility != Visibility.Visible)
        {
            OpenFindBar();
            return;
        }
        OpenSearchOverlay();
    }

    internal void OpenSearchOverlay()
    {
        MainContent.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 8 };
        SearchPanel.ResetInput();
        SearchPanel.ReloadHistory();
        SearchOverlay.Visibility = Visibility.Visible;
        SearchPanel.FocusSearchBox();
    }

    internal void CloseSearchOverlay()
    {
        if (SearchOverlay.Visibility != Visibility.Visible) return;
        SearchOverlay.Visibility = Visibility.Collapsed;
        MainContent.Effect = null;
    }

    private void SearchOverlayBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseSearchOverlay();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+B：收起/展开侧边栏（2026-10-01 用户需求）。VS Code / Cursor 的侧边栏惯例键，
        // 与全局热键（Ctrl+Alt+*）和应用内 Ctrl+S（沉浸记录保存）无冲突。
        // 必须 Handled：RichTextBox 里 Ctrl+B 默认是 EditingCommands.ToggleBold（加粗），不放行。
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (!_drawerOpen) RefreshDrawer();
            OpenDrawer(!_drawerOpen);
            return;
        }
        // Esc：先关搜索覆盖层（仅覆盖层打开时接管），再关会话内查找条 —— 都不碰输入框等处 Esc 的既有行为
        if (e.Key == Key.Escape && SearchOverlay.Visibility == Visibility.Visible)
        {
            CloseSearchOverlay();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    /// <summary>搜索面板点击（最近会话行 / 搜索结果行）→ 打开会话；带 query 时定位命中处（由 ChatSearchPanel 回调）。
    /// 复用侧边栏点击会话的 LoadHistorySession 打开会话；命中词短暂高亮见 HighlightSearchTerm。</summary>
    internal void OpenSessionFromSearchPanel(string filePath, string? query)
    {
        CloseSearchOverlay();
        LoadHistorySession(filePath);
        if (!string.IsNullOrWhiteSpace(query)) HighlightSearchTerm(query);
    }

    // ── 4c：会话内搜 query 定位 + 命中词词级短暂高亮（2026-09-25 词级化）──
    // 旧方案（命中气泡整体换背景色）已删 —— 效果弱到用户以为没实现。
    // 新方案：气泡正文是只读 TextBox（承担拖选回填，不能换 TextBlock），词级高亮用 TextBox 选区实现：
    // 滚到首条命中气泡 + BubbleText.Select(首处命中) + 亮黄 SelectionBrush/深色选区字，2.5s 后清除。
    // TextBox 只有一个选区 → 只标首处命中；高亮期间用户若自己改了选区，清除时让位不再动它。
    private static readonly SolidColorBrush SearchTermHighlightBrush = new(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly SolidColorBrush SearchTermHighlightTextBrush = new(Color.FromRgb(0x1A, 0x1A, 0x1A));

    private async void HighlightSearchTerm(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        // 等消息渲染到 MessagesList（LoadHistorySession 同步填充源，但渲染需一帧）
        await Dispatcher.BeginInvoke(new Action(() => { }), DispatcherPriority.Loaded);

        await Dispatcher.BeginInvoke(new Action(() =>
        {
            var needle = query;
            for (int i = 0; i < MessagesList.Items.Count; i++)
            {
                if (MessagesList.Items[i] is not ChatBubbleViewModel vm) continue;
                var idx = string.IsNullOrEmpty(vm.Content)
                    ? -1
                    : vm.Content.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                if (MessagesList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                {
                    fe.BringIntoView();
                    if (FindBubbleTextBox(fe) is { } tb)
                    {
                        // 非焦点态默认不画选区（TextBoxBase.IsInactiveSelectionHighlightEnabled 默认 false），
                        // 跳转后焦点不在气泡上，必须打开才能看见高亮
                        tb.IsInactiveSelectionHighlightEnabled = true;
                        tb.SelectionBrush = SearchTermHighlightBrush;
                        tb.SelectionTextBrush = SearchTermHighlightTextBrush;
                        tb.Select(idx, needle.Length);
                        _ = ClearSearchHighlightAsync(tb, idx, needle.Length);
                    }
                }
                return;
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>在气泡模板可视化树里找正文 TextBox（x:Name="BubbleText"）。
    /// 不能「找第一个 TextBox」—— 思考过程（ReasoningArea）里也有 TextBox 且排在正文前面。</summary>
    private static TextBox? FindBubbleTextBox(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBox { Name: "BubbleText" } tb) return tb;
            var found = FindBubbleTextBox(child);
            if (found != null) return found;
        }
        return null;
    }

    private static async Task ClearSearchHighlightAsync(TextBox tb, int start, int length)
    {
        await Task.Delay(2500);
        // 只清我们做的那个选区；期间用户自己拖选了别处就不动，尊重用户当前选区
        if (tb.SelectionStart == start && tb.SelectionLength == length)
            tb.Select(0, 0);
    }

    // ── 4c'：会话内查找条（2026-09-25 新增，3号搜索展开态）──
    // 收起态标题栏 🔍 在对话打开时向右展开：输入即搜当前会话（InSessionFindMatcher 纯逻辑，内存即时算），
    // 计数「n/m」+ ‹ › 循环切换；当前命中用 BubbleText.Select 高亮（黄选区），查找期间不自动消失，关闭才清。
    // 与 4c 的全局搜索跳转高亮互不干扰：那边 2.5s 自动清，这边生命周期跟着查找条走。
    private List<InSessionFindHit> _findHits = [];
    private int _findIndex = -1;
    private TextBox? _lastFindTb;   // 上一处高亮的正文框（换命中/关闭时清它的选区）

    /// <summary>展开查找条（只管显隐与焦点；命中随文本变化即时重算）。</summary>
    private void OpenFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Text = "";
        _findHits = [];
        _findIndex = -1;
        UpdateFindCount();
        FindBox.Focus();
    }

    private void BtnFindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    /// <summary>收起查找条：清高亮与状态。会话切换（Activate）也会调它 —— 命中是按会话算的，换会话即失效。</summary>
    private void CloseFindBar()
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        ClearFindHighlight();
        _findHits = [];
        _findIndex = -1;
        FindBox.Text = "";
        FindCount.Text = "";
        FindBar.Visibility = Visibility.Collapsed;
    }

    private void FindBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible) return;   // 关闭时的清框不需要重算
        RefreshFindMatches();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                StepFind(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFindBar();
                e.Handled = true;
                break;
        }
    }

    private void BtnFindPrev_Click(object sender, RoutedEventArgs e) => StepFind(-1);

    private void BtnFindNext_Click(object sender, RoutedEventArgs e) => StepFind(1);

    /// <summary>重算当前会话的全部命中（输入即时触发；气泡是内存数据，无需防抖）。</summary>
    private void RefreshFindMatches()
    {
        ClearFindHighlight();
        _findHits = [];
        _findIndex = -1;
        var q = FindBox.Text;
        if (q.Length > 0 && _active != null)
            _findHits = InSessionFindMatcher.FindAll(
                _active.Bubbles.Select(b => (string?)b.Content).ToList(), q);

        if (_findHits.Count > 0)
        {
            _findIndex = 0;
            ShowCurrentFindHit();
        }
        UpdateFindCount();
    }

    /// <summary>上一个/下一个：对命中表做循环步进（-1 = 上一个）。</summary>
    private void StepFind(int direction)
    {
        if (_findHits.Count == 0) return;
        _findIndex = (_findIndex + direction + _findHits.Count) % _findHits.Count;
        ShowCurrentFindHit();
        UpdateFindCount();
    }

    /// <summary>滚到当前命中的气泡并把命中词标成黄选区。查找期间选区不自动消失（区别于 4c 的 2.5s 高亮）。</summary>
    private void ShowCurrentFindHit()
    {
        if (_active == null || _findIndex < 0 || _findIndex >= _findHits.Count) return;
        var hit = _findHits[_findIndex];
        var fe = MessagesList.ItemContainerGenerator.ContainerFromIndex(hit.BubbleIndex) as FrameworkElement;
        if (fe == null)
        {
            MessagesList.UpdateLayout();   // 兜底：容器还没生成时先逼一帧布局
            fe = MessagesList.ItemContainerGenerator.ContainerFromIndex(hit.BubbleIndex) as FrameworkElement;
        }
        if (fe == null) return;

        fe.BringIntoView();
        if (FindBubbleTextBox(fe) is { } tb)
        {
            if (_lastFindTb != null && _lastFindTb != tb) _lastFindTb.Select(0, 0);   // 换气泡时清上一处
            tb.IsInactiveSelectionHighlightEnabled = true;   // 焦点不在气泡上也要画出选区（同 4c 口径）
            tb.SelectionBrush = SearchTermHighlightBrush;
            tb.SelectionTextBrush = SearchTermHighlightTextBrush;
            tb.Select(hit.Start, FindBox.Text.Length);
            _lastFindTb = tb;
        }
    }

    /// <summary>清掉查找高亮（只清我们自己标的那个选区）。</summary>
    private void ClearFindHighlight()
    {
        _lastFindTb?.Select(0, 0);
        _lastFindTb = null;
    }

    /// <summary>计数文案：当前第 n 个/共 m 个；无关键词不显示，有关键词无命中显示 0/0。</summary>
    private void UpdateFindCount() =>
        FindCount.Text = FindBox.Text.Length == 0
            ? ""
            : $"{(_findIndex >= 0 ? _findIndex + 1 : 0)}/{_findHits.Count}";

    // ── 抽屉布局（阶段二）：宽度参数化 + 拖拽 + 跨启动记忆 ──

    private const double DrawerMinWidth = 150;   // 防拖没了
    private const double DrawerMaxWidth = 480;

    /// <summary>
    /// 标题栏两态的显隐切换（2026-09-26 用户拍板的新布局）：
    /// <list type="bullet">
    /// <item><b>展开态</b>：标题栏左半截（宽度 = 侧边栏宽度）显示「助手名 + [搜索][收起]」，
    /// 图标右对齐紧贴竖线 —— 拖列宽时整块跟着走，图标永远落在分隔线边上。右半截那组图标隐藏。</item>
    /// <item><b>收起态</b>：左半截宽度为 0 自然不可见，图标组落在对话区标题栏最左（≡ 在左、搜索在右）。</item>
    /// </list>
    /// 竖线只在展开态可见：侧边栏都不在了，没有"两个区域"要分。
    /// <para><b>为什么抽成独立方法</b>：快照 Seed（SeedSidebarForSnapshot）刻意不走 OpenDrawer（避开 180ms 动画），
    /// 若显隐逻辑写死在 OpenDrawer 里，快照里的侧边栏展开图就会缺掉整套标题栏内容。</para>
    /// </summary>
    private void ApplyHeaderLayout(bool open)
    {
        SidebarHeaderPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CollapsedHeaderPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        DrawerSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 把设置里的「AI 助手名称」刷到窗口标题两处：窗口内标题栏（TextBlock）与原生标题栏（Window.Title）。
    /// 2026-09-26 用户拍板：AI 问答窗口不再显示「AI 问答 / AI 翻译」这种模式名，一律跟随助手名 ——
    /// 用户自定义后窗口内标题栏、任务栏、Alt-Tab 一起变。留空回退默认名。
    /// </summary>
    private void ApplyAssistantName()
    {
        var name = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName.Trim();
        TitleText.Text = name;
        Title = name;
    }

    /// <summary>展开/收起抽屉（"历史"按钮与抽屉内收起按钮共用）。
    /// 动画仍作用于 Sidebar.Width（铁律：不动 ColumnDefinition）；展开宽度 = 设置记忆宽度（240 参数化）。
    /// 收起状态不记忆——下次展开仍用记忆宽度。</summary>
    private void OpenDrawer(bool open)
    {
        _drawerOpen = open;
        ApplyHeaderLayout(open);
        DrawerSplitter.IsEnabled = false; // 动画期间禁用拖拽，避免与动画打架（铁律 2）
        if (open)
        {
            Sidebar.MinWidth = 0;   // 动画从 0 长到目标，MinWidth 边界动画结束后恢复
            RefreshDrawer();
        }
        else
        {
            Sidebar.MinWidth = 0;   // 收到 0 需先解除 MinWidth 顶住
        }

        var target = open ? Math.Clamp(_settings.AiDrawerWidth, DrawerMinWidth, DrawerMaxWidth) : 0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // 2026-09-21 修复"抽屉拖不动"：BeginAnimation 默认 HoldEnd —— 动画播完后仍占着 Width 属性，
            // 之后 DragDelta 对 Width 的赋值全被它覆盖，拖拽形同虚设（用户反馈：有双箭头但拖不动）。
            // 先落一个本地值再解除占用，属性不会回落。收起后输入区左下圆角恢复（抽屉不在了，窗口角归它）。
            Sidebar.Width = target;
            Sidebar.BeginAnimation(WidthProperty, null);
            InputArea.CornerRadius = _drawerOpen ? new CornerRadius(0, 0, 0, 6) : new CornerRadius(0, 0, 6, 6);
            if (_drawerOpen) Sidebar.MinWidth = DrawerMinWidth;
            DrawerSplitter.IsEnabled = true;
        };
        Sidebar.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>抽屉宽度拖拽：直接改 Sidebar.Width（不改 ColumnDefinition，铁律 2），有边界。</summary>
    private void DrawerSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_drawerOpen) return;
        Sidebar.Width = Math.Clamp(Sidebar.ActualWidth + e.HorizontalChange, DrawerMinWidth, DrawerMaxWidth);
    }

    /// <summary>拖拽结束：宽度记忆（收起状态不记忆）</summary>
    private void DrawerSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_drawerOpen || Sidebar.Width <= 0) return;
        _settings.AiDrawerWidth = Sidebar.Width;
        _settings.Save();
    }

    /// <summary>刷新抽屉列表 + 分组视图（启动下拉/操作完成后宿主调用，数据变更事件也会调）</summary>
    private void RefreshDrawer()
    {
        if (_drawerOpen)
        {
            Sidebar.Load(ChatSessionService.ListSessions(), ChatGroupStore.Load());
            // 昵称与头像跟着设置走：在设置里改完，下次刷新侧边栏就同步（不必重开窗口）
            Sidebar.SetUser(_settings.ChatUserNickname, ChatAssetsService.LoadUserAvatar());
        }
        // 分组视图与侧边栏的显隐**无关**（侧边栏收起时它照样占着主区）——
        // 以前这两件事被同一个 `if (!_drawerOpen) return` 捆着，导致「收起侧边栏后停在分组里」
        // 时新建/删会话看不到变化。RefreshGroupView 自己在不在分组视图时会短路，不受影响。
        RefreshGroupView();
    }

    /// <summary>同步周期完成后的外部刷新入口（MainWindow 经 AIDialogHelper 调用，拉到新会话立即上列表）</summary>
    internal void RefreshDrawerIfOpen() => RefreshDrawer();

    /// <summary>
    /// 会话 / 分组数据变更 → 刷新侧边栏（2026-10-01 用户要求"实时更新"）。
    ///
    /// 为什么必须**合并**：事件可能在后台线程触发（Agent 工具落盘、批量删分组会逐个会话 Save，几十次），
    /// 每次都全量重扫磁盘会让界面卡；这里用一个"已排队"标志把同一批事件收成一次刷新，
    /// 排在 Background 优先级 —— 等界面把手头的事做完再跑。
    ///
    /// 收起态不刷是既有的正确行为（列表根本不在图上），由 RefreshDrawer 自己的短路兜住；
    /// 这里不重复判断，免得同一条规则两处各写一半、将来改一处漏一处。
    /// </summary>
    private void OnChatDataChanged()
    {
        if (_closed || _drawerRefreshPending) return;
        _drawerRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _drawerRefreshPending = false;
            if (!_closed) RefreshDrawerIfOpen();
        }), DispatcherPriority.Background);
    }

    // ── 历史会话管理（阶段二）：条目操作 / 批量操作 / 分组管理 / 回收站 ──

    /// <summary>条目操作（三个点菜单）。改字段统一走"内存会话优先"：改的就是当前打开的会话时
    /// 直接改内存字段（否则文件与内存漂移），否则 Load 文件改后 Save。</summary>
    private void HandleItemAction(HistoryItemViewModel item, ChatItemAction action, string? context)
    {
        switch (action)
        {
            // 批量操作（多选）入口随旧抽屉一起退场：新侧边栏是单条操作。
            // 枚举值 ChatItemAction.BatchStart 保留 —— 不为一个不再发出的动作去动公共枚举。

            case ChatItemAction.Rename:
            {
                // 初始值带当前标题（2026-09-24 用户要求：保留原名并全选，可整体替换也可局部改，
                // 而不是让用户看着空框从零打）。留空确认 = 恢复默认预览的语义保留。
                var name = PromptDialog.Show(this, "重命名会话", "会话标题（留空恢复默认预览）：", item.Title);
                if (name == null) return;
                ApplySessionMeta(item, s => s.Title = name);
                RefreshDrawer();
                break;
            }

            case ChatItemAction.TogglePin:
                ApplySessionMeta(item, s => s.Pinned = !s.Pinned);
                RefreshDrawer();
                break;

            case ChatItemAction.Group:
                ApplySessionMeta(item, s => s.GroupId = context ?? "");
                RefreshDrawer();
                break;

            // 收藏与分组共用 GroupId 一个字段 —— 收藏就是内置保留分区。
            // 所以「收藏」= 把归属改成收藏；「取消收藏」= 回到未分组。
            // 归属互斥是用户定的语义：一条会话要么在某分组、要么在收藏，不会同时在两边。
            case ChatItemAction.Favorite:
                ApplySessionMeta(item, s => s.GroupId = ChatGroupStore.IsFavorite(s.GroupId)
                    ? ""
                    : ChatGroupStore.FavoriteId);
                RefreshDrawer();
                break;

            case ChatItemAction.Export:
                if (Enum.TryParse<ExportFormat>(context, out var fmt))
                    ExportOne(item, fmt);
                break;

            case ChatItemAction.Delete:
                if (DeleteSessions([item])) RefreshDrawer();   // 确认删除才刷（取消时列表纹丝不动）
                break;
        }
    }

    // HandleBatchAction 已删除（2026-09-23）：批量操作入口随旧抽屉退场，新侧边栏是单条操作。
    // 它用到的 DeleteSessions / ExportMany 都还在（单条删除与单条导出走它们），功能没丢。

    /// <summary>新建分组（侧边栏「新分组」）。查重与落盘统一走 ChatGroupService，与其它入口行为一致。</summary>
    private void HandleNewGroup()
    {
        var name = PromptDialog.Show(this, "新建分组", "分组名称：");
        if (string.IsNullOrWhiteSpace(name)) return;

        ChatGroupService.Create(name, out var result);
        if (result == ChatGroupService.CreateResult.NameExists)
        {
            MessageBox.Show(this, "已存在同名分组", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshDrawer();
    }

    /// <summary>点侧边栏里的某个分组（收藏也走这里）→ 主区切到分组视图。
    /// 2026-09-26 用户改版：进分组**不再收起侧边栏** —— 分组详情直接在原对话区展示（对标千问布局），
    /// 侧边栏保持展开，随时可点别的分组/会话（2026-09-24「进分组就让内容区最大化」的旧拍板被本条推翻）。</summary>
    private void HandleGroupSelected(SidebarGroupHeader group)
    {
        OpenGroupView(group.GroupId, group.Name);
    }

    /// <summary>分组三点菜单：重命名 / 置顶此分组 / 删除此分组</summary>
    private void HandleGroupAction(SidebarGroupHeader group, GroupMenuAction action)
    {
        switch (action)
        {
            case GroupMenuAction.Rename:
            {
                var name = PromptDialog.Show(this, "重命名分组", "新名称：", group.Name);
                if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;
                if (!ChatGroupService.Rename(group.GroupId, name, out var error) && error.Length > 0)
                    MessageBox.Show(this, error, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            }

            // 「置顶此分组」= **分组本身**浮到分组列表最前（ChatGroup.Pinned，2026-09-24 改语义）。
            // 旧实现（组内会话全部置顶）在空分组上零反馈，用户实测"点了没反应"；
            // 且分组行不动，有会话也看不出效果。菜单文案按 group.IsPinned 动态（侧边栏生成）。
            case GroupMenuAction.Pin:
                ChatGroupService.SetPinned(group.GroupId, !group.IsPinned);
                break;

            case GroupMenuAction.Delete:
            {
                if (MessageBox.Show(this,
                        $"删除分组「{group.Name}」？组内会话将回到未分组（会话本身不删除）。",
                        "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                ChatGroupService.DeleteGroup(group.GroupId, out _);
                break;
            }
        }
        RefreshDrawer();
    }

    // ── 分组视图（2026-09-23）：点侧边栏里的分组进来 ──

    private string _activeGroupId = "";   // 当前打开的分组（空 = 不在分组视图里）

    /// <summary>
    /// 打开分组视图：主区切成该分组的会话列表。
    /// 输入区**不动** —— 在分组视图里输入就是在该分组下新建会话，
    /// 这正是用户要的"开对话之前先选好分组"，而不是对话完了再归类。
    /// </summary>
    private void OpenGroupView(string groupId, string groupName)
    {
        ExitGroupBatchMode();   // 切分组 = 换一批条目，旧分组的批量勾选没有保留意义（不在批量态时是 no-op）
        _activeGroupId = groupId;

        var isFavorite = ChatGroupStore.IsFavorite(groupId);
        GroupViewTitle.Text = isFavorite ? "★  " + groupName : groupName;

        var instruction = ChatGroupService.GetInstruction(groupId);
        // 指令默认**折叠**（2026-10-01 用户要求）：每次进分组都收起，要看点「查看指令」。
        // 刻意每次 OpenGroupView 都复位，而不是记住上次的展开态 —— 用户明确要的是「默认隐藏」。
        SetGroupInstructionPanel(instruction, expanded: false);

        // 收藏是**视图**不是真分组：不能改名 / 删除 / 加指令。按钮直接隐藏，不给"点了没反应"的假入口。
        var editable = isFavorite ? Visibility.Collapsed : Visibility.Visible;
        BtnGroupInstruction.Visibility = editable;
        BtnGroupRename.Visibility = editable;
        BtnGroupDelete.Visibility = editable;

        GroupViewPanel.Visibility = Visibility.Visible;
        MessagesScroll.Visibility = Visibility.Collapsed;

        RefreshGroupView();
        RefreshComposerLayout();   // 快照实测抓到：进分组视图后欢迎语还挂着、输入框还居中 ——
                                   // 分组视图的欢迎语语义不成立（它的空态提示是"说第一句"那行字），布局必须重算
        InputBox.Focus();
    }

    /// <summary>退出分组视图，回到普通对话。退出分组靠侧边栏切换或发消息（2026-09-24 已去掉「返回对话」按钮）</summary>
    private void CloseGroupView()
    {
        if (_activeGroupId.Length == 0) return;
        ExitGroupBatchMode();                      // 批量态是分组视图的一部分，一起清（内部重刷一次，随后整块隐藏）
        _activeGroupId = "";
        ExitGroupSearch();                      // 搜索态是分组视图的一部分，一起清掉
        GroupViewPanel.Visibility = Visibility.Collapsed;
        MessagesScroll.Visibility = Visibility.Visible;
    }

    /// <summary>刷新分组视图的会话列表（只列本分组；顺序由 ListSessions 保证 = 置顶优先 → 时间倒序）</summary>
    private void RefreshGroupView()
    {
        if (_activeGroupId.Length == 0) return;

        var items = ChatSessionService.ListSessions()
            .Where(s => string.Equals(s.GroupId, _activeGroupId, StringComparison.Ordinal))
            .Select(s => new HistoryItemViewModel(
                s.Id, s.FilePath, s.SavedAt, AiModeText.Get(s.Mode),
                string.IsNullOrWhiteSpace(s.Title) ? s.Preview : s.Title,
                s.Pinned, s.GroupId, "", s.Preview))
            .ToList();

        // 批量态必须挂回新建的 VM：复选框显隐（IsBatchMode）与勾选（IsSelected）都靠它 ——
        // 漏了这里，批量中来一次刷新（重命名/置顶/同步拉取都会刷）勾选框就整片消失。
        foreach (var vm in items)
        {
            vm.IsBatchMode = _groupBatchMode;
            vm.IsSelected = _groupBatchSelected.Contains(vm.Id);
        }

        GroupSessionList.ItemsSource = items;
        // 文案一并恢复 —— 搜索态会把这条提示改成"没有匹配…"，不清回去的话
        // 清空搜索框后空分组会显示上一轮的搜索提示
        GroupSessionEmptyHint.Text = "这个分组还没有会话 —— 在下面输入框里说第一句，就会开始一个属于它的话题。";
        GroupSessionEmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 选择分组（2026-09-24 补做，对标 WorkBuddy 的「选择工作空间」）──
    // 起手态：选中的分组存 _pendingGroupId，发第一句时会话直接建在该分组；
    // 对话态：显示当前会话的分组，点击可移动归属。

    private string _pendingGroupId = "";   // 起手态选定的目标分组（空 = 未分组）

    private void BtnPickGroup_Click(object sender, RoutedEventArgs e)
    {
        // 刻意用 new ContextMenu()：对象初始化器自带的 Style 会顶掉深色模板（弹出层变白条，踩过）
        var menu = new ContextMenu();

        var currentId = ResolveCurrentGroupId();
        var none = new MenuItem { Header = "未分组", IsChecked = currentId.Length == 0 };
        none.Click += (_, _) => ApplyPickGroup("");
        menu.Items.Add(none);

        foreach (var g in ChatGroupStore.Load().Where(g => !ChatGroupStore.IsFavorite(g.Id)))
        {
            var targetId = g.Id;
            var item = new MenuItem { Header = g.Name, IsChecked = g.Id == currentId };
            item.Click += (_, _) => ApplyPickGroup(targetId);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = BtnPickGroup;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>当前"该显示哪个分组"：对话态取活跃会话的归属；起手态取起手选择</summary>
    private string ResolveCurrentGroupId()
    {
        if (_active is { Bubbles.Count: > 0 }) return _active.Session.GroupId;
        return _pendingGroupId;
    }

    private void ApplyPickGroup(string groupId)
    {
        // 对话态 = 移动当前会话归属（立即生效并落盘）；起手态 = 记住，第一句话时归组
        if (_active is { Bubbles.Count: > 0 })
        {
            _active.Session.GroupId = groupId;
            _active.Session.Save();
            RefreshDrawer();
        }
        else
        {
            _pendingGroupId = groupId;
        }
        RefreshPickGroupLabel();
    }

    /// <summary>刷新「选择分组」按钮的文案。挂在发送/切换会话/退出分组视图等状态变化点上。</summary>
    private void RefreshPickGroupLabel()
    {
        var id = ResolveCurrentGroupId();
        PickGroupLabel.Text = id.Length == 0
            ? "选择分组"
            : ChatGroupService.GetGroupName(id) is { Length: > 0 } name ? name : "选择分组";
    }

    // ── 分组内搜索（2026-09-24 补做：用户指出漏了；范围=本分组，含消息正文全文）──
    // 底层走 ChatSearchService（快层 13 条 + 慢层检查点已守），这里只做 UI 接线：
    // 输入防抖 280ms → 后台线程搜索 → 回 UI 线程渲染。单轮搜索全量读盘，严禁每次按键都同步跑。

    private CancellationTokenSource? _groupSearchCts;

    private void BtnGroupSearch_Click(object sender, RoutedEventArgs e)
    {
        GroupToolbar.Visibility = Visibility.Collapsed;
        GroupSearchBar.Visibility = Visibility.Visible;
        GroupSearchBox.Focus();
    }

    private void BtnGroupSearchClose_Click(object sender, RoutedEventArgs e)
    {
        ExitGroupSearch();
        RefreshGroupView();
    }

    /// <summary>退出搜索态：清框、恢复工具行（CloseGroupView 与搜索关闭按钮共用）</summary>
    private void ExitGroupSearch()
    {
        _groupSearchCts?.Cancel();
        GroupSearchBox.Text = "";
        GroupSearchPlaceholder.Visibility = Visibility.Visible;
        GroupSearchBar.Visibility = Visibility.Collapsed;
        GroupToolbar.Visibility = Visibility.Visible;
    }

    private void GroupSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        GroupSearchPlaceholder.Visibility = string.IsNullOrEmpty(GroupSearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        _ = RunGroupSearchAsync(GroupSearchBox.Text);
    }

    private async Task RunGroupSearchAsync(string query)
    {
        var groupId = _activeGroupId;
        if (groupId.Length == 0) return;

        _groupSearchCts?.Cancel();
        var cts = _groupSearchCts = new CancellationTokenSource();
        var token = cts.Token;

        if (string.IsNullOrWhiteSpace(query))
        {
            RefreshGroupView();   // 清空搜索框 = 回到该分组的完整列表
            return;
        }

        try { await Task.Delay(280, token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }

        IReadOnlyList<ChatSearchHit> hits;
        try
        {
            hits = await Task.Run(() => ChatSearchService.Search(query, groupId), token);
        }
        catch (OperationCanceledException) { return; }

        if (token.IsCancellationRequested || _activeGroupId != groupId) return;
        // 丢弃返回值（DispatcherOperation 可等待，不丢弃会吃 CS4014 警告）；结果不需要等待
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_activeGroupId != groupId) return;
            RenderGroupSearchResults(hits, groupId, query);
        }));
    }

    /// <summary>渲染分组搜索命中。**异步接线（RunGroupSearchAsync）与快照 Seeder 共用同一口径** ——
    /// 改这里两处同时生效：命中渲染成与正常列表同标准的条目（展示规格一致）；
    /// 片段放在预览位（悬停可见），标题仍是会话标题。</summary>
    private void RenderGroupSearchResults(IReadOnlyList<ChatSearchHit> hits, string groupId, string query)
    {
        var items = hits.Select(h => new HistoryItemViewModel(
            h.SessionId, h.FilePath, h.SavedAt, "",
            string.IsNullOrWhiteSpace(h.Title) ? h.Preview : h.Title,
            false, groupId, h.Snippet, h.Snippet)).ToList();
        // 搜索结果也走同一套批量态（搜索中进入/退出批量、勾选都必须显示正确）
        foreach (var vm in items)
        {
            vm.IsBatchMode = _groupBatchMode;
            vm.IsSelected = _groupBatchSelected.Contains(vm.Id);
        }
        GroupSessionList.ItemsSource = items;
        GroupSessionEmptyHint.Text = hits.Count == 0
            ? $"没有匹配「{query.Trim()}」的会话。"
            : "";
        GroupSessionEmptyHint.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GroupSessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item }) return;

        // 点在行首复选框上：放行给它自己处理（勾选状态由 CheckBox 绑定翻转，
        // 走到这里再翻一次会把状态翻回去 —— 同 ChatSidebar.FromBatchCheck 一条防线）
        if (ChatSidebar.FromBatchCheck(e.OriginalSource)) return;

        // 批量模式下点会话 = 选中/取消（不再打开会话）；平时点 = 打开该会话
        if (_groupBatchMode)
        {
            item.IsSelected = !item.IsSelected;
            SyncGroupBatchPick(item);
            return;
        }
        CloseGroupView();                       // 打开某条会话 = 退出分组视图，回到对话
        LoadHistorySession(item.FilePath);
    }

    // ── 分组内会话三点菜单（2026-09-26 用户要求对标千问，收藏视图同套）──
    // 项：重命名 / 置顶此对话 / 批量管理 / 移动到分组▸ / 导出对话▸ / 删除对话。
    // 除「移动到分组」外全部沿用未分组三点的 HandleItemAction 逻辑 —— 不在这里另起一套业务。

    /// <summary>分组视图会话行的三点按钮</summary>
    private void GroupSessionMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item } btn) return;

        // 刻意用 new ContextMenu() 而不是对象初始化器：自带 Style 会顶掉 App.xaml 深色模板出白条（项目已踩过）
        var menu = new ContextMenu();

        menu.Items.Add(MakeGroupSessionItem(item, ChatItemAction.Rename, "重命名"));
        menu.Items.Add(MakeGroupSessionItem(item, ChatItemAction.TogglePin,
            item.Pinned ? "取消置顶" : "置顶此对话"));

        // 批量管理：进多选（行首出复选框、底部出操作条）
        var batch = new MenuItem { Header = "批量管理" };
        batch.Click += (_, _) => EnterGroupBatchMode();
        menu.Items.Add(batch);

        // 移动到分组 ▸：与批量条的「移动到分组」共用同一构建器（现有分组 + 移出本组/移出收藏 + 新增分组）
        menu.Items.Add(BuildMoveToGroupMenu([item], item.GroupId));

        // 导出对话 ▸（格式清单与侧边栏共用 ChatSidebar.ExportFormats，别两处各列一套）
        var exportMenu = new MenuItem { Header = "导出对话" };
        foreach (var (formatName, format) in ChatSidebar.ExportFormats)
        {
            var menuItem = new MenuItem { Header = formatName };
            var captured = format;
            menuItem.Click += (_, _) => HandleItemAction(item, ChatItemAction.Export, captured.ToString());
            exportMenu.Items.Add(menuItem);
        }
        menu.Items.Add(exportMenu);

        var deleteItem = new MenuItem
        {
            Header = "删除对话",
            Foreground = ChatThemeService.Brush("Chat_E08080", _settings.ChatShade),
        };
        deleteItem.Click += (_, _) => HandleItemAction(item, ChatItemAction.Delete, null);
        menu.Items.Add(deleteItem);

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>分组视图菜单里的普通项：动作统一交回 HandleItemAction（与未分组三点同一条业务链路）</summary>
    private MenuItem MakeGroupSessionItem(HistoryItemViewModel item, ChatItemAction action, string header, string? context = null)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => HandleItemAction(item, action, context);
        return menuItem;
    }

    /// <summary>
    /// 「移动到分组」子菜单（2026-09-26 千问对标，单条与批量共用）：
    /// 现有各分组（含「收藏」，点即移入）→ 分隔线 → 移出本组 / 移出收藏（回未分组）→ ＋ 新增分组。
    /// currentGroupId 非空时给当前命中项打勾；批量移动不打勾（选中项可能不在同一分组）。
    /// </summary>
    private MenuItem BuildMoveToGroupMenu(IReadOnlyList<HistoryItemViewModel> targets, string currentGroupId)
    {
        var menu = new MenuItem { Header = "移动到分组" };
        var groups = ChatGroupStore.Load();

        // 普通分组在前、收藏垫底（千问口径：列表末尾是收藏；我们数据层收藏与分组同字段，一样能列）
        foreach (var g in groups.Where(g => !ChatGroupStore.IsFavorite(g.Id)))
        {
            var targetId = g.Id;
            var menuItem = new MenuItem { Header = g.Name, IsChecked = g.Id == currentGroupId };
            menuItem.Click += (_, _) => MoveSessionsTo(targets, targetId);
            menu.Items.Add(menuItem);
        }
        var favorite = groups.FirstOrDefault(g => ChatGroupStore.IsFavorite(g.Id));
        if (favorite != null)
        {
            var favItem = new MenuItem { Header = "收藏", IsChecked = favorite.Id == currentGroupId };
            var favId = favorite.Id;
            favItem.Click += (_, _) => MoveSessionsTo(targets, favId);
            menu.Items.Add(favItem);
        }

        menu.Items.Add(new Separator());

        // 移出本组 / 移出收藏：回到**未分组**（用户拍板：移出后恢复未分组状态）
        var current = currentGroupId.Length > 0 ? currentGroupId : targets.FirstOrDefault()?.GroupId ?? "";
        if (current.Length > 0)
        {
            var remove = new MenuItem
            {
                Header = ChatGroupStore.IsFavorite(current) ? "移出收藏" : "移出本组",
            };
            remove.Click += (_, _) => MoveSessionsTo(targets, "");
            menu.Items.Add(remove);
        }

        // 新增分组：想移到一个还不存在的分组时用（建完直接把选中会话移进去）
        var add = new MenuItem { Header = "＋ 新增分组" };
        add.Click += (_, _) => CreateGroupAndMove(targets);
        menu.Items.Add(add);

        return menu;
    }

    /// <summary>把（些）会话移动到目标分组（""= 未分组），随后重刷并退出批量态</summary>
    private void MoveSessionsTo(IReadOnlyList<HistoryItemViewModel> targets, string groupId)
    {
        foreach (var item in targets)
            ApplySessionMeta(item, s => s.GroupId = groupId);
        RefreshDrawer();            // 连带 RefreshGroupView：被移出的条目从当前分组列表消失
        ExitGroupBatchMode();       // 移动完成 = 本轮批量结束（不在批量态时是 no-op）
    }

    /// <summary>「＋ 新增分组」：建组并把选中会话移进去。
    /// 同名只提示、**不**把会话静默塞进老分组 —— 与侧边栏「新建分组」同一条纪律（REGRESSION B-21）。</summary>
    private void CreateGroupAndMove(IReadOnlyList<HistoryItemViewModel> targets)
    {
        var name = PromptDialog.Show(this, "新增分组", "分组名称：");
        if (string.IsNullOrWhiteSpace(name)) return;

        var group = ChatGroupService.Create(name, out var result);
        if (result == ChatGroupService.CreateResult.NameExists)
        {
            MessageBox.Show(this, "已存在同名分组", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (group != null) MoveSessionsTo(targets, group.Id);
    }

    // ── 分组视图批量管理（2026-09-26 用户要求 1:1 复刻千问批量管理）──
    // 行首复选框 + 底部操作条（全选/已选计数 | 取消 / 移动到分组 / 删除）。
    // 与侧边栏批量互斥（进这边先退那边，反向亦然）；计数不设上限（用户拍板「已选 n」）。

    private bool _groupBatchMode;
    private readonly HashSet<string> _groupBatchSelected = new(StringComparer.Ordinal);

    /// <summary>进入分组批量多选（行三点「批量管理」）</summary>
    private void EnterGroupBatchMode()
    {
        if (_activeGroupId.Length == 0) return;
        Sidebar.ExitBatchMode();               // 两处批量互斥（侧边栏入口进来时也已反向退出）
        _groupBatchMode = true;
        _groupBatchSelected.Clear();
        GroupBatchAll.IsChecked = false;
        GroupBatchCount.Text = "已选 0";
        GroupBatchBar.Visibility = Visibility.Visible;
        RefreshGroupView();                    // 重建 VM 并挂上 IsBatchMode / IsSelected
    }

    /// <summary>退出分组批量多选（取消按钮 / 移动完成 / 切分组 / 退出分组视图共用）</summary>
    private void ExitGroupBatchMode()
    {
        if (!_groupBatchMode) return;
        _groupBatchMode = false;
        _groupBatchSelected.Clear();
        GroupBatchAll.IsChecked = false;
        GroupBatchCount.Text = "已选 0";
        GroupBatchBar.Visibility = Visibility.Collapsed;
        RefreshGroupView();
    }

    /// <summary>行首复选框点击：勾选状态已由 IsChecked↔IsSelected 双向绑定翻好，这里只同步集合与计数</summary>
    private void GroupBatchCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item }) return;
        SyncGroupBatchPick(item);
    }

    /// <summary>按条目当前 IsSelected 同步进集合（行点击与勾选框共用）</summary>
    private void SyncGroupBatchPick(HistoryItemViewModel item)
    {
        if (item.IsSelected) _groupBatchSelected.Add(item.Id);
        else _groupBatchSelected.Remove(item.Id);
        GroupBatchCount.Text = $"已选 {_groupBatchSelected.Count}";
    }

    /// <summary>全选勾选框：全选/清空**当前列表**（搜索态即搜索结果）。Click 在 toggle 后引发，IsChecked 已是新值。</summary>
    private void GroupBatchAll_Click(object sender, RoutedEventArgs e)
    {
        var all = GroupListItems();
        var pick = GroupBatchAll.IsChecked == true;
        _groupBatchSelected.Clear();
        if (pick)
            foreach (var vm in all) _groupBatchSelected.Add(vm.Id);
        foreach (var vm in all) vm.IsSelected = pick;
        GroupBatchCount.Text = $"已选 {_groupBatchSelected.Count}";
    }

    private void GroupBatchCancel_Click(object sender, RoutedEventArgs e) => ExitGroupBatchMode();

    /// <summary>批量条「移动到分组」：弹与行三点完全一致的子菜单（分组列表 + 移出本组 + 新增分组）</summary>
    private void GroupBatchMove_Click(object sender, RoutedEventArgs e)
    {
        var picked = GroupPickedItems();
        if (picked.Count == 0) return;
        if (sender is not FrameworkElement btn) return;

        var menu = new ContextMenu { Items = { BuildMoveToGroupMenu(picked, "") } };
        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
        // 不挂 Closed 强退：打开菜单又关掉不该丢勾选；真移动由 MoveSessionsTo 收尾
    }

    /// <summary>批量条「删除」：确认框点「是」才删并退出批量（点「否」勾选保留）</summary>
    private void GroupBatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var picked = GroupPickedItems();
        if (picked.Count == 0) return;
        if (!DeleteSessions(picked)) return;
        ExitGroupBatchMode();
        RefreshDrawer();
    }

    /// <summary>当前分组列表里的可见条目（批量操作只对看得见的条目生效）</summary>
    private List<HistoryItemViewModel> GroupListItems() =>
        (GroupSessionList.ItemsSource as IEnumerable<HistoryItemViewModel>)?.ToList() ?? [];

    /// <summary>选中且仍在当前列表里的条目</summary>
    private List<HistoryItemViewModel> GroupPickedItems() =>
        GroupListItems().Where(vm => _groupBatchSelected.Contains(vm.Id)).ToList();

    /// <summary>
    /// 指令面板三处状态一处管（2026-10-01 用户要求）：文本内容 + 展开与否 + 按钮文案。
    /// 收敛成一个方法是因为三者必须同步 —— 散开写就会出现「按钮写着查看、其实已经展开」这类对不上。
    /// 无指令 → 按钮「添加指令」（点了直接进弹窗）；有指令 → 「查看指令 / 收起指令」（点了切换只读区）。
    /// </summary>
    private void SetGroupInstructionPanel(string instruction, bool expanded)
    {
        var hasInstruction = instruction.Length > 0;
        var shown = expanded && hasInstruction;

        GroupInstructionText.Text = hasInstruction ? "分组指令：" + instruction : "";
        GroupInstructionPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        // 文案由纯函数给（ChatListRules.InstructionButtonText）—— 三态对应三个文案，快层检查点守着它
        BtnGroupInstruction.Content = ChatListRules.InstructionButtonText(hasInstruction, shown);
    }

    /// <summary>点「添加指令」直接进编辑弹窗；已有指令时点「查看指令」只展开只读区（再点收起）</summary>
    private void BtnGroupInstruction_Click(object sender, RoutedEventArgs e)
    {
        var instruction = ChatGroupService.GetInstruction(_activeGroupId);
        if (instruction.Length == 0)
        {
            EditGroupInstruction();     // 还没指令 → 直接进弹窗
            return;
        }
        // 已有指令 → 只切换只读区显隐，不直接进弹窗（用户要求「先查看，再决定改不改」）
        SetGroupInstructionPanel(instruction, GroupInstructionPanel.Visibility != Visibility.Visible);
    }

    /// <summary>展开区里的「编辑」按钮 → 进编辑弹窗</summary>
    private void BtnGroupInstructionEdit_Click(object sender, RoutedEventArgs e) => EditGroupInstruction();

    /// <summary>添加 / 修改分组指令（多行输入）。保存后展开只读区，让用户看到结果</summary>
    private void EditGroupInstruction()
    {
        var current = ChatGroupService.GetInstruction(_activeGroupId);
        var text = PromptDialog.Show(this, current.Length > 0 ? "编辑指令" : "添加指令",
            "在这个分组里，你说的话默认是对谁说的？\n" +
            "例：我的一切指令默认对象都是得到大脑（于是你说「上传笔记」即可，不必每次带「到得到大脑」）。\n" +
            "优先级：它可以**覆盖系统的默认行为**（例如「写操作前先问一句」这类默认习惯），\n" +
            "但**不能覆盖安全红线**（不许谎报成功、不许泄露密钥、不许让用户去敲命令行、不可恢复的删除等），冲突时一律以红线为准。",
            current, multiline: true);
        if (text == null) return;               // 取消

        ChatGroupService.SetInstruction(_activeGroupId, text);
        RefreshDrawer();
        OpenGroupView(_activeGroupId, ChatGroupService.GetGroupName(_activeGroupId));   // 重刷标题
        // 保存后展开（OpenGroupView 会复位成折叠）—— 否则「弹窗确定完什么都没变」，像没保存上
        SetGroupInstructionPanel(ChatGroupService.GetInstruction(_activeGroupId), expanded: true);
    }

    private void BtnGroupRename_Click(object sender, RoutedEventArgs e)
    {
        var current = ChatGroupService.GetGroupName(_activeGroupId);
        var name = PromptDialog.Show(this, "编辑分组名", "新名称：", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;

        if (!ChatGroupService.Rename(_activeGroupId, name, out var error))
        {
            if (error.Length > 0)
                MessageBox.Show(this, error, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshDrawer();
        OpenGroupView(_activeGroupId, name);
    }

    private void BtnGroupDelete_Click(object sender, RoutedEventArgs e)
    {
        var groupId = _activeGroupId;
        var name = ChatGroupService.GetGroupName(groupId);
        if (name.Length == 0) { CloseGroupView(); return; }

        if (MessageBox.Show(this,
                $"删除分组「{name}」？组内会话将回到未分组（会话本身不删除）。",
                "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        ChatGroupService.DeleteGroup(groupId, out _);
        CloseGroupView();
        RefreshDrawer();
    }

    // ── 快照专用（只在 --snapshot 分支调用，正常启动不受影响；与其它 Seed*ForSnapshot 同套做法）──

    private string _snapshotGroupId = "";   // 快照造出来的分组 Id（分组视图那张图要用）

    /// <summary>
    /// 快照：造几条会话与分组并展开侧边栏。
    /// 沙箱里一个会话都没有 —— 不塞数据这张图只有"新对话 / 新分组"两个按钮，等于没验。
    /// 刻意不走 OpenDrawer（180ms 动画）：快照要的是稳定终态，不是动画中间帧。
    /// </summary>
    internal void SeedSidebarForSnapshot()
    {
        // 每个快照场景都 new 一个窗口，但**沙箱是同一个** —— 不清掉上一个场景造的数据，
        // 第二张图里就会出现两套一模一样的会话（实测踩过：10c 的侧边栏里每条都重复了一遍，
        // 而且"得到大脑"撞名建不出来 → 分组视图压根没打开，图上看不出这个错）。
        // 只在沙箱里清：快照工具启动时把 RootOverride 指向临时目录，这里再加一道判断兜底。
        if (!string.IsNullOrEmpty(FocusCapturePaths.RootOverride))
        {
            var chatDir = FocusCapturePaths.Combine("chat_history");
            if (Directory.Exists(chatDir))
                foreach (var file in Directory.EnumerateFiles(chatDir, "*.json")) File.Delete(file);

            var groupsFile = FocusCapturePaths.Combine("chat_groups.json");
            if (File.Exists(groupsFile)) File.Delete(groupsFile);
        }

        var devGroup = ChatGroupService.Create("项目开发", out _);
        var brainGroup = ChatGroupService.Create("得到大脑", out _);
        if (brainGroup != null)
        {
            ChatGroupService.SetInstruction(brainGroup.Id, "我的一切指令默认对象都是得到大脑");
            _snapshotGroupId = brainGroup.Id;
        }

        void MakeSession(string text, bool pinned, string? groupId)
        {
            var svc = new ChatSessionService(ExplainMode.Ask);
            svc.AddUser(text);
            svc.AddAssistant("好的，记下了。");
            if (!string.IsNullOrEmpty(groupId)) svc.GroupId = groupId;
            svc.Pinned = pinned;
            svc.Save();
        }

        // 覆盖三个分区：最近（未分组）/ 置顶（跨分组）/ 分组内
        MakeSession("帮我整理一下这周的待办", false, null);
        MakeSession("灵感：银发经济的专题要不要做", false, null);
        MakeSession("把这份资料归档到项目里", true, devGroup?.Id);
        MakeSession("上传笔记", false, brainGroup?.Id);

        // 只发文件、不打字的会话（2026-10-01 优化2）：不塞这一条，快照里就看不见「文件：<文件名>」这档标题 ——
        // 而"标题显示成空白行"正是用户实测到的那个问题。图上没有 = 没守护，所以必须造出来。
        var fileOnly = new ChatSessionService(ExplainMode.Ask);
        fileOnly.AddUser("", new List<ChatAttachment>
        {
            new()
            {
                FileName = "季度报告.pdf", StoredName = "snapshot-quarterly.pdf",
                Kind = ChatAttachmentKind.Document, SizeBytes = 1024,
            },
        });
        fileOnly.AddAssistant("收到，已经看到这份文件了。");
        fileOnly.Save();

        _settings.ChatUserNickname = "彭杰";

        // 头像圆形裁剪（2026-09-26）：给沙箱造一张**长方形**头像并导入 ——
        // 不塞图的话快照里只有"昵称首字色块"那一支，圆形裁剪根本不在图上，等于没守护。
        TrySeedAvatarForSnapshot();

        _drawerOpen = true;
        ApplyHeaderLayout(true);   // 快照不走 OpenDrawer（避开动画），标题栏两态的显隐必须在这里补上
        Sidebar.Width = 220;
        Sidebar.MinWidth = DrawerMinWidth;
        RefreshDrawer();
    }

    /// <summary>快照：在沙箱数据根里现造一张**长方形**头像图并导入，供「头像圆形裁剪」出图守护。
    /// 为什么刻意用长方形：方图裁成圆肉眼看不出差别，长方形才能一眼看出是"按圆裁掉多余的"、
    /// 而不是拉伸变形（Stretch=UniformToFill 的老写法会把人脸压扁）。
    /// 造图失败只吞掉自己（快照的其它场景不受影响），绝不抛。</summary>
    private static void TrySeedAvatarForSnapshot()
    {
        try
        {
            var source = FocusCapturePaths.Combine("snapshot_avatar_source.png");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)), null, new Rect(0, 0, 90, 30));
                dc.DrawEllipse(Brushes.White, null, new Point(45, 15), 7, 7);
            }
            var bitmap = new RenderTargetBitmap(90, 30, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(source)) encoder.Save(fs);
            ChatAssetsService.ImportUserAvatar(source);
        }
        catch
        {
            // 快照造图失败不该让整轮快照挂掉：这张图没了，其它场景照出
        }
    }

    /// <summary>快照：在侧边栏数据之上再点进某个分组（验分组视图的排版）</summary>
    internal void SeedGroupViewForSnapshot()
    {
        SeedSidebarForSnapshot();
        if (_snapshotGroupId.Length > 0) OpenGroupView(_snapshotGroupId, "得到大脑");
    }

    /// <summary>快照：批量多选态 —— 底部操作条 + ✓ 选中高亮 + 计数文案，
    /// 三处都只有进入多选才出现，10b/10c 的默认布局永远覆盖不到。</summary>
    internal void SeedBatchModeForSnapshot()
    {
        SeedSidebarForSnapshot();
        Sidebar.EnterBatchMode();
        Sidebar.SelectBatchForSnapshot(
            Sidebar.Items.OfType<HistoryItemViewModel>().Take(2).Select(vm => vm.Id).ToList());
    }

    /// <summary>快照：分组视图的批量管理态（2026-09-26 新增）——
    /// 行首复选框 + 底部「全选/已选 n | 取消 / 移动到分组 / 删除」操作条，只有进分组再点批量管理才出现。</summary>
    internal void SeedGroupBatchForSnapshot()
    {
        SeedGroupViewForSnapshot();
        if (_snapshotGroupId.Length == 0) return;
        EnterGroupBatchMode();
        foreach (var vm in GroupListItems().Take(2))
        {
            vm.IsSelected = true;
            SyncGroupBatchPick(vm);
        }
    }

    /// <summary>快照：分组视图的搜索态（搜索框覆盖工具行）+ 真实命中结果。
    /// 不走 280ms 防抖的异步链（快照截图流程等不到它，结果必然缺席），改为同步跑同一底层
    /// ChatSearchService、按同一渲染口径（RenderGroupSearchResults）落图 —— 布局与结果都真实。</summary>
    internal void SeedGroupSearchForSnapshot()
    {
        SeedGroupViewForSnapshot();
        if (_snapshotGroupId.Length == 0) return;
        BtnGroupSearch_Click(this, new RoutedEventArgs());   // 复用真实入口：收工具行、显示搜索行
        GroupSearchBox.Text = "上传";
        GroupSearchPlaceholder.Visibility = Visibility.Collapsed;
        _groupSearchCts?.Cancel();                          // TextChanged 启动的异步链到此作废（下面同步渲染）
        RenderGroupSearchResults(ChatSearchService.Search("上传", _snapshotGroupId), _snapshotGroupId, "上传");
    }

    /// <summary>快照：对话态（有消息）—— 输入区必须沉到底部。
    /// 为什么要这张图（2026-09-26）：用户实测「发完首条消息，输入框还停在正中间」，
    /// 根因是布局只在输入区内容变化时重算、气泡加进来那一刻漏了重算 —— 这条链路没有出图就守不住。
    /// 走真实的加载链路（LoadHistorySession → Activate），不手搓气泡：伪造的状态验不出真问题。
    /// 侧边栏一起开着（SeedSidebarForSnapshot 里展开），顺带把「刚刚 / N分钟前」的相对时间也验在图上。</summary>
    internal void SeedConversationForSnapshot()
    {
        SeedSidebarForSnapshot();
        var latest = ChatSessionService.ListSessions().FirstOrDefault();
        if (latest != null) LoadHistorySession(latest.FilePath);
    }

    /// <summary>快照：回答中的发送/停止按钮（蓝底圆 + 中心白色圆角方块，用户给的"铜钱"样式）。
    /// 只切视觉状态，不真发请求 —— 快照要的是样子。底色与图标是两处独立设置，
    /// 「只切图标、底色恒绿」正是这次要修的老毛病，一张图就能看出来。</summary>
    internal void SeedBusyForSnapshot()
    {
        SeedConversationForSnapshot();
        SetBusyUi(true);
    }

    private void HandleRecycleBin()
    {
        new ChatTrashWindow { Owner = this }.ShowDialog();
        RefreshDrawer();
    }
    /// <summary>改单会话元数据：当前打开的会话改内存字段（保持内存与文件一致），否则 Load → 改 → Save。
    /// Save 自动自增 Rev + 触发 SessionChanged（同步管道入口），重命名/置顶/分组无需额外接线。</summary>
    private void ApplySessionMeta(HistoryItemViewModel item, Action<ChatSessionService> mutate)
    {
        // 优先改内存里的 runtime（含后台回答中的）：它的下次 Save 会带上新 meta，不会被覆盖丢失
        if (_runtimes.TryGetValue(item.Id, out var rt))
        {
            mutate(rt.Session);
            rt.Session.Save();
            return;
        }
        var svc = ChatSessionService.Load(item.FilePath);
        if (svc == null) return;
        mutate(svc);
        svc.Save();
    }

    /// <summary>删除（单项/批量共用）：确认弹窗 → 当前打开的会话先切断内存引用（新会话接管，防后续 Save 复活文件）
    /// → 经 AIDialogHelper.SessionDeleted 走 MarkDeleted（trash + 删除清单 + Notify，闭环①）。
    /// 返回是否真的删了（用户在确认框点「否」返回 false —— 调用方据此决定要不要退出批量态/刷新列表，2026-09-26）。</summary>
    private bool DeleteSessions(IReadOnlyList<HistoryItemViewModel> items)
    {
        var tip = items.Count == 1
            ? "删除该会话？会移入会话回收站。"
            : $"删除选中的 {items.Count} 个会话？将移入会话回收站。";
        if (System.Windows.MessageBox.Show(this, tip, "删除会话",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        foreach (var item in items)
        {
            // 内存里有 runtime（含后台回答中）→ 先 cancel 它再移除，避免流还在往已删会话写
            if (_runtimes.TryGetValue(item.Id, out var rt))
            {
                rt.Deleted = true;   // 标记已删：其流跑完的 finally 不得再 Save，否则会把移入回收站的文件复活
                try { rt.Cts?.Cancel(); } catch (ObjectDisposedException) { /* 已结束 */ }
                _runtimes.Remove(item.Id);
                if (_active == rt)
                    StartNewSession(rt.Mode, null);   // 活跃会话被删 → 新建空会话接管前台
            }
            AIDialogHelper.SessionDeleted?.Invoke(item.Id);
        }
        return true;
    }

    /// <summary>单会话导出：SaveFileDialog 选位置（默认文件名 = 标题/预览）</summary>
    private void ExportOne(HistoryItemViewModel item, ExportFormat format)
    {
        var raw = ChatSessionService.LoadFile(item.FilePath);
        if (raw == null)
        {
            System.Windows.MessageBox.Show(this, "会话文件损坏或无法读取", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出会话",
            Filter = "所有文件|*.*",
            FileName = NoteExportService.SanitizeFileName(ChatExportService.ResolveTitle(raw)) + ChatExportService.ExtensionOf(format),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var path = File.Exists(dlg.FileName) ? NoteExportService.GetUniquePath(dlg.FileName) : dlg.FileName;
            ChatExportService.ExportTo(raw, format, path);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>批量导出：选目录 → 每个会话各导出一个文件（同名自动 _1/_2 不覆盖）</summary>
    private void ExportMany(IReadOnlyList<HistoryItemViewModel> items, ExportFormat format)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择导出目录（选中的每个会话各导出一个文件，重名自动加序号不覆盖）",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var ok = 0;
        var fail = 0;
        foreach (var item in items)
        {
            try
            {
                var raw = ChatSessionService.LoadFile(item.FilePath);
                if (raw == null) { fail++; continue; }
                var name = NoteExportService.SanitizeFileName(ChatExportService.ResolveTitle(raw));
                if (string.IsNullOrEmpty(name)) name = "会话";
                var path = NoteExportService.GetUniquePath(Path.Combine(dlg.SelectedPath, name + ChatExportService.ExtensionOf(format)));
                ChatExportService.ExportTo(raw, format, path);
                ok++;
            }
            catch { fail++; }
        }

        System.Windows.MessageBox.Show(this,
            $"导出完成：成功 {ok} 个{(fail > 0 ? $"，失败 {fail} 个" : "")}。\n目录：{dlg.SelectedPath}",
            "批量导出", MessageBoxButton.OK,
            fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>加载历史会话回看（可继续对话）。**不中断当前活跃会话的后台回答**：若该会话已在内存（活跃/后台跑），直接切过去保留运行态。</summary>
    private void LoadHistorySession(string filePath)
    {
        var loaded = ChatSessionService.Load(filePath);
        if (loaded == null)
        {
            System.Windows.MessageBox.Show(this, "会话文件损坏或无法读取", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 分组视图开着时从侧边栏（置顶/最近/收藏）或搜索面板点会话：必须先退分组视图，
        // 否则会话在背后被加载、界面却被分组列表挡着 —— 表现就是"点了没反应"（2026-09-25 修的根因）。
        // 分组内行点击自己已先调 CloseGroupView，这里再调是 no-op；快照链路不在分组视图，同样 no-op。
        CloseGroupView();

        // 已是本窗口内存中的 runtime（活跃或后台跑）→ 直接切过去，保留运行态与已生成气泡
        if (_runtimes.TryGetValue(loaded.SessionId, out var existing))
        {
            Activate(existing);
            return;
        }

        // 规则**版本升级**（2026-10-01 v1 → v2）：旧块先清掉，再让 AppendAgentRulesOnce 按新版本重新注入。
        // 只追加不清理的话，新旧两套规则同时在场（旧版「写操作必须先问」vs 新版「分组指令可覆盖」），
        // 模型两头都听 —— 这正是改完规则"看起来没生效"的那类坑。
        if (loaded.Messages.Count > 0 && loaded.Messages[0].Role == ChatRoles.System
            && !loaded.Messages[0].Content.Contains(AgentRulesMarker)
            && loaded.Messages[0].Content.Contains(LegacyAgentRulesMarker))
        {
            loaded.RemoveSystemRuleBlock(LegacyAgentRulesMarker);
        }

        var runtime = new ConversationRuntime(loaded, loaded.Mode, null)
        {
            // Agent 会话的 system 消息里已含规则文本，避免继续对话时重复注入
            AgentRulesAdded = loaded.Messages.Count > 0
                && loaded.Messages[0].Role == ChatRoles.System
                && loaded.Messages[0].Content.Contains(AgentRulesMarker)
        };
        _runtimes[loaded.SessionId] = runtime;
        foreach (var m in loaded.Messages)
        {
            if (m.Role == ChatRoles.User) runtime.Bubbles.Add(new ChatBubbleViewModel(true, m.Content, attachments: BuildAttachmentVms(m.Attachments)));
            else if (m.Role == ChatRoles.Assistant) runtime.Bubbles.Add(new ChatBubbleViewModel(false, m.Content));
            // tool / assistant(tool_calls) 中间消息不渲染为气泡
        }
        Activate(runtime);
    }

    // 2026-09-24：BtnNewSession_Click 已删 —— 标题栏「+ 新会话」按钮随标题栏精简移除，
    // 新会话入口交给侧边栏「新对话」（Sidebar.NewChatRequested → CloseGroupView + StartNewSession）。

    // Agent 规则块的标记。**改规则正文时必须同步改这里的版本号** —— 见 LoadHistorySession 与
    // ChatSessionService.RemoveSystemRuleBlock：存量会话带着旧标记，不升版本就不会重新注入，等于改了没生效
    // （2026-10-01 v1 → v2 就是走的这条路）。
    private const string AgentRulesMarker = "[Agent 工具规则 v2]";
    private const string LegacyAgentRulesMarker = "[Agent 工具规则]";

    /// <summary>
    /// Agent 模式系统规则（每个会话只注入一次）。**分两层**（2026-10-01 用户拍板）：
    /// 【安全红线】任何来源的指令（含分组指令）都覆盖不了；【默认行为】可被分组指令覆盖。
    ///
    /// 为什么必须分层而不是"用户指令最大"：分组指令会**跨端同步**，若连防幻觉 / 防泄露的红线也能被它覆盖，
    /// 等于把安全开关交给了任何能写这条指令的地方。用户要的"实时指令优先"落在【默认行为】层即可满足
    /// （典型就是 D1 的「写操作前先问一句」），红线一根不松。
    /// </summary>
    private void AppendAgentRulesOnce(ConversationRuntime runtime)
    {
        if (runtime.AgentRulesAdded) return;
        runtime.Session.AppendSystemRules(
            AgentRulesMarker + "\n" +
            "本节分两层：**【安全红线】**任何来源的指令（包括分组指令）都不能覆盖；**【默认行为】**可被分组指令覆盖。\n" +
            "── 安全红线（不可覆盖）──\n" +
            "R1. 工具执行返回的结果是唯一事实来源：工具返回成功才可以说完成；返回失败必须如实告知。严禁在没有调用工具、或工具未返回成功的情况下宣称已完成任何操作。\n" +
            "R2. 没有对应工具的能力就直说做不到，不要编造替代方案的结果。\n" +
            "R3. 只根据工具真正返回的内容作答：文件读不出文字时（如扫描件 PDF、图片）必须如实说读不了，绝不许编造文件里没有的内容；工具返回的是部分数据（只列了前 N 行/条）时要说明这是部分。\n" +
            "R4. 删除类操作（删笔记 / 删待办等）动手前必须先征得用户明确同意 —— 这条是红线，分组指令也不能免除。\n" +
            "── 默认行为（可被分组指令覆盖）──\n" +
            "D1. 一般写操作（新增/修改笔记或待办、保存内容、上传文件等）执行前，先在回复中列出将要执行的具体动作，等用户明确同意后再调用工具执行。**若当前分组指令已明确要求某类条件成立时直接执行，则按分组指令直接执行，不再逐次征询。**\n" +
            "D2. 引用或修改某条笔记/待办时，用列表/搜索工具输出中方括号里的时间戳作为 ref_time 定位。\n" +
            "── 优先级 ──\n" +
            "分组指令与【默认行为】冲突时，以分组指令为准；与【安全红线】冲突时，一律以安全红线为准。");
        runtime.AgentRulesAdded = true;
    }

    /// <summary>注册表构建时的文件工具开关快照 —— 开关翻转后缓存作废重建（见 EnsureAgentRegistry）</summary>
    private bool _registryBuiltWithFsTools;

    /// <summary>装配工具注册表：本地工具 + 各外发目的地能力（新增目的地在此注册一行）</summary>
    private void EnsureAgentRegistry()
    {
        // 缓存自愈（2026-10-01 实测）：窗口单例常驻，注册表构建后「允许 AI 读写本地文件」开关的
        // 翻转不会反映进来 —— 用户先开窗口后勾开关，registry 永远停在"无文件工具"的状态，
        // 模型只能如实答"没有工具"。发现快照与当前开关不一致就作废重建。
        // 白名单增删不需要重建：工具持有 settings 引用，每次执行时实时读。
        if (_registry != null && _registryBuiltWithFsTools == _settings.AiFsToolsEnabled) return;
        _registryBuiltWithFsTools = _settings.AiFsToolsEnabled;
        var registry = new AgentToolRegistry();
        registry.Register(new SearchNotesTool(_noteService));
        registry.Register(new ListTodosTool(_noteService));
        registry.Register(new SaveQuickNoteTool(_noteService));
        registry.Register(new CreateTodoTool(_noteService));
        registry.Register(new CompleteTodoTool(_noteService));
        registry.Register(new ReopenTodoTool(_noteService));
        // 改内容（2026-09-17）：原地替换 + 旧内容进回收站。此前只有"改状态"，用户说"把那条改一下"AI 只能答做不到。
        registry.Register(new UpdateNoteTool(_noteService));
        registry.Register(new DeleteNoteTool(_noteService));

        // 查询与统计（2026-09-17）：AI 此前看不到「某天记了什么」「这个月记了多少」
        registry.Register(new ListNotesByDateTool(_noteService));
        registry.Register(new NoteStatsTool(_noteService));

        // 回收站（2026-09-17）：只给「看」与「恢复」。
        // 明确不给「彻底删除 / 清空回收站」—— 破坏性动作不给 AI，这是项目既有红线。
        registry.Register(new ListRecycleBinTool(_noteService));
        registry.Register(new RestoreDeletedTool(_noteService));

        // 导出（2026-09-17）：落到设置里的默认导出文件夹，**不上传网盘**（用户明确要求）
        registry.Register(new ExportNotesTool(_noteService, _settings));

        // 文件类工具（2026-09-16，五条链路）：
        // 这是本次更新的基座 —— 没有这几个工具，网盘接入就退化成一个需要手动操作的同步盘。
        // 注意它们**没有路径参数**：链路的可达范围由用户点过的牌号（handle）与本地元数据编号（file_id）决定，
        // 模型编不出这两样东西，因此拿不到没被授权过的本机文件。
        registry.Register(new StoreFileToCloudTool(CurrentSessionAttachments));
        registry.Register(new FindCloudFilesTool());
        registry.Register(new FetchCloudFileTool());
        registry.Register(new ReadCloudFileTool());
        // 文件记录维护 + 文档读取（2026-09-17）：read_cloud_file 已支持 docx/xlsx/pdf；
        // update_file_meta 只改本机显示名/标签，**不动云端文件名**（描述里已写明，防模型谎报"已改名"）。
        registry.Register(new UpdateFileMetaTool());
        // 文档读取（2026-09-17 handle/file_id；2026-10-01 注入授权上下文 + 新增 read_docx）：
        // path 来源受「允许 AI 读写本地文件」总开关 + FsGuard 白名单双重约束（用户拍板的受控放开）；
        // read_docx 补齐「AI 找得到 Word 却读不了」的能力洞，老版 .doc 明确不支持（工具内如实拒绝）。
        registry.Register(new ReadSpreadsheetTool(_settings, AskAllowDirAsync));
        registry.Register(new ReadPdfTool(_settings, AskAllowDirAsync));
        registry.Register(new ReadDocxTool(_settings, AskAllowDirAsync));

        var getNote = new GetNoteDestination(_settings) { AskAllowDir = AskAllowDirAsync };
        foreach (var capability in getNote.Capabilities)
            registry.Register(new OutboundTool(getNote, capability));
        // getnote_save_image（2026-10-01）随上面这轮注册；其 image_path 由模型给出，
        // 在 SaveImageAsync 内过 FsGuard 门禁（与本地文件 / 文档工具同一套白名单 + 弹窗加白）。

        // Skill 运行时（2026-09-20）：扫描 Skills\ 目录 + 挂两个工具（load_skill / run_skill_script）。
        // 核心逻辑全在 Services/Skills/ 里，这里只是装配 —— 出问题可整目录删掉回退，不牵存量功能。
        _skillCatalog = new SkillCatalog(FocusCapturePaths.Combine("Skills"), msg => AppLog.Warn("Skill", msg));
        _skillRuntime = new SkillRuntime(AppContext.BaseDirectory);
        _skillDeps = SkillDependencies.All(AppContext.BaseDirectory);   // 外部依赖表（应用自带优先，其次 PATH）

        var scriptRunner = new SkillScriptRunner(_skillCatalog, _skillRuntime, dependencies: _skillDeps)
        {
            TrustedSkills = new HashSet<string>(_settings.SkillTrusted, StringComparer.OrdinalIgnoreCase),
            OnTrusted = RememberSkillTrust,
            TrustPrompt = ConfirmSkillTrustAsync,
            AuthPrompt = ConfirmSkillAuthAsync,
        };
        registry.Register(new LoadSkillTool(_skillCatalog, _skillDeps));
        registry.Register(new RunSkillScriptTool(scriptRunner));

        // 本地文件工具（2026-10-01，授权目录制）：总开关默认关 —— 关着时**根本不注册**，模型看不见。
        // 这是有意对上面"没有路径参数"红线的受控突破：模型直接给路径，边界由
        // FsGuard 白名单（两路添加：设置手动 / 访问时弹窗确认）+ 删除必进 Windows 回收站 + 覆盖写自动 .bak 守住。
        // 弹窗实现经 UiThread 封送（工具体跑在线程池线程，B-16 红线④），且独立于写确认弹窗开关、不可被关掉。
        if (_settings.AiFsToolsEnabled)
        {
            registry.Register(new ListDirTool(_settings, AskAllowDirAsync));
            registry.Register(new ReadFileTool(_settings, AskAllowDirAsync));
            registry.Register(new WriteFileTool(_settings, AskAllowDirAsync));
            registry.Register(new MovePathTool(_settings, AskAllowDirAsync));
            registry.Register(new DeletePathTool(_settings, AskAllowDirAsync));
        }

        _registry = registry;
    }

    /// <summary>
    /// AI 文件工具的目录授权弹窗（2026-10-01）：AI 要访问白名单外的目录时问用户「是否加入白名单」。
    /// ⚠ 被 FsGuard 在**工具线程**（线程池）上调用 —— 必须经 UiThread 封送回 UI 线程（同 Skill 准入确认）；
    /// 调度失败按「用户拒绝」处理（UiThread.AskAsync 的 fail-closed 方向）。
    /// </summary>
    private async Task<bool> AskAllowDirAsync(string dir)
    {
        return await UiThread.AskAsync(Dispatcher, () => System.Windows.MessageBox.Show(
            this,
            $"AI 请求访问以下目录（当前不在 AI 文件访问白名单中）：\n\n{dir}\n\n" +
            "加入白名单后，AI 可读写该目录内的文件。\n" +
            "（删除始终移入 Windows 回收站，可还原；读取的文件内容会随对话发送给大模型服务）\n\n是否加入白名单？",
            "AI 文件访问授权", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK,
            msg => AppLog.Warn("Agent", msg));
    }

    /// <summary>
    /// Skill 准入确认（2026-09-20）：某个 Skill 第一次要跑脚本时问一次，允许后记进设置，之后不再问。
    ///
    /// <para>
    /// 为什么不每次弹：会烦死人。为什么不能不弹：脚本执行的风险比"新增一条笔记"高一个量级，
    /// 沿用写工具"默认不弹窗"的既有策略不合适。**准入式确认是这一整套安全模型的地基** ——
    /// 脚本跑起来之后读什么、连什么，宿主管不了，所以防线只能放在"谁被允许跑"上。
    /// </para>
    /// 
    /// <para>撤销入口在「设置 → Skill」；只能授权不能撤销的安全机制是残缺的。</para>
    /// </summary>
    /// <para>
    /// ⚠ <b>必须经 <see cref="UiThread"/> 封送回 UI 线程</b> —— 本方法是被
    /// <see cref="SkillScriptRunner"/> 在**工具线程**（线程池，非 UI 线程）上调用的，
    /// 直接在这里 <c>MessageBox.Show(this, …)</c> 会抛「调用线程无法访问此对象」。
    /// </para>
    /// <summary>
    /// Skill 依赖授权（2026-09-20）：某个 Skill 依赖的外部 CLI（如 lark-cli）还没登录时，
    /// **在应用内**把标准设备码流走完 —— 弹二维码、用户扫一次、窗口自动关闭。
    ///
    /// <para>
    /// 为什么必须有这一环：授权原本是完全空白的，模型只能照着 CLI 输出里的提示自己发挥 ——
    /// 实测它让用户去终端敲 <c>lark-cli auth login</c>，还把开发机上的安装目录拼进了命令里。
    /// **该由宿主做的事外包给用户，比 bug 本身更贵。**
    /// </para>
    /// <para>
    /// ⚠ 本方法在**工具线程**（线程池）上被调用 —— 必须经 <see cref="UiThread"/> 封送回 UI 线程，
    /// 否则 <c>ShowDialog</c> 会因跨线程访问窗口对象而抛「调用线程无法访问此对象」。
    /// </para>
    /// <para>返回 false = 用户没完成授权；执行器据此拒绝执行脚本，绝不假装成功。</para>
    /// </summary>
    private Task<bool> ConfirmSkillAuthAsync(SkillDependency dep, DependencyStatus status) =>
        UiThread.AskAsync(Dispatcher, () =>
        {
            var window = new SkillAuthWindow(dep, status) { Owner = this };
            return window.ShowDialog() == true;
        }, msg => AppLog.Warn("Skill", msg));

    private Task<bool> ConfirmSkillTrustAsync(SkillInfo skill) =>
        UiThread.AskAsync(Dispatcher, () => ShowSkillTrustDialog(skill), msg => AppLog.Warn("Skill", msg));

    /// <summary>准入确认弹窗本体 —— <b>只允许被 <see cref="ConfirmSkillTrustAsync"/> 在 UI 线程上调用</b></summary>
    private bool ShowSkillTrustDialog(SkillInfo skill)
    {
        var scripts = skill.ScriptFiles.Count > 0 ? string.Join("、", skill.ScriptFiles) : "（无）";
        var msg =
            $"允许 Skill「{skill.Name}」在你的电脑上执行脚本吗？\n\n" +
            $"目录：{skill.RootPath}\n" +
            $"脚本：{scripts}\n\n" +
            "允许后，这个 Skill 再次执行脚本时不再询问（可在「设置 → Skill」里撤销）。\n\n" +
            "注意：脚本运行起来之后做什么，本应用拦不住 —— 请只允许你信任来源的 Skill。";

        var ok = System.Windows.MessageBox.Show(this, msg, "Skill 执行确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        AppLog.Info("Skill", $"准入确认：{skill.Name} → {(ok ? "允许" : "用户取消")}");
        return ok;
    }

    /// <summary>把刚授权的 Skill 名落盘（去重后 Save）</summary>
    private void RememberSkillTrust(string skillName)
    {
        if (_settings.SkillTrusted.Any(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase)))
            return;

        _settings.SkillTrusted.Add(skillName);
        try
        {
            _settings.Save();
            AppLog.Info("Skill", $"已记住授权：{skillName}");
        }
        catch (Exception ex)
        {
            // 记不住不影响本次执行（内存里已加入），只是下次会再问一遍
            AppLog.Warn("Skill", $"保存 Skill 授权失败：{ex.Message}");
        }
    }

    /// <summary>回填-追加到原笔记（受沉浸式锁定约束）</summary>
    private void BtnFillAppend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatBubbleViewModel bubble } btn) return;
        var target = _active?.TargetNote;
        if (target == null || bubble.IsUser || bubble.IsFilled) return;

        if (ImmersiveSessionService.IsLocked(target.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可回填", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 优先气泡内选中文字，无选中则全文
        var fillText = GetBubbleSelectedText(btn, bubble);
        if (string.IsNullOrEmpty(fillText)) return;

        if (_noteService.AppendToNote(target, fillText))
        {
            bubble.IsFilled = true; // 按钮文本由 DataTrigger 自动更新为"已回填"并禁用
        }
        else
        {
            System.Windows.MessageBox.Show(this, "回填失败：未找到笔记文件或写入失败", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>回填-单独形成一条新笔记（不关联原笔记，无锁定约束）</summary>
    private void BtnFillNew_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatBubbleViewModel bubble } btn) return;
        if (bubble.IsUser || bubble.IsFilled) return;

        var fillText = GetBubbleSelectedText(btn, bubble);
        if (string.IsNullOrEmpty(fillText)) return;

        if (_noteService.SaveAiNote(fillText) != null)
        {
            bubble.IsFilled = true;
        }
        else
        {
            System.Windows.MessageBox.Show(this, "保存失败：笔记写入出错", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>读取气泡内选中文字；无选中返回全文。从按钮上溯到气泡 Border 找 BubbleText（只读 TextBox）</summary>
    private static string GetBubbleSelectedText(Button btn, ChatBubbleViewModel bubble)
    {
        try
        {
            DependencyObject? current = btn;
            while (current != null && current is not Border)
                current = VisualTreeHelper.GetParent(current);
            var box = current is Border b ? FindVisualChild<TextBox>(b) : null;
            var selected = box?.SelectedText?.Trim();
            if (!string.IsNullOrEmpty(selected)) return selected;
        }
        catch
        {
            // 可视化树查找失败时回退全文
        }
        return bubble.Content.Trim();
    }

    /// <summary>在可视树中查找指定类型的第一个后代</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is T found) return found;
        }
        return null;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        SaveActiveDraft();                      // 关窗前把当前输入框草稿存到活跃会话
        SaveDraftToDisk(_active?.DraftText);    // 只落盘活跃会话那份草稿（方案确认的边界）
        // 关窗即停止所有后台回答（多会话并行：每个 runtime 各自 cancel + 落盘）
        foreach (var r in _runtimes.Values)
        {
            try { r.Cts?.Cancel(); } catch (ObjectDisposedException) { /* 已结束 */ }
            try { r.Session.Save(); } catch { /* best effort */ }
        }
        _runtimes.Clear();
        _active = null;
        FileDeliveryHub.Delivered -= OnFileDelivered;
        FileDeliveryHub.OpenRequested -= OnFileOpenRequested;
        FileDeliveryHub.LocateRequested -= OnFileLocateRequested;
        // 与订阅成对（构造函数里挂、这里摘）—— 漏摘 = 旧窗口被静态事件钉住，永不回收
        ChatSessionService.SessionChanged -= OnChatDataChanged;
        ChatGroupStore.GroupsChanged -= OnChatDataChanged;
        try { _preview.Dispose(); } catch { /* 预览浮层释放失败不影响关闭 */ }
        AIDialogHelper.NotifyClosed();
    }

    public bool IsClosed => _closed;
}

/// <summary>三处入口共用的对话框单例管理（标题栏 / 悬浮球 / 托盘 / 右键 / 浮动工具条）</summary>
public static class AIDialogHelper
{
    private static AIDialogWindow? _dialog;
    private static NoteService? _noteService;
    private static AppSettings? _settings;
    private static Window? _owner;

    /// <summary>删除会话管道（阶段二删除 UI → 阶段一 MarkDeleted：trash + 删除清单 + Notify）。
    /// 由 MainWindow 创建 ChatSyncEngine 后注入；未注入（未配同步）时仅本地行为退化为无删除——
    /// 历史管理必须可用，故 AIDialogWindow 在此兜底直接移文件（不动同步清单）。</summary>
    public static Action<string>? SessionDeleted;

    /// <summary>删除会话（UI 唯一入口）：有同步引擎走 MarkDeleted 闭环①；无引擎直接移 trash（纯本地）。</summary>
    public static void DeleteSession(string sessionId)
    {
        if (SessionDeleted != null) { SessionDeleted.Invoke(sessionId); return; }
        try
        {
            var svc = ChatSessionService.LoadByAnyId(sessionId);
            if (svc != null)
            {
                Directory.CreateDirectory(ChatSessionService.TrashDir);
                File.Move(svc, Path.Combine(ChatSessionService.TrashDir, Path.GetFileName(svc)));
            }
        }
        catch { /* 删除失败下次对账兜底；本地无同步时无清单可补 */ }
    }

    public static void Initialize(NoteService noteService, AppSettings settings, Window? owner)
    {
        _noteService = noteService;
        _settings = settings;
        _owner = owner;
    }

    public static void NotifyClosed() => _dialog = null;

    /// <summary>应用退出时关闭单例对话框</summary>
    public static void CloseAll()
    {
        if (_dialog != null && !_dialog.IsClosed)
        {
            try { _dialog.Close(); } catch { /* best effort */ }
        }
        _dialog = null;
    }

    /// <summary>同步周期完成后刷新展开中的历史抽屉（后台线程调用安全，内部 marshal 回 UI）</summary>
    public static void RefreshOpenDrawer()
    {
        var dialog = _dialog;
        if (dialog == null) return;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!dialog.IsClosed) dialog.RefreshDrawerIfOpen();
        }));
    }

    /// <summary>网盘文件清单同步完成后刷新打开中的对话框（句柄可能已过期、云文件卡片状态可能变化）</summary>
    public static void NotifyFileListChanged()
    {
        var dialog = _dialog;
        if (dialog == null) return;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!dialog.IsClosed) dialog.RefreshFileViews();
        }));
    }

    /// <summary>设置里改了 AI 问答界面相关的项（昵称 / 自定义欢迎语 / 用户头像）后刷新打开中的窗口：
    /// 起手页那句话与侧边栏底部用户区。窗口没开着就什么都不做（下次打开自然是新值）。</summary>
    public static void NotifyChatUiSettingsChanged()
    {
        var dialog = _dialog;
        if (dialog == null) return;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!dialog.IsClosed) dialog.RefreshChatUiFromSettings();
        }));
    }

    /// <summary>打开 AI 对话框；Key 为空时提示并返回</summary>
    /// <param name="attachmentPaths">
    /// 要预置到输入区的本地文件（悬浮球拖放保存的「用 AI 问答打开」，2026-09-16 新增）。
    /// 原签名只有 (mode, targetNote, selectedText)，塞不进文件附件 —— 所以这里扩展了第四个参数。
    /// </param>
    public static void Open(ExplainMode mode, NoteEntry? targetNote = null, string? selectedText = null,
        IReadOnlyList<string>? attachmentPaths = null)
    {
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureAiChat, "AI 问答")) return;
        if (_noteService == null || _settings == null) return;

        // 2026-09-23 多供应商改造：不再直判「AiApiKey / AiModel 是否为空」（那是单供应商时代的写法，
        // 多供应商下这两个字段可能只是历史残留），改为按「当前使用模型」的解析结果判断。
        // 提示仍分两种，比原来更具体：缺整体配置 / 缺该供应商的 Key。
        var activeModel = AiModelResolver.ResolveActive(_settings);
        if (activeModel == null)
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在 设置 → AI 模型 中添加模型供应商并选择模型", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(activeModel.ApiKey))
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在 设置 → AI 模型 中填写该供应商的 API Key", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 旧实例不可用（null / 已关闭 / 已关闭但标志未更新的 IsVisible=false）一律重建——
        // 否则复用已关闭的 Window 调 OpenSession 会抛异常，导致对话框完全弹不出来（"点了没反应"）
        if (_dialog == null || _dialog.IsClosed || !_dialog.IsVisible)
        {
            _dialog = new AIDialogWindow(_noteService, _settings);
            if (_owner != null && _owner.IsVisible)
                _dialog.Owner = _owner;
        }

        try
        {
            _dialog.OpenSession(mode, targetNote, selectedText);
        }
        catch
        {
            // OpenSession 异常（旧窗口状态异常等）→ 重建后重试一次，绝不静默失败
            _dialog = new AIDialogWindow(_noteService, _settings);
            if (_owner != null && _owner.IsVisible) _dialog.Owner = _owner;
            _dialog.OpenSession(mode, targetNote, selectedText);
        }

        _dialog.Show();
        if (_dialog.WindowState == WindowState.Minimized) _dialog.WindowState = WindowState.Normal;
        _dialog.Activate();
        _dialog.Focus();

        // 2026-09-14 修复：输入框必须显式聚焦，且要等窗口显示并完成布局之后再聚焦。
        // 此前只调了窗口级 Activate()/Focus()，用户看到的现象是"打开后打字没反应，得先用鼠标点一下输入框"。
        _dialog.Dispatcher.BeginInvoke(new Action(_dialog.FocusInput), DispatcherPriority.Input);

        // 草稿回填（问题三，2026-09-21）：上次关窗落盘的未发送文本，重开时回填到当前会话输入框。
        // 同为 Input 优先级、排在 FocusInput 之后 —— 回填完光标落在文末仍是聚焦态。
        _dialog.Dispatcher.BeginInvoke(new Action(_dialog.RestoreDraftFromDisk), DispatcherPriority.Input);

        // 拖放保存（2026-09-16）：附件在**窗口显示之后**才落。
        // 走 Normal 优先级，早于上面 Input 优先级的聚焦 —— 先落附件、再把光标还给输入框。
        if (attachmentPaths is { Count: > 0 })
        {
            var seed = _dialog;
            seed.Dispatcher.BeginInvoke(new Action(() => seed.AddAttachmentPaths(attachmentPaths)));
        }
    }
}
