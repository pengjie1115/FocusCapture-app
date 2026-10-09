using System.Diagnostics;
using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// 沉浸式长笔记保存服务：会话内多次保存 = 覆盖同一条 `- [HH:mm]` 记录。
/// 时间戳作为记录 ID，由调用方在 Show 时生成、会话期间保持不变。
/// </summary>
public class LongNoteService
{
    private const string BlockMarker = "## 沉浸记录";
    private readonly string _notesPath;

    public LongNoteService(string notesPath)
    {
        _notesPath = notesPath;
    }

    /// <summary>
    /// 保存会话内容。
    /// noteTimestamp == null → 新建一条（用当前时间作为 ID）
    /// noteTimestamp != null → 用相同 ID 覆盖已有记录
    /// </summary>
    public bool SaveLongNote(string content, DateTime? noteTimestamp)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        var text = content.Trim();
        var todayFile = Path.Combine(_notesPath, $"灵感_{DateTime.Now:yyyy-MM-dd}.md");
        var ts = noteTimestamp ?? DateTime.Now;
        var timeStr = ts.ToString("HH:mm");
        var newLine = $"- [{timeStr}] {FlattenMultiLine(text)}";

        try
        {
            Directory.CreateDirectory(_notesPath);
            var existing = File.Exists(todayFile)
                ? File.ReadAllText(todayFile, Encoding.UTF8)
                : "";
            var updated = InsertOrUpdate(existing, newLine, timeStr);
            File.WriteAllText(todayFile, updated, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 长笔记保存失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>多行内容合并为单行（用 `; ` 分隔），保证速览正则匹配</summary>
    private static string FlattenMultiLine(string content)
    {
        var lines = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l));
        return string.Join("; ", lines);
    }

    /// <summary>插入或按时间戳更新：找到 `- [HH:mm]` 匹配则替换，否则追加到沉浸记录块</summary>
    private static string InsertOrUpdate(string existing, string newLine, string timeStr)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return $"{BlockMarker}\n\n{newLine}\n";

        // 按 `- [HH:mm]` 匹配行首（仅行首，避免误匹配嵌入）
        var linePrefix = $"- [{timeStr}]";
        var lineIndex = FindLineStart(existing, linePrefix);

        if (lineIndex >= 0)
        {
            // 找到同时间戳记录 → 替换该行
            var lineEnd = existing.IndexOf('\n', lineIndex);
            if (lineEnd < 0) lineEnd = existing.Length;
            return existing[..lineIndex] + newLine + "\n" + existing[(lineEnd + 1)..].TrimStart('\n', ' ');
        }

        // 没找到 → 追加到 `## 沉浸记录` 块下
        if (existing.Contains(BlockMarker))
        {
            var markerIdx = existing.IndexOf(BlockMarker, StringComparison.Ordinal) + BlockMarker.Length;
            var nextHeading = FindNextHeading(existing, markerIdx);
            var insertPos = nextHeading >= 0 ? nextHeading : existing.Length;
            return existing[..(int)insertPos].TrimEnd() + $"\n{newLine}\n" + existing[(int)insertPos..].TrimStart('\n');
        }

        // 没有块 → 创建
        return existing.TrimEnd() + $"\n\n{BlockMarker}\n\n{newLine}\n";
    }

    /// <summary>查找行首匹配 prefix 的位置（行首 = 文件开头或前一个换行之后）</summary>
    private static int FindLineStart(string text, string prefix)
    {
        var idx = text.IndexOf(prefix, StringComparison.Ordinal);
        while (idx >= 0)
        {
            if (idx == 0 || text[idx - 1] == '\n') return idx;
            idx = text.IndexOf(prefix, idx + 1, StringComparison.Ordinal);
        }
        return -1;
    }

    /// <summary>从 startIndex 起查找下一个 `## ` 标题位置；找不到返回 -1</summary>
    private static int FindNextHeading(string text, int startIndex)
    {
        var searchFrom = text.IndexOf('\n', startIndex);
        if (searchFrom < 0) return -1;
        return text.IndexOf("\n## ", searchFrom, StringComparison.Ordinal);
    }
}
