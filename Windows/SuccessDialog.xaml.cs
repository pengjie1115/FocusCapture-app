namespace FocusCapture.Windows;

public partial class SuccessDialog : Window
{
    private readonly string _filePath;

    public SuccessDialog(string filePath)
    {
        InitializeComponent();
        _filePath = filePath;
        TxtFilePath.Text = filePath;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"[FocusCapture] 打开文件失败: {ex.Message}"); }
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", $"/select,\"{_filePath}\""); }
        catch (Exception ex) { Debug.WriteLine($"[FocusCapture] 打开文件夹失败: {ex.Message}"); }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
