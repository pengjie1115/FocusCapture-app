using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// AI 整理预览对照窗（2026-09-26 新增；2026-09-27 规则化改造；同日 phase2 交互打磨）：
/// 左原文只读、右整理结果（文本流 或 提取待办的行列表），底栏规则条（左列下）+ 出口（右列下）。
///
/// <para><b>为什么必须有这一屏</b>：AI 整理会改写用户自己的原始记录，而模型整理错（漏要点、顺手改写、
/// 把两件事并成一件）是<b>看不出来</b>的 —— 直接落库等于让模型替用户做决定。先对照再选择，
/// 且结果可编辑（用户顺手改两个字就能用，不必重来一次）。</para>
///
/// <para><b>窗口只负责"选哪个规则 + 选哪个出口 + 最终文本是什么"，不碰数据层</b>：落库由
/// <see cref="AiTidyFlow"/> 统一做（各调用方的刷新方式不同），避免这个窗口依赖 NoteService。</para>
///
/// <para><b>2026-09-27 phase2</b>：① <b>先开窗后整理</b> —— 点 AI 按钮立即弹窗（出口禁用、状态行绿字
/// 「正在整理…」），结果回来自动填入，不再让用户对着空气等（拍板：立即弹窗，免得像卡住了）；
/// ② 状态行按消息类型上色：进行中/成功 = 绿，错误 = 红，中性说明 = 灰（原来通通红色像报错）；
/// ③ 「创建待办」不再关窗 —— 原地绿字报「创建成功 N 条」，按钮随即禁用，改动任一行才恢复（防手滑重复创建）；
/// ④ 规则按钮悬停显示适用场景说明（自定义规则在设置里填，见 <see cref="TidyRule.Description"/>）；
/// ⑤ 提取待办的行内时间拆成小时 / 分钟两框；日期框收窄压浅灰；⑥ 底栏并进对照区 Grid 随中缝联动 +
/// 细横向滚动条 + Shift+滚轮横滚。</para>
///
/// <para><b>非模态</b>（2026-09-27 用户拍板：预览窗开着时灵感速览要能操作）：用 Show 不用 ShowDialog，
/// 不再依赖 DialogResult —— 选了出口就把属性落好再 Close，由 AiTidyFlow 在 Closed 后读取。</para>
/// </summary>
public partial class AiTidyPreviewWindow : Window
{
    /// <summary>预览窗的输入包：原文、首整理结果、可用规则、模型与 provider（重跑用）。
    /// Rules / ShowCustomEntry 可写 —— 预览窗与设置窗非模态并存，用户改完设置回来时按 settings 刷新。</summary>
    public sealed class TidyPreviewRequest
    {
        public string Original { get; init; } = "";
        public string InitialTidied { get; init; } = "";
        public TidyRule InitialRule { get; init; } = new();
        public IReadOnlyList<TidyRule> Rules { get; set; } = Array.Empty<TidyRule>();
        public bool ShowCustomEntry { get; set; } = true;
        public string? ModelLabel { get; init; }
        public IChatProvider? Provider { get; init; }
        public AppSettings? Settings { get; init; }

        /// <summary>true = 首整理还没回来（AiTidyFlow 先开窗后整理）：窗口进「整理中」态，
        /// 结果由 <see cref="CompleteInitialTidy"/> 填入；false = InitialTidied 已经是成品。</summary>
        public bool PendingFirstTidy { get; init; }

        /// <summary>「创建待办」的落库入口（AiTidyFlow 注入，窗口不碰数据层）：传入草稿、返回实际创建条数。
        /// null = 当前环境没有落库入口（快照等），点了给提示不崩。创建后窗口不关（2026-09-27 phase2 拍板）。</summary>
        public Func<List<TidyTodoDraft>, int>? CreateHandler { get; init; }
    }

    /// <summary>待办行视图模型（提取待办形态下的一行：文字 + 截止日期 + 截止时间小时/分钟）。
    /// 带变更通知：创建过一批待办后任何行被改动都要把「创建待办」按钮恢复可用（防手滑重复创建）。</summary>
    public sealed class TodoRowVm : INotifyPropertyChanged
    {
        private string _text = "";
        private DateTime? _date;
        private string _hourText = "";
        private string _minuteText = "";

