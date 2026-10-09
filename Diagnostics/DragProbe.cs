using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// 只取 Ellipse 一个类型：直接 using System.Windows.Shapes 会让 WinForms/WPF 的 Path
// 与 System.IO.Path 撞成 CS0104 不明确引用（本项目其他文件也有同样处理）。
using Ellipse = System.Windows.Shapes.Ellipse;

namespace FocusCapture.Diagnostics;

/// <summary>
/// 拖放探针（2026-09-16 引入）：验证「把文件/文字拖到悬浮球上」这条交互在**真实宿主条件**下到底能不能成立。
///
/// 用法：<c>FocusCapture.exe --dragprobe</c>
/// 输出：<c>%TEMP%\fc-dragprobe\yyyyMMdd-HHmmss.log</c>，同时实时显示在诊断面板上。
///
/// <b>为什么必须做成主程序里的诊断窗口，而不是一个独立小程序：</b>
/// 要验证的是「无边框 + 透明分层（AllowsTransparency）+ 圆形容器」这种特殊窗口能不能收到 OLE 拖放。
/// 独立小程序一旦窗口配置有细微差异（少一个 AllowsTransparency、多一个 Background），
/// 得到的「能收到」就是假绿 —— 这个项目已经在别处吃过假绿的亏。
/// 所以本窗口的窗口属性是**逐行照抄** Windows/FloatBall.xaml 的。
///
/// <b>数据隔离</b>：本探针不读写任何用户数据，日志只写 %TEMP%（另设 RootOverride 沙箱守一道）。
///
/// <b>安全</b>：全程把 Effects 置为 None —— 源程序（资源管理器等）会认为目标拒绝，
/// 因此**绝不会移动/复制用户的文件**。代价是拖动光标显示「禁止」符号，这是故意为之。
///
/// 用完后本文件与 App.xaml.cs 里的 --dragprobe 分支可直接删除，不影响主程序。
/// </summary>
internal static class DragProbe
{
    /// <summary>触发探针的命令行开关。</summary>
    public const string Flag = "--dragprobe";

    /// <summary>命令行是否请求了探针模式。</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    public static void Run(string[] args)
    {
        var logDir = Path.Combine(Path.GetTempPath(), "fc-dragprobe");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

        // 沙箱救生圈：探针本身不碰数据层，但万一将来有人往里加代码，也不至于写到真实数据根
        try
        {
            var sandbox = Path.Combine(logDir, "sandbox");
            Directory.CreateDirectory(sandbox);
            FocusCapturePaths.RootOverride = sandbox;
        }
        catch { /* 设置失败不影响探针本体 */ }

        var session = new ProbeSession(logPath);
        var panel = BuildPanel(session);
        var ball = BuildBall(session);

        panel.Show();
        ball.Show();

        session.Write("══════════════ 拖放探针已就绪 ══════════════");
        session.Write($"日志文件：{logPath}");
        session.Write("探针球目标：窗口属性照抄 FloatBall.xaml（无边框/透明分层/48×48/Opacity 0.85）");
        session.Write("球被命中会闪绿；日志按事件逐条记录原始数据格式。");
        session.Write("全程 Effects=None（拒绝拖放）—— 不会改动任何文件，光标显示「禁止」属正常。");
        session.Write("");
    }

    // ─────────────────────────────────────────────────────────────
    // 探针窗口：窗口属性逐行照抄 FloatBall.xaml
    // ─────────────────────────────────────────────────────────────

