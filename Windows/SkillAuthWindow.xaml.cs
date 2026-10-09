using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusCapture.Services;
using FocusCapture.Services.Skills;

namespace FocusCapture.Windows;

/// <summary>
/// 授权窗口当前停在哪一步。**它不是内部实现细节，而是"用户看到的东西"** ——
/// 暴露出来是为了让检查点能断言"失败之后没有偷偷进入出码阶段"
/// （"看着像在出码、其实已经失败"正是当初那场事故的形态）。
/// </summary>
public enum AuthStage
{
    /// <summary>还没开始</summary>
    Pending,

    /// <summary>等用户选「创建应用」还是「用现成凭据」（只有第一次用才会停在这）</summary>
    FirstRun,

    /// <summary>正在准备应用凭据（可能要用户在浏览器里建应用）</summary>
    Preparing,

    /// <summary>准备就绪，正在申请授权码 / 出二维码 / 等扫码</summary>
    Scanning,

    /// <summary>授权完成</summary>
    Done,

    /// <summary>停住了 —— 失败、取消或用户没确认</summary>
    Failed,

    /// <summary>
    /// 依赖压根不在本机（应用没自带、也没下载过）—— 授权根本开始不了，得先把它装下来。
    /// 与 <see cref="Failed"/> 分开：那不是失败，是"还差一步"。
    /// </summary>
    NotInstalled,
}

/// <summary>
/// Skill 依赖授权窗口（2026-09-20；2026-09-21 改「先准备再扫码」）。
///
/// <para>
/// <b>它存在的理由：</b>阶段一交付时，Skill 需要授权却没有任何应用内入口 —— 模型只能照着
/// CLI 输出里的提示自己发挥，最后变成"请你在系统里跑这条命令"，还带着开发机上的安装路径。
/// 授权是宿主的事：这里把全部步骤做完，用户只做两件事 —— **第一次建一个应用、扫码**。
/// </para>
/// <para>
/// <b>两步，不是一步（文案必须照这个写）：</b>
/// ① 应用凭据（第一次使用才有）：浏览器里创建应用，或粘贴现成凭据；
/// ② 用户授权：扫码。
/// 少了第①步，<c>auth login</c> 连 device_code 都拿不到，**二维码根本画不出来** ——
/// 所以界面绝不能写"扫码即可"，那是只在第二台以后的机器上才成立的说法。
/// </para>
/// <para>
/// <b>三种进入形态：</b>凭据已就绪 → 零点击自动出码（老体验不变）；
/// 凭据没配 → 停在选择面板（创建应用 / 用现成凭据）；用户没选就不开始 ——
/// 因为这两条路都必须他动手（去浏览器 / 粘凭据），自动开跑的后果只是弹他一脸浏览器。
/// </para>
/// <para>
/// <b>线程：</b>流程跑在 UI 线程上（<c>await</c> 不阻塞消息循环）；来自"读进程输出"线程的
/// 回调必须经 <c>Dispatcher.Invoke</c> 回到 UI 线程 —— 那个线程上碰控件会抛。
/// </para>
/// </summary>
public partial class SkillAuthWindow : Window
{
    private readonly SkillDependency _dep;
    private readonly List<(DepCredentialField Field, Control Input)> _credentialInputs = new();

    /// <summary>最近一次探测到的状态（2026-09-21 起是字段：下载装好之后要重新探一次）</summary>
    private DependencyStatus _status;


    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private DateTime _deadline;
    private bool _closed;
    private bool _cancelled;
    private string? _tempDir;

    /// <summary>当前阶段（检查点断言用，也是排查问题时最想知道的一件事）</summary>
    public AuthStage Stage { get; private set; } = AuthStage.Pending;

    /// <summary>二维码那块目前的提示文案 —— 失败之后它必须不再是"正在生成二维码"（R10）</summary>
    public string QrPlaceholderMessage => QrPlaceholder.Text;

    /// <summary>兜底入口渲染了几个输入框（应当与协议给的字段数一致）</summary>
    public int CredentialInputCount => _credentialInputs.Count;

    /// <summary>授权协议（2026-09-21 起从依赖基类搬到实现类）。为 null = 这个依赖不需要登录。</summary>
    private IDepAuthFlow? Flow => _dep.Flow;

