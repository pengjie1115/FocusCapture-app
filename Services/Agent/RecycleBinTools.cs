using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 回收站只读视图（2026-09-17 新增）。
///
/// 存在的意义：delete_note 一直是「进回收站可恢复」，但 AI 看不见回收站 ——
/// 用户说「刚才删的那条能找回来吗」，它只能答"你去界面上看"。现在这条链路闭环了。
/// </summary>
public class ListRecycleBinTool : AgentTool
{
    private readonly NoteService _noteService;
    public ListRecycleBinTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "list_recycle_bin";

    public override string Description =>
        "查看回收站里被删除的笔记/待办（只读）。用户说「我刚才删的那条」「回收站里有什么」时使用。\n" +
        "返回每行开头方括号里的**删除时间**，是恢复时用的定位标识（传给 restore_deleted）。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"limit":{"type":"number","description":"可选，最多返回几条，默认 20"}},"required":[]}""";

    public override bool IsReadOnly => true;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var limit = ToolArgs.TryGetInt(argumentsJson, "limit", out var lim) && lim > 0 ? Math.Min(lim, 100) : 20;

        var items = _noteService.RecycleBin.List();
        if (items.Count == 0) return Task.FromResult("回收站是空的。");

        var lines = items.Take(limit)
            .Select(x => $"[{x.Entry.DeletedAt:yyyy-MM-dd HH:mm}] {x.Entry.Preview}");
        var tail = items.Count > limit ? $"\n（共 {items.Count} 条，只列出最近 {limit} 条）" : "";

        return Task.FromResult(
            $"回收站共 {items.Count} 条：\n" + string.Join("\n", lines)
            + tail
            + "\n\n（要恢复某条，用 restore_deleted 传它的删除时间。）");
    }
}

/// <summary>
/// 从回收站恢复条目（写，需确认）。2026-09-17 新增。
///
/// 红线遵守：只恢复、**不提供"彻底删除/清空回收站"** —— 破坏性动作不给 AI（项目既有纪律）。
/// </summary>
public class RestoreDeletedTool : AgentTool
{
    private readonly NoteService _noteService;
    public RestoreDeletedTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "restore_deleted";

    public override string Description =>
        "把回收站里被删除的笔记/待办恢复回去。deleted_at 用 list_recycle_bin 输出中方括号里的**删除时间**；" +
        "同一分钟删了多条时，用 content_hint 给出内容关键词消歧。\n" +
        "⚠️ 只恢复用户明确指定的条目。恢复前先向用户确认是哪一条。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"deleted_at":{"type":"string","description":"删除时间，格式 yyyy-MM-dd HH:mm（来自 list_recycle_bin 输出）"},"content_hint":{"type":"string","description":"可选，内容关键词，同分钟多条时用于消歧"}},"required":["deleted_at"]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "deleted_at", out var at);
        ToolArgs.TryGetString(argumentsJson, "content_hint", out var hint);
        return $"从回收站恢复条目 [删除于 {at}]" + (hint.Length > 0 ? $"（含「{hint}」）" : "");
    }

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "deleted_at", out var atStr))
            return Task.FromResult("错误：缺少参数 deleted_at（删除时间，来自 list_recycle_bin 输出）。");

        if (!DateTime.TryParseExact(atStr.Trim(), "yyyy-MM-dd HH:mm", null,
                System.Globalization.DateTimeStyles.None, out var deletedAt))
            return Task.FromResult($"错误：deleted_at「{atStr}」格式不对，必须是 yyyy-MM-dd HH:mm。");

        ToolArgs.TryGetString(argumentsJson, "content_hint", out var hint);

        var all = _noteService.RecycleBin.List();
        var matches = all
            .Where(x => x.Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm") == deletedAt.ToString("yyyy-MM-dd HH:mm")
                        && (hint.Length == 0 ||
                            x.Entry.Preview.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
            return Task.FromResult(
                $"错误：回收站里找不到删除于 {deletedAt:yyyy-MM-dd HH:mm} 的{(hint.Length == 0 ? "条目" : $"含「{hint}」的条目")}。" +
                "请先用 list_recycle_bin 核实删除时间后再试。");

        if (matches.Count > 1)
        {
            var list = string.Join("\n", matches.Take(5).Select(m => $"- [{m.Entry.DeletedAt:yyyy-MM-dd HH:mm}] {m.Entry.Preview}"));
            return Task.FromResult(
                $"错误：{deletedAt:yyyy-MM-dd HH:mm} 命中 {matches.Count} 条，请补充参数 content_hint（内容关键词）消歧：\n{list}");
        }

        var (fileName, entry) = matches[0];
        try
        {
            _noteService.RecycleBin.Restore(fileName, entry, _noteService);
            _noteService.RaiseNotesChanged();
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", "从回收站恢复失败", ex);
            return Task.FromResult("错误：恢复失败 —— " + ex.Message + "。条目仍留在回收站，可稍后重试。");
        }

        return Task.FromResult($"已从回收站恢复：{entry.Preview}（原文件 {entry.RelativePath}）。");
    }
}
