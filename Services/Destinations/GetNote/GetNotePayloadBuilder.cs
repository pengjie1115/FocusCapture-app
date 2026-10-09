using System.Text;
using FocusCapture.Models;

namespace FocusCapture.Services.Destinations.GetNote;

/// <summary>
/// 包装台①：按钮路径的制式包装（AGENT_IMPLEMENTATION.md 5.2，规则写死）。
/// 勾选 N 条合成 1 篇（超 5000 字按条目边界分篇）；未勾选由调用方传入当前列表。
/// 每条转 markdown 列表项「- [HH:mm] 内容（来源: xx）」，存储换行符 ⏎ 还原为真实换行（缩进续行保列表结构）。
/// </summary>
public static class GetNotePayloadBuilder
{
    /// <summary>单篇正文字数上限（超出按条目边界分篇，防超限/难读）</summary>
    private const int MaxChunkLength = 5000;

    public sealed record PayloadChunk(string Title, string Content, List<string> Tags, List<NoteEntry> Entries);

    /// <summary>把一批条目包装成 1..N 篇制式 payload（按条目边界分篇）</summary>
    public static List<PayloadChunk> Build(IReadOnlyList<NoteEntry> entries, bool fromSelection)
    {
        var title = BuildTitle(entries, fromSelection);
        var lines = entries.Select(FormatEntry).ToList();

        var chunks = new List<PayloadChunk>();
        var current = new List<string>();
        var currentEntries = new List<NoteEntry>();
        var length = 0;

        foreach (var (line, entry) in lines.Zip(entries))
        {
            if (current.Count > 0 && length + line.Length > MaxChunkLength)
            {
                chunks.Add(MakeChunk(title, chunks.Count + 1, current, currentEntries));
                current = new List<string>();
                currentEntries = new List<NoteEntry>();
                length = 0;
            }
            current.Add(line);
            currentEntries.Add(entry);
            length += line.Length + 1;
        }
        if (current.Count > 0)
            chunks.Add(MakeChunk(title, chunks.Count + 1, current, currentEntries));

        return chunks;
    }

    /// <summary>标题：灵感_已选N条_yyyy-MM-dd / 灵感_yyyy-MM-dd；跨日期时段用首末日期（罕见，仅搜索/区间模式）</summary>
    private static string BuildTitle(IReadOnlyList<NoteEntry> entries, bool fromSelection)
    {
        var prefix = fromSelection ? $"灵感_已选{entries.Count}条_" : "灵感_";
        var dates = entries.Select(e => e.Timestamp.Date).Distinct().OrderBy(d => d).ToList();
        var datePart = dates.Count == 1
            ? dates[0].ToString("yyyy-MM-dd")
            : $"{dates[0]:yyyy-MM-dd}_{dates[^1]:yyyy-MM-dd}";
        return prefix + datePart;
    }

    private static PayloadChunk MakeChunk(string baseTitle, int chunkIndex, List<string> lines, List<NoteEntry> entries)
    {
        // 分篇时标题加序号后缀（_1/_2），单篇保持原标题
        var title = chunkIndex == 1 ? baseTitle : $"{baseTitle}_{chunkIndex}";
        var tags = entries.Select(e => e.Tag).Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.TrimStart('#').Trim())
            .Where(t => t.Length > 0)
            .Append("灵感")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PayloadChunk(title, string.Join("\n", lines), tags, entries);
    }

    /// <summary>单条 → 列表项。待办保留【待办】语义（云端能看出是待办），提醒/已办状态并入括注。</summary>
    private static string FormatEntry(NoteEntry e)
    {
        var content = (e.EditedContent ?? e.Content)
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");   // 先归一，下面统一还原为缩进续行
        content = content.Replace("\u23CE", "\n  ");

        var line = $"- [{e.Timestamp:HH:mm}] ";
        if (e.Type == NoteType.Todo)
        {
            line += $"【待办】{content}";
            if (e.DueTime.HasValue) line += $"（提醒: {e.DueTime.Value:yyyy-MM-dd HH:mm}）";
            if (e.TodoStatus == TodoStatus.Done) line += "（已办）";
        }
        else
        {
            line += content;
        }

        if (!string.IsNullOrWhiteSpace(e.SourceWindow))
            line += $"（来源: {e.SourceWindow}）";
        return line;
    }
}
