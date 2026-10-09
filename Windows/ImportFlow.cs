using System.IO;
using System.Windows;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 导入笔记的完整交互流程：选文件 → 解析 → 预览勾选/选目标日期 → 写入 MD。
///
/// 为什么单独抽出来（2026-09-13）：该流程此前只存在于 ExportDialog 内部（"导出"对话框里藏着一个
/// 导入按钮，入口很隐蔽）。灵感速览面板要加同一个入口时若就地复制一份，就会形成两份必然漂移的逻辑
/// —— 项目既有约定是"同一能力只允许一个实现"（同 MainWindow.ShowTodoSummary 的处理）。
///
/// 本类不持有状态，纯流程编排，可被任意窗口调用；owner 为 null 时对话框不归属任何窗口
/// （供无窗口场景兜底，正常调用都会传）。
/// </summary>
internal static class ImportFlow
{
    /// <summary>执行一次导入流程。返回 true 表示确实写入了笔记（调用方可据此刷新列表）。</summary>
    public static bool Run(Window? owner, NoteService noteService, AppSettings settings)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的文件",
                Filter = NoteImportService.FormatFilter,
                CheckFileExists = true,
                Multiselect = false
            };
            // 默认起始目录：当前导出生效文件夹 或 我的文档
            var startDir = !string.IsNullOrWhiteSpace(settings.ExportFolderPath) && Directory.Exists(settings.ExportFolderPath)
                ? settings.ExportFolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            dlg.InitialDirectory = startDir;

            var picked = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            if (picked != true) return false;

            NoteImportService.ImportPreview? preview;
            try
            {
                preview = new NoteImportService().Parse(dlg.FileName);
            }
            catch (Exception parseEx)
            {
                Notify(owner, $"解析失败：{parseEx.Message}", "导入失败", MessageBoxImage.Warning);
                return false;
            }

            if (preview.Entries.Count == 0)
            {
                Notify(owner, "文件中没有可识别的笔记内容。", "导入", MessageBoxImage.Information);
                return false;
            }

            var previewDialog = owner != null
                ? new ImportPreviewDialog(preview, noteService) { Owner = owner }
                : new ImportPreviewDialog(preview, noteService);
            if (previewDialog.ShowDialog() != true) return false;

            // 复用调用方传入的 NoteService 实例（保证 NotesChanged 被同步引擎订阅到）
            var written = noteService.ImportNotes(previewDialog.SelectedEntries, previewDialog.TargetDate);
            Notify(owner, $"成功导入 {written} 条笔记到 {previewDialog.TargetDate:yyyy-MM-dd}。",
                "导入完成", MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            Notify(owner, $"导入失败：{ex.Message}", "错误", MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>MessageBox 带归属窗口的重载在 owner 为 null 时会抛异常，这里统一兜底。</summary>
    private static void Notify(Window? owner, string message, string title, MessageBoxImage icon)
    {
        if (owner != null) MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }
}
