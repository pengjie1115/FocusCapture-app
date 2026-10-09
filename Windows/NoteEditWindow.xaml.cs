using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// 全屏笔记编辑窗口：独立 Window（可拖动/缩放），供长文本编辑。
/// 与行内编辑共享 NoteEntryViewModel.EditText（双向同步）。
/// 保存：Ctrl+S / 保存按钮；取消：Esc / 取消按钮。
/// v3.5：待办保存走 TodoEditService.SaveEdited（原地改行），保存后本地规则/LLM 时间识别，
/// 识别到未来时间弹建议条（设为提醒/忽略，10 秒自动消失）。
/// </summary>
public partial class NoteEditWindow : Window
{
    private readonly NoteService _noteService;
    private readonly NoteEntryViewModel _vm;
    private readonly IChatProvider? _provider;
    private readonly AppSettings? _settings;   // 「AI 整理」要按设置里的「AI 整理模型」解析模型（可空：老调用点不传也能跑）

    // v3.5 建议条状态
    private DateTime? _suggestDue;
    private DispatcherTimer? _suggestTimer;

    public NoteEditWindow(NoteService noteService, NoteEntryViewModel vm, string title,
        IChatProvider? provider = null, AppSettings? settings = null)
    {
        _noteService = noteService;
        _vm = vm;
        _provider = provider;
        _settings = settings;
        InitializeComponent();
        Title = title;
        NoteInfoText.Text = $"{title}  ·  来源: {vm.SourceWindow}";
        // 同步行内编辑的最新内容（打开全屏前可能已在行内改过）
        EditBox.Text = vm.EditText;
        EditBox.Focus();
        EditBox.CaretIndex = 0;
        EditBox.ScrollToHome();
        UpdateCharCount();
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCharCount();

    private void UpdateCharCount()
    {
        var len = EditBox.Text.Length;
        CharCountText.Text = $"{len} 字符";
    }

    private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(); // 取消：不保存
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Save();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

    // ── AI 整理（2026-09-26）──

    /// <summary>同一时间只允许整理一次（按钮禁用 + 本标志双保险）。</summary>
    private bool _tidyRunning;

    /// <summary>
    /// 「AI」按钮：把编辑框里<b>当前</b>内容交给大模型整理，先出预览对照窗再决定改不改。
    /// 落库口径与另两个入口共用一份（<see cref="AiTidyFlow"/>）：
    /// 「替换原文」立刻原地写盘（旧行先进回收站，可恢复），随后编辑框同步成整理结果，用户可继续润色；
    /// 「另存为新笔记」只新增一条笔记、编辑框不动；「复制」什么都不改。
    ///
    /// <para>这里刻意<b>不</b>顺带弹时间识别建议条 —— 整理只该改文字，不该顺手改用户的提醒设置；
    /// 想设提醒，保存时那条链路照旧会跑。</para>
    /// </summary>
    private async void BtnAiTidy_Click(object sender, RoutedEventArgs e)
    {
        if (_tidyRunning) return;

        if (ImmersiveSessionService.IsLocked(_vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可整理", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _tidyRunning = true;
        var original = BtnAiTidy.Content;
        BtnAiTidy.IsEnabled = false;
        BtnAiTidy.Content = "…";
        try
        {
            // 预览窗非模态（2026-09-27）：窗口打开后本方法即返回；落库发生在关窗时，经回调同步编辑框
            await AiTidyFlow.RunAsync(this, _noteService, _settings, _provider, _vm.Entry, EditBox.Text,
                result =>
                {
                    if (result?.Choice != TidyChoice.Replace) return;

                    // 替换已在 AiTidyFlow 里原地写盘；把编辑框与「行内编辑共享的 EditText」一起同步成整理结果，
                    // 用户可接着改（内容没再变时点保存等于无操作，不会重复写盘）
                    _vm.EditText = result.Text;
                    EditBox.Text = result.Text;
                    EditBox.CaretIndex = 0;
                    EditBox.ScrollToHome();
                    UpdateCharCount();
                });
        }
        finally
        {
            _tidyRunning = false;
            BtnAiTidy.IsEnabled = true;
            BtnAiTidy.Content = original;
        }
    }

    /// <summary>
    /// 保存（v5 2026-09-20）：分离 AI 释义后——
    /// 笔记/待办统一走 TodoEditService.SaveEdited **原地替换**改行（笔记此前走 AppendEdit 追加【编辑】行，
    /// 因跨端同步双条/孤儿卡问题经用户拍板改为替换；红线 2 例外）。
    /// 待办保存后做时间识别（规则优先，规则未命中才调 LLM 兜底）：识别到未来时间 → 弹建议条（不立即关窗）；
    /// 未识别到时间 → 不自动清除原提醒，直接关窗。
    /// async void：LLM 调用放后台线程（DetectDueAsync 内部），禁止 UI 线程同步阻塞等 LLM。
    /// </summary>
    private async void Save()
    {
        if (ImmersiveSessionService.IsLocked(_vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 分离 AI 释义块（编辑框可能含行内编辑带入的释义预览），只保存主内容
        var (content, _) = NoteEntryViewModel.SplitEditText(EditBox.Text.Trim());
        if (string.IsNullOrEmpty(content))
        {
            System.Windows.MessageBox.Show(this, "内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _vm.EditText = content;

        // 内容未变化：不改行（替换语义下无变化即无操作）
        var displayBase = _vm.Entry.EditedContent ?? _vm.Entry.Content;
        var changed = content != displayBase;
        if (changed && !TodoEditService.SaveEdited(_noteService, _vm.Entry, content))
        {
            System.Windows.MessageBox.Show(this, "保存失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (changed)
        {
            // 存储层状态同步（v5 2026-09-20：笔记编辑=原地替换，同待办）：
            // UpdateNote/UpdateTodo 内部已回写 entry.Content；笔记的旧 EditedContent
            //（历史【编辑】痕迹的合并结果）必须清掉——痕迹行已随替换清理
            _vm.Entry.Content = content;
            if (_vm.Entry.Type != NoteType.Todo)
                _vm.Entry.EditedContent = null;
        }

        // 普通笔记：保存即关闭（现状不变）
        if (_vm.Entry.Type != NoteType.Todo)
        {
            DialogResult = true;
            Close();
            return;
        }

        // 待办：时间识别（规则优先，规则未命中才调 LLM 兜底；未配 Key/异常 → 优雅降级不弹建议）
        // v2：ResolveDueAsync 统一处理——裸时钟已过弹三选一、纯日期弹"问几点"、未来时间直接返回
        var due = await TodoEditService.ResolveDueAsync(this, content, _provider);
        if (!due.HasValue)
        {
            // 未识别到时间：不自动清除原提醒（DueTime 保持），直接关窗
            DialogResult = true;
            Close();
            return;
        }

        _suggestDue = due.Value;
        ShowSuggestBar(due.Value);
    }

    /// <summary>显示建议条（覆盖层浮在编辑框上方，10 秒自动消失）</summary>
    private void ShowSuggestBar(DateTime due)
    {
        SuggestText.Text = $"检测到{TimeParser.FormatNaturalTime(due)}，设为提醒？";
        // 定位：建议条浮在编辑框上方（SuggestLayer 覆盖全窗口）
        try
        {
            var p = EditBox.TransformToVisual(SuggestLayer).Transform(new Point(0, 0));
            Canvas.SetLeft(SuggestBar, Math.Max(0, Math.Min(p.X, SuggestLayer.ActualWidth - SuggestBar.ActualWidth - 4)));
            Canvas.SetTop(SuggestBar, Math.Max(0, p.Y - SuggestBar.ActualHeight - 4));
        }
        catch { Canvas.SetLeft(SuggestBar, 10); Canvas.SetTop(SuggestBar, 10); }
        SuggestBar.Visibility = Visibility.Visible;

        _suggestTimer?.Stop();
        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _suggestTimer.Tick += (_, _) =>
        {
            _suggestTimer.Stop();
            HideSuggestBar();
            DialogResult = true;   // 超时未处理：内容已保存，视同完成编辑
            Close();
        };
        _suggestTimer.Start();
    }

    private void HideSuggestBar()
    {
        _suggestTimer?.Stop();
        SuggestBar.Visibility = Visibility.Collapsed;
        _suggestDue = null;
    }

    /// <summary>点「设为提醒」：UpdateTodo 重新定位（Content 已同步为新正文）→ 设提醒时间 → 关窗保存成功</summary>
    private void SuggestDueSet_Click(object sender, RoutedEventArgs e)
    {
        var due = _suggestDue;
        HideSuggestBar();
        if (!due.HasValue) { DialogResult = true; Close(); return; }
        if (_noteService.UpdateTodo(_vm.Entry, newContent: _vm.Entry.Content, dueTime: due.Value))
        {
            DialogResult = true;
            Close();
        }
        else
        {
            System.Windows.MessageBox.Show(this, "设置提醒失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = true;   // 保存本身已成功，仅提醒设置失败 → 照常关窗
            Close();
        }
    }

    /// <summary>点「忽略」：不设提醒，关窗（编辑内容已保存）</summary>
    private void SuggestDueIgnore_Click(object sender, RoutedEventArgs e)
    {
        HideSuggestBar();
        DialogResult = true;
        Close();
    }
}