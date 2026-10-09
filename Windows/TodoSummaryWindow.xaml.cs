using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// v3.5（Phase 3）：分组待办汇总窗（点击悬浮球角标弹出）。
/// 两组：标题「待处理」(Open，未读未办) /「已暂缓」(Read)。
/// 待处理条目操作与每日汇总一致（按钮按类型补全）；已暂缓条目：恢复提醒(Read→Open) / 标记完成(→Done)。
/// 两组都空 → 「没有待办事项」。
/// 始终有打开按钮兜底：角标/汇总靠本窗处理状态，处理完刷新列表。
///
/// v3.10（2026-09-22）补上「只能看不能改」的缺口：
/// ① 双击条目**就地**进编辑（文本框 + 保存/取消），保存走 TodoEditService.SaveEdited 原地替换；
/// ② 右键菜单：编辑 / 复制 / 跳转到灵感速览 / 设置提醒… / 取消提醒。
/// 保存后的时间识别**与灵感速览共用同一条路径**（TodoEditService.ResolveDueAsync：规则优先 →
/// 纯日期问几点 → 裸时钟已过三选一 → LLM 兜底），识别到时间弹建议条，点「设为提醒」才真写 DueTime
/// —— 不自动落盘，与速览行为一字不差（用户 2026-09-22 拍板：这里改时间就照搬灵感速览）。
/// </summary>
public partial class TodoSummaryWindow : Window
{
    private readonly NoteService _notes;
    private readonly AppSettings _settings;
    /// <summary>时间识别 LLM 兜底（与灵感速览同源；未配 Key 时内部短路不发请求）。
    /// 非 readonly：设置在运行期改过 AI 配置后，由 MainWindow 调 <see cref="UpdateAiProvider"/> 换新实例。</summary>
    private IChatProvider? _aiProvider;

    /// <summary>v3.7：当前显示的全部未办条目（已提醒暂缓+待处理+已过期），供一键清理使用</summary>
    private readonly List<NoteEntry> _visibleEntries = new();

    /// <summary>「跳转到灵感速览」请求。面板只发请求，开窗与定位逻辑收在 MainWindow
    /// —— 与灵感速览的 ExternalActionRequested 同一模式，避免面板各开各窗造成两套行为。</summary>
    public Action<NoteEntry>? JumpToQuickViewRequested;

    // ── v3.10 行内编辑：同一时间只允许编一条（与灵感速览一致）──
    private RowUi? _editingRow;
    private bool _closed;

    // ── v3.10 建议条（保存后时间识别）──
    private NoteEntry? _suggestEntry;
    private DateTime? _suggestDue;
    private DispatcherTimer? _suggestTimer;

    /// <summary>
    /// 一行待办的 UI 元素引用。编辑态靠**切换可见性**就地完成，不重建列表 ——
    /// 重建会换掉 NoteEntry 对象引用（LoadAllEntries 每次都是新对象），编辑目标按引用就匹配不上了。
    /// </summary>
    private sealed class RowUi
    {
        public required NoteEntry Entry { get; init; }
        public required Border Row { get; init; }
        public required TextBlock ContentText { get; init; }
        public required TextBox EditBox { get; init; }
        public required StackPanel ReadButtons { get; init; }
        public required StackPanel EditButtons { get; init; }
    }

    public TodoSummaryWindow(NoteService notes, AppSettings settings, IChatProvider? aiProvider = null)
    {
        InitializeComponent();
        _notes = notes;
        _settings = settings;
        _aiProvider = aiProvider;
        // 关窗：停掉建议条计时器并打标记 —— SaveEdit 是 async 的（时间识别可能走 LLM），
        // 回来后若窗口已关就绝不能再往控件上贴 UI。
        Closed += (_, _) => { _closed = true; _suggestTimer?.Stop(); };
    }

    /// <summary>设置里改了 AI 配置 → MainWindow 换掉共享 provider（与灵感速览 UpdateAiProvider 同款）。</summary>
    public void UpdateAiProvider(IChatProvider? p) => _aiProvider = p;

