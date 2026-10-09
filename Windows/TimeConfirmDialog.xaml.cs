namespace FocusCapture.Windows;

/// <summary>
/// v2：裸时钟（无时段无日期）解析出的时间已过 → 弹此确认窗。
/// 中文口语常把"晚上8点"说成"8点"，此时用户意图可能是今晚或明早——让用户选，不做默认。
/// 选项动态生成：
/// - 小时 1~11：今晚 h 点(分) / 明早 h 点(分) / 取消
/// - 小时 12：明天中午12点(分) / 取消（"今晚12点"=0点反常识，不提供）
/// - 小时 13~23：明天 h 点(分) / 取消（24 小时制表达无歧义，只给顺延）
/// 返回 ConfirmedTime；取消 → null。
/// </summary>
public partial class TimeConfirmDialog : Window
{
    /// <summary>用户确认后的提醒时间；取消 → null</summary>
    public DateTime? ConfirmedTime { get; private set; }

    public TimeConfirmDialog(DateTime past)
    {
        InitializeComponent();
        var h = past.Hour;
        var m = past.Minute;
        var minText = m == 0 ? "" : $"点{m}分";

        if (h >= 1 && h <= 11)
        {
            HintText.Text = $"「{h}点{minText}」已经过了，你说的是：";
            BtnPrimary.Content = $"今晚{h}点{minText}";
            BtnPrimary.Tag = DateTime.Today.AddHours(h + 12).AddMinutes(m);
            BtnSecondary.Content = $"明早{h}点{minText}";
            BtnSecondary.Tag = DateTime.Today.AddDays(1).AddHours(h).AddMinutes(m);
        }
        else if (h == 12)
        {
            HintText.Text = "「12点」已经过了，你说的是：";
            BtnPrimary.Content = $"明天中午12点{minText}";
            BtnPrimary.Tag = DateTime.Today.AddDays(1).AddHours(12).AddMinutes(m);
            BtnSecondary.Visibility = Visibility.Collapsed;
        }
        else
        {
            HintText.Text = $"「{h}点{minText}」已经过了，你说的是：";
            BtnPrimary.Content = $"明天{h}点{minText}";
            BtnPrimary.Tag = DateTime.Today.AddDays(1).AddHours(h).AddMinutes(m);
            BtnSecondary.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnPrimary_Click(object sender, RoutedEventArgs e)
    {
        ConfirmedTime = (DateTime)BtnPrimary.Tag;
        DialogResult = true;
        Close();
    }

    private void BtnSecondary_Click(object sender, RoutedEventArgs e)
    {
        ConfirmedTime = (DateTime)BtnSecondary.Tag;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