        public string Text { get => _text; set { _text = value; OnChanged(); } }
        public DateTime? Date { get => _date; set { _date = value; OnChanged(); } }
        public string HourText { get => _hourText; set { _hourText = value; OnChanged(); } }
        public string MinuteText { get => _minuteText; set { _minuteText = value; OnChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
    }

    /// <summary>用户选的出口（关窗 / Esc = None）。创建待办不再经这里关窗（phase2：原地创建），枚举保留给落库分流用。</summary>
    public TidyChoice Choice { get; private set; } = TidyChoice.None;

    /// <summary>用户最终确认的文本（可能被手动微调过；提取待办形态 = 各行重组的文本）。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>「创建待办」出口的草稿列表（仅该出口下有内容）。</summary>
    public List<TidyTodoDraft> TodoItems { get; private set; } = new();

    private readonly TidyPreviewRequest _req;
    private TidyRule _currentRule;
    private readonly ObservableCollection<TodoRowVm> _todoRows = new();
    private bool _rerunning;          // 重跑进行中：规则条与出口一并禁用，防拿到半截结果
    private bool _initialPending;     // 首整理在飞（先开窗后整理）：同样禁用底栏，结果回来才放行
    private bool _resultDirty;        // 结果被用户手改过（换规则覆盖前要确认）
    private bool _applyingResult;     // 程序性写结果时抑制 dirty 标记
    private bool _todosCreated;       // 已按当前行创建过一批待办：按钮禁用，行被改动才恢复

    public AiTidyPreviewWindow(TidyPreviewRequest request)
    {
        InitializeComponent();
        // 深色标题栏：WPF 不主动申请，系统深色模式下也可能渲染成白底（项目已踩过）
        DarkTitleBar.Enable(this);
        _req = request;
        _currentRule = request.InitialRule;
        TodoList.ItemsSource = _todoRows;
        _todoRows.CollectionChanged += (_, _) => OnTodoRowsChanged();
        OriginalBox.Text = request.Original ?? "";
        ModelText.Text = string.IsNullOrWhiteSpace(request.ModelLabel) ? "" : "整理模型：" + request.ModelLabel;
        RebuildRuleBar();
        ResultBox.TextChanged += (_, _) => { if (!_applyingResult) _resultDirty = true; };
        Activated += (_, _) => RefreshChipsFromSettings();   // 跳设置改完规则回来 → 规则条自动跟上（非模态下两窗同时开着）

        if (request.PendingFirstTidy)
        {
            // 先开窗后整理（2026-09-27 phase2 拍板）：立即给用户一个窗口，结果回来由 CompleteInitialTidy 填入。
            // 括号说明只进 ToolTip 不上 UI（2026-10-01 拍板：状态行保持一句话干净）
            _initialPending = true;
            SetBottomBarEnabled(false);
            SetStatus("正在整理…", StatusKind.Progress);
            StatusText.ToolTip = "窗口先开出来，结果一到自动填到右边；不想等可随时关闭。";
            ResultHeader.Text = "整理结果 · " + _currentRule.Name + "（整理中…）";
        }
        else
        {
            ApplyTextResult(request.InitialTidied ?? "");
        }
    }

    /// <summary>首整理的结果落地（AiTidyFlow 在开窗后异步整理完成时调用）。窗已关 = 用户不等了，直接丢弃。</summary>
    public void CompleteInitialTidy(NoteTidyService.TidyOutcome outcome)
    {
        if (!IsLoaded) return;
        _initialPending = false;
        SetBottomBarEnabled(true);
        if (!outcome.Ok)
        {
            SetStatus(outcome.Error ?? "整理失败。", StatusKind.Error);
            return;   // 结果区空着，出口点了会被空值校验拦住；用户可换规则重跑或关窗
        }
        if (TidyRuleCatalog.KindOf(_currentRule) == TidyRuleKind.ExtractTodo)
            ApplyTodoResult(outcome.Text);
        else
            ApplyTextResult(outcome.Text);
        SetStatus("", StatusKind.Info);
    }

    // ═══════════════ 规则条 ═══════════════

    /// <summary>重建规则条（首次 + 从设置回来时）。规则来自 settings 的活引用 —— 设置里改了显隐 / 排序 / 增删，这里照实刷新。</summary>
    private void RebuildRuleBar()
    {
        RuleBar.Children.Clear();
        foreach (var rule in _req.Rules)
            RuleBar.Children.Add(MakeRuleChip(rule));
        BtnCustom.Visibility = _req.ShowCustomEntry ? Visibility.Visible : Visibility.Collapsed;
        HighlightCurrentChip();
        if (_rerunning || _initialPending)
            SetBottomBarEnabled(false);   // 重建发生在重跑 / 首整理期间（如从设置回来）→ 新 chip 也要禁用
    }

    /// <summary>窗口被再次激活时同步一次规则条（设置窗是非模态单例，用户可能刚在里面增删改过规则）。</summary>
    private void RefreshChipsFromSettings()
    {
        var settings = _req.Settings;
        if (settings == null) return;
        _req.Rules = TidyRuleCatalog.ResolveVisible(settings);
        _req.ShowCustomEntry = settings.TidyShowCustomEntry;
        RebuildRuleBar();
    }

    private Button MakeRuleChip(TidyRule rule)
    {
        var b = new Button
        {
            Content = rule.Name,
            FontSize = 12,
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Tag = rule,
            // 悬停显示适用场景 / 用法（2026-09-27 phase2：不再只显示「内置规则」几个字；
            // 自定义规则的说明在设置里填，没填就提示去哪补）
            ToolTip = $"{rule.Name} —— {ResolveRuleDescription(rule)}",
        };
        b.Click += async (_, _) => await UseRuleAsync(rule);
        return b;
    }

    private static string ResolveRuleDescription(TidyRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Description)) return rule.Description;
        var builtin = TidyRuleCatalog.Find(rule.Id);
        if (builtin != null) return builtin.Description;
        return rule.IsBuiltin ? "内置规则" : "自定义规则（未填写适用场景说明 —— 到 设置 → 整理规则 编辑这条规则补一句）";
    }