    /// <summary>载入并分组展示所有未办待办。v2（2026-08-28）：只显示「今天及以前」的（无 DueTime 纯待办也算），
    /// 明天及以后的不出现（去灵感速览「未到期」档看）。三分组：已提醒暂缓(Read)最上 → 待处理(无提醒或今天还没到点) → 已过期(时间已过)最后。</summary>
    public void RefreshAll(List<NoteEntry>? allItems)
    {
        var all = allItems ?? new List<NoteEntry>();
        var today = DateTime.Today;
        var now = DateTime.Now;
        var relevant = all.Where(e => e.Type == NoteType.Todo
            && (!e.DueTime.HasValue || e.DueTime.Value.Date <= today)).ToList();
        var read = relevant.Where(e => e.TodoStatus == TodoStatus.Read).ToList();
        var openPending = relevant.Where(e => e.TodoStatus == TodoStatus.Open
            && (!e.DueTime.HasValue || e.DueTime.Value > now)).ToList();
        var openOverdue = relevant.Where(e => e.TodoStatus == TodoStatus.Open
            && e.DueTime.HasValue && e.DueTime.Value <= now).ToList();

        PendingList.Children.Clear();
        ReadList.Children.Clear();
        OverdueList.Children.Clear();
        _visibleEntries.Clear();
        // 列表整批重建 → 正在编辑的那一行已被换成只读行：编辑态就此结束，未保存的草稿丢弃。
        // 与灵感速览的 Refresh 同一口径（那边也会整表重载），所以外部刷新不会留个半编辑态挂着。
        _editingRow = null;

        EmptyText.Visibility = (read.Count == 0 && openPending.Count == 0 && openOverdue.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;

        ReadHeaderText.Visibility = read.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PendingHeaderText.Visibility = openPending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        OverdueHeaderText.Visibility = openOverdue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnCleanAll.Visibility = (read.Count + openPending.Count + openOverdue.Count) == 0
            ? Visibility.Collapsed : Visibility.Visible;

        foreach (var e in read) { ReadList.Children.Add(BuildRow(e, isRead: true)); _visibleEntries.Add(e); }
        foreach (var e in openPending) { PendingList.Children.Add(BuildRow(e, isRead: false)); _visibleEntries.Add(e); }
        foreach (var e in openOverdue) { OverdueList.Children.Add(BuildRow(e, isRead: false)); _visibleEntries.Add(e); }
    }

    /// <summary>v3.7：一键清理——把当前显示的全部未办标为已完成（Done，落盘保留可追溯），带二次确认。
    /// 逐条 UpdateTodo；个别失败（如文件被外部改动）不影响其余条目，最后汇总提示。</summary>
    private void BtnCleanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleEntries.Count == 0) return;

        var result = System.Windows.MessageBox.Show(
            $"确认清理全部 {_visibleEntries.Count} 条待办？\n\n它们将被标记为「已完成」，不再出现在待办汇总中。",
            "一键清理", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        var failed = 0;
        foreach (var entry in _visibleEntries.ToList())
        {
            if (!_notes.UpdateTodo(entry, newContent: entry.EditedContent ?? entry.Content, status: TodoStatus.Done))
                failed++;
        }

        HideSuggestBar();   // 清单已被清空，建议条指着的条目可能已不在此面板
        ReloadFromNotes();
        if (failed > 0)
            System.Windows.MessageBox.Show($"{failed} 条清理失败（未在笔记文件中找到，可能已被外部修改）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ═══════════════ 行构建（只读态 + 编辑态同在一行，靠可见性切换） ═══════════════

    /// <summary>
    /// 构建一行。只读态与编辑态的元素**都建出来**，靠 Visibility 互斥切换 ——
    /// 与灵感速览 DataTemplate 里 IsEditing 触发器的做法同构，只是那边在 XAML、这里用代码。
    /// 为什么不用「双击时重建列表」：LoadAllEntries 每次都产出新的 NoteEntry 对象，
    /// 重建后编辑目标按引用匹配不上，而且重建等于把用户正在敲的内容丢回原值。
    /// </summary>
    private Border BuildRow(NoteEntry e, bool isRead)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8)
        };

