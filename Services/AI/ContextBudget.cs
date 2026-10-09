namespace FocusCapture.Services.AI;

/// <summary>
/// 裁剪用的消息视图（2026-09-23）。刻意**不直接用 <c>ChatMessage</c>** ——
/// 那样这个文件就得依赖 ChatAttachment 等一堆东西，进不了快层。
/// 调用方做一次几行的映射，换来「裁剪规则能被秒级检查点钉死」。
/// </summary>
public sealed record ContextItem(
    string Role,
    bool HasToolCalls,
    int TextTokens,
    int ImageTokens)
{
    public int TotalTokens => TextTokens + ImageTokens;
}

/// <summary>
/// 裁剪计划。<paramref name="UserHint"/> 是给用户看的轻提示（没裁剪时为 null）。
/// <paramref name="KnownTokens"/> / <paramref name="UsableTokens"/> 是诊断值：
/// 前者是本次内容的估算总量，后者是扣掉固定开销与安全余量后的可用预算（不含固定部分）。
/// </summary>
public sealed record ContextTrimPlan(
    bool ShouldTrim,
    int KeepFromIndex,
    int DroppedCount,
    bool SingleMessageTooLarge,
    string? UserHint,
    int KnownTokens = 0,
    int UsableTokens = 0);

/// <summary>
/// 上下文预算与裁剪（2026-09-23）。
///
/// <para><b>为什么单独做成纯函数</b>：这里每一个坑错了都不会报错，只会在特定长会话里
/// 表现为「AI 莫名其妙忘了前面说的」或者「随机 400」。所以必须能被机器反复测。</para>
///
/// <para><b>六个坑（设计稿 §6）</b>：
/// ① 估算必然不准 → 保守系数 + 20% 安全余量，绝不顶格；
/// ② **工具调用对不能拆散** → 最小丢弃单位是「assistant(tool_calls) + 其后所有 tool 结果」整组；
/// ③ tools 定义的固定开销不能丢，必须先扣；
/// ④ **图片 token 不按字符算**（按分辨率折算），字符估算对图片彻底失效 → 每张给固定值；
/// ⑤ 裁剪是隐形的 → 必须给用户轻提示，否则只会被解读成「AI 变笨了」；
/// ⑥ 单条消息本身就超窗口 → 拒绝发送 + 人话提示，别去撞英文 400。</para>
/// </summary>
public static class ContextBudget
{
    /// <summary>安全余量。估算天生不准（没有 tokenizer），顶格算必然偶发超窗。</summary>
    public const int SafetyMarginPercent = 20;

    /// <summary>单张图片的固定估算值。一张 1568px 的图约消耗 1000~2000 token，取保守值。</summary>
    public const int ImageTokensPerImage = 1400;

    /// <summary>CJK 字符的保守系数：约 1.5 字符/token（中文比英文密，不能按 ASCII 的 4 字符算）。</summary>
    private const double CharsPerTokenCjk = 1.5;

    /// <summary>非 CJK 字符系数：约 4 字符/token。</summary>
    private const double CharsPerTokenOther = 4.0;

    /// <summary>
    /// 直接写字面量而不用 <c>RoleTool</c>：引用那个常量就得把 ChatModels.cs 也链进来，
    /// 而它带着 ChatAttachment 一串依赖 —— 这个文件就进不了快层了。
    /// </summary>
    private const string RoleTool = "tool";

