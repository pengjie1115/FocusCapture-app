namespace FocusCapture.Windows;

/// <summary>
/// v3.5：设置提醒时间对话框（快速 View 右键「设置提醒…」用）。
/// 返回解析出的绝对时间（yyyy-MM-dd HH:mm），取消或非法输入返回 null。
/// </summary>
public partial class DueTimeDialog : Window
{
    /// <summary>确认后解析出的提醒时间；未确认/取消 → null</summary>
    public DateTime? DueTime { get; private set; }

    public DueTimeDialog(DateTime? current, string? prompt = null)
    {
        InitializeComponent();
        // 预填：已有提醒时间则回显，否则默认明天 09:00（常见上班时间）
        if (!string.IsNullOrEmpty(prompt))
        {
            Title = "设置提醒时间";
            PromptText.Text = prompt;
        }
        DueInput.Text = current?.ToString("yyyy-MM-dd HH:mm")
            ?? DateTime.Today.AddDays(1).AddHours(9).ToString("yyyy-MM-dd HH:mm");
        DueInput.Focus();
        DueInput.SelectAll();
    }

    private void DueInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnOk_Click(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.TryParse(DueInput.Text.Trim(), out var t))
        {
            DueTime = t;
            DialogResult = true;
            Close();
        }
        else
        {
            System.Windows.MessageBox.Show(this, "时间格式不正确，请输入如 2026-08-28 09:00", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}