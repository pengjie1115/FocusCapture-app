using System.Text.Json;

namespace FocusCapture.Services;

/// <summary>
/// AI 问答全局搜索的「搜索词历史」读写（%APPDATA%\FocusCapture\chat_search_history.json，独立文件，纯本机）。
///
/// 为什么独立成文件而不塞进 settings.json / chat_history 目录：
/// · settings.json 是跨端同步与设置清洗的领地，搜索词是纯本地使用痕迹，混进去会被同步语义拖累；
/// · chat_history 目录被 ListSessions / ChatSearchService 按 *.json 枚举，放这里会出现「幽灵会话」。
///
/// 纪律沿用 ChatSearchService：磁盘异常一律吞掉降级（历史丢了不该影响搜索本身），落盘 best effort。
/// 排序口径：最近搜过的在最前（RecordTerm 把重复项挪到队首）；上限 HistoryLimit 条，超出淘汰最旧的。
/// </summary>
public static class ChatSearchHistoryStore
{
    /// <summary>历史保留条数上限（超出淘汰最旧的；胶囊流式排布下太多也没有浏览价值）。</summary>
    public const int HistoryLimit = 20;

    private static string StorePath => FocusCapturePaths.Combine("chat_search_history.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly object Gate = new();

    /// <summary>读取搜索词历史（最近在前；文件不存在 / 损坏返回空表，不抛）。</summary>
    public static List<string> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(StorePath)) return [];
                var terms = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath, Encoding.UTF8));
                return terms ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[FocusCapture] 搜索词历史读取失败: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>记录一次搜索词：去重（重复项挪到队首并保留最新写法）→ 截上限 → 落盘（best effort）。
    /// 空白词不入历史。供 UI 在一次搜索真正执行后调用。</summary>
    public static void Record(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return;
        lock (Gate)
        {
            var updated = RecordTerm(Load(), term, HistoryLimit);
            try { File.WriteAllText(StorePath, JsonSerializer.Serialize(updated, JsonOptions), Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[FocusCapture] 搜索词历史落盘失败: {ex.Message}");
            }
        }
    }

    /// <summary>删除单条历史并落盘（best effort）。</summary>
    public static void Remove(string term)
    {
        lock (Gate)
        {
            var current = Load();
            var updated = RemoveTerm(current, term);
            if (updated.Count == current.Count) return;   // 没变化不落盘
            try { File.WriteAllText(StorePath, JsonSerializer.Serialize(updated, JsonOptions), Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[FocusCapture] 搜索词历史落盘失败: {ex.Message}");
            }
        }
    }

    /// <summary>清空全部历史并落盘（best effort）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            try { File.WriteAllText(StorePath, "[]", Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[FocusCapture] 搜索词历史清空失败: {ex.Message}");
            }
        }
    }

    /// <summary>记录一条搜索词的**纯逻辑**（不碰磁盘，供检查点直接断言）：
    /// 空白词忽略 / 重复项挪到队首（保留最新写法）/ 截断到 cap。输入列表不被修改。</summary>
    public static List<string> RecordTerm(IReadOnlyList<string> existing, string term, int cap)
    {
        var trimmed = term.Trim();
        if (trimmed.Length == 0) return existing.ToList();

        var result = new List<string> { trimmed };
        foreach (var t in existing)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            // 比较忽略大小写（搜 "ABC" 和 "abc" 是同一个词），但队首保留最新一次的写法
            if (string.Equals(t.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(t.Trim());
        }
        if (result.Count > cap) result.RemoveRange(cap, result.Count - cap);
        return result;
    }

    /// <summary>删除单条的**纯逻辑**（不碰磁盘，供检查点直接断言）。找不到原样返回新副本。</summary>
    public static List<string> RemoveTerm(IReadOnlyList<string> existing, string term) =>
        existing.Where(t => !string.Equals(t.Trim(), term.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
}