    private static Window BuildBall(ProbeSession session)
    {
        var ellipse = new Ellipse
        {
            Width = DragProbeSizes.BallDiameter,
            Height = DragProbeSizes.BallDiameter,
            Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid();
        grid.Children.Add(ellipse);
        // 大窗口模式的可视边界：窗口背景是 Transparent，用户根本看不见「窗口有多大」——
        // 第一版没画这个框，用户直接反馈「哪有什么空白方块？」，透明区测试因此没法做。
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        grid.Children.Add(frame);

        var ball = new Window
        {
            // ↓↓↓ 以下属性与 Windows/FloatBall.xaml 的窗口属性逐行一致，改动即失去验证意义 ↓↓↓
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Transparent,
            Opacity = 0.85,
            // ↑↑↑ 照抄结束 ↑↑↑

            Width = DragProbeSizes.BallWindowSize,
            Height = DragProbeSizes.BallWindowSize,
            Title = "拖放探针球",
            Content = grid,

            // 探针唯一新增的窗口属性
            AllowDrop = true,
        };

        var wa = SystemParameters.WorkArea;
        ball.Left = wa.Right - 140;
        ball.Top = wa.Top + 260;

        session.BindBall(ellipse, ball, frame);

        ball.DragEnter += (s, e) => session.HandleDrag("DragEnter", e);
        ball.DragOver += (s, e) => session.HandleDrag("DragOver", e);
        ball.DragLeave += (s, e) => session.HandleDrag("DragLeave", e);
        ball.Drop += (s, e) => session.HandleDrag("Drop", e);

        return ball;
    }

    private static Window BuildPanel(ProbeSession session)
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "拖放探针 DragProbe",
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var hint = new TextBlock
        {
            Text =
                "右侧那个深色圆球就是探针目标。拖动时鼠标光标会显示「禁止」符号 —— 那是故意的，\n" +
                "代表探针拒绝了这次拖放，因此不会移动或复制你的任何文件。\n" +
                "观察点：球被命中会闪绿，下方日志会多出一条记录；若球没闪绿 = 这次拖放没被收到。",
            FontSize = 12,
            LineHeight = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        var modeText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var btnSlim = MakeButton("切换为吸附态 8×36");
        btnSlim.Click += (s, e) => session.ToggleSlim();
        var btnBig = MakeButton("切换大窗口 160×160");
        btnBig.Click += (s, e) => session.ToggleBig();
        var btnClear = MakeButton("清空日志");
        btnClear.Click += (s, e) => session.ClearView();
        var btnFolder = MakeButton("打开日志文件夹");
        btnFolder.Click += (s, e) =>
        {
            try { Process.Start("explorer.exe", session.LogDirectory); } catch { }
        };

        bar.Children.Add(btnSlim);
        bar.Children.Add(btnBig);
        bar.Children.Add(btnClear);
        bar.Children.Add(btnFolder);
        bar.Children.Add(modeText);

        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Padding = new Thickness(6),
        };
        Grid.SetRow(box, 3);
        root.Children.Add(box);

        var panel = new Window
        {
            Title = "拖放探针（--dragprobe）",
            Width = 620,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Content = root,
        };

        var wa = SystemParameters.WorkArea;
        panel.Left = wa.Left + Math.Max(20, (wa.Width - 620) / 2 - 260);
        panel.Top = wa.Top + 60;

        session.BindView(box, modeText);
        return panel;
    }

    private static Button MakeButton(string text) => new Button
    {
        Content = text,
        FontSize = 12,
        Height = 28,
        Padding = new Thickness(10, 0, 10, 0),
        Margin = new Thickness(0, 0, 6, 6),
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        BorderThickness = new Thickness(1),
    };
}

/// <summary>探针尺寸常量（DragProbe 与 ProbeSession 共用）。</summary>
internal static class DragProbeSizes
{
    public const double BallWindowSize = 48;
    public const double BigWindowSize = 160;
    public const double BallDiameter = 40;
    public const double SlimWidth = 8;
    public const double SlimHeight = 36;
}

/// <summary>
/// 一次探针会话：日志落盘 + 面板显示 + 球体形态切换 + 拖放事件解析。
/// </summary>
internal sealed class ProbeSession
{
    /// <summary>虚拟文件（无磁盘路径）的判定信号。注意 FileNameW/FileName 不算 —— 它们只是「建议文件名」，
    /// 资源管理器这类有真实路径的源程序也会提供，放进这里会产生误报。</summary>
    private static readonly string[] VirtualFileFormats =
    {
        "FileGroupDescriptorW", "FileGroupDescriptor", "FileContents",
    };

    private static readonly string[] HighlightFormats =
    {
        "FileDrop", "UnicodeText", "Text", "HTML Format", "Rich Text Format", "RTF",
        // FileNameW / FileName（CFSTR_FILENAMEW）是源程序对**虚拟文件**建议的保存名。
        // 实测微信拖图片时 FileDrop 给的是 temp 目录下的哈希文件名（6338bbca….jpg），
        // 原始名很可能就藏在这里，必须一并取出来看。
        "FileNameW", "FileName",
    };

    private readonly string _logPath;
    private StreamWriter? _writer;
    private TextBox? _view;
    private TextBlock? _modeText;
    private Window? _ball;
    private Ellipse? _ballShape;
    private Border? _frame;

    private bool _slim;
    private bool _big;
    private int _eventCount;

