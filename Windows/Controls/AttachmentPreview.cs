using System.Windows.Media.Imaging;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows.Controls;

/// <summary>
/// 附件悬停预览浮层（2026-09-14 二版）。
///
/// 为何不用 WPF 的 ToolTip：
/// ① ToolTip 有约 400ms 的系统级延迟，且每次显示都要走一遍 ToolTipService 的完整流程
///    （含 PopupRoot 的创建），实测悬停后要一两秒才出图 —— 用户体感明显卡顿；
/// ② 自绘 Popup 实例全局复用，窗口只创建一次，配合解码结果缓存，第二次起显示几乎无延迟。
///
/// 生命周期：由宿主窗口持有，窗口关闭时 Dispose。同一时刻只有一个预览，跟随鼠标位置。
/// </summary>
public sealed class AttachmentPreviewHost : IDisposable
{
    /// <summary>悬停多久出预览。比系统 ToolTip 的 400ms 快一倍多，但不至于滑过就闪</summary>
    private const int HoverDelayMs = 140;

    /// <summary>图片缓存上限（超出整体清空，实现简单且不会长期占内存）</summary>
    private const int CacheLimit = 24;

    private static readonly Dictionary<string, ImageSource> ThumbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> FullCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Popup _popup;
    private readonly StackPanel _stack;
    private readonly TextBlock _title;
    private readonly TextBlock _sub;
    private readonly Image _image;
    private readonly Border _root;
    private readonly DispatcherTimer _timer;

    private ChatAttachment? _pending;
    private bool _prewarmed;
    private bool _disposed;

    public AttachmentPreviewHost()
    {
        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        _sub = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };

        _image = new Image
        {
            MaxWidth = 340,
            MaxHeight = 260,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        _stack = new StackPanel();
        _stack.Children.Add(_title);
        _stack.Children.Add(_image);
        _stack.Children.Add(_sub);

        _root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            MaxWidth = 380,
            Child = _stack,
        };

        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Placement = PlacementMode.Mouse,
            HorizontalOffset = 14,
            VerticalOffset = 18,
            PopupAnimation = PopupAnimation.None,   // 不要动画：动画本身就是延迟
            Child = _root,
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverDelayMs) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            ShowNow();
        };
    }

    /// <summary>
    /// 预热：提前把 Popup 的宿主窗口建出来。
    /// Popup 首次显示要创建 PopupRoot 窗口，这一步在低配机上可感知（百余毫秒）；
    /// 在窗口加载完成后以 0 透明度偷偷开合一次，把这份开销挪到用户看不见的时候。
    /// </summary>
    public void Prewarm()
    {
        if (_prewarmed || _disposed) return;
        _prewarmed = true;
        try
        {
            _popup.Opacity = 0;
            _popup.IsOpen = true;
            _popup.IsOpen = false;
            _popup.Opacity = 1;
        }
        catch
        {
            // 预热纯粹是优化，失败不影响功能
        }
    }

    /// <summary>鼠标进入附件卡片：开始计时准备显示预览</summary>
    public void HoverEnter(ChatAttachment att)
    {
        if (_disposed) return;
        _pending = att;

        if (_popup.IsOpen)
        {
            // 预览已经开着（鼠标从一张卡片滑到相邻卡片）：直接换内容，不再等延迟
            ShowNow();
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>鼠标离开附件卡片：立即收起</summary>
    public void HoverLeave()
    {
        if (_disposed) return;
        _timer.Stop();
        _pending = null;
        _popup.IsOpen = false;
    }

    private void ShowNow()
    {
        var att = _pending;
        if (att == null || _disposed) return;

        _title.Text = att.FileName;

        var path = ChatAttachmentService.ResolvePath(att);

        if (att.Kind == ChatAttachmentKind.Image)
        {
            var src = LoadImage(path, 480, ThumbCache);
            if (src != null)
            {
                _image.Source = src;
                _image.Visibility = Visibility.Visible;
                _sub.Text = $"{ChatAttachmentViewModel.FormatSize(att.SizeBytes)} · {att.PixelWidth}×{att.PixelHeight}    双击看大图";
            }
            else
            {
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
                _sub.Text = "图片不在本机（仅存于原设备）";
            }
        }
        else
        {
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            _sub.Text = $"文件 · {ChatAttachmentViewModel.FormatSize(att.SizeBytes)}    双击用默认程序打开";
        }

        _popup.IsOpen = true;
    }

    /// <summary>按需解码并缓存（限制解码宽度省内存；OnLoad + Freeze 免锁文件、可跨线程）</summary>
    internal static ImageSource? LoadImage(string path, int decodeWidth, Dictionary<string, ImageSource> cache)
    {
        if (!File.Exists(path)) return null;
        if (cache.TryGetValue(path, out var hit)) return hit;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();

            if (cache.Count >= CacheLimit) cache.Clear();
            cache[path] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    internal static ImageSource? LoadFullImage(string path) => LoadImage(path, 0, FullCache);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _pending = null;
        _popup.IsOpen = false;
        _popup.Child = null;
    }
}

/// <summary>
/// 双击附件后的大图查看窗（2026-09-14）：全屏深色底，初始按屏幕自适应，滚轮缩放，单击或 Esc 关闭。
/// 独立成窗而不是调用系统看图程序，是为了不打断"问答 → 看图 → 继续问"这条主线。
/// </summary>
public sealed class ImagePreviewWindow : Window
{
    private readonly ScrollViewer _scroll;
    private readonly Image _image;
    private double _zoom = 1;
    private bool _zoomInitialized;

    public ImagePreviewWindow(string fileName, ImageSource source)
    {
        Title = fileName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D));
        Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Cursor = Cursors.SizeAll;

        _image = new Image
        {
            Source = source,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _image,
            PanningMode = PanningMode.Both,
        };

        var hint = new TextBlock
        {
            Text = "滚轮缩放 · 单击或 Esc 关闭",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 14, 10),
            IsHitTestVisible = false,
        };

        var title = new TextBlock
        {
            Text = fileName,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 12, 16, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };

        var grid = new Grid();
        grid.Children.Add(_scroll);
        grid.Children.Add(title);
        grid.Children.Add(hint);
        Content = grid;

        Loaded += (_, _) => InitZoom();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            // 单击关闭：看图场景下这是最顺手的退出方式（与系统截图预览一致）
            if (e.ClickCount >= 1) Close();
        };
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>初始缩放：大图缩到适应屏幕，小图保持原尺寸不放大</summary>
    private void InitZoom()
    {
        if (_zoomInitialized || _image.Source == null) return;
        _zoomInitialized = true;

        var vw = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth;
        var vh = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight;
        var iw = _image.Source.Width;
        var ih = _image.Source.Height;
        if (vw <= 0 || vh <= 0 || iw <= 0 || ih <= 0) return;

        _zoom = Math.Min(1.0, Math.Min(vw / iw, vh / ih));
        ApplyZoom();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _zoom = Math.Clamp(_zoom * factor, 0.1, 8.0);
        ApplyZoom();
        e.Handled = true;
    }

    private void ApplyZoom() =>
        _image.LayoutTransform = new ScaleTransform(_zoom, _zoom);
}