        var contentText = new TextBlock
        {
            Text = e.EditedContent ?? e.Content,
            Foreground = new SolidColorBrush(isRead
                ? Color.FromRgb(0xCC, 0xCC, 0xCC)
                : Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320
        };

        // 编辑框：固定 80px（约 4 行）高，超出内部滚动 —— 待办常有长文本，让它撑高会把列表挤没。
        // 滚动条**不做任何样式覆盖**：走 App.xaml 的隐式 ScrollBar 样式（深色细条 + 悬停变绿），
        // 全局一套强过这里再养一套（用户 2026-09-22：别出现老式白条）。
        var editBox = new TextBox
        {
            Text = e.EditedContent ?? e.Content,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed
        };

        var timeText = new TextBlock
        {
            Text = e.DueTime.HasValue ? $"提醒时间:{e.DueTime:yyyy-MM-dd HH:mm}" : "无提醒时间",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Margin = new Thickness(0, 2, 0, 6)
        };

        var readButtons = BuildReadButtons(e, isRead);
        // 「AI 整理」放在按钮区最前：与灵感速览一样的位置语义（它那边也是排在其它操作按钮之前）
        readButtons.Children.Insert(0, MakeTidyButton(e));

        var editButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed
        };
        var btnSave = MakeButton("保存", Color.FromRgb(0x4C, 0xAF, 0x50));
        var btnCancel = MakeButton("取消", Color.FromRgb(0xE0, 0xE0, 0xE0));
        // 编辑态同样留一个入口：用户往往在改的过程中才决定"这段得理一理"
        editButtons.Children.Add(MakeTidyButton(e));
        editButtons.Children.Add(btnSave);
        editButtons.Children.Add(btnCancel);

        // 内容区用 Grid 叠放：只读文本与编辑框共用同一格，切换只改可见性，行内不跳动
        var contentHost = new Grid();
        contentHost.Children.Add(contentText);
        contentHost.Children.Add(editBox);

        var panel = new StackPanel();
        panel.Children.Add(contentHost);
        panel.Children.Add(timeText);
        panel.Children.Add(readButtons);
        panel.Children.Add(editButtons);
        row.Child = panel;

        // 元素引用挂在行上（不另建集合：列表重建时行一起换，引用天然同步）
        var ui = new RowUi
        {
            Entry = e, Row = row, ContentText = contentText, EditBox = editBox,
            ReadButtons = readButtons, EditButtons = editButtons
        };
        row.Tag = ui;

