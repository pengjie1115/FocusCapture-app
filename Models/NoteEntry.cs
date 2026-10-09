namespace FocusCapture.Models;

/// <summary>笔记类型：普通笔记 / 待办（v3.5）</summary>
public enum NoteType { Note, Todo }

/// <summary>待办状态：未办 / 已办 / 已读（暂缓）（v3.5）</summary>
public enum TodoStatus { Open, Done, Read }

public class NoteEntry
{
    public DateTime Timestamp { get; set; }
    public string Content { get; set; } = string.Empty;
    public string SourceWindow { get; set; } = string.Empty;
    public string? Tag { get; set; }  // extracted #tag

    // ── v3.5 待办与提醒 ──
    public NoteType Type { get; set; } = NoteType.Note;
    public DateTime? DueTime { get; set; }        // 提醒时间（仅 Type=Todo 时有值）
    public TodoStatus TodoStatus { get; set; } = TodoStatus.Open;

    /// <summary>最后修改时间（v3.10：今天创建的笔记/待办被实质性修改后记录，落盘到行尾 `(改: 时间)` 标记；
    /// 显示层优先于创建/提醒时间。仅"今天创建且非未到期"的条目修改时写入；历史/未到期不写。
    /// 解析时从行尾改标记填充，与 EditedContent 同属"不参与序列化、靠解析填充"的运行时字段。</summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>AI 回填追加的释义列表（展示层用，解析【AI 释义】标记行关联到原笔记时填充；不参与存储序列化）</summary>
    public List<string> AiFills { get; set; } = new();

    /// <summary>编辑后的内容（展示层优先显示；解析【编辑】标记行关联到原笔记时填充；不参与存储序列化）</summary>
    public string? EditedContent { get; set; }

    /// <summary>
    /// 本条目对应的**原始物理行**（解析时记录；不参与存储序列化）。
    /// 标记行（【编辑】/【AI 释义】）合并失败成孤儿卡时必填——孤儿卡的 Content 是剥掉前缀/ref 后的展示文本，
    /// 用它反向定位/删除/改行永远匹配不上物理行（2026-09-20 实测：同步来的孤儿卡删除必报
    /// "未在笔记文件中找到该条目"）。有了 RawLine，删除/原地改行按它精确匹配物理行。
    /// 普通行条目也记录（代价为零，IsEntryLine 多一个精确匹配分支）。
    /// </summary>
    public string? RawLine { get; set; }

    /// <summary>生成单行 markdown 条目，多段内容中的换行用 \u23CE 转义</summary>
    public string ToMarkdownLine()
    {
        if (Type == NoteType.Todo) return FormatTodoLine(this);

        // 笔记分支保持现状（一行不改）
        // 完整时间戳：写入日期，便于按任意日期回查；读取时兼容旧的 [HH:mm] 格式
        var time = Timestamp.ToString("yyyy-MM-dd HH:mm");
        // 把多段内容的换行转义为单个标记，保证单行存储格式不被破坏
        var escaped = Content
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{time}] {escaped}";
        if (!string.IsNullOrWhiteSpace(SourceWindow))
            line += $" — 来源: {SourceWindow}";
        if (ModifiedAt.HasValue)
            line += $" (改: {ModifiedAt.Value:yyyy-MM-dd HH:mm})";
        return line;
    }

    /// <summary>
    /// 待办行格式化（与 UpdateTodo 重建行共用同一套格式，v3.5）。
    /// 行内属性顺序固定：`【待办】正文 (提醒: yyyy-MM-dd HH:mm:ss, 状态: 已办)`——
    /// 有提醒+有状态为组合括号（逗号分隔），仅有其一输出单独括号，无属性不带括号。
    /// v3.7：提醒时间落盘精确到秒（此前到分钟，秒被截断导致提醒总在整分钟触发）；界面展示仍只到分钟。
    /// withSeconds=false 生成旧版分钟精度行文本，仅供 IsEntryLine 回退匹配旧格式落盘行（不用于写盘）。
    /// </summary>
    public static string FormatTodoLine(NoteEntry e, bool withSeconds = true)
    {
        var time = e.Timestamp.ToString("yyyy-MM-dd HH:mm");
        var escaped = e.Content
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{time}] 【待办】{escaped}";

        if (e.DueTime.HasValue)
        {
            var dueStr = withSeconds
                ? e.DueTime.Value.ToString("yyyy-MM-dd HH:mm:ss")
                : e.DueTime.Value.ToString("yyyy-MM-dd HH:mm");
            line += $" (提醒: {dueStr}";
            if (e.TodoStatus != TodoStatus.Open)
                line += $", 状态: {(e.TodoStatus == TodoStatus.Done ? "已办" : "已读")}";
            line += ")";
        }
        else if (e.TodoStatus != TodoStatus.Open)
        {
            line += $" (状态: {(e.TodoStatus == TodoStatus.Done ? "已办" : "已读")})";
        }

        if (!string.IsNullOrWhiteSpace(e.SourceWindow))
            line += $" — 来源: {e.SourceWindow}";
        if (e.ModifiedAt.HasValue)
            line += $" (改: {e.ModifiedAt.Value:yyyy-MM-dd HH:mm})";
        return line;
    }

    public string FirstLine => Content.Split('\n')[0].Trim();
}

public enum ExportFormat
{
    Markdown,
    Json,
    Txt,
    Word
}
