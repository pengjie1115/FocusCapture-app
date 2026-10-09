namespace FocusCapture.Services.AI;

/// <summary>
/// 消息正文全文搜索的纯匹配逻辑（无 IO、无状态）。
/// 为什么单独拆一个类：本类会被"快层测试"以链接源码的方式直接编进测试工程，
/// 因此**严禁引用项目内任何其他类型**（只用 string / 基本类型），一旦引入依赖链接就会编译失败。
/// 好处是它没有任何副作用，服务层与 UI 可以并发安全调用。
/// </summary>
public static class ChatSearchMatcher
{
    /// <summary>片段上下文字符数：命中词前后各取这么多字符。</summary>
    public const int ContextChars = 20;

    /// <summary>在 content 中查找 query 第一次出现的位置，返回带上下文的片段。
    /// 大小写不敏感（StringComparison.OrdinalIgnoreCase）。未命中返回 null。
    /// 片段 = 命中词 + 前后各 contextChars 个字符（越界则从行首/行尾裁，不补省略号）。
    /// Start / Length = 命中词在【返回片段内】的起始下标与长度（供 UI 高亮用）。</summary>
    public static (string Snippet, int Start, int Length)? ExtractSnippet(string content, string query, int contextChars = ContextChars)
    {
        // 入参兜底：这里刻意不抛异常 —— 搜索路径上任何一次抛异常都会让整份结果作废，
        // 所以"输入不合法"一律按"没命中"处理（返回 null，由调用方跳过该条）。
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (string.IsNullOrEmpty(content)) return null;

        var hit = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (hit < 0) return null;

        // 负的 contextChars 无意义，夹到 0，避免下面算出越界下标。
        var ctx = contextChars < 0 ? 0 : contextChars;

        // 先在【原始】正文里按真实下标裁出窗口（这样"是否命中"完全忠于磁盘上的原文）。
        var start = Math.Max(0, hit - ctx);
        var end = Math.Min(content.Length, hit + query.Length + ctx);
        var raw = content.Substring(start, end - start);

        // 关键坑：正文里的换行/制表符会让 UI 片段断成多行、撑坏布局，统一折叠成单个空格。
        // 折叠会改变串长（\r\n 两个字符 → 一个空格），所以命中下标**必须在片段里重新定位**；
        // 若直接沿用 hit - start 会得到错位的高亮区间（这是最容易踩的坑）。
        var snippet = NormalizeWhitespace(raw);

        var newStart = snippet.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (newStart < 0)
        {
            // 走到这里通常只有一种情况：query 自身含有被折叠的空白（例如用户粘贴了带换行的词），
            // 折叠后的片段里已找不到原样 query。用同样规则归一化 query 再定位，保证不抛且高亮合理。
            var normalizedQuery = NormalizeWhitespace(query);
            newStart = normalizedQuery.Length == 0
                ? 0
                : snippet.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
            if (newStart < 0) newStart = 0; // 极端兜底：宁可高亮从片段开头起，也不返回越界下标

            var len = normalizedQuery.Length;
            if (len > snippet.Length - newStart) len = snippet.Length - newStart;
            return (snippet, newStart, len < 0 ? 0 : len);
        }

        return (snippet, newStart, query.Length);
    }

    /// <summary>统计 content 中 query 出现的总次数（大小写不敏感，不重叠计数）。</summary>
    public static int CountMatches(string content, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (string.IsNullOrEmpty(content)) return 0;

        var count = 0;
        var pos = 0;
        while (true)
        {
            var idx = content.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            count++;
            pos = idx + query.Length; // 不重叠：命中后跳到词尾继续找（query 非空，pos 必然前进，循环可终止）
        }
        return count;
    }

    /// <summary>把 \r\n / \n / \r / \t 各折叠成一个空格（\r\n 作为一个整体只算一个空格）。
    /// 只做"控制空白 → 空格"这一件事，不合并连续空格 —— 保持与"命中次数/位置"的直观一致，也避免过度改写正文。</summary>
    private static string NormalizeWhitespace(string s)
    {
        // 先扫一遍判断是否需要重建：纯单行的常见情况可原样返回，省一次字符串分配。
        var needs = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\r' || c == '\n' || c == '\t') { needs = true; break; }
        }
        if (!needs) return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\r')
            {
                sb.Append(' ');
                // \r\n 视为一个换行标记：吞掉后面的 \n，否则会多出一个空格
                if (i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
            else if (c == '\n' || c == '\t')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
