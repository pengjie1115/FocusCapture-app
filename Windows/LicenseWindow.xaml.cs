using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 激活码窗口（2026-10-08）。
///
/// <para><b>定位</b>：被拦截时的「就地激活」入口 —— 用户点某个高级功能才发现试用到期，
/// 此时最不该做的是把他丢去设置里自己找。窗口只做三件事：说清为什么被拦、
/// 说清当前还剩多少天、让他当场输码激活。</para>
///
/// <para><b>与设置里「激活码」板块的关系</b>：板块是「主动查看/管理」的完整入口（状态详情 + 激活）；
/// 本窗口是「被动拦截」的轻量入口（一行状态 + 输入框 + 激活）。两者共用 <see cref="LicenseGate"/>，
/// 状态一律现算，不各自缓存结论。</para>
///
/// <para><b>纪律</b>：所有外部操作自己 try 住、绝不把异常抛到 UI 线程；
/// 激活失败（含验签不过、写盘失败）一律不关窗，让用户能改码重试。</para>
/// </summary>
public partial class LicenseWindow : Window
{
    /// <summary>本次是否激活成功（调用方可据此决定要不要继续原动作）。</summary>
    /// <remarks>刻意不叫 Activated —— <see cref="Window"/> 已有同名事件（窗口成为前台窗口时触发），
    /// 重名会遮蔽它并让后来人误读。</remarks>
    public bool Succeeded { get; private set; }

    /// <summary>
    /// <paramref name="featureName"/>：被拦的功能名（如「AI 问答」），用于说明文案。
    /// 传 null 表示不是「被拦」语境（如从别处主动打开），标题走通用文案。
    /// </summary>
    public LicenseWindow(string? featureName = null)
    {
        InitializeComponent();

        HeadlineText.Text = string.IsNullOrWhiteSpace(featureName)
            ? "输入激活码解锁高级功能"
            : $"「{featureName}」需要激活后使用";

        RefreshStatus();
        Loaded += (_, _) => CodeInput.Focus();
    }

    /// <summary>把状态区刷成当前真实状态（每次打开现算，与设置板块同一套判定）。</summary>
    private void RefreshStatus()
    {
        try
        {
            // 已激活：理论上不会走到这个窗口（门卫已放行），但用户可能手动打开，照实显示。
            if (LicenseGate.IsLicenseValid())
            {
                StatusText.Text = "当前已激活，全部高级功能可用。";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                return;
            }

            var status = LicenseGate.GetStatus();
            if (status.Expired)
            {
                StatusText.Text = $"免费体验已结束（已用满 {status.TrialDays} 天）。";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x7B, 0x39));
            }
            else
            {
                StatusText.Text = $"免费体验中：已使用 {status.UsedDays} 天，剩余 {status.RemainingDays} 天。";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态读取失败，请查看运行日志。";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x7B, 0x39));
            AppLog.Warn("License", $"激活窗刷新状态失败：{ex.Message}");
        }
    }

    /// <summary>输入变化时清掉上一条提示 —— 避免「改了码还显示旧错误」。</summary>
    private void CodeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MessageText != null) MessageText.Text = "";
    }

    private void CodeInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnActivate_Click(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = (CodeInput.Text ?? "").Trim();
            if (code.Length == 0)
            {
                SetMessage("请先粘贴激活码。", warning: true);
                return;
            }

            if (LicenseGate.TryActivate(code, out var error))
            {
                Succeeded = true;

                // 提示保存码（2026-10-08 用户拍板要做）。
                // ⚠ 口径（2026-10-08 用户纠正）：**不提「不绑设备 / 可在任意电脑上使用」** ——
                //   那等于把「一码走天下」当卖点写出来，是在鼓励白嫖。只说「永久有效 + 请妥善保存」。
                System.Windows.MessageBox.Show(
                    this,
                    "激活成功，全部高级功能已解锁。\n\n" +
                    "建议把这串激活码保存到安全的地方（例如微信收藏、备忘录或密码管理器）。\n" +
                    "激活码永久有效，以后重装或更换设备时还要用它。",
                    "激活成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;   // ShowDialog 调用方靠这个收口
                Close();
            }
            else
            {
                // 失败不关窗：让用户能改码重试
                SetMessage(error, warning: true);
                CodeInput.SelectAll();
                CodeInput.Focus();
            }
        }
        catch (Exception ex)
        {
            SetMessage("激活时发生异常，请查看运行日志。", warning: true);
            AppLog.Warn("License", $"激活窗激活失败：{ex.Message}");
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SetMessage(string text, bool warning)
    {
        MessageText.Text = text;
        MessageText.Foreground = new SolidColorBrush(
            warning ? Color.FromRgb(0xE0, 0x7B, 0x39) : Color.FromRgb(0x4C, 0xAF, 0x50));
    }
}