        // 交互：双击进编辑 —— Border 是 Decorator 不是 Control，没有 MouseDoubleClick 事件，
        // 只能自己看 ClickCount；右键菜单按条目现建（闭包捕获目标，不必走 PlacementTarget 那套）
        row.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2) return;
            args.Handled = true;
            EnterEdit(ui);
        };
        row.ContextMenu = BuildContextMenu(e);

        btnSave.Click += (_, _) => SaveEdit(ui);
        btnCancel.Click += (_, _) => ExitEdit(ui);

        // 2026-09-26 用户要求：点编辑框以外的任何地方 = 自动保存并退出编辑态
        //（灵感速览面板早有这套行为，这里按本面板「代码建行」的结构移植一份，口径保持一致）
        editBox.LostFocus += (_, _) => OnEditBoxLostFocus(ui);

        return row;
    }

    /// <summary>只读态按钮区：待处理行按类型补全（同每日汇总）；已暂缓行：恢复提醒 / 标记完成。</summary>
    private StackPanel BuildReadButtons(NoteEntry e, bool isRead)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        if (isRead)
        {
            var btnRestore = MakeButton("恢复提醒", Color.FromRgb(0x4C, 0xAF, 0x50));
            var btnDone = MakeButton("标记完成", Color.FromRgb(0xE0, 0xE0, 0xE0));
            btnRestore.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Open); ReloadFromNotes(); };
            btnDone.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Done); ReloadFromNotes(); };
            bar.Children.Add(btnRestore);
            bar.Children.Add(btnDone);
            return bar;
        }

        var btnDone2 = MakeButton("已完成", Color.FromRgb(0x4C, 0xAF, 0x50));
        var btnRight = e.DueTime.HasValue
            ? MakeButton("顺延到明天", Color.FromRgb(0xE0, 0xE0, 0xE0))
            : MakeButton("稍后查看", Color.FromRgb(0xE0, 0xE0, 0xE0));
        var btnRead = MakeButton("已知悉", Color.FromRgb(0xE0, 0xE0, 0xE0));

        btnDone2.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Done); ReloadFromNotes(); };
        if (e.DueTime.HasValue)
            btnRight.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, dueTime: (e.DueTime ?? DateTime.Now).AddDays(1)); ReloadFromNotes(); };
        else
            btnRight.Click += (_, _) => ReloadFromNotes(); // 稍后查看 = 仅收起该行，下次再显示
        btnRead.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Read); ReloadFromNotes(); };

        bar.Children.Add(btnDone2);
        bar.Children.Add(btnRight);
        bar.Children.Add(btnRead);
        return bar;
    }

    private static Button MakeButton(string text, Color accent) => new()
    {
        Content = text, Margin = new Thickness(6, 0, 0, 0),
        Foreground = new SolidColorBrush(accent), BorderBrush = new SolidColorBrush(accent),
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), FontSize = 12
    };

    // ═══════════════ 行内编辑 ═══════════════

    private void EnterEdit(RowUi ui)
    {
        if (ImmersiveSessionService.IsLocked(ui.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 同一时间只允许编一条：上一条直接收回只读态（草稿丢弃，与灵感速览一致）
        if (_editingRow != null && !ReferenceEquals(_editingRow, ui)) ExitEdit(_editingRow);

        ui.EditBox.Text = ui.Entry.EditedContent ?? ui.Entry.Content;
        ui.ContentText.Visibility = Visibility.Collapsed;
        ui.EditBox.Visibility = Visibility.Visible;
        ui.ReadButtons.Visibility = Visibility.Collapsed;
        ui.EditButtons.Visibility = Visibility.Visible;
        _editingRow = ui;
        HideSuggestBar();   // 开始新一轮编辑 → 收掉上一条建议，免得指错条目

        // 等布局跑完再聚焦（此刻编辑框刚从 Collapsed 转 Visible，立刻 Focus 会被布局吞掉）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ui.EditBox.Focus();
            ui.EditBox.SelectAll();
            ui.EditBox.ScrollToHome();
        }), DispatcherPriority.Input);
    }

    /// <summary>退出编辑态（取消 / 切换编辑目标）：只还原 UI，不碰数据 —— 取消语义就是「什么都不改」。</summary>
    private void ExitEdit(RowUi ui)
    {
        ui.ContentText.Visibility = Visibility.Visible;
        ui.EditBox.Visibility = Visibility.Collapsed;
        ui.ReadButtons.Visibility = Visibility.Visible;
        ui.EditButtons.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(_editingRow, ui)) _editingRow = null;
    }

    // ═══════════════ 点编辑框以外 → 自动保存退出（2026-09-26 用户要求）═══════════════

    /// <summary>
    /// 编辑框失焦 → 若焦点真的离开了这一行，就自动保存并退出编辑态。
    ///
    /// <para><b>为什么延迟到 Background 优先级</b>：让本行「保存 / 取消」两个按钮的 Click 先跑完 ——
    /// 它们在 Click 里已经退出编辑态（<c>_editingRow</c> 置空或换人），本回调据此直接返回，
    /// 于是既不会"点保存后又自动保存一次"，也不会"点取消反而被存下来"。焦点还在本行（编辑框/两个按钮/
    /// AI 按钮）时同样不动作，否则一点按钮就会先把草稿存掉。</para>
    ///
    /// <para><b>空内容</b>：直接取消退出、丢弃改动（2026-09-26 用户拍板，与点「取消」一致）——
    /// 点外面就弹「内容不能为空」会把这个"无感保存"的体验彻底打断。</para>
    ///
    /// <para><b>非空</b>：走与点「保存」完全一致的链路（原地替换 + 时间识别，识别到时间会弹建议条问几点），
    /// 这是用户明确要求的"自动保存 = 真保存"。</para>
    /// </summary>
    private void OnEditBoxLostFocus(RowUi ui)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_editingRow, ui)) return;   // 已退出 / 已换编辑目标 / 列表已重建
            if (IsFocusInRow(ui)) return;                    // 焦点仍在编辑框或本行按钮上

            if (string.IsNullOrWhiteSpace(ui.EditBox.Text))
            {
                ExitEdit(ui);   // 空内容：丢弃改动直接退出
                return;
            }

            SaveEdit(ui);
        }), DispatcherPriority.Background);
    }

    /// <summary>焦点是否还落在这一行内 —— 行内所有元素（编辑框 / 各按钮）都在行根 Border 之下，用 Tag 认行。</summary>
    private static bool IsFocusInRow(RowUi ui)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused != null)
        {
            if (focused is Border b && ReferenceEquals(b.Tag, ui)) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    /// <summary>
    /// 保存（v3.10）：与灵感速览 SaveEditNote 同一条链路 ——
    /// 内容有变 → TodoEditService.SaveEdited 原地替换（旧行进回收站 + 发墓碑）；内容没变 → 不改行。
    /// 随后做时间识别（规则优先，未命中才 LLM 兜底），识别到时间弹建议条等用户确认。
    /// async void：LLM 兜底要 await，禁止在 UI 线程上同步等它。
    /// </summary>
    private async void SaveEdit(RowUi ui)
    {
        var e = ui.Entry;
        var text = ui.EditBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
        {
            System.Windows.MessageBox.Show(this, "内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var baseText = e.EditedContent ?? e.Content;
        if (text != baseText)
        {
            if (!TodoEditService.SaveEdited(_notes, e, text))
            {
                System.Windows.MessageBox.Show(this, "保存失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;   // 保留编辑态，用户可以重试或取消
            }
        }

        HideSuggestBar();
        ReloadFromNotes();   // 列表重建 → 编辑态自然结束（_editingRow 在 RefreshAll 里清）

        // 时间识别：与灵感速览共用同一条路径（纯日期会弹「几点提醒」、裸时钟已过弹三选一）
        var due = await TodoEditService.ResolveDueAsync(this, text, _aiProvider, _settings);
        if (_closed || !due.HasValue) return;
        ShowSuggestBar(e, due.Value);
    }

    // ═══════════════ 建议条（保存后时间识别） ═══════════════

    /// <summary>
    /// 顶部建议条。位置**必须**钉在面板顶部、不能挂在被编辑那一行上：汇总面板只显示「今天及以前」，
    /// 用户在编辑框里写个未来时间，保存后这条待办立刻移出本面板（归到速览的「未到期」档），
    /// 行都没了，挂行上的建议条无处安放。文案里明说去了哪，免得用户以为东西丢了。
    /// </summary>
    private void ShowSuggestBar(NoteEntry e, DateTime due)
    {
        _suggestEntry = e;
        _suggestDue = due;

        var when = TimeParser.FormatNaturalTime(due);
        var moved = due.Date > DateTime.Today;
        SuggestText.Text = moved
            ? $"已保存 · 该待办已移入「未到期」档 · 检测到{when}，设为提醒？"
            : $"已保存 · 检测到{when}，设为提醒？";
        SuggestBar.Visibility = Visibility.Visible;

        _suggestTimer?.Stop();
        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _suggestTimer.Tick += (_, _) =>
        {
            _suggestTimer.Stop();
            HideSuggestBar();   // 超时未处理：内容已保存，视同忽略（不设提醒）
        };
        _suggestTimer.Start();
    }

    private void HideSuggestBar()
    {
        _suggestTimer?.Stop();
        SuggestBar.Visibility = Visibility.Collapsed;
        _suggestEntry = null;
        _suggestDue = null;
    }

    /// <summary>点「设为提醒」：UpdateTodo 写 DueTime。正文用 Entry.Content —— 它是刚保存后的权威内容
    /// （SaveEdited 内部已回写；用 EditedContent 可能带出历史编辑痕迹的旧值）。</summary>
    private void BtnSuggestSet_Click(object sender, RoutedEventArgs e)
    {
        var entry = _suggestEntry;
        var due = _suggestDue;
        HideSuggestBar();
        if (entry == null || !due.HasValue) return;

        if (_notes.UpdateTodo(entry, newContent: entry.Content, dueTime: due.Value))
            ReloadFromNotes();
        else
            System.Windows.MessageBox.Show(this, "设置提醒失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>点「忽略」：不设提醒（编辑内容早在保存时就已落盘）</summary>
    private void BtnSuggestIgnore_Click(object sender, RoutedEventArgs e) => HideSuggestBar();

    // ═══════════════ AI 整理（2026-09-26）═══════════════

    /// <summary>同一时间只允许整理一条（按钮禁用 + 本标志双保险）—— 防连点发出多个请求、回来时行已不在。</summary>
    private bool _tidyRunning;

    /// <summary>
    /// 「AI」按钮：文字小按钮 + 悬停说明（与灵感速览行尾那个同款口径）。
    /// 只读态与编辑态各建一个实例 —— 同一个控件不能同时挂在两个父容器上。
    /// </summary>
    private Button MakeTidyButton(NoteEntry e)
    {
        var btn = MakeButton("AI", Color.FromRgb(0x4C, 0xAF, 0x50));
        btn.ToolTip = "AI 整理：把这一大段理成有条理的内容（先出预览，再决定改不改）";
        btn.Click += async (_, _) => await RunTidyAsync(e, btn);
        return btn;
    }

    /// <summary>
    /// 整理一条待办。内容取自「正在编辑就用编辑框里的、否则用展示内容」——
    /// 用户在编辑态点它，期待整理的是自己刚写的那版，而不是已落盘的旧文本。
    /// 落库口径（替换原文 / 另存新笔记 / 复制）全部收在 <see cref="AiTidyFlow"/>，
    /// 与灵感速览面板、全屏编辑窗共用一份，防三处行为漂移。
    /// </summary>
    private async Task RunTidyAsync(NoteEntry e, Button btn)
    {
        if (_tidyRunning) return;

        if (ImmersiveSessionService.IsLocked(e.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可整理", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ui = FindRowUi(e);
        var text = ui != null && ReferenceEquals(_editingRow, ui)
            ? ui.EditBox.Text
            : (e.EditedContent ?? e.Content);

        _tidyRunning = true;
        var original = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "…";
        try
        {
            // 预览窗非模态（2026-09-27）：窗口打开后本方法即返回；落库发生在关窗时，经回调重载列表
            await AiTidyFlow.RunAsync(this, _notes, _settings, _aiProvider, e, text,
                result =>
                {
                    // 「替换原文」与「创建待办」要重载列表（本面板显示待办，新建的待办要露面）；
                    // 另存出来的是一条普通笔记，本面板不显示它，重载反而会把编辑态连同用户没保存的草稿一起丢掉。
                    if (result?.Choice is TidyChoice.Replace or TidyChoice.CreateTodos) ReloadFromNotes();
                });
        }
        finally
        {
            _tidyRunning = false;
            btn.IsEnabled = true;
            btn.Content = original;
        }
    }

    /// <summary>按条目找它当前那一行的 UI 引用（列表重建后行引用会换，不能缓存）。</summary>
    private RowUi? FindRowUi(NoteEntry e)
    {
        foreach (var host in new[] { ReadList, PendingList, OverdueList })
        {
            foreach (var child in host.Children)
            {
                if (child is Border b && b.Tag is RowUi ui && ReferenceEquals(ui.Entry, e)) return ui;
            }
        }
        return null;
    }

    // ═══════════════ 右键菜单 ═══════════════

    /// <summary>
    /// 行右键菜单：编辑 / 复制 / 跳转到灵感速览 /（分隔）设置提醒… / 取消提醒。
    /// **不要在这里写 Style 或模板** —— App.xaml 有一条隐式（无 Key）ContextMenu 模板，这里的
    /// new ContextMenu() 会自动套用它；显式指定样式反而把深色模板顶掉，默认模板左侧那道浅色图标槽就是"白条"。
    /// </summary>
    private ContextMenu BuildContextMenu(NoteEntry e)
    {
        var menu = new ContextMenu();

        var miEdit = new MenuItem { Header = "编辑" };
        miEdit.Click += (_, _) => BeginEditByEntry(e);
        var miCopy = new MenuItem { Header = "复制" };
        miCopy.Click += (_, _) => CopyEntry(e);
        var miJump = new MenuItem { Header = "跳转到灵感速览" };
        miJump.Click += (_, _) => JumpToQuickViewRequested?.Invoke(e);

        menu.Items.Add(miEdit);
        menu.Items.Add(miCopy);
        menu.Items.Add(miJump);

        // 提醒两项只有待办能用（本面板 100% 是待办，判断留着是为了语义明确 + 将来复用）
        var miSetDue = new MenuItem { Header = "设置提醒…" };
        miSetDue.Click += (_, _) => SetDue(e);
        var miClearDue = new MenuItem { Header = "取消提醒" };
        miClearDue.Click += (_, _) => ClearDue(e);

        if (e.Type == NoteType.Todo)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(miSetDue);
            menu.Items.Add(miClearDue);
        }

        // 「取消提醒」在本来就没提醒时间时置灰（本项目惯例：不可用的项一眼能看出来）
        menu.Opened += (_, _) => miClearDue.IsEnabled = e.DueTime.HasValue;
        return menu;
    }

    /// <summary>右键「编辑」：找到该条目当前那一行，走与双击同一条入口。</summary>
    private void BeginEditByEntry(NoteEntry e)
    {
        if (FindRowUi(e) is { } ui) EnterEdit(ui);
    }

    /// <summary>右键「复制」：与灵感速览 CtxCopy_Click 同一条路径（标记自复制 + SafeClipboard 容错，绝不抛）。
    /// 失败直接弹提示 —— 汇总面板没有速览那样的面板内状态条，为一次剪贴板失败单养一条 UI 不划算。</summary>
    private void CopyEntry(NoteEntry e)
    {
        var text = e.EditedContent ?? e.Content;
        if (string.IsNullOrEmpty(text)) return;

        ClipboardHookService.MarkSelfCopy();
        if (!SafeClipboard.TrySetTextHardened(text, WpfClipboard.SetText, Win32.WriteClipboardText))
            System.Windows.MessageBox.Show(this, "复制失败：剪贴板被其他程序占用，请稍后重试", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>右键「设置提醒…」：弹时间输入框 → UpdateTodo 写 DueTime（与灵感速览同一行为：
    /// 正文带当前展示内容，防覆盖编辑）</summary>
    private void SetDue(NoteEntry e)
    {
        var dlg = new DueTimeDialog(e.DueTime) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.DueTime.HasValue) return;

        if (_notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, dueTime: dlg.DueTime.Value))
            ReloadFromNotes();
        else
            System.Windows.MessageBox.Show(this, "设置提醒失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>右键「取消提醒」：UpdateTodo 清掉 DueTime</summary>
    private void ClearDue(NoteEntry e)
    {
        if (_notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, clearDue: true))
            ReloadFromNotes();
        else
            System.Windows.MessageBox.Show(this, "取消提醒失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>按钮处理后重新从文件载入刷新分组（状态已落盘）。</summary>
    private void ReloadFromNotes()
    {
        RefreshAll(_notes.LoadAllEntries());
    }

    // ═══════════════ 界面快照专用入口（--snapshot；正常启动路径一个都不会调到） ═══════════════

    /// <summary>仅供界面快照：把第 index 行切进编辑态。
    /// 编辑态只在用户双击后才出现，默认快照覆盖不到 —— 而「编辑框里的滚动条是不是深色细条」
    /// 「保存/取消有没有被挤出可视区」正是必须出图才看得出、静态代码查不出来的东西。</summary>
    internal void SeedEditStateForSnapshot(int index)
    {
        var rows = new List<Border>();
        foreach (var host in new[] { ReadList, PendingList, OverdueList })
            foreach (var child in host.Children)
                if (child is Border b) rows.Add(b);

        if (index >= 0 && index < rows.Count && rows[index].Tag is RowUi ui) EnterEdit(ui);
    }

    /// <summary>仅供界面快照：模拟「保存后识别到未来时间」→ 显示建议条。
    /// 用未来时间是为了让文案里的「已移入「未到期」档」这句提示出现在图上（这是产品上的关键提示）。</summary>
    internal void SeedSuggestBarForSnapshot(int index)
    {
        if (index < 0 || index >= _visibleEntries.Count) return;
        ShowSuggestBar(_visibleEntries[index], DateTime.Today.AddDays(3).AddHours(9));
    }
}
