using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class InputWindow : Window
{
    private readonly NoteService _noteService;
    private readonly Models.AppSettings _settings;
    private bool _isSaving;
    private DispatcherTimer? _idleTimer;
    /// <summary>本次显示期间用户是否拖动过窗口（拖动后 AdjustHeight 不再吸回默认位置）</summary>
    private bool _userDragged;

    /// <summary>v3.5：当前类型（Note/Todo）。Ctrl+T 走全局热键（MainWindow → ToggleType），不在输入框按键内处理防双触发。</summary>
    private string _currentType = "Note";

    public event Action? NoteSaved;

    public InputWindow(NoteService noteService, Models.AppSettings settings)
    {
        InitializeComponent();
        _noteService = noteService;
        _settings = settings;
        Opacity = settings.InputOpacity;
        RootBorder.CornerRadius = new CornerRadius(settings.InputBorderRadius);
    }

    /// <summary>v3.5：切换笔记/待办类型（全局热键调用）。窗口可见时立即刷新高亮，不可见时仅切字段（下次打开生效）。</summary>
    public void ToggleType()
    {
        _currentType = _currentType == "Note" ? "Todo" : "Note";
        UpdateTypeButtons();
        if (IsVisible) InputBox.Focus();
    }

    /// <summary>刷新类型按钮高亮：选中 = 边框 #4CAF50 + 文字亮，未选中 = 灰色</summary>
    private void UpdateTypeButtons()
    {
        var isTodo = _currentType == "Todo";
        SetTypeButtonStyle(BtnNoteType, !isTodo);
        SetTypeButtonStyle(BtnTodoType, isTodo);
    }

    private static void SetTypeButtonStyle(Button btn, bool selected)
    {
        btn.BorderBrush = new SolidColorBrush(selected
            ? Color.FromRgb(0x4C, 0xAF, 0x50)
            : Color.FromRgb(0x3A, 0x3A, 0x3A));
        btn.Foreground = new SolidColorBrush(selected
            ? Color.FromRgb(0xE0, 0xE0, 0xE0)
            : Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void TypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string t && t != _currentType)
        {
            _currentType = t;
            UpdateTypeButtons();
        }
        InputBox.Focus();   // 焦点还回输入框：否则回车触发的是按钮 Click 而非保存
        RestartIdleTimer(); // v3.6：点击类型按钮也算交互，刷新倒计时
    }

    public void SetOpacity(double o) => Opacity = Math.Clamp(o, 0.3, 1.0);

    /// <summary>设置输入框边框圆角（2026-09-23 新增；由设置面板「输入框圆角」滑块实时联动调用）</summary>
    public void SetCornerRadius(double r) => RootBorder.CornerRadius = new CornerRadius(Math.Clamp(r, 0, 30));

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        InputBox.Focus();
        _idleTimer = new DispatcherTimer();
        _idleTimer.Tick += (_, _) => { if (!_isSaving) Hide(); };
        RestartIdleTimer();
    }

    /// <summary>
    /// v3.6：按设置重启自动隐藏计时。始终显示 = 不计时；自定义 = 最短 3 秒上不封顶。
    /// 打字/点击类型按钮等交互会调用本方法刷新倒计时，停止操作 N 秒后才隐藏。
    /// </summary>
    private void RestartIdleTimer()
    {
        if (_idleTimer == null) return;
        _idleTimer.Stop();
        if (_settings.InputAlwaysVisible) return;
        _idleTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, _settings.InputAutoHideSeconds));
        _idleTimer.Start();
    }

    /// <summary>
    /// v3.6：记住上次位置开启且已拖动过 → 出现在上次位置（夹取到当前工作区内，防换显示器后飞出屏幕）；
    /// 否则用默认位置（底部居中偏上 80px，与旧版一致）。
    /// </summary>
    private void PositionWindow()
    {
        var s = SystemParameters.WorkArea;
        if (_settings.InputRememberPosition && _settings.InputLeft >= 0 && _settings.InputTop >= 0)
        {
            _userDragged = true; // 视为自定义位置：AdjustHeight 时保持底部锚定，不吸回默认位置
            Left = Math.Clamp(_settings.InputLeft, s.Left, Math.Max(s.Left, s.Right - Width));
            Top = Math.Clamp(_settings.InputTop, s.Top, Math.Max(s.Top, s.Bottom - Height));
        }
        else
        {
            _userDragged = false;
            Left = (s.Width - Width) / 2 + s.Left;
            Top = s.Bottom - Height - 80;
        }
    }

    /// <summary>v3.6：按住空白/边框区域拖动整个输入框（文本框与按钮上不触发）</summary>
    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 命中文本框或按钮时不拖动，保证正常编辑/点击
        if (e.OriginalSource is DependencyObject d
            && (IsInside<System.Windows.Controls.Primitives.TextBoxBase>(d)
                || IsInside<System.Windows.Controls.Primitives.ButtonBase>(d))) return;
        try
        {
            DragMove();
            _userDragged = true;
            _settings.InputLeft = Left;
            _settings.InputTop = Top;
            _settings.Save();
        }
        catch { /* DragMove 仅在左键按下时有效，忽略偶发异常 */ }
    }

    /// <summary>沿可视化树向上找，判断命中点是否在指定类型控件内（如 TextBox/Button）</summary>
    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // v3.6：始终显示模式下失焦不隐藏，只有 Esc/保存/再次唤起快捷键才收起
    private void Window_Deactivated(object sender, EventArgs e) { if (!_isSaving && !_settings.InputAlwaysVisible) Hide(); }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RestartIdleTimer(); // v3.6：打字刷新自动隐藏倒计时
        AdjustHeight();
    }

    /// <summary>
    /// 按行数自适应高度。v3.6：用户拖动过（或记住了自定义位置）→ 底部锚定向上生长，
    /// 保持用户摆放的位置不跳动；默认位置则维持旧的吸附底部行为。
    /// </summary>
    private void AdjustHeight()
    {
        var lc = InputBox.LineCount;
        var newHeight = lc <= 5 ? Math.Max(80, lc * 22 + 40) : 190;
        var s = SystemParameters.WorkArea;
        if (_userDragged)
        {
            Top += Height - newHeight; // 底边不动，向上/下生长
            Top = Math.Clamp(Top, s.Top, Math.Max(s.Top, s.Bottom - newHeight));
        }
        else
        {
            Top = s.Bottom - newHeight - 80;
        }
        Height = newHeight;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Save(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Hide(); }
    }

    /// <summary>
    /// v2：待办保存前做时间识别——命中且未来 → 直接保存；纯日期（30号/下周三）→ 弹窗问几点；
    /// 裸时钟已过（下午输"8点"）→ 弹三选一确认；规则未命中 → 不弹（创建路径暂不接 LLM，本地规则已覆盖常用表达）。
    /// 识别结果通过 dueTime 显式传给 SaveNote，避免二次解析。
    /// </summary>
    private async void Save()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) { Hide(); return; }

        var type = _currentType == "Todo" ? NoteType.Todo : NoteType.Note;

        // 添加前重复预检：命中弹窗让用户定夺是否重复添加；选否则收起不保存（草稿保留，可改后重试）
        if (!DuplicatePrompt.Confirm(_noteService, text, type, this))
        {
            Hide();
            return;
        }

        _isSaving = true;
        try
        {
            if (type == NoteType.Todo)
            {
                var due = await TodoEditService.ResolveDueAsync(this, text, null);
                _noteService.SaveNote(text, type: NoteType.Todo, dueTime: due);
            }
            else
            {
                _noteService.SaveNote(text);
            }
        }
        finally { _isSaving = false; }
        InputBox.Text = ""; NoteSaved?.Invoke(); Hide();
    }

    public void SaveDirect(string content)
    {
        // 添加前重复预检（输入框入口，宿主为本窗口）
        if (!DuplicatePrompt.Confirm(_noteService, content, NoteType.Note, this)) return;
        _isSaving = true;
        try { _noteService.SaveNote(content); } finally { _isSaving = false; }
        NoteSaved?.Invoke();
    }

    public new void Show()
    {
        // 2026-08-15 修复：保留未保存草稿。自动隐藏/失焦/Esc 只收起窗口不清空内容，
        // 再次唤起（悬浮球/快捷键）可继续编辑；保存成功（Save 内已清空）后下次唤起才是空白。
        var hasDraft = !string.IsNullOrEmpty(InputBox.Text);
        Placeholder.Visibility = hasDraft ? Visibility.Collapsed : Visibility.Visible;
        if (hasDraft) { AdjustHeight(); InputBox.CaretIndex = InputBox.Text.Length; }
        else
        {
            // v3.5：草稿为空 → 类型重置为设置默认值，并刷新按钮高亮
            _currentType = _settings.InputDefaultType == "Todo" ? "Todo" : "Note";
            UpdateTypeButtons();
            Height = 80;
        }
        _isSaving = false;
        base.Show();
        PositionWindow();
        Activate(); // 确保窗口激活，快捷键唤起后可直接打字
        TryEnableIme();
        // 延迟一拍：窗口完全显示后再抢焦点，避免过早 Focus 被窗口激活流程吞掉
        Dispatcher.BeginInvoke(new Action(() =>
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
        }), DispatcherPriority.Input);
        RestartIdleTimer();
    }

    public new void Hide()
    {
        _idleTimer?.Stop();
        base.Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _idleTimer?.Stop();
        _idleTimer = null;
        base.OnClosed(e);
    }

    /// <summary>IME 激活（最佳努力：个别输入法不生效时焦点已就位，仍可直接打字）</summary>
    private void TryEnableIme()
    {
        try { InputMethod.SetIsInputMethodEnabled(InputBox, true); }
        catch { /* 个别输入法不支持，忽略 */ }
    }
}
