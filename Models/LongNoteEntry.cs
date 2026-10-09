namespace FocusCapture.Models;

/// <summary>沉浸式长笔记模型：整段保存到今日灵感文件</summary>
public class LongNoteEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Content { get; set; } = string.Empty;

    /// <summary>按 ## 沉浸记录 [HH:mm] + 正文 格式序列化</summary>
    public string ToMarkdownSection()
    {
        var timeStr = Timestamp.ToString("HH:mm");
        var sb = new StringBuilder();
        sb.AppendLine($"## 沉浸记录 [{timeStr}]");
        sb.AppendLine();
        sb.AppendLine(Content.Trim());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }
}