    // DragOver 高频（每秒几十次），只累计不逐条写；DragLeave/Drop 时汇总输出
    private int _overCount;
    private int _overOffShapeCount;
    private DateTime _enterTime;
    private Point _lastOverPos;

    public ProbeSession(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Path.GetTempPath());
        try
        {
            _writer = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch { _writer = null; }
    }

    public string LogDirectory => Path.GetDirectoryName(_logPath) ?? Path.GetTempPath();

    /// <summary>接流实测的落盘目录（虚拟文件流会被写成真实文件，供事后核对）。</summary>
    public string StreamDumpDir
    {
        get
        {
            var dir = Path.Combine(LogDirectory, "streamdump");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void BindView(TextBox view, TextBlock modeText)
    {
        _view = view;
        _modeText = modeText;
        RefreshMode();
    }

    public void BindBall(Ellipse shape, Window ball, Border frame)
    {
        _ballShape = shape;
        _ball = ball;
        _frame = frame;
        RefreshMode();
    }

    /// <summary>写一行到日志与面板。</summary>
    public void Write(string text)
    {
        try { _writer?.WriteLine(text); } catch { /* 磁盘满等极端情况不阻断探针 */ }

        if (_view != null)
        {
            _view.AppendText(text + Environment.NewLine);
            _view.ScrollToEnd();
        }
    }

    public void ClearView() => _view?.Clear();

    // ── 球体形态切换 ──

    public void ToggleSlim()
    {
        _slim = !_slim;
        if (_slim) _big = false;
        ApplyShape();
        Write($"── 形态切换：{(_slim ? "吸附态 8×36（模拟悬浮球贴边收起）" : "正常 48×48")} ──");
    }

    public void ToggleBig()
    {
        _big = !_big;
        _slim = false;
        ApplyShape();
        Write($"── 形态切换：{(_big ? "大窗口 160×160（可见圆仍 40×40，用于测透明区）" : "正常 48×48")} ──");
    }

    private void ApplyShape()
    {
        if (_ball == null || _ballShape == null) return;

        if (_slim)
        {
            _ball.Width = DragProbeSizes.SlimWidth;
            _ball.Height = DragProbeSizes.SlimHeight;
            _ballShape.Width = DragProbeSizes.SlimWidth;
            _ballShape.Height = DragProbeSizes.SlimHeight;
            _ballShape.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            _ball.Opacity = 0.6;
        }
        else
        {
            var size = _big ? DragProbeSizes.BigWindowSize : DragProbeSizes.BallWindowSize;
            _ball.Width = size;
            _ball.Height = size;
            _ballShape.Width = DragProbeSizes.BallDiameter;
            _ballShape.Height = DragProbeSizes.BallDiameter;
            _ballShape.Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            _ball.Opacity = 0.85;
        }

        KeepOnScreen();
        if (_frame != null)
            _frame.Visibility = _big ? Visibility.Visible : Visibility.Collapsed;
        RefreshMode();
    }

    private void KeepOnScreen()
    {
        if (_ball == null) return;
        var wa = SystemParameters.WorkArea;
        if (_ball.Left + _ball.Width > wa.Right) _ball.Left = wa.Right - _ball.Width - 20;
        if (_ball.Top + _ball.Height > wa.Bottom) _ball.Top = wa.Bottom - _ball.Height - 40;
        if (_ball.Left < wa.Left) _ball.Left = wa.Left + 20;
        if (_ball.Top < wa.Top) _ball.Top = wa.Top + 20;
    }

    private void RefreshMode()
    {
        if (_modeText == null) return;
        var shape = _slim ? "吸附态 8×36" : (_big ? "大窗口 160×160" : "正常 48×48");
        _modeText.Text = $"当前形态：{shape}　|　累计事件：{_eventCount}";
    }

    // ── 拖放事件解析 ──

    public void HandleDrag(string kind, DragEventArgs e)
    {
        if (_ball == null) return;

        var pos = e.GetPosition(_ball);
        var now = DateTime.Now;

        if (kind == "DragOver")
        {
            _overCount++;
            _lastOverPos = pos;
            // 透明区计数：≥1 次说明「窗口矩形内、圆外」那片透明区**也能**命中；
            // 恒为 0 说明透明区穿透（指针一到圆外就收不到了）。
            if (!IsOnVisibleShape(pos)) _overOffShapeCount++;
            // 拒绝拖放：保护用户文件绝不被移动/复制
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (kind == "DragEnter")
        {
            _overCount = 0;
            _overOffShapeCount = 0;
            _enterTime = now;
            _eventCount++;
            FlashBall();
        }

        if (kind is "DragLeave" or "Drop")
        {
            _eventCount++;
            FlashBall();
        }

        var sb = new StringBuilder();
        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine($"[{now:HH:mm:ss.fff}] {kind}");

        // 拖放持续时间与 DragOver 心跳频率 —— 决定「悬停 N 秒触发」这类交互可不可做
        if (kind is "DragLeave" or "Drop")
        {
            var span = (now - _enterTime).TotalMilliseconds;
            var hz = span > 1 ? _overCount / (span / 1000.0) : 0;
            sb.AppendLine($"  拖放持续：{span:F0} ms　DragOver 心跳：{_overCount} 次（约 {hz:F0} 次/秒）");
            sb.AppendLine($"  最后一次 DragOver 位置：{_lastOverPos.X:F1}, {_lastOverPos.Y:F1}");
            sb.AppendLine(_overOffShapeCount > 0
                ? $"  透明区命中：有 {_overOffShapeCount} 次 DragOver 落在圆外 → **透明区也能接收拖放**"
                : "  透明区命中：0 次落在圆外（全程贴着可见图形）");
        }

        sb.AppendLine($"  窗口尺寸：{_ball.Width:F0} × {_ball.Height:F0}　形态：{(_slim ? "吸附态" : _big ? "大窗口" : "正常")}");
        sb.AppendLine($"  指针位置（窗口内）：{pos.X:F1}, {pos.Y:F1}");
        // 只有 DragEnter 的坐标可信：实测 DragLeave 时 GetPosition 恒返回 (4,4)，
        // 拿它做圆心距判定会得出「透明区也能命中」的假结论，所以只在 DragEnter 输出这一行。
        if (kind == "DragEnter")
            sb.AppendLine($"  是否落在可见图形内：{(IsOnVisibleShape(pos) ? "是" : "否 ← 进窗口第一点就在圆外，说明透明区不穿透")}");
        sb.AppendLine($"  允许的效果 AllowedEffects：{e.AllowedEffects}");
        sb.AppendLine($"  源程序前台窗口：{DescribeForegroundWindow()}");

        var data = e.Data;
        if (data == null)
        {
            sb.AppendLine("  ⚠ IDataObject 为 null，无法读取格式");
            Write(sb.ToString().TrimEnd());
            RefreshMode();
            return;
        }

        var formatsConvert = SafeFormats(data, true);
        var formatsRaw = SafeFormats(data, false);
        sb.AppendLine($"  格式总数：autoConvert=true → {formatsConvert.Count} 个；autoConvert=false → {formatsRaw.Count} 个");
        sb.AppendLine($"  格式清单（autoConvert=true）：{string.Join(", ", formatsConvert)}");

        var extra = formatsConvert
            .Where(f => !HighlightFormats.Contains(f) && !VirtualFileFormats.Contains(f))
            .ToList();
        if (extra.Count > 0)
            sb.AppendLine($"  其余格式（未逐条取值）：{string.Join(", ", extra)}");

        sb.AppendLine("  ── 逐个关键格式取值 ──");
        var verdicts = new List<string>();

        foreach (var fmt in formatsConvert.Where(f => HighlightFormats.Contains(f)))
        {
            var (ok, desc) = TryDescribe(data, fmt);
            var note = fmt == "Text" ? "　⚠ 这是 ANSI 版（CF_TEXT），实测 Chromium 给的中文会乱码，取值一律以 UnicodeText 为准" : "";
            sb.AppendLine($"  [{fmt}] {(ok ? desc : "取不到 → " + desc)}{note}");
        }

        var paths = TryGetFileDrop(data);
        if (paths.Count > 0)
        {
            sb.AppendLine($"  [文件判定] 真实文件路径 {paths.Count} 个：");
            foreach (var p in paths)
            {
                var exists = File.Exists(p);
                var isDir = Directory.Exists(p);
                var sizeText = "";
                try { if (exists) sizeText = $"（{new FileInfo(p).Length} 字节）"; } catch { }
                sb.AppendLine($"      {(exists || isDir ? "✓" : "✗")} {p}　存在={exists} 是目录={isDir}{sizeText}");
            }
            verdicts.Add("真实文件（带磁盘路径，可直接交给现有文件仓库）");
        }

        var virtualHits = formatsConvert.Where(f => VirtualFileFormats.Contains(f)).ToList();
        if (virtualHits.Count > 0)
        {
            if (paths.Count == 0)
            {
                sb.AppendLine($"  [虚拟文件标志] 无磁盘路径，命中格式：{string.Join(", ", virtualHits)}");
                foreach (var fmt in virtualHits)
                    sb.AppendLine($"  [{fmt}] {DescribeVirtualFormat(data, fmt)}");
                // 接流落盘实测：直接回答「虚拟文件流存下来到底是不是一张真图」
                sb.AppendLine($"  [接流实测] {TryDumpStream(data, StreamDumpDir)}");
                verdicts.Add("⚠ 虚拟文件流（无磁盘路径）—— 需要额外接流落盘，现有链路直接吃不下");
            }
            else
            {
                // 资源管理器/WPS 实测总会顺带提供这几个格式作为备选（即使 FileDrop 有真实路径），
                // 早期版本在这里一律报「虚拟文件」属于误报，故只在**没有 FileDrop** 时才当信号。
                sb.AppendLine($"  [注] 源程序另提供了 {string.Join("/", virtualHits)}，但 FileDrop 已有真实路径，以路径为准");
            }
        }

        var textPreview = TryGetText(data);
        if (textPreview.Length > 0)
            verdicts.Add($"含文本（{textPreview.Length} 字）");

        sb.AppendLine(verdicts.Count > 0
            ? "  ★ 判定：" + string.Join("；", verdicts)
            : "  ★ 判定：未识别出可用内容（可能只是窗口被划过）");

        // 拒绝拖放：绝不让源程序对用户文件做实际动作
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        Write(sb.ToString().TrimEnd());
        RefreshMode();
    }

    private static List<string> SafeFormats(IDataObject data, bool autoConvert)
    {
        try { return data.GetFormats(autoConvert).ToList(); }
        catch { return new List<string>(); }
    }

    private static (bool Ok, string Desc) TryDescribe(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format)) return (false, "GetDataPresent=false");
            var value = data.GetData(format, true);
            if (value == null) return (false, "值为 null");

            switch (value)
            {
                case string s:
                    var flat = s.Replace("\r", " ").Replace("\n", " ");
                    return (true, $"类型=String 长度={s.Length}　预览：{Truncate(flat, 120)}");
                case string[] arr:
                    // FileNameW / FileName 返回的是 String[]，只报「共 N 项」等于没测 ——
                    // 微信拖图片时原始文件名是否藏在里面，全靠这一行看清。
                    var items = arr.Length == 0
                        ? "（空数组）"
                        : string.Join(" | ", arr.Take(3).Select(x =>
                              Truncate((x ?? "").Replace("\r", " ").Replace("\n", " "), 90)))
                          + (arr.Length > 3 ? $" …（共 {arr.Length} 项）" : "");
                    return (true, $"类型=String[] 共 {arr.Length} 项：{items}");
                case Stream stream:
                    return (true, $"类型=Stream（{stream.Length} 字节，需接流落盘）");
                default:
                    return (true, $"类型={value.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>虚拟文件格式的取证：只报类型与大小，不拉取数据本体。</summary>
    private static string DescribeVirtualFormat(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format)) return "GetDataPresent=false";
            // FileContents 是数据本体，取值会把整个文件（可能几十 MB）拉进内存 —— 探针只看格式，不取样
            if (string.Equals(format, "FileContents", StringComparison.Ordinal))
                return "存在（数据流本体，未取样以免拉取整个文件）";
            var value = data.GetData(format);
            return value switch
            {
                Stream s => $"类型=Stream，{s.Length} 字节（FILEGROUPDESCRIPTOR 结构，内含文件名与大小）",
                null => "值为 null",
                _ => $"类型={value.GetType().Name}",
            };
        }
        catch (Exception ex)
        {
            return "取不到 → " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// 把 FileContents 流真的写到磁盘，再用文件头魔数判断「它到底是不是一张真图片」。
    /// 这是回答"浏览器拖来的网页图片，存下来是不是真图"的唯一办法 ——
    /// 只看格式类型名答不了这个问题，必须落盘看字节。
    /// </summary>
    private static string TryDumpStream(IDataObject data, string outDir)
    {
        const long MaxBytes = 100L * 1024 * 1024;
        try
        {
            if (!data.GetDataPresent("FileContents")) return "源程序没提供 FileContents";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (data.GetData("FileContents") is not Stream stream)
                return "取到的不是 Stream，拿不到文件本体";

            var path = Path.Combine(outDir, $"stream-{DateTime.Now:HHmmss}.bin");
            long total = 0;
            var buffer = new byte[81920];
            using (var fs = File.Create(path))
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fs.Write(buffer, 0, read);
                    total += read;
                    if (total >= MaxBytes) break;
                }
            }
            sw.Stop();
            return $"✅ 成功落盘 {total} 字节（{sw.ElapsedMilliseconds} ms）→ {path}"
                 + Environment.NewLine + $"      文件头判定：{GuessFileKind(path)}";
        }
        catch (Exception ex)
        {
            return $"❌ 接流失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>按文件头魔数判断落盘的东西是不是真图片（族谱图/空壳/链接都会在这里露馅）。</summary>
    private static string GuessFileKind(string path)
    {
        try
        {
            var head = new byte[16];
            using var fs = File.OpenRead(path);
            var n = fs.Read(head, 0, head.Length);
            if (n >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                return "PNG —— 是一张真图片";
            if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
                return "JPEG —— 是一张真图片";
            if (n >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46)
                return "GIF —— 是一张真图片";
            if (n >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
                && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
                return "WebP —— 是一张真图片";
            if (n >= 2 && head[0] == 0x42 && head[1] == 0x4D)
                return "BMP —— 是一张真图片";
            if (n >= 8 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70)
                return "ISO 媒体文件（mp4/mov 类）—— 是真实媒体数据";
            if (n >= 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46)
                return "PDF —— 是真实文档数据";
            if (n >= 2 && head[0] == 0x50 && head[1] == 0x4B)
                return "ZIP/OOXML 容器 —— 是真实压缩数据";
            return "不是常见图片格式（可能是 HTML/文本或其它数据）";
        }
        catch
        {
            return "文件头读取失败";
        }
    }

    private static List<string> TryGetFileDrop(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
            return data.GetData(DataFormats.FileDrop) as string[] is { } arr
                ? arr.ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static string TryGetText(IDataObject data)
    {
        foreach (var fmt in new[] { DataFormats.UnicodeText, DataFormats.Text })
        {
            try
            {
                if (data.GetDataPresent(fmt) && data.GetData(fmt) is string s && s.Length > 0)
                    return s;
            }
            catch { }
        }
        return "";
    }

    /// <summary>
    /// 指针是否落在**可见图形**内。
    /// 落在外面的「窗口矩形内、图形外」区域却仍收到事件 = 透明区也能命中（好事，将来可用来放热区）；
    /// 收不到事件 = 透明区穿透（那热区必须做成独立窗口，不能靠透明区接）。
    /// </summary>
    private bool IsOnVisibleShape(Point p)
    {
        if (_ball == null) return false;
        var cx = _ball.Width / 2;
        var cy = _ball.Height / 2;

        if (_slim)
            return Math.Abs(p.X - cx) <= DragProbeSizes.SlimWidth / 2 + 1
                && Math.Abs(p.Y - cy) <= DragProbeSizes.SlimHeight / 2 + 1;

        var dx = p.X - cx;
        var dy = p.Y - cy;
        // +0.5 = 椭圆那 1px 描边的外缘。实测 DragEnter 的落点圆心距集中在 19.68~20.35，
        // 按纯几何半径 20 判会把「贴着描边」的落点误报成「透明区命中」（第一版就栽在这，
        // 日志里那几行"透明区也能接收拖放"是判定半径偏小造出来的假信号）。
        var r = DragProbeSizes.BallDiameter / 2 + 0.5;
        return dx * dx + dy * dy <= r * r;
    }

    private void FlashBall()
    {
        if (_ballShape == null) return;
        // 吸附态也要闪 —— 第一版在这里直接 return 了，导致用户拖到细条上时**完全没有任何反馈**，
        // 误以为「吸附态收不到拖放」（实测日志里其实收到了 11 次）。反馈缺失会被读成功能失效。
        var idle = _slim
            ? Color.FromRgb(0x55, 0x55, 0x55)
            : Color.FromRgb(0x3A, 0x3A, 0x3A);

        _ballShape.Fill = new SolidColorBrush(Colors.LimeGreen);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            if (_ballShape != null)
                _ballShape.Fill = new SolidColorBrush(idle);
        };
        timer.Start();
    }

    private static string DescribeForegroundWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "(取不到)";
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return $"(hwnd={hwnd}, pid 未知)";
            var proc = Process.GetProcessById((int)pid);
            return $"{proc.ProcessName}.exe (pid={pid})";
        }
        catch (Exception ex)
        {
            return "(异常：" + ex.GetType().Name + ")";
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
