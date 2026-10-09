using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class FloatBall : Window
{
    // 拖动状态
    private bool _isDragging;
    private bool _dragMoveUsed; // DragMove 走原生拖拽，MouseLeftButtonUp 时跳过点击逻辑
    private Point _dragStartPos;
    private double _dragStartLeft, _dragStartTop;
    private bool _isCollapsed;

    // 吸附阈值
    private const int SnapThreshold = 12;

    /// <summary>
    /// 展开态窗口边长。**必须与 FloatBall.xaml 的 Width/Height 一致** —— XAML 与 C# 没法共用常量，
    /// 只能靠这条注释 + 慢层检查点维持同步。
    /// 2026-09-19：48 → 56（球四周留白 4px → 8px），这是「角标能往外移」和「投影能淡完」的前提。
    /// </summary>
    private const int ExpandedSize = 56;

    // 颜色笔刷
    private SolidColorBrush _normalBrush = new(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private SolidColorBrush _hoverBrush = new(Color.FromRgb(0x50, 0x50, 0x50));
    private SolidColorBrush _captureBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private SolidColorBrush? _flashBrush;

    public event Action? InputRequested;
    public event Action? QuickViewRequested;
    public event Action? VoiceInputRequested;
    public event Action? SettingsRequested;
    public event Action? AiAskRequested;
    public event Action? ExitRequested;
    public event Action? BadgeClicked;   // v3.5：点击角标 → 打开待办汇总窗

    /// <summary>
    /// 拖放保存（2026-09-16）：松手后把解析结果交出去。
    /// 本窗口**只负责判定与反馈**（展开 / 闪绿 / 原子），不落地任何数据 ——
    /// 「什么都不做」是文件拖入的正确行为，别在这里偷偷复制或上传（本功能**没有暂存区**）。
    /// </summary>
    public event Action<DragPayload>? DropReceived;

    /// <summary>
    /// 拖放保存总开关。**关闭时 AllowDrop=false**：拖放完全无反应，连光标都不变，
    /// 与改造前行为完全一致。这是"默认关"这条设计能被用户验证的唯一落点。
    /// </summary>
    public void SetDragToSaveEnabled(bool enabled)
    {
        AllowDrop = enabled;
        if (!enabled) ClearDragState();
    }

    /// <summary>当前这一次拖放的解析结果（DragEnter 时算一次，DragOver 高频心跳直接复用）。
    /// 不在 DragOver 里反复 Parse：那里每秒几十次，而 Parse 要查前台窗口进程，扛不住。</summary>
    private DragPayload _pendingDrag = DragPayload.Empty;

    /// <summary>本次拖入是否已经展开过球（避免 DragEnter 抖动时反复动画）。</summary>
    private bool _dragExpanded;

    private void ClearDragState()
    {
        _pendingDrag = DragPayload.Empty;
        _dragExpanded = false;
    }

    private void Ball_DragEnter(object sender, DragEventArgs e)
    {
        if (!AllowDrop) return;
        _pendingDrag = DragDropSaveService.Parse(e.Data);

        if (_pendingDrag.Kind == DragPayloadKind.None)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 吸附态只有 8×36，不展开用户根本瞄不准
        if (!_dragExpanded)
        {
            _dragExpanded = true;
            ExpandBall();
        }

        // 显示"可以放"的光标，而不是禁止符号
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Ball_DragOver(object sender, DragEventArgs e)
    {
        if (!AllowDrop) return;
        e.Effects = _pendingDrag.Kind == DragPayloadKind.None
            ? DragDropEffects.None
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Ball_DragLeave(object sender, DragEventArgs e)
    {
        if (!AllowDrop) return;
        ClearDragState();
    }

    private void Ball_Drop(object sender, DragEventArgs e)
    {
        if (!AllowDrop) return;

        // Drop 的 e.Data 是权威数据（本次拖放的终态），以它为准；取不到才退回 DragEnter 的缓存
        var payload = DragDropSaveService.Parse(e.Data);
        if (payload.Kind == DragPayloadKind.None) payload = _pendingDrag;

        ClearDragState();
        e.Handled = true;

        if (payload.Kind == DragPayloadKind.None) return;

        FlashGreen();
        DropReceived?.Invoke(payload);
    }

    /// <summary>AI 助手显示名称（MainWindow 从 AppSettings 注入，三处入口同源）</summary>
    public string AiAssistantName { get; set; } = "AI 问答";

    public FloatBall()
    {
        InitializeComponent();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - 80;
        Top = wa.Bottom - 200;

        // 拖放保存：四个事件的接线放在这里而不是 XAML —— AllowDrop 是运行时按设置项开关的，
        // 接线与开关放一处才看得出"关闭时到底为什么没反应"。
        // ⚠ 这几行是「写了但没生效」的高危形态（订阅缺失不报编译错、只在运行时静默失效），
        //   改完必须 Grep 复核 + 真机拖一次。
        DragEnter += Ball_DragEnter;
        DragOver += Ball_DragOver;
        DragLeave += Ball_DragLeave;
        Drop += Ball_Drop;
    }

    public void SetOpacity(double o) => Opacity = Math.Clamp(o, 0.3, 1.0);

    public void SetCaptureActive(bool active)
    {
        _normalBrush = active
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        Ball.Fill = _normalBrush;
    }

    // ── v3.5：未办待办角标 ──

    /// <summary>
    /// 设置角标：count = 未办待办总数（Open+Read，不是纯 Open）；count &lt;=0 → 隐藏；
    /// hasRead（存在已读暂缓）→ 红底(#E24B4A)否则绿底(#4CAF50)。
    /// </summary>
    public void SetBadge(int count, bool hasRead)
    {
        if (count <= 0 || !IsLoaded)
        {
            Badge.Visibility = Visibility.Collapsed;
            return;
        }
        BadgeText.Text = count.ToString();
        Badge.Background = new SolidColorBrush(
            Color.FromRgb(hasRead ? (byte)0xE2 : (byte)0x4C, hasRead ? (byte)0x4B : (byte)0xAF, hasRead ? (byte)0x4A : (byte)0x50));
        Badge.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 角标点击：e.Handled = true 吞掉事件，防止冒泡触发悬浮球拖拽/点击唤出逻辑；
    /// 触发 BadgeClicked（MainWindow 订阅 → 打开待办汇总窗）。
    /// </summary>
    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BadgeClicked?.Invoke();
    }
    public void ApplyPosition(double l, double t)
    {
        var s = SystemParameters.WorkArea;
        if (l < s.Left || l > s.Right - 20) l = s.Right - 80;
        if (t < s.Top || t > s.Bottom - 20) t = s.Bottom - 200;
        Left = l; Top = t;
    }

    public void FlashGreen()
    {
        if (_flashBrush != null)
        {
            _flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _flashBrush.Color = Colors.LimeGreen;
        }
        else
        {
            _flashBrush = new SolidColorBrush(Colors.LimeGreen);
        }

        Ball.Fill = _flashBrush;
        var ca = new ColorAnimation
        {
            From = Colors.LimeGreen,
            To = Color.FromRgb(0x3A, 0x3A, 0x3A),
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ca.Completed += (_, _) => Ball.Fill = _normalBrush;
        _flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, ca);
    }

    public (double left, double top) GetPosition() => (Left, Top);
    public bool IsCollapsed => _isCollapsed;

    // ── 位置动画 ──

    private void AnimateTo(double targetLeft, double targetTop, int durationMs = 220)
    {
        var animL = new DoubleAnimation
        {
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var animT = new DoubleAnimation
        {
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(LeftProperty, animL);
        BeginAnimation(TopProperty, animT);
    }

    private void StopAnimations()
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
    }

    // ── 拖动：用 DragMove() 做原生拖拽（系统级批量处理，流畅不卡） ──

    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragMoveUsed = false;
        _dragStartPos = e.GetPosition(this);
        _dragStartLeft = Left;
        _dragStartTop = Top;

        StopAnimations();

        // 抓取视觉反馈
        Ball.RenderTransform = new ScaleTransform(1.08, 1.08, 20, 20);
        Ball.Fill = _hoverBrush;

        Ball.CaptureMouse();
    }

    private void Ball_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStartPos.X) > 3 || Math.Abs(p.Y - _dragStartPos.Y) > 3)
        {
            _isDragging = true;
            _dragMoveUsed = true;
            Ball.ReleaseMouseCapture();

            try
            {
                // DragMove 走系统原生拖拽循环，流畅度等同于拖动任何标准窗口
                DragMove();
                OnDragComplete();
            }
            catch
            {
                // 极少数情况 DragMove 会抛异常（如超快速拖动），安全回退
                _dragMoveUsed = false;
                _isDragging = false;
                Ball.RenderTransform = null;
                Ball.Fill = _normalBrush;
            }
        }
    }

    private void OnDragComplete()
    {
        Ball.RenderTransform = null;

        var s = SystemParameters.WorkArea;
        bool l = Left <= s.Left + SnapThreshold;
        bool r = Left + Width >= s.Right - SnapThreshold;
        bool u = Top <= s.Top + SnapThreshold;
        bool d = Top + Height >= s.Bottom - SnapThreshold;

        if (l || r || u || d)
            SnapToEdge(l, r, u, d);
        else
            Ball.Fill = _normalBrush;

        _isDragging = false;
    }

    private void Ball_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Ball.ReleaseMouseCapture();

        // DragMove 已处理拖动和吸附，跳过点击逻辑
        if (_dragMoveUsed)
        {
            _dragMoveUsed = false;
            return;
        }

        if (_isDragging)
        {
            // 手动拖拽回退（不应走到这里，但保留防御）
            Ball.RenderTransform = null;
            Ball.Fill = _normalBrush;
        }
        else
        {
            Ball.RenderTransform = null;
            Ball.Fill = _normalBrush;
            InputRequested?.Invoke();
        }
        _isDragging = false;
    }

    // ── 吸附到边缘 ──

    private void SnapToEdge(bool left, bool right, bool top, bool bottom)
    {
        var s = SystemParameters.WorkArea;
        _isCollapsed = true;

        BallGrid.Visibility = Visibility.Collapsed;
        CollapsedBar.Visibility = Visibility.Visible;

        double targetLeft = Left, targetTop = Top;
        int targetW = ExpandedSize, targetH = ExpandedSize;

        if (left)
        {
            targetW = 8; targetH = 36;
            targetLeft = s.Left;
            targetTop = Math.Clamp(Top, s.Top, s.Bottom - targetH);
        }
        else if (right)
        {
            targetW = 8; targetH = 36;
            targetLeft = s.Right - targetW;
            targetTop = Math.Clamp(Top, s.Top, s.Bottom - targetH);
        }
        else if (top)
        {
            targetW = 36; targetH = 8;
            targetTop = s.Top;
            targetLeft = Math.Clamp(Left, s.Left, s.Right - targetW);
        }
        else if (bottom)
        {
            targetW = 36; targetH = 8;
            targetTop = s.Bottom - targetH;
            targetLeft = Math.Clamp(Left, s.Left, s.Right - targetW);
        }

        Width = targetW; Height = targetH;
        AnimateTo(targetLeft, targetTop);
    }

    /// <summary>v3.6：主动展开悬浮球（鼠标滑过收起条 / 提醒触发时调用）。收起态 → 还原 48x48 圆形球并归位。</summary>
    public void ExpandBall()
    {
        if (!_isCollapsed) return;
        _isCollapsed = false;
        CollapsedBar.Visibility = Visibility.Collapsed;
        BallGrid.Visibility = Visibility.Visible;
        StopAnimations();
        Width = ExpandedSize; Height = ExpandedSize;
        var s = SystemParameters.WorkArea;
        if (Left < s.Left + 5) Left = s.Left + 10;
        if (Left + Width > s.Right - 5) Left = s.Right - Width - 10;
        if (Top < s.Top + 5) Top = s.Top + 10;
        if (Top + Height > s.Bottom - 5) Top = s.Bottom - Height - 10;
    }

    private void CollapsedBar_MouseEnter(object sender, MouseEventArgs e) => ExpandBall();

    private void Ball_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_isDragging) return;
        Ball.Fill = _hoverBrush;
        Ball.RenderTransform = new ScaleTransform(1.1, 1.1, 20, 20);
    }

    private void Ball_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragging) return;
        Ball.Fill = _normalBrush;
        Ball.RenderTransform = Transform.Identity;
    }

    private void Ball_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            Foreground = Brushes.White,
            FontSize = 13,
        };
        menu.Items.Add(CreateMenuItem("  灵感速览", () => QuickViewRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("  沉浸记录", () => VoiceInputRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem($"  {AiAssistantName}", () => AiAskRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("  设置", () => SettingsRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("  退出", () => ExitRequested?.Invoke()));
        menu.IsOpen = true; e.Handled = true;
    }

    private static MenuItem CreateMenuItem(string h, Action a)
    { var item = new MenuItem { Header = h }; item.Click += (_, _) => a(); return item; }
}
