using FocusCapture.Services.AI;

namespace FocusCapture.Services;

/// <summary>全文搜索命中条目。
/// SnippetMatchStart / SnippetMatchLength 是命中词在 Snippet 内的下标与长度，供 UI 高亮命中词（口径见 ChatSearchMatcher.ExtractSnippet）。</summary>
public sealed record ChatSearchHit(
    string SessionId,
    string FilePath,
    DateTime SavedAt,
    string Title,
    string Preview,
    string Snippet,
    int SnippetMatchStart,
    int SnippetMatchLength,
    int HitCount);

/// <summary>
/// AI 问答历史会话的全文搜索（底层服务）。
/// 设计取舍：同步阻塞 + 直接读磁盘，不做缓存/索引。会话文件都在本地、数量有限，建索引收益不大、
/// 还会引入"索引与文件不同步"的新坑；代价是调用方**必须在后台线程调用**，否则会卡住 UI 消息循环。
/// </summary>
public static class ChatSearchService
{
    /// <summary>结果上限：搜索是"够用就好"的入口，返回过多既拖慢 UI 也没有检索价值。</summary>
    public const int MaxResults = 100;

    /// <summary>全文搜索。query 为空返回空列表；groupIdFilter 为 null/空 = 搜全部会话，非空 = 只搜该分组的会话。
    /// 同步阻塞 + 磁盘 IO，调用方必须在后台线程调用。</summary>
    public static IReadOnlyList<ChatSearchHit> Search(string query, string? groupIdFilter)
    {
        var result = new List<ChatSearchHit>();

        // 铁律：本地功能不能因外部异常（磁盘/权限/半写文件）炸掉。
        // 最外层再兜一层 try，即使目录枚举本身出问题，也只返回"已搜到的部分"。
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return result;

            var dir = FocusCapturePaths.Combine("chat_history");
            if (!Directory.Exists(dir)) return result;

            // 空/全空白等价于"不过滤"。用变量接一下，避免循环内反复判空。
            var filter = string.IsNullOrWhiteSpace(groupIdFilter) ? null : groupIdFilter;

            // EnumerateFiles 默认不递归 → chat_history\trash（会话回收站）天然不会被搜到，
            // 这正是我们要的：回收站里的会话不该出现在搜索结果里。
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));

                    // 有效性判据与 ListSessions() 完全一致：Mode 能解析 + 至少有一条非 system 消息。
                    // 两边口径必须同步，否则搜索能搜到列表里看不到的"幽灵会话"。
                    if (payload == null || !Enum.TryParse<ExplainMode>(payload.Mode, out _)) continue;

                    var messages = payload.Messages ?? new List<ChatMessage>();
                    if (!messages.Any(m => m.Role != ChatRoles.System)) continue; // 空会话（仅 system）不进结果

                    if (filter != null && (payload.GroupId ?? "") != filter) continue;

                    var text = BuildSearchText(payload);
                    var hitCount = ChatSearchMatcher.CountMatches(text, query);
                    if (hitCount == 0) continue;

                    var snippet = ChatSearchMatcher.ExtractSnippet(text, query);
                    var firstUser = messages.FirstOrDefault(m => m.Role == ChatRoles.User)?.Content ?? "";

                    result.Add(new ChatSearchHit(
                        string.IsNullOrEmpty(payload.Id) ? Path.GetFileNameWithoutExtension(file) : payload.Id,
                        file,
                        payload.SavedAt,
                        payload.Title ?? "",
                        BuildPreview(payload.Title, firstUser),
                        snippet?.Snippet ?? "",
                        snippet?.Start ?? 0,
                        snippet?.Length ?? 0,
                        hitCount));
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // 单个文件损坏 / 被占用只跳过它，绝不中断整体搜索。
                    Debug.WriteLine($"[FocusCapture] 搜索跳过会话文件: {file}: {ex.Message}");
                }
            }

            // 排序：SavedAt 倒序（最近的在最前）。
            // 这里刻意不做"置顶优先"（历史列表有），因为搜索场景下用户要的是相关性/时间，而非收藏顺序。
            result.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
            if (result.Count > MaxResults) result.RemoveRange(MaxResults, result.Count - MaxResults);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 会话全文搜索失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>把会话转成可搜索正文：标题 + 全部非 system 消息的 Content（用 \n 连接）。
    /// 为什么不直接构造会话实例再取 Messages：搜索只需要"文本 + 元数据"，直接读文件结构更省内存、也不触发会话相关的副作用。</summary>
    public static string BuildSearchText(SessionFile session)
    {
        var sb = new StringBuilder();
        sb.Append(session.Title ?? "");

        var messages = session.Messages ?? new List<ChatMessage>();
        foreach (var m in messages)
        {
            if (m.Role == ChatRoles.System) continue;      // system 是模板提示词，搜它没意义还容易误命中
            // tool 是 Agent 工具返回的**机器数据**（文件内容 / JSON / 网页正文），动辄几千字。
            // 收录它会让一次搜索命中大量「其实与用户想问的无关」的会话，把真正的用户内容淹没。
            if (m.Role == ChatRoles.Tool) continue;
            if (string.IsNullOrEmpty(m.Content)) continue; // 空内容不占位，避免正文里出现无意义的连续换行
            sb.Append('\n').Append(m.Content);
        }
        return sb.ToString();
    }

    /// <summary>列表预览口径：标题优先，否则首条用户消息前 40 字 + 省略号。
    /// 这里是 ChatSessionService.BuildPreview 的私有副本 —— 对方是 private，且本次改动不允许动那个文件，
    /// 所以只能复制一份。两者口径必须保持一致，搜索结果里的 Preview 才能跟历史列表里看到的完全一样。</summary>
    private static string BuildPreview(string? title, string firstUser)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title;
        return firstUser.Length > 40 ? firstUser[..40] + "…" : firstUser;
    }
}