    /// <summary>
    /// 估算文本 token 数（**保守偏高**）。没有 tokenizer 就只能这样 ——
    /// 宁可早一点裁剪，也不要「以为还装得下」然后被供应商 400。
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        foreach (var ch in text)
            if (IsCjk(ch)) cjk++;
        var other = text.Length - cjk;
        return (int)Math.Ceiling(cjk / CharsPerTokenCjk + other / CharsPerTokenOther);
    }

    private static bool IsCjk(char ch)
        => (ch >= 0x4E00 && ch <= 0x9FFF)     // 基本汉字
        || (ch >= 0x3000 && ch <= 0x303F)     // 中文标点
        || (ch >= 0xFF00 && ch <= 0xFFEF)     // 全角字符
        || (ch >= 0x3040 && ch <= 0x30FF);    // 日文假名

    /// <summary>
    /// 组装裁剪计划。<paramref name="items"/> 里**不含系统提示词**（系统的 token 由
    /// <paramref name="fixedOverheadTokens"/> 覆盖），因为系统提示词永远不会被丢。
    ///
    /// <para>窗口为 0（用户留空）→ 直接不裁剪，与改造前行为一致。</para>
    /// </summary>
    public static ContextTrimPlan Plan(
        IReadOnlyList<ContextItem> items,
        int contextWindow,
        int maxOutputTokens,
        int fixedOverheadTokens)
    {
        var known = items.Sum(x => x.TotalTokens);
        if (contextWindow <= 0 || items.Count == 0)
            return new ContextTrimPlan(false, 0, 0, false, null, known, 0);

        var usable = contextWindow - Math.Max(0, maxOutputTokens) - Math.Max(0, fixedOverheadTokens);
        usable -= usable * SafetyMarginPercent / 100;
        if (usable <= 0)
        {
            // 固定开销（系统提示词 + 工具定义）已经把窗口吃光了：这种情况连一条消息都塞不下
            return new ContextTrimPlan(false, 0, 0, true,
                "系统提示词与工具定义已经占满模型窗口，请换上下文窗口更大的模型",
                known, Math.Max(0, usable));
        }

        var groups = GroupByToolUnit(items);

        // 从最后一组往前累加，累到装不下为止 —— 保留最近的对话（最新的上下文最要紧）
        var total = 0;
        var keepFrom = groups.Count;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var size = groups[i].Sum(x => x.TotalTokens);
            if (total + size > usable) break;
            total += size;
            keepFrom = i;
        }

        // 最后一组（用户最新那条）自己就超窗口 —— 裁剪历史救不了，只能拒绝发送
        if (keepFrom == groups.Count)
        {
            var last = groups[^1].Sum(x => x.TotalTokens);
            if (last > usable)
            {
                return new ContextTrimPlan(false, 0, 0, true,
                    $"这一条内容太大了（约 {last} token），超过模型能装下的上限（约 {usable} token）。"
                    + "可以拆成几次发，或换上下文窗口更大的模型。",
                    known, usable);
            }
            // 理论上到不了这里；真到了说明预算算错，保守地不裁剪（宁可撞 400 也不静默丢消息）
            return new ContextTrimPlan(false, 0, 0, false, null, known, usable);
        }

        if (keepFrom == 0) return new ContextTrimPlan(false, 0, 0, false, null, known, usable);

        var dropped = groups.Take(keepFrom).Sum(g => g.Count);
        return new ContextTrimPlan(true, keepFrom, dropped, false,
            $"已达上下文上限：最早的 {dropped} 条本轮不再发送（更早的内容可以开新对话继续）",
            known, usable);
    }

    /// <summary>
    /// 按「完整工具调用单元」分组 —— <b>这是坑 ② 的落地</b>。
    ///
    /// <para>Agent 会话里有 <c>assistant(tool_calls)</c> 紧跟若干 <c>tool</c> 结果的结构。
    /// 若丢掉其中一半，接口会直接 400（tool_call_id 找不到对应调用），而且**随机复现**。
    /// 所以最小丢弃单位是整组：要么全留，要么全丢。</para>
    /// </summary>
    private static List<List<ContextItem>> GroupByToolUnit(IReadOnlyList<ContextItem> items)
    {
        var groups = new List<List<ContextItem>>();
        foreach (var item in items)
        {
            var cur = groups.Count > 0 ? groups[^1] : null;
            var joinsPrevious = item.Role == RoleTool
                && cur != null
                && (cur[0].HasToolCalls || cur[^1].Role == RoleTool);
            if (joinsPrevious) cur!.Add(item);
            else groups.Add(new List<ContextItem> { item });
        }
        return groups;
    }
}
