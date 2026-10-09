using System.Windows.Forms;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class ExportDialog : Window
{
    private readonly AppSettings _settings;
    private readonly NoteService _noteService;   // 2026-08-15 新增：导入需要复用现有实例触发 NotesChanged

    public ExportConfig Config { get; private set; } = new();
    public bool Confirmed { get; private set; }
    public string FolderPath { get; private set; } = "";

    public ExportDialog(AppSettings settings, NoteService noteService)
    {
        InitializeComponent();
        _settings = settings;
        _noteService = noteService;

        // 加载上次配置
        Config = settings.LastExportConfig ?? new ExportConfig();
        ChkTime.IsChecked = Config.IncludeTime;
        ChkSource.IsChecked = Config.IncludeSource;
        ChkContent.IsChecked = Config.IncludeContent;

        // Word 导出属高级档（2026-10-08）：未解锁时**置灰该项**而不是整个导出功能 ——
        // Markdown / 纯文本是免费档，拦整个导出等于把免费用户的基本盘也砍了。
        // ⚠ 必须在恢复选中态**之前**判断：否则「上次选的正好是 Word」会出现
        //   「选中但禁用」的死状态（用户看着选了 Word，点导出却拿到 Markdown）。
        if (!LicenseGate.IsAllowed(LicenseGate.FeatureExportWord))
        {
            RbWord.IsEnabled = false;
            // XAML 里前景是显式 #CCCCCC，会盖掉 WPF 禁用态的默认变灰 —— 只靠 IsEnabled=false
            // 出图上几乎看不出差别（用户不知道该点哪）。所以这里显式调暗前景，让「点不了」一眼可见。
            RbWord.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
            RbWord.ToolTip = "Word 导出需激活后使用。Markdown / 纯文本无需激活。";
            WordLockedHint.Visibility = Visibility.Visible;
            if (Config.Format == ExportFormat.Word) Config.Format = ExportFormat.Markdown;  // 退回免费格式
        }

        RbMarkdown.IsChecked = Config.Format == ExportFormat.Markdown;
        RbWord.IsChecked = Config.Format == ExportFormat.Word;
        RbTxt.IsChecked = Config.Format == ExportFormat.Txt;

        UpdateFolderDisplay();
    }

    private void UpdateFolderDisplay()
    {
        TxtFolderPath.Text = string.IsNullOrWhiteSpace(_settings.ExportFolderPath)
            ? "(未设置 — 导出时会提示选择)"
            : _settings.ExportFolderPath;
    }

    private void BtnChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择默认导出文件夹",
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(_settings.ExportFolderPath))
            dialog.SelectedPath = _settings.ExportFolderPath;
        else
            dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _settings.ExportFolderPath = dialog.SelectedPath;
            _settings.Save();
            UpdateFolderDisplay();
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        // 读取勾选状态
        Config = new ExportConfig
        {
            IncludeTime = ChkTime.IsChecked == true,
            IncludeSource = ChkSource.IsChecked == true,
            IncludeContent = ChkContent.IsChecked == true,
            Format = RbWord.IsChecked == true ? ExportFormat.Word
                : RbTxt.IsChecked == true ? ExportFormat.Txt
                : ExportFormat.Markdown
        };

        // 持久化配置偏好
        _settings.LastExportConfig = Config;

        // 确定保存文件夹
        if (string.IsNullOrWhiteSpace(_settings.ExportFolderPath))
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择保存位置",
                ShowNewFolderButton = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return; // 用户取消

            FolderPath = dialog.SelectedPath;
            _settings.ExportFolderPath = FolderPath;
            _settings.Save();
        }
        else
        {
            FolderPath = _settings.ExportFolderPath;
        }

        Confirmed = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>导入按钮：走 ImportFlow 的统一流程。
    /// 2026-09-13 起流程体抽到 ImportFlow —— 灵感速览面板也加了同一个入口，
    /// 两处必须共用一份实现，否则迟早各改各的、行为漂移。</summary>
    private void BtnImport_Click(object sender, RoutedEventArgs e) => ImportFlow.Run(this, _noteService, _settings);
}
