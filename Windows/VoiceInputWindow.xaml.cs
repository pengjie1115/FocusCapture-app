using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class VoiceInputWindow : Window
{
    // ── 服务 ──
    private readonly AppSettings _settings;
    private readonly LongNoteService _longNoteService;
    private readonly ThemeService _themeService;
    private readonly VoiceService _voiceService;

    // ── 状态 ──
    private bool _isDirty;
    private bool _isVoiceListening;
    private bool _isTopmost;
    private string _currentTheme = "Dark";
    private DateTime? _currentNoteTimestamp; // 当前会话笔记的时间戳 ID（首次保存时赋值）

    // ── 任务栏图标 ──
    private System.Windows.Forms.NotifyIcon? _taskbarIcon;
    private System.Drawing.Icon? _taskbarIconHandle;   // 图标句柄（它自己拥有，Dispose 时必须释放）
    private IntPtr _hwnd;

    // ── 保存位置/大小（最小化和最大化前） ──
    private double _prevLeft, _prevTop, _prevWidth, _prevHeight;

    // ── 常量 ──
    private const int MaxContentLength = 500_000;

    // ── Win32: WM_NCHITTEST ──
    private const int WM_NCHITTEST = 0x0084;
    private const int RESIZE_BORDER = 8;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int HTCAPTION = 2;
    private const int HTCLIENT = 1;

    public VoiceInputWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _longNoteService = new LongNoteService(settings.NotesPath);
        _themeService = new ThemeService();
        _voiceService = new VoiceService();

        _voiceService.PartialText += text => Dispatcher.Invoke(() =>
        {
            // SenseVoice 非流式：partial 仅作状态提示
            VoiceStatusText.Text = "正在识别…";
        });
        _voiceService.FinalText += text => Dispatcher.Invoke(() =>
        {
            // 直接追加识别的文本（SenseVoice 自带标点），多段之间加空格分隔
            if (ContentBox.Text.Length > 0 && !ContentBox.Text.EndsWith("\n") && !ContentBox.Text.EndsWith(" "))
                ContentBox.AppendText(" ");
            ContentBox.AppendText(text);
            ContentBox.ScrollToEnd();
        });
        _voiceService.VolumeLevel += level => Dispatcher.Invoke(() =>
        {
            VolumeBar.Value = Math.Clamp(level, 0, 1);
        });
        _voiceService.StatusChanged += text => Dispatcher.Invoke(() =>
        {
            VoiceStatusText.Text = text;
        });
        _voiceService.Error += text => Dispatcher.Invoke(() =>
        {
            VoiceStatusText.Text = $"错误: {text}";
            VoiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            // 不调用 StopVoiceListening()，避免覆盖错误信息
            _isVoiceListening = false;
            VolumeBar.Visibility = Visibility.Collapsed;
            VolumeBar.Value = 0;
            BtnVoice.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        });
        _voiceService.Ready += text => Dispatcher.Invoke(() =>
        {
            VoiceStatusText.Text = "聆听中…";
            VoiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        });

        LoadSettings();
        ApplyTheme();
    }

    // ══════════════════════════════════════════════
    //  生命周期 + WndProc
    // ══════════════════════════════════════════════

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RestoreWindowPosition();
        ContentBox.Focus();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && WindowState != WindowState.Maximized)
        {
            handled = true;
            return (IntPtr)HitTestNCA(lParam, ActualWidth, ActualHeight);
        }
        return IntPtr.Zero;
    }

    /// <summary>无边框窗口边缘 hit-test + 标题栏拖动</summary>
    private int HitTestNCA(IntPtr lParam, double w, double h)
    {
        var screenX = (short)(lParam.ToInt32() & 0xFFFF);
        var screenY = (short)(lParam.ToInt32() >> 16);
        var pt = PointFromScreen(new Point(screenX, screenY));
        var x = pt.X;
        var y = pt.Y;

        bool left = x < RESIZE_BORDER;
        bool right = x > w - RESIZE_BORDER;
        bool top = y < RESIZE_BORDER;
        bool bottom = y > h - RESIZE_BORDER;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;

        // 标题栏区域返回 HTCLIENT，让 WPF 正常处理按钮事件；
        // 窗口拖动通过 TitleBar_MouseDown + DragMove 实现
        return HTCLIENT;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 保存快捷键（默认 Ctrl+S，可自定义）
        if (IsSaveHotkey(e))
        {
            e.Handled = true;
            SaveContent();
            return;
        }
        // Esc 关闭（仅非语音监听时）
        if (e.Key == Key.Escape && !_isVoiceListening)
        {
            TryClose();
        }
    }

    private bool IsSaveHotkey(KeyEventArgs e)
    {
        var hk = _settings.SaveHotkey;
        if (hk.Key == 0) return false;
        var expectedKey = KeyInterop.KeyFromVirtualKey(hk.Key);
        return e.Key == expectedKey && (int)Keyboard.Modifiers == hk.Modifiers;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPosition();
        if (_isDirty && !string.IsNullOrWhiteSpace(ContentBox.Text))
            SaveContent(silent: true);
        ImmersiveSessionService.Stop();
        _voiceService.Dispose();
        DisposeTaskbarIcon();
        base.OnClosing(e);
    }

    public new void Show()
    {
        DisposeTaskbarIcon();
        ShowInTaskbar = false;
        base.Show();
        Activate();
        ContentBox.Focus();
    }

    /// <summary>窗口关闭（隐藏）即结束沉浸式会话，解除笔记编辑锁定</summary>
    public new void Hide()
    {
        ImmersiveSessionService.Stop();
        base.Hide();
    }

    /// <summary>同步最大化按钮图标（Aero Snap / 双击 / 按钮都可能触发 WindowState 变化）</summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    /// <summary>非置顶时点击窗口外 → 最小化到任务栏，保留所有内容</summary>
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_isTopmost && IsVisible && !_isVoiceListening)
            MinimizeToTaskbar();
    }

    // ══════════════════════════════════════════════
    //  窗口位置/大小持久化
    // ══════════════════════════════════════════════

    private void LoadSettings()
    {
        _isTopmost = _settings.VoiceTopmost;
        Topmost = _isTopmost;
        _currentTheme = _settings.VoiceTheme;
    }

    private void RestoreWindowPosition()
    {
        if (_settings.VoiceWindowLeft >= 0 && _settings.VoiceWindowTop >= 0)
        {
            var s = SystemParameters.WorkArea;
            Left = Math.Clamp(_settings.VoiceWindowLeft, s.Left, s.Right - 200);
            Top = Math.Clamp(_settings.VoiceWindowTop, s.Top, s.Bottom - 200);
        }
        if (_settings.VoiceWindowWidth >= 500)
            Width = _settings.VoiceWindowWidth;
        if (_settings.VoiceWindowHeight >= 350)
            Height = _settings.VoiceWindowHeight;
    }

    private void SaveWindowPosition()
    {
        if (WindowState == WindowState.Maximized) return;
        _settings.VoiceWindowLeft = Left;
        _settings.VoiceWindowTop = Top;
        _settings.VoiceWindowWidth = Width;
        _settings.VoiceWindowHeight = Height;
        _settings.VoiceTheme = _currentTheme;
        _settings.VoiceTopmost = _isTopmost;
        _settings.Save();
    }

    // ══════════════════════════════════════════════
    //  标题栏拖动
    // ══════════════════════════════════════════════

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 双击 → 最大化/还原
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        // WindowState.Maximized 下拖动标题栏 → Windows 原生自动还原+移动
        try { DragMove(); }
        catch { }
    }

    // ══════════════════════════════════════════════
    //  汉堡菜单（新建 / 保存 / 导出）
    // ══════════════════════════════════════════════

    private void BtnBurger_Click(object sender, RoutedEventArgs e)
    {
        if (BtnBurger.ContextMenu == null) return;
        BtnBurger.ContextMenu.PlacementTarget = BtnBurger;
        BtnBurger.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        BtnBurger.ContextMenu.IsOpen = true;
    }

    private void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        // 新建：先保存当前会话（如果有未保存内容），然后清空开始新会话
        if (_isDirty && !string.IsNullOrWhiteSpace(ContentBox.Text))
            SaveContent(silent: true);
        ContentBox.Text = "";
        _isDirty = false;
        _currentNoteTimestamp = null; // 重置：下一条笔记将用新时间戳
        ImmersiveSessionService.Stop(); // 结束当前会话，解除笔记编辑锁定
        ContentBox.Focus();
    }

    private void MenuSave_Click(object sender, RoutedEventArgs e) => SaveContent();

    private void MenuExportMd_Click(object sender, RoutedEventArgs e) => ExportAs(ExportFormat.Markdown);
    private void MenuExportDocx_Click(object sender, RoutedEventArgs e) => ExportAs(ExportFormat.Word);
    private void MenuExportTxt_Click(object sender, RoutedEventArgs e) => ExportAs(ExportFormat.Txt);

    // ══════════════════════════════════════════════
    //  设置下拉（主题 / 置顶）
    // ══════════════════════════════════════════════

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        UpdatePinMenuState();
        if (BtnSettings.ContextMenu == null) return;
        BtnSettings.ContextMenu.PlacementTarget = BtnSettings;
        BtnSettings.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        BtnSettings.ContextMenu.IsOpen = true;
    }

    private void MenuThemeDark_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = "Dark";
        ApplyTheme();
    }

    private void MenuThemeLight_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = "Light";
        ApplyTheme();
    }

    private void MenuPinTop_Click(object sender, RoutedEventArgs e)
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;
        UpdatePinMenuState();
    }

    private void UpdatePinMenuState()
    {
        MenuPinTop.Header = _isTopmost ? "  置顶 ✓" : "  置顶";
        MenuPinTop.Foreground = _isTopmost
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
    }

    // ══════════════════════════════════════════════
    //  标题栏按钮
    // ══════════════════════════════════════════════

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => MinimizeToTaskbar();

    private void MinimizeToTaskbar()
    {
        if (WindowState != WindowState.Maximized)
        {
            _prevLeft = Left;
            _prevTop = Top;
            _prevWidth = Width;
            _prevHeight = Height;
        }
        CreateTaskbarIcon();
        Hide();
    }

    private void CreateTaskbarIcon()
    {
        if (_taskbarIcon != null) return;

        // 图标与托盘 / 任务栏窗口同源（2026-09-19）：设了自定义图标就用它；
        // 没设则沿用原来的绿色方块（沉浸记录的识别色，保持不变）。
        var icon = AppIconService.BuildTrayIcon(_settings.CustomIconPath, System.Drawing.Color.FromArgb(0x4C, 0xAF, 0x50));
        _taskbarIconHandle = icon;

        _taskbarIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "FocusCapture - 沉浸记录"
        };
        _taskbarIcon.Click += TaskbarIcon_Click;
    }

    private void TaskbarIcon_Click(object? sender, EventArgs e)
    {
        ShowInTaskbar = false;
        Left = _prevLeft;
        Top = _prevTop;
        Width = _prevWidth;
        Height = _prevHeight;
        Show();
        Activate();
        ContentBox.Focus();
        DisposeTaskbarIcon();
    }

    private void DisposeTaskbarIcon()
    {
        if (_taskbarIcon != null)
        {
            _taskbarIcon.Visible = false;
            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }
        _taskbarIconHandle?.Dispose();   // 句柄归它自己所有，不释放就是 GDI 泄漏
        _taskbarIconHandle = null;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => TryClose();

    private void TryClose()
    {
        if (_isDirty && !string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            SaveContent(silent: true);
        }
        DisposeTaskbarIcon();
        Hide();
    }

    // ══════════════════════════════════════════════
    //  分隔条（保存拖动后的比例）
    // ══════════════════════════════════════════════

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var row1 = ((Grid)WindowBorder.Child).RowDefinitions[1];
        var row3 = ((Grid)WindowBorder.Child).RowDefinitions[3];
        var total = row1.ActualHeight + row3.ActualHeight;
        if (total > 0)
            _settings.VoiceSplitterPosition = row1.ActualHeight / total;
    }

    // ══════════════════════════════════════════════
    //  正文区
    // ══════════════════════════════════════════════

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _isDirty = true;

        if (ContentBox.Text.Length > MaxContentLength)
        {
            WarningBar.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { WarningBar.Visibility = Visibility.Collapsed; timer.Stop(); };
            timer.Start();
        }
    }

    // ══════════════════════════════════════════════
    //  语音按钮
    // ══════════════════════════════════════════════

    private void BtnVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_isVoiceListening) StopVoiceListening();
        else StartVoiceListening();
    }

    private void StartVoiceListening()
    {
        _isVoiceListening = true;
        VoiceStatusText.Text = "正在启动…";
        VoiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        VolumeBar.Visibility = Visibility.Visible;
        BtnVoice.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3E));
        _voiceService.Start();
    }

    private void StopVoiceListening()
    {
        _isVoiceListening = false;
        _voiceService.Stop();
        VoiceStatusText.Text = "开始说话";
        VoiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        VolumeBar.Visibility = Visibility.Collapsed;
        VolumeBar.Value = 0;
        BtnVoice.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    }

    public void AppendRecognizedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Dispatcher.Invoke(() =>
        {
            ContentBox.AppendText(text);
            ContentBox.ScrollToEnd();
        });
    }

    public void UpdateVolume(double level)
    {
        Dispatcher.Invoke(() =>
        {
            VolumeBar.Value = Math.Clamp(level, 0, 1);
        });
    }

    // ══════════════════════════════════════════════
    //  保存
    // ══════════════════════════════════════════════

    /// <summary>保存到今日笔记速览。会话内多次保存 = 覆盖同一条记录。silent=true 时不弹 Toast</summary>
    private void SaveContent(bool silent = false)
    {
        var text = ContentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        // 首次保存时记下时间戳，后续用相同时间戳覆盖
        _currentNoteTimestamp ??= DateTime.Now;
        ImmersiveSessionService.Start(_currentNoteTimestamp.Value);

        var ok = _longNoteService.SaveLongNote(text, _currentNoteTimestamp);
        if (ok)
        {
            _isDirty = false;
            if (!silent) ShowSaveToast();
        }
    }

    /// <summary>底部临时提示「已保存」2 秒后消失</summary>
    private void ShowSaveToast()
    {
        WarningBar.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        WarningBar.Visibility = Visibility.Visible;
        var txt = WarningBar.Child as TextBlock;
        if (txt != null) txt.Text = "已保存到今日笔记速览";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            WarningBar.Visibility = Visibility.Collapsed;
            WarningBar.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            if (txt != null) txt.Text = "内容过长可能影响性能，建议分段保存";
            timer.Stop();
        };
        timer.Start();
    }

    // ══════════════════════════════════════════════
    //  导出（导出到指定文件夹，区别于"保存"）
    // ══════════════════════════════════════════════

    private void ExportAs(ExportFormat format)
    {
        var text = ContentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("当前没有内容可导出。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = _settings.ExportFolderPath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择导出文件夹",
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            folder = dlg.SelectedPath;
            _settings.ExportFolderPath = folder;
            _settings.Save();
        }

        var svc = new NoteExportService();
        var ext = svc.GetFileExtension(format);
        var baseName = $"沉浸笔记_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        var fileName = NoteExportService.SanitizeFileName(baseName) + ext;
        var filePath = NoteExportService.GetUniquePath(Path.Combine(folder, fileName));

        try
        {
            var notes = new List<NoteEntry>
            {
                new() { Timestamp = DateTime.Now, Content = text }
            };
            var config = new ExportConfig
            {
                IncludeTime = true,
                IncludeSource = false,
                IncludeContent = true,
                Format = format
            };

            if (format == ExportFormat.Word)
            {
                var bytes = svc.BuildWord(notes, config);
                File.WriteAllBytes(filePath, bytes);
            }
            else
            {
                var content = svc.BuildExport(notes, config);
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }

            var result = new SuccessDialog(filePath) { Owner = this };
            result.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ══════════════════════════════════════════════
    //  主题
    // ══════════════════════════════════════════════

    private void ApplyTheme()
    {
        var colors = _themeService.SetTheme(_currentTheme);

        WindowBorder.Background = ParseColor(colors.WindowBg);
        WindowBorder.BorderBrush = ParseColor(colors.BorderColor);
        TitleBar.Background = ParseColor(colors.TitleBarBg);
        ContentBox.Background = ParseColor(colors.BodyBg);
        ContentBox.Foreground = ParseColor(colors.TextColor);
        BottomInputArea.Background = ParseColor(colors.TitleBarBg);
        VoiceSplitter.Background = ParseColor(colors.SplitterColor);
    }

    private static SolidColorBrush ParseColor(string hex)
    {
        if (hex.StartsWith("#")) hex = hex[1..];
        var r = byte.Parse(hex[..2], NumberStyles.HexNumber);
        var g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
        var b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}
