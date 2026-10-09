namespace FocusCapture.Services.AI;

/// <summary>会话内查找的一条命中：第几条气泡（按 Bubbles 顺序）+ 命中词在气泡正文里的起始下标。</summary>
public readonly record struct InSessionFindHit(int BubbleIndex, int Start);

/// <summary>
/// 会话内查找的**纯逻辑**（不碰 UI，供检查点直接断言）：把一条关键词在会话全部气泡正文里找齐。
/// 口径与 ChatSearchMatcher 一致：OrdinalIgnoreCase（大小写不敏感）；空关键词 = 无命中。
/// 命中顺序 = 气泡顺序 → 气泡内从左到右，UI 的「上一个 / 下一个」直接对这个列表做循环步进。
/// </summary>
public static class InSessionFindMatcher
{
    /// <summary>在气泡正文列表里找齐全部命中。bubbleTexts 与会话 Bubbles 一一对应（null/空串 = 该气泡无正文）。</summary>
    public static List<InSessionFindHit> FindAll(IReadOnlyList<string?> bubbleTexts, string query)
    {
        var hits = new List<InSessionFindHit>();
        if (string.IsNullOrEmpty(query)) return hits;

        for (var i = 0; i < bubbleTexts.Count; i++)
        {
            var text = bubbleTexts[i];
            if (string.IsNullOrEmpty(text)) continue;

            var start = 0;
            int idx;
            while ((idx = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                hits.Add(new InSessionFindHit(i, idx));
                start = idx + query.Length;
            }
        }
        return hits;
    }
}
