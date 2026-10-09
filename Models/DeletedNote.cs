namespace FocusCapture.Models;

/// <summary>已删除笔记的指纹（用于软删除跟踪）</summary>
public class DeletedNote
{
    /// <summary>源 .md 文件名（含 .md 后缀），如 "灵感_2026-07-19.md" 或 "工作.md"</summary>
    public string File { get; set; } = "";

    /// <summary>笔记原始时间戳</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>内容前 30 字指纹（防误删比对）</summary>
    public string ContentFingerprint { get; set; } = "";

    /// <summary>删除时间（用于 90 天自动清理）</summary>
    public DateTime DeletedAt { get; set; } = DateTime.Now;
}
