using System.Windows;
using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// 添加笔记/待办前的重复预检弹窗（用户拍板：添加时检测重复，让用户定夺是否重复添加）。
/// 仅覆盖手动输入框 + 剪贴板自动捕获；AI 回填 / Agent 工具 / 拖放保存不经此路径。
/// </summary>
public static class DuplicatePrompt
{
    /// <summary>添加前预检：若已存在内容相同的条目，弹窗确认是否重复添加。
    /// owner 为 null 时弹窗居中无宿主（剪贴板自动捕获等无窗口场景）。
    /// 返回 true=继续添加（无重复或用户确认）；false=取消添加。</summary>
    public static bool Confirm(NoteService notes, string content, NoteType type, Window? owner)
    {
        var existing = notes.FindDuplicate(content, type);
        if (existing == null) return true;

        var when = NoteService.TodoDisplayTime(existing);
        var typeLabel = type == NoteType.Todo ? "待办" : "笔记";
        var preview = existing.FirstLine;
        var msg = $"已存在内容相同的{typeLabel}：\n\n时间：{when:yyyy-MM-dd HH:mm}\n内容：{preview}\n\n是否重复添加？";
        return MessageBox.Show(owner, msg, "重复提醒",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
