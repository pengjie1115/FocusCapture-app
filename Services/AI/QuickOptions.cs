namespace FocusCapture.Services.AI;

/// <summary>
/// 快捷选项协议（2026-09-27，用户需求「AI 需要确认/多选时主动给按钮」）。
///
/// 模型侧：系统提示词要求模型在需要用户选择/确认/补参数时，于回复末尾输出一行
/// <c>[[OPTIONS:选项一|选项二|选项三]]</c>；不需要时绝不输出。
/// 前端侧：流式期间用 <see cref="StripForDisplay"/> 把标记（含尾部半截）从正文里藏掉；
/// 回答结束用 <see cref="Parse"/> 剥除标记并把选项渲染成气泡下方的胶囊按钮，
/// 点击 = 把选项文字作为用户消息直接发送（发送型）。会话历史保留含标记的原文，
/// 剥除只发生在显示层 —— 模型下一轮仍能看到自己给过什么选项（上下文连贯）。
/// </summary>
public static class QuickOptions
{
    public const string MarkerStart = "[[OPTIONS:";
    public const string MarkerEnd = "]]";
    public const int MaxOptions = 4;
    public const int MaxOptionLength = 20;

    /// <summary>注入 BuildSystemPrompt 的协议说明（各模式共用段落）。
    /// static readonly 而非 const：插值串里带 int 上限值，不构成常量表达式。</summary>
    public static readonly string SystemPromptRules =
        "当你的回答需要用户做出选择、确认或补充信息时（例如：询问是否执行操作、给出多个方案让用户挑、" +
        "缺少必要信息无法继续），在回答末尾另起一行输出选项标记，格式：" + MarkerStart + "选项一|选项二|选项三" + MarkerEnd +
        $"。最多 {MaxOptions} 个选项，每个不超过 {MaxOptionLength} 个字，选项必须是用户点一下就能直接发出的完整短句。" +
        "用户看到的是可点击按钮，点击后相当于替用户发送了那句话；你收到后按普通消息继续处理。" +
        "不需要用户选择时绝不要输出标记，也不要向用户解释这个标记或按钮的存在。";

    /// <summary>
    /// 从完整回复解析选项。cleanText = 剥除标记后的正文（标记独占一行时连同换行清干净）。
    /// 没有标记或标记残缺（无收尾 ]]）→ 正文原样、选项空表。
    /// 选项按 | 切分后去空白、跳过空项与超长项（&gt; <see cref="MaxOptionLength"/> 字的整条丢弃，防模型跑飞），
    /// 最多取前 <see cref="MaxOptions"/> 个。
    /// </summary>
    public static IReadOnlyList<string> Parse(string? content, out string cleanText)
    {
        cleanText = content ?? "";
        if (string.IsNullOrEmpty(content)) return Array.Empty<string>();

        var start = content.LastIndexOf(MarkerStart, StringComparison.Ordinal);
        if (start < 0) return Array.Empty<string>();
        var valueStart = start + MarkerStart.Length;
        var end = content.IndexOf(MarkerEnd, valueStart, StringComparison.Ordinal);
        if (end < 0) return Array.Empty<string>();   // 残缺标记不解析（正文原样保留，用户至少能看到模型说了什么）

        var options = content[valueStart..end]
            .Split('|')
            .Select(o => o.Trim())
            .Where(o => o.Length > 0 && o.Length <= MaxOptionLength)
            .Take(MaxOptions)
            .ToList();

        var head = content[..start].TrimEnd('\r', '\n');
        var tail = content[(end + MarkerEnd.Length)..].TrimStart('\r', '\n');
        cleanText = tail.Length > 0 ? head + "\n\n" + tail : head;
        return options;
    }

    /// <summary>
    /// 流式显示用：剥掉完整标记，并截掉尾部**半截**标记（模型逐字输出时
    /// "[[OPTIO" 这类前缀片段若直接显示，用户会看到标记闪现又消失）。
    /// 与 Parse 的区别：半截标记不是剥除而是隐藏显示，正文缓冲区不动。
    /// </summary>
    public static string StripForDisplay(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";
        var text = content;
        var start = text.LastIndexOf(MarkerStart, StringComparison.Ordinal);
        if (start >= 0)
        {
            // 有 MarkerStart：若它有收尾则剥完整标记；没有收尾则它是半截，从它起全藏
            var end = text.IndexOf(MarkerEnd, start + MarkerStart.Length, StringComparison.Ordinal);
            text = end >= 0
                ? JoinDisplayParts(text[..start], text[(end + MarkerEnd.Length)..])
                : text[..start];
        }
        // 尾部再查一次半截前缀（剥完整标记后不可能再有，这里只处理纯半截场景）
        foreach (var k in Enumerable.Range(1, MarkerStart.Length - 1).Reverse())
        {
            var prefix = MarkerStart[..k];
            if (text.EndsWith(prefix, StringComparison.Ordinal)) return text[..^k];
        }
        return text;
    }

    /// <summary>剥标记后拼接前后两段：与 Parse 的 cleanText 同口径（标记独占行时换行清干净、中段留空行）。</summary>
    private static string JoinDisplayParts(string head, string tail)
    {
        head = head.TrimEnd('\r', '\n');
        tail = tail.TrimStart('\r', '\n');
        return tail.Length > 0 ? head + "\n\n" + tail : head;
    }
}
