using FocusCapture;
using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// 软删除跟踪服务：不修改源 .md 文件，仅记录被"隐藏"的笔记指纹。
/// 误删可从源文件回溯；定期清理 90 天前的旧删除记录防止 deleted.json 无限膨胀。
/// </summary>
public class DeletedNoteService
{
    private const int AutoCleanupDays = 90;

    /// <summary>删除记录文件路径（实例级，2026-09-11 由 static 改为实例字段）。</summary>
    private readonly string _filePath;

    private List<DeletedNote> _records = new();

    /// <summary>
    /// 构造函数。
    /// <paramref name="filePath"/> 为 null → 使用默认全局位置
    /// （`%AppData%\FocusCapture\deleted.json`，随 <see cref="FocusCapturePaths"/> 改道），与改造前行为一致；
    /// 显式传入 → 该实例独立持有删除记录，供「多设备模拟」等需要各自独立的场景使用。
    /// </summary>
    /// <remarks>
    /// 背景：原先为 static 路径，导致同一进程内多个 NoteService 实例共享同一份删除记录
    /// （测试模拟 A/B 双设备时相互串台）。生产环境单进程单实例不受影响，故默认值保持全局。
    /// </remarks>
    public DeletedNoteService(string? filePath = null)
    {
        _filePath = filePath ?? FocusCapturePaths.Combine("deleted.json");
        Load();
        CleanupOld();
    }

    /// <summary>从 NoteEntry 计算对应的 .md 文件名</summary>
    public static string GetFileName(NoteEntry entry)
    {
        return string.IsNullOrEmpty(entry.Tag)
            ? $"灵感_{entry.Timestamp:yyyy-MM-dd}.md"
            : $"{entry.Tag}.md";
    }

    /// <summary>内容指纹：取前 30 字（去前后空格）</summary>
    public static string ComputeFingerprint(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var trimmed = content.Trim();
        return trimmed.Length > 30 ? trimmed[..30] : trimmed;
    }

    /// <summary>是否已被标记为删除</summary>
    public bool IsDeleted(NoteEntry entry)
    {
        var file = GetFileName(entry);
        var fp = ComputeFingerprint(entry.Content);
        return _records.Any(d =>
            d.File.Equals(file, StringComparison.OrdinalIgnoreCase) &&
            d.Timestamp == entry.Timestamp &&
            d.ContentFingerprint == fp);
    }

    /// <summary>标记单条为已删除（去重）</summary>
    public void MarkDeleted(NoteEntry entry)
    {
        if (IsDeleted(entry)) return;
        _records.Add(new DeletedNote
        {
            File = GetFileName(entry),
            Timestamp = entry.Timestamp,
            ContentFingerprint = ComputeFingerprint(entry.Content),
            DeletedAt = DateTime.Now
        });
        Save();
    }

    /// <summary>批量标记</summary>
    public void MarkDeletedRange(IEnumerable<NoteEntry> entries)
    {
        var changed = false;
        foreach (var e in entries)
        {
            if (IsDeleted(e)) continue;
            _records.Add(new DeletedNote
            {
                File = GetFileName(e),
                Timestamp = e.Timestamp,
                ContentFingerprint = ComputeFingerprint(e.Content),
                DeletedAt = DateTime.Now
            });
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>已删除记录数（用于状态显示/调试）</summary>
    public int Count => _records.Count;

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _records = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListDeletedNote) ?? new();
        }
        catch { _records = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_records,
                AppJsonContext.Default.ListDeletedNote), Encoding.UTF8);
        }
        catch { /* best effort */ }
    }

    /// <summary>清理超过 N 天的删除记录，防止文件无限膨胀</summary>
    private void CleanupOld()
    {
        var cutoff = DateTime.Now.AddDays(-AutoCleanupDays);
        var before = _records.Count;
        _records.RemoveAll(d => d.DeletedAt < cutoff);
        if (_records.Count != before) Save();
    }
}