    public SkillAuthWindow(SkillDependency dep, DependencyStatus status)
    {
        InitializeComponent();
        _dep = dep;
        _status = status;

        HeadText.Text = $"用飞书扫码授权 —— {dep.DisplayName}";
        DepText.Text = $"当前状态：{status.Detail}\n本次将申请：{dep.AuthScopeText}";

        BuildCredentialInputs();

        if (!status.Resolved)
        {
            // 组件不在位：授权根本没法开始（拿不到码、画不出二维码）→ 给出应用内的下一步
            Stage = AuthStage.NotInstalled;
            MissingPanel.Visibility = Visibility.Visible;
            QrBorder.Visibility = Visibility.Collapsed;
            ScanHintText.Visibility = Visibility.Collapsed;
            UrlBox.Visibility = Visibility.Collapsed;
            StatusText.Text = "缺少必要的组件，先装好它才能授权。";
        }
        else if (status.NeedsPrepare && Flow != null)
        {
            // 第一次用：停在这儿等用户选路，不自动开跑
            Stage = AuthStage.FirstRun;
            FirstRunText.Text =
                "这台电脑还没配过飞书应用。第一次使用需要先有一个应用 —— " +
                "点下面的按钮，会在浏览器里打开创建页面，跟着点一下就行。\n" +
                "创建完成后这里会自动继续，然后是扫码 —— 这是两步，不是扫一次码就完事。";
            FirstRunPanel.Visibility = Visibility.Visible;
            QrBorder.Visibility = Visibility.Collapsed;
            ScanHintText.Visibility = Visibility.Collapsed;
            UrlBox.Visibility = Visibility.Collapsed;
            StatusText.Text = "等待选择：创建新应用，或使用已有凭据。";
        }

        Loaded += async (_, _) =>
        {
            // 已经配好凭据的机器：保持原来的零点击体验（申请码 → 出码 → 等扫码）
            // 另外两种形态（第一次用 / 组件没装）都要等用户先动手，不许自动开跑
            if (Stage == AuthStage.Pending) await StartAsync().ConfigureAwait(true);
        };
        Closed += (_, _) => Cleanup();
    }

    /// <summary>
    /// 下载缺失的依赖组件 —— 与设置页那个入口共用同一个下载器与同一套配方（两处不各写一套）。
    ///
    /// <para>
    /// ⚠ 这是在 UI 事件处理器里做外部 IO（网络 + 落盘），**必须自己 try 住**：
    /// 异常冒泡到全局处理器会弹一个模态框，而模态框会吃掉后续点击 ——
    /// 用户看到的现象是"点了没反应"（本项目踩过这个坑）。
    /// </para>
    /// </summary>
    private async void OnDownloadDependency(object sender, RoutedEventArgs e)
    {
        var recipe = RuntimeRecipes.For(_dep.Id);
        if (recipe == null)
        {
            MissingText.Text = "这个组件没有已知的下载来源，暂时装不了。";
            return;
        }

        DownloadDepButton.IsEnabled = false;
        try
        {
            var downloader = new RuntimeDownloader { OnLog = m => AppLog.Info("Skill", m) };
            // Progress 在 UI 线程上创建 → 回调自动回到 UI 线程（读流线程上碰控件会抛）
            var progress = new Progress<RuntimeProgress>(p => MissingText.Text = p.Describe());

            var result = await downloader.InstallAsync(recipe, progress).ConfigureAwait(true);
            if (!result.Ok)
            {
                MissingText.Text = "没装成：" + result.Detail;
                DownloadDepButton.IsEnabled = true;
                AppLog.Warn("Skill", $"依赖组件安装失败（{_dep.Id}）：{result.Detail}");
                return;
            }

            MissingText.Text = "已装好，正在重新检查…";
            _status = await _dep.ProbeAsync().ConfigureAwait(true);
            if (!_status.Resolved)
            {
                MissingText.Text = "装好了，但状态仍不对：" + _status.Detail;
                DownloadDepButton.IsEnabled = true;
                return;
            }

            // 回到正常流程：把它藏起来，把扫码区放出来，接着走"先准备再扫码"
            MissingPanel.Visibility = Visibility.Collapsed;
            QrBorder.Visibility = Visibility.Visible;
            ScanHintText.Visibility = Visibility.Visible;
            UrlBox.Visibility = Visibility.Visible;
            Stage = AuthStage.Pending;
            await StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Skill", $"下载依赖组件出错（{_dep.Id}）", ex);
            MissingText.Text = "下载出错：" + ex.Message;
            DownloadDepButton.IsEnabled = true;
        }
    }