    private void HighlightCurrentChip()
    {
        foreach (var child in RuleBar.Children.OfType<Button>())
        {
            if (child.Tag is not TidyRule rule) continue;
            var current = rule.Id == _currentRule.Id;
            child.BorderBrush = new SolidColorBrush(current ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0x55, 0x55, 0x55));
            child.Foreground = new SolidColorBrush(current ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xCC, 0xCC, 0xCC));
            child.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ═══════════════ 换规则重跑 ═══════════════

    /// <summary>
    /// 点规则 chip：按<b>原文</b>按新规则重新整理。已手改过的结果先确认再覆盖；
    /// 新规则是提取待办 → 结果区切待办行列表。失败保留原结果并报人话（规则已切走，但结果不丢）。
    /// </summary>
    private async Task UseRuleAsync(TidyRule rule)
    {
        if (_rerunning || _initialPending || rule.Id == _currentRule.Id) return;
        if (_req.Provider == null)
        {
            SetStatus("当前环境没有可用的整理模型，无法换规则重跑。", StatusKind.Error);
            return;
        }
        if (_resultDirty && ResultBox.Text.Trim().Length > 0)
        {
            var confirm = System.Windows.MessageBox.Show(this,
                "换规则会覆盖当前整理结果（包括你手动改过的部分），继续吗？",
                "AI 整理", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        _rerunning = true;
        SetBottomBarEnabled(false);
        SetStatus($"正在按「{rule.Name}」重新整理…", StatusKind.Progress);
        try
        {
            var outcome = await NoteTidyService.TidyAsync(_req.Provider, _req.Original, rule).ConfigureAwait(true);
            if (!outcome.Ok)
            {
                SetStatus(outcome.Error ?? "整理失败。", StatusKind.Error);
                return;   // 结果区保留上一个规则的结果，用户可继续选出口或再换规则
            }
            _currentRule = rule;
            if (TidyRuleCatalog.KindOf(rule) == TidyRuleKind.ExtractTodo)
                ApplyTodoResult(outcome.Text);
            else
                ApplyTextResult(outcome.Text);
            SetStatus("", StatusKind.Info);
        }
        finally
        {
            _rerunning = false;
            SetBottomBarEnabled(true);
            HighlightCurrentChip();
        }
    }

    private void SetBottomBarEnabled(bool enabled)
    {
        foreach (var child in RuleBar.Children.OfType<Button>()) child.IsEnabled = enabled;
        BtnCustom.IsEnabled = enabled;
        BtnCopy.IsEnabled = enabled;
        BtnSaveAsNew.IsEnabled = enabled;
        BtnReplace.IsEnabled = enabled;
        BtnCreateTodos.IsEnabled = enabled;
        BtnClose.IsEnabled = enabled;
    }

    // ═══════════════ 状态行（按消息类型上色，2026-09-27 phase2：不再通通红色像报错） ═══════════════

    private enum StatusKind { Progress, Error, Info }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.ToolTip = null;   // 换状态时同步清掉旧 ToolTip（只有「正在整理…」挂说明）
        StatusText.Text = message;
        StatusText.Foreground = kind switch
        {
            StatusKind.Progress => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),   // 绿：进行中 / 成功
            StatusKind.Error => new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),      // 红：真出错
            _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),                     // 灰：中性说明
        };
    }

    // ═══════════════ 结果区两种形态 ═══════════════

    private void ApplyTextResult(string text)
    {
        _applyingResult = true;
        try
        {
            ResultBox.Text = text;
            _resultDirty = false;
        }
        finally { _applyingResult = false; }
        ResultBox.Visibility = Visibility.Visible;
        TodoHost.Visibility = Visibility.Collapsed;
        BtnCreateTodos.Visibility = Visibility.Collapsed;
        ResultHeader.Text = "整理结果 · " + _currentRule.Name;
    }

    /// <summary>
    /// 提取待办形态：回包逐行解析成待办行（文字 + 截止时间回填到行内日期 / 时间框）。
    /// 模型漏写行首【】时由 <see cref="TodoTimeFallback"/>（TimeParser）从行内再认一次时间
    /// ——「10月1日上午9点12分坐高铁」这类表达直接回填日期与钟点（月日今年已过由 TimeParser 顺延一年）。
    /// 解析出 0 条（含「未发现待办」）→ 状态栏说明 + 退回纯文本展示，「创建待办」不出现 —— 兜底不许缺席。
    /// </summary>
    private void ApplyTodoResult(string text)
    {
        var parsed = NoteTidyPrompt.ParseTodoLines(text, TodoTimeFallback);
        if (NoteTidyPrompt.IsNoTodoMarker(text) || parsed.Count == 0)
        {
            // 兜底：解析出 0 条（含「未发现待办」）→ 退回纯文本展示，「创建待办」不出现
            ApplyTextResult(text);
            SetStatus(NoteTidyPrompt.IsNoTodoMarker(text)
                ? "这段内容里没有识别到待办。"
                : "模型这次没有按行返回待办，已按普通文本展示 —— 可以复制 / 另存，或换个规则再试。", StatusKind.Info);
            return;
        }

        _applyingResult = true;
        try
        {
            _todoRows.Clear();
            foreach (var (t, due) in parsed)
            {
                var row = new TodoRowVm { Text = t, Date = due?.Date };
                if (due is { } d && d.TimeOfDay != TimeSpan.Zero)
                {
                    row.HourText = d.Hour.ToString();
                    row.MinuteText = d.Minute.ToString("00");
                }
                HookRow(row);
                _todoRows.Add(row);
            }
            _resultDirty = false;
        }
        finally { _applyingResult = false; }
        ResultBox.Visibility = Visibility.Collapsed;
        TodoHost.Visibility = Visibility.Visible;
        BtnCreateTodos.Visibility = Visibility.Visible;
        ResultHeader.Text = "整理结果 · " + _currentRule.Name + "（可改文字 / 补日期时间）";
    }

    /// <summary>行没带【】时的机械兜底：TimeParser 从行内认时间（纯静态、只读；认不出返回 null 不强填）。</summary>
    private static DateTime? TodoTimeFallback(string lineText)
    {
        var r = TimeParser.Parse(lineText);
        return r.Matched ? r.Time : null;
    }

    /// <summary>行内日期 + 小时/分钟合成截止时间；只填一半 / 范围非法返回 null（出口处有对应校验提示）。</summary>
    private static DateTime? CombineDue(TodoRowVm r)
    {
        if (r.Date == null) return null;
        var due = r.Date.Value.Date;
        var hour = (r.HourText ?? "").Trim();
        var minute = (r.MinuteText ?? "").Trim();
        if (hour.Length == 0 && minute.Length == 0) return due;
        if (int.TryParse(hour, out var h) && int.TryParse(minute, out var mi) && h <= 23 && mi <= 59)
            return due.AddHours(h).AddMinutes(mi);
        return null;
    }

    /// <summary>重组当前待办行为文本（复制 / 另存 / 替换出口在待办形态下也走文本口径）。</summary>
    private string BuildTodoText()
    {
        var lines = new List<string>();
        foreach (var r in _todoRows)
        {
            var t = (r.Text ?? "").Trim();
            if (t.Length == 0) continue;
            var due = CombineDue(r);
            lines.Add(due != null ? $"【{due:yyyy-MM-dd HH:mm}】{t}" : t);
        }
        return string.Join("\n", lines);
    }

    // ═══════════════ 出口 ═══════════════

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();   // Choice 保持 None = 什么都没做
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.Copy);
    private void BtnSaveAsNew_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.SaveAsNew);
    private void BtnReplace_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.Replace);
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 「创建待办」（2026-09-27 phase2 改：创建后<b>不关窗</b>）—— 落库走 <see cref="TidyPreviewRequest.CreateHandler"/>
    /// （AiTidyFlow 注入，顺带通知列表刷新），原地绿字报「创建成功 N 条」。
    /// 创建后按钮禁用，改动任一行（文字 / 日期 / 时间 / 删行）才恢复 —— 防手滑连点重复创建。
    /// </summary>
    private void BtnCreateTodos_Click(object sender, RoutedEventArgs e)
    {
        if (_rerunning || _initialPending) return;
        if (!TryCollectTodoDrafts(out var drafts)) return;
        if (_req.CreateHandler == null)
        {
            SetStatus("当前环境没有创建待办的入口（预览快照态）。", StatusKind.Error);
            return;
        }
        var created = _req.CreateHandler(drafts);
        if (created <= 0)
        {
            SetStatus("一条都没创建成功 —— 请检查各行内容后重试。", StatusKind.Error);
            return;
        }
        _todosCreated = true;
        BtnCreateTodos.IsEnabled = false;
        SetStatus($"创建成功 {created} 条待办。窗口保持打开 —— 改文字 / 补时间后再点可创建新改动；不需要就点「关闭」。", StatusKind.Progress);
    }

    /// <summary>待办行校验 + 组装草稿（创建待办与复制 / 另存 / 替换出口共用）。失败在状态行报人话并返回 false。</summary>
    private bool TryCollectTodoDrafts(out List<TidyTodoDraft> drafts)
    {
        drafts = new List<TidyTodoDraft>();
        var rows = _todoRows.Select((r, i) => (Row: r, Index: i + 1)).Where(x => (x.Row.Text ?? "").Trim().Length > 0).ToList();
        if (rows.Count == 0)
        {
            SetStatus("没有可用的待办 —— 每行文字都空着，请先补上内容或直接关掉这个窗口。", StatusKind.Error);
            return false;
        }
        foreach (var (row, index) in rows)
        {
            var hour = (row.HourText ?? "").Trim();
            var minute = (row.MinuteText ?? "").Trim();
            var hasTime = hour.Length > 0 || minute.Length > 0;
            if (hasTime && row.Date == null)
            {
                SetStatus($"第 {index} 条填了时间但没填日期 —— 时间要跟日期一起填，或把时间清掉。", StatusKind.Error);
                return false;
            }
            if (hasTime && (!int.TryParse(hour, out var h) || !int.TryParse(minute, out var mi) || h > 23 || mi > 59))
            {
                SetStatus($"第 {index} 条的时间不对 —— 小时 0~23、分钟 0~59，两个框都要填（例如 9 和 30）。", StatusKind.Error);
                return false;
            }
            drafts.Add(new TidyTodoDraft(row.Text.Trim(), CombineDue(row)));
        }
        return true;
    }

    /// <summary>取出结果并关窗（非模态：不再用 DialogResult，属性落好即走）。空结果不许出口。</summary>
    private void Take(TidyChoice choice)
    {
        if (_rerunning || _initialPending) return;

        string text;
        if (TodoHost.Visibility == Visibility.Visible)
        {
            if (!TryCollectTodoDrafts(out var drafts)) return;
            text = BuildTodoText();
            TodoItems = drafts;
        }
        else
        {
            text = (ResultBox.Text ?? "").Trim();
            if (text.Length == 0)
            {
                SetStatus("整理结果不能为空 —— 请先补上内容，或直接关掉这个窗口。", StatusKind.Error);
                return;
            }
        }

        Choice = choice;
        ResultText = text;
        Close();
    }

    private void BtnRemoveTodoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is TodoRowVm vm) _todoRows.Remove(vm);
    }

    /// <summary>任一待办行被改动（文字 / 日期 / 时间 / 增删行）→ 解除「已创建」锁定，按钮恢复可用。</summary>
    private void OnTodoRowsChanged()
    {
        if (!_todosCreated) return;
        _todosCreated = false;
        BtnCreateTodos.IsEnabled = true;
    }

    private void HookRow(TodoRowVm row) => row.PropertyChanged += (_, _) => OnTodoRowsChanged();

    /// <summary>
    /// 底栏滚轮横向滚动。两条路（2026-10-01 扩充）：
    /// ① 鼠标悬停在滚动条区域上（含 10px 透明命中区）→ 滚轮直接横滚，不用按 Shift；
    /// ② 其余底栏区域维持 Shift+滚轮（2026-09-27 phase2 口径），避免在规则条上正常浏览时被抢走滚轮。
    /// </summary>
    private void BottomBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var overScrollbar = e.OriginalSource is DependencyObject d && FindAncestorScrollBar(d) != null;
        if (!overScrollbar && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0) return;
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>沿可视树向上找 ScrollBar（滚动条悬停判据；找不到返回 null）。</summary>
    private static System.Windows.Controls.Primitives.ScrollBar? FindAncestorScrollBar(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is System.Windows.Controls.Primitives.ScrollBar sb) return sb;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // ═══════════════ 跳设置 + 快照 ═══════════════

    /// <summary>
    /// 「自定义」入口：打开设置 → 整理规则板块。<b>非模态</b>（2026-09-27 拍板）：
    /// 设置窗本来就是非模态单例，本预览窗保持打开，用户改完规则切回来规则条自动刷新。
    /// </summary>
    private void BtnCustom_Click(object sender, RoutedEventArgs e)
    {
        if (WpfApp.Current.MainWindow is MainWindow main)
        {
            main.OpenSettingsToTidyRules();
            return;
        }
        SetStatus("找不到主窗口，请手动打开 设置 → 整理规则。", StatusKind.Error);
    }

    /// <summary>快照专用：不调模型，直接切到提取待办形态并填入样例行（出图守护要看行内日期 / 时间框长什么样）。</summary>
    public void SeedTodoModeForSnapshot()
    {
        var rule = _req.Rules.FirstOrDefault(r => TidyRuleCatalog.KindOf(r) == TidyRuleKind.ExtractTodo)
                   ?? _currentRule;
        _currentRule = rule;
        ApplyTodoResult("【2026-09-27 10:00】把整理好的方案发到项目群\n给供应商回电话问加急费");
        HighlightCurrentChip();
    }
}
