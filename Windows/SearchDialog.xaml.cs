using System.Windows;

namespace FocusCapture.Windows;

/// <summary>全局查找弹窗：输入关键词 + 确认/取消。Owner 端 ShowDialog 后读 Keyword。</summary>
public partial class SearchDialog : Window
{
    /// <summary>用户输入的关键词（ShowDialog 返回 true 时有效）。</summary>
    public string Keyword { get; private set; } = "";

    /// <summary>打开时预填的关键词（用于在已有查找上修改/清除）。</summary>
    public string InitialKeyword { get; set; } = "";

    public SearchDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputBox.Text = InitialKeyword;
            InputBox.SelectAll();
            InputBox.Focus();
        };
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // 空输入时按钮变「清除」：叠加筛选下留空确认=清除当前查找关键词
        BtnOk.Content = string.IsNullOrWhiteSpace(InputBox.Text) ? "清除" : "查找";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Keyword = InputBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
        else if (e.Key == System.Windows.Input.Key.Enter && BtnOk.IsEnabled)
        {
            e.Handled = true;
            BtnOk_Click(sender, new RoutedEventArgs());
        }
    }
}