    // ────────────────────────── 主流程 ──────────────────────────

    /// <summary>
    /// 开始流程：**先准备应用凭据，再扫码**。「创建应用」按钮与「重新生成二维码」都走它。
    /// </summary>
    public async Task StartAsync()
    {
        CancelCurrentFlow();          // 重入时先把上一轮收干净（device_code 会作废，不能复用）

        _cancelled = false;
        FirstRunPanel.Visibility = Visibility.Collapsed;
        CredentialPanel.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;

        var flow = Flow;
        if (flow == null)
        {
            ShowFailure($"{_dep.DisplayName} 不需要登录，没有可执行的授权流程。", "启动");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        // ── 阶段 0：前置配置 ──
        // 失败必须**停在这里**：没有应用凭据时 auth login 连 device_code 都拿不到，
        // 继续往下走只会让界面停在"正在生成二维码" —— 那正是当初那场事故的形态。
        if (!await EnsurePreparedAsync(flow, cts.Token).ConfigureAwait(true)) return;
        if (_cancelled || _closed) return;

        // ── 阶段 1..3：申请码 → 出二维码 → 领回 token ──
        Stage = AuthStage.Scanning;
        ShowScanUi("正在获取授权码…");

        var (ok, message, session) = await flow.StartAuthAsync(cts.Token).ConfigureAwait(true);
        if (_cancelled || _closed) return;
        if (!ok || session == null)
        {
            ShowFailure(message.Length > 0 ? message : "无法获取授权码。", "申请授权码");
            return;
        }

        UrlBox.Text = session.VerificationUrl;

        var png = await flow.MakeQrPngAsync(session.VerificationUrl, TempDir(), cts.Token).ConfigureAwait(true);
        if (_cancelled || _closed) return;

        if (png != null)
        {
            try
            {
                // OnLoad：把文件读进内存后立刻释放句柄，否则临时目录删不掉
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(png);
                bmp.EndInit();
                QrImage.Source = bmp;
                QrImage.Visibility = Visibility.Visible;
                QrPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Skill", $"二维码图片加载失败：{ex.Message}");
                QrPlaceholder.Text = "二维码图片无法显示\n可直接用下面的链接授权";
            }
        }
        else
        {
            // 降级不是失败：还可以走"复制链接到浏览器"。绝不假装已经出码。
            QrPlaceholder.Text = "二维码生成失败\n请点「在浏览器打开」，或复制下面的链接";
        }

        StartCountdown(session.ExpiresInSeconds);

        var (done, doneMsg) = await flow.CompleteAuthAsync(
            session.DeviceCode, session.ExpiresInSeconds * 1000, cts.Token).ConfigureAwait(true);

        if (_cancelled || _closed) return;
        StopCountdown();

        if (done)
        {
            Stage = AuthStage.Done;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xD1, 0x8F));
            StatusText.Text = "已授权。窗口即将关闭，你可以直接再说一次刚才那句话。";
            AppLog.Info("Skill", $"依赖授权完成：{_dep.Id}");
            await Task.Delay(700).ConfigureAwait(true);
            CloseWith(true);
            return;
        }

        ShowFailure(doneMsg, "等待扫码确认");
    }

    /// <summary>
    /// 前置配置（第一次用才真做事；已配置的机器这一步什么都不做）。
    /// 返回 false = **不许继续走出码**。
    /// </summary>
    private async Task<bool> EnsurePreparedAsync(IDepAuthFlow flow, CancellationToken ct)
    {
        Stage = AuthStage.Preparing;
        ShowScanUi("正在检查应用凭据…");
        QrPlaceholder.Text = "正在准备飞书应用…";

        DepPrepareResult result;
        try
        {
            result = await flow.PrepareAsync(ShowVerificationUrlAsync, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Skill", $"{_dep.Id} 前置配置异常", ex);
            ShowFailure($"准备应用凭据时出错：{ex.Message}", "准备应用凭据");
            return false;
        }

        if (result.State == DepPrepareState.NotNeeded) return true;   // 常态：已经配好了

        if (!result.CanContinue)
        {
            ShowFailure(result.Message.Length > 0 ? result.Message : "应用凭据还没准备好。", "准备应用凭据");
            return false;
        }

        AppLog.Info("Skill", $"{_dep.Id} 应用凭据已就绪：{result.Message}");
        return true;
    }

    /// <summary>准备过程中拿到浏览器链接时的回调 —— **它在"读进程输出"的线程上被调用**，必须封送</summary>
    private Task ShowVerificationUrlAsync(string url)
    {
        Dispatcher.Invoke(() =>
        {
            UrlBox.Visibility = Visibility.Visible;
            UrlBox.Text = url;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
            StatusText.Text = "已在浏览器里打开创建页面 —— 跟着点一下完成创建，这里会自动继续。";
            QrPlaceholder.Text = "正在等你在浏览器里完成应用创建…";
            TryOpenBrowser(url);
        });
        return Task.CompletedTask;
    }

    // ────────────────────────── 兜底入口：粘贴现成凭据 ──────────────────────────

    /// <summary>按协议给的字段渲染输入框（窗口**不需要认识**具体是哪个 CLI）</summary>
    private void BuildCredentialInputs()
    {
        CredentialInputs.Children.Clear();
        _credentialInputs.Clear();

        var flow = Flow;
        if (flow == null || flow.CredentialFields.Count == 0)
        {
            UseCredentialButton.Visibility = Visibility.Collapsed;   // 该依赖不支持粘贴凭据
            return;
        }

        foreach (var field in flow.CredentialFields)
        {
            CredentialInputs.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

            // 敏感字段用密码框：屏幕上不留明文（样式见 Window.Resources，深色一并给了）
            Control input = field.Secret
                ? new PasswordBox { MinHeight = 26 }
                : new TextBox { MinHeight = 26 };
            input.Margin = new Thickness(0, 0, 0, 10);
            CredentialInputs.Children.Add(input);
            _credentialInputs.Add((field, input));
        }
    }

    private Dictionary<string, string> ReadCredentialValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, input) in _credentialInputs)
        {
            values[field.Key] = input switch
            {
                PasswordBox p => p.Password,
                TextBox t => t.Text,
                _ => "",
            };
        }
        return values;
    }

    /// <summary>用后立即清空输入控件 —— 凭据在本应用里**不留副本**（也不写日志）</summary>
    private void ClearCredentialInputs()
    {
        foreach (var (_, input) in _credentialInputs)
        {
            if (input is PasswordBox p) p.Clear();
            else if (input is TextBox t) t.Text = "";
        }
    }

    private void OnShowCredential(object sender, RoutedEventArgs e)
    {
        Stage = AuthStage.FirstRun;
        FirstRunPanel.Visibility = Visibility.Collapsed;
        CredentialPanel.Visibility = Visibility.Visible;
        StatusText.Text = "粘贴凭据后点「用它继续」。";
    }

    private void OnBackToCreate(object sender, RoutedEventArgs e)
    {
        CredentialPanel.Visibility = Visibility.Collapsed;
        FirstRunPanel.Visibility = Visibility.Visible;
        StatusText.Text = "等待选择：创建新应用，或使用已有凭据。";
    }

    private void OnCreateApp(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void OnSubmitCredential(object sender, RoutedEventArgs e) => _ = SubmitCredentialAsync();

    private async Task SubmitCredentialAsync()
    {
        var flow = Flow;
        if (flow == null || _closed) return;

        var values = ReadCredentialValues();
        ClearCredentialInputs();     // 用后即清，别让它挂在界面上

        var cts = new CancellationTokenSource();
        _cts = cts;

        SubmitCredentialButton.IsEnabled = false;
        try
        {
            Stage = AuthStage.Preparing;
            CredentialPanel.Visibility = Visibility.Collapsed;
            ShowScanUi("正在保存应用凭据…");
            QrPlaceholder.Text = "正在保存应用凭据…";

            var result = await flow.PrepareWithCredentialAsync(values, cts.Token).ConfigureAwait(true);
            if (_cancelled || _closed) return;

            if (!result.CanContinue)
            {
                ShowFailure(result.Message.Length > 0 ? result.Message : "应用凭据没能保存。", "保存应用凭据");
                CredentialPanel.Visibility = Visibility.Visible;   // 让他改完再试（阶段仍停在 Failed）
                return;
            }

            AppLog.Info("Skill", $"{_dep.Id} 应用凭据已就绪（现成凭据）");
            await StartAsync().ConfigureAwait(true);   // 接着走扫码（其中会再核一次状态）
        }
        finally
        {
            SubmitCredentialButton.IsEnabled = true;
        }
    }

    // ────────────────────────── 界面状态 ──────────────────────────

    /// <summary>切到"出码/等待"那套界面（二维码区可见、提示隐藏）</summary>
    private void ShowScanUi(string status)
    {
        QrBorder.Visibility = Visibility.Visible;
        ScanHintText.Visibility = Visibility.Visible;
        UrlBox.Visibility = Visibility.Visible;
        QrImage.Visibility = Visibility.Collapsed;
        QrPlaceholder.Visibility = Visibility.Visible;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
        StatusText.Text = status;
    }

    /// <summary>
    /// 失败/停住时统一收口。
    ///
    /// <para>
    /// ⚠ <b>必须同时改二维码那块占位文案（R10）</b>：原实现只切显隐、不改文字，
    /// 于是"正在生成二维码…"和红字报错并排挂着 —— 用户以为它还在转，实际早就停了。
    /// </para>
    /// </summary>
    private void ShowFailure(string message, string stage)
    {
        Stage = AuthStage.Failed;
        StopCountdown();

        // R9：失败**写日志**，并且把阶段一并记上（原实现只把话丢给界面，事后翻不到任何线索）
        AppLog.Warn("Skill", $"{_dep.Id} 授权未完成（阶段：{stage}）：{message}");

        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x9A));
        StatusText.Text = message.Length > 0 ? message : "授权未完成。";
        RetryButton.Visibility = Visibility.Visible;

        var qrShown = QrImage.Visibility == Visibility.Visible;
        QrPlaceholder.Visibility = qrShown ? Visibility.Collapsed : Visibility.Visible;
        QrPlaceholder.Text = qrShown ? "" : "授权没有完成\n点「重新生成二维码」可以再试一次";
        ScanHintText.Visibility = qrShown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartCountdown(int seconds)
    {
        StopCountdown();
        _deadline = DateTime.Now.AddSeconds(seconds);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var left = _deadline - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            StatusText.Text = $"等待扫码并在飞书中确认… 剩余 {left:mm\\:ss}";
        };
        _timer.Start();
        StatusText.Text = $"等待扫码并在飞书中确认… 剩余 {TimeSpan.FromSeconds(seconds):mm\\:ss}";
    }

    private void StopCountdown()
    {
        _timer?.Stop();
        _timer = null;
    }

    private string TempDir()
    {
        if (_tempDir != null && Directory.Exists(_tempDir)) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "fc-skill-auth-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = StartAsync();

    /// <summary>打开浏览器。**外部操作一律自己兜住** —— 抛出去会冒泡成模态框，把后续点击全吃掉。</summary>
    private void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Skill", $"打开配置链接失败：{ex.Message}");
            StatusText.Text = "没能自动打开浏览器 —— 请点「在浏览器打开」，或复制上面的链接手动打开。";
        }
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text;
        if (string.IsNullOrWhiteSpace(url)) return;
        TryOpenBrowser(url);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        CloseWith(false);
    }

    /// <summary>关窗幂等：自动完成与用户取消可能撞在一起，重复设置 DialogResult 会抛</summary>
    private void CloseWith(bool result)
    {
        if (_closed) return;
        _closed = true;
        CancelCurrentFlow();
        try { DialogResult = result; } catch { /* 不是模态打开时设置会抛，忽略 */ }
        Close();
    }

    private void CancelCurrentFlow()
    {
        StopCountdown();
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        _cts?.Dispose();
        _cts = null;
    }

    private void Cleanup()
    {
        _closed = true;
        _cancelled = true;
        CancelCurrentFlow();
        ClearCredentialInputs();     // 窗口关了就别把凭据留在内存控件里
        if (_tempDir == null) return;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 临时目录删不掉不影响功能 */ }
    }
}
