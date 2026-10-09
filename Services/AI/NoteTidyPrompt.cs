using System.Text.RegularExpressions;

namespace FocusCapture.Services.AI;

/// <summary>
/// 「AI 整理」的提示词、长度闸门与结果清洗 —— <b>刻意零项目依赖</b>（只吃 string、不碰配置 / 日志 / WPF），
/// 所以能被快层检查点直接链接并秒级验证（同 TokenCountParser / ContextBudget 的做法）。
///
/// <para><b>为什么这些要单独成文件而不是塞进调用点</b>：这三件事坏了的表现<b>全是静默的</b> ——
/// 长度闸门算错 → 超长正文照发，用户等半天拿到 400 或半截结果；
/// 结果清洗漏掉围栏 → 笔记里凭空多出 ``` 和「以下是整理后的内容：」；
/// 提示词漏掉「不翻译」→ 模型开始自由发挥，把用户的原始记录改成了另一件事。</para>
///
/// <para><b>2026-09-27 规则化改造</b>：系统提示词拆成「通用输出纪律 + 规则指令段」两段
/// （<see cref="OutputDiscipline"/> + <see cref="TidyInstruction"/> 等四段预置指令），
/// 预置规则两段拼接；<b>自定义规则整段替换、不拼任何硬约束</b>（用户拍板：完全交给用户）。
/// 原来的⑦「只用 - 与 **」按用户要求取消 —— 整理结果是给人看的，层级用正常序号（1. 2. 3.）与缩进。</para>
/// </summary>
public static class NoteTidyPrompt
{
    /// <summary>
    /// 单次整理允许的最大输入字符数（超出直接拒绝，不做截断 / 分段 —— 2026-09-26 用户拍板）。
    /// 取值依据：中文 1 字符约 1~1.7 token，8000 字符 ≈ 8k~13k token，加上系统提示词与输出余量，
    /// 主流 32k 窗口模型都能吃下；再长就该让用户自己先精简，而不是赌模型记得住后半段。
    /// </summary>
    public const int MaxInputChars = 8000;

    /// <summary>
    /// 预置规则共用的<b>通用输出纪律</b>（原「硬约束」收窄后的残留）：
    /// 不翻译 / 无开场白无围栏 / 层级用正常序号 / 禁 Markdown 记号。
    /// 原①「不新增事实」**不在**这里 —— 与「解释扩展」冲突（它就是要新增解释）、与「总结」的"压缩"也有张力，
    /// 所以移进各规则的指令段自己声明（2026-09-27）。
    ///
    /// <para><b>2026-09-27 phase2</b>：④「禁 Markdown 记号」为用户拍板新增 —— 结果区是纯 TextBox，
    /// 模型带回的 ** 与 - 展示出来就是裸符号（用户原话：「彻底放弃这种排版样式」）。
    /// 提示词之外还有 <see cref="NormalizePlainFormatting"/> 机械兜底，双保险。</para>
    /// </summary>
    public const string OutputDiscipline =
        "你是笔记整理助手。遵守以下通用输出纪律：" +
        "① 保持原文语言（中文还是中文），不要翻译；" +
        "② 直接输出结果正文，不要任何开场白、结语、说明或代码块围栏；" +
        "③ 分层与分点用正常序号（1. 2. 3.）与缩进表达，小标题直接写一行文字（可用冒号收尾）；" +
        "④ 不用任何 Markdown 记号：不用 ** 加粗、不用 - 或 * 当分点符号 —— 输出是给人直接读的纯文本，不是渲染源码。";

    /// <summary>「理顺条理」（默认规则）的指令段：不改原意，只做逻辑重组 —— 原默认提示词的 ②③⑤。</summary>
    public const string TidyInstruction =
        "用户的文字可能是复制来的一大段，也可能是随手写的、逻辑不清的内容，请把它整理得条理清楚。规则：" +
        "① 完整保留原意与全部信息，不新增任何事实、不做评价、不删要点；" +
        "② 按逻辑重新组织：相关的内容归到一起，用分点或短段落，必要时加小标题；" +
        "③ 去掉重复啰嗦与口误，改正明显的错别字，把口语化的表达改通顺；" +
        "④ 若内容是待办事项，保留时间、地点、对象等要素，并按执行顺序排列。";

    /// <summary>「总结摘要」的指令段：压缩成简明摘要，硬信息必须保留。</summary>
    public const string SummaryInstruction =
        "把用户给的这段文字压缩成一份简明摘要。规则：" +
        "① 先用一两句话给出核心结论，再用分点列出关键信息；" +
        "② 时间、日期、数字、人名、金额等硬信息必须原样保留，不许改写；" +
        "③ 不新增任何事实、不做评价；摘要省略细节是本规则的预期行为，不算丢要点。";

    /// <summary>
    /// 「提取待办」的指令段：逐行输出、行内带时间戳，供 <see cref="ParseTodoLines"/> 机械解析
    /// —— 格式是机器约定，坏一行就少一条待办，所以这里用「本规则不用序号」显式压住输出纪律的③。
    ///
    /// <para><b>2026-09-27 phase2</b>：① 明确年份补全规则（没写年份：未到补当年、已过顺延一年 ——
    /// 今天日期由 <c>NoteTidyService</c> 随消息注入，模型自己不知道今天几号）；② 明确「上午9点12分 → 09:12」
    /// 的换算示例；③ 禁止照抄原文里的「【yyyy-MM-dd】」占位写法（实测模型抄过，见 <see cref="ParseTodoLines"/> 兜底）；
    /// ④ 要求正文不重复时间（已在行首【】里）。</para>
    /// </summary>
    public const string ExtractTodoInstruction =
        "从用户给的文字中只提取「要做的事」（待办事项），按执行顺序排列。输出格式（本规则不用序号、不用分点符号）：" +
        "每行一条待办；若原文提到了时间（钟点、日期、星期、「明天上午」这类表达都算），必须换算成真实日期写在行首" +
        "「【yyyy-MM-dd HH:mm】」（只有日期没有具体钟点就「【yyyy-MM-dd】」，「上午9点12分」要写成 09:12）；" +
        "原文没写年份的日期：今年还没到就补今年，今年已经过了就顺延一年（消息会告知今天的日期，按它算）；" +
        "【】里必须写换算出来的真实日期，原文里的占位写法（例如「【yyyy-MM-dd】」）绝不允许照抄；" +
        "时间已经写在行首【】里，正文就不要再重复时间表达；除此之外不要输出任何别的文字。若确实没有待办，只输出「未发现待办」。";

    /// <summary>
    /// 「解释扩展」的指令段：唯一允许新增内容的预置规则，边界收在「末尾附注」——
    /// 原文一字不改，解释以附注小节追加，产出天然只适合复制 / 另存。
    /// </summary>
    public const string ExplainInstruction =
        "先完整保留用户原文（只改正明显的错别字，不删要点、不改内容、不重组结构），" +
        "然后在末尾另起一段加小节「—— 名词解释」：从原文中挑出真正影响理解这段话的概念（最多 5 个），" +
        "每个概念用 1-2 句说明「是什么 + 为什么在这段话里重要」；" +
        "原文之外的主题、无关紧要的名词一律不解释、不展开。";

    /// <summary>默认系统提示词 = 输出纪律 + 理顺条理（规则为空时的兜底，与旧版行为对齐）。</summary>
    public const string DefaultSystem = OutputDiscipline + "\n" + TidyInstruction;

    /// <summary>
    /// 组装一次请求的消息对（system + 用户正文）。
    /// <paramref name="system"/> 传 null = 按默认规则（理顺条理）；预置规则由调用方拼
    /// OutputDiscipline + 指令段，自定义规则直接传用户提示词（整段替换，见 <see cref="OutputDiscipline"/> 注释）。
    /// 返回元组而非 ChatMessage，是为了保持本文件零依赖。
    /// </summary>
    public static (string System, string User) BuildMessages(string? text, string? system = null)
        => (string.IsNullOrWhiteSpace(system) ? DefaultSystem : system!, (text ?? "").Trim());

    /// <summary>正文字符数（前后空白不计）。</summary>
    public static int CharCount(string? text) => (text ?? "").Trim().Length;

    /// <summary>是否超长（超长 = 拒绝整理，见 <see cref="TooLongMessage"/>）。</summary>
    public static bool IsTooLong(string? text) => CharCount(text) > MaxInputChars;

    /// <summary>超长时给用户的人话提示（带上限，用户才知道要精简到多少）。</summary>
    public static string TooLongMessage(int chars) =>
        $"这段内容太长了（{chars} 字符，上限 {MaxInputChars} 字符），AI 整理不了这么长的正文。\n\n" +
        "请先自己删掉一部分再试 —— 硬发过去要么被服务商拒绝，要么只整理了前半段（那样更糟：你会以为后面那半段也整理过了）。";

    /// <summary>
    /// 解析「提取待办」的回包为待办行（文字 + 可空截止时间）。<b>机械解析、绝不抛</b>：
    /// ① 逐行剥掉行首的序号 / 分点符号（模型不听"不用序号"的招呼时兜住）；
    /// ② 行首【yyyy-MM-dd( HH:mm)】识别为截止时间（识别不出就整个当纯文字）；
    /// ③ 行首字面量「【yyyy-MM-dd】」占位写法直接剥掉（2026-09-27 phase2：模型照抄原文占位符，实测发生过）；
    /// ④ 行没带【】时交给 <paramref name="timeFallback"/> 再认一次时间（2026-09-27 phase2：
    ///    调用方注入 <c>TimeParser</c>，本文件保持零依赖；fallback 只补截止时间、不动文字 ——
    ///    文字里的时间表达要不要剥离由模型按提示词重写，机械剥离会切坏「十点**前**」这类嵌字）；
    /// ⑤ 空行跳过。解析出 0 条由调用方兜底（预览窗退回纯文本、创建待办置灰）。
    /// </summary>
    public static List<(string Text, DateTime? Due)> ParseTodoLines(string? raw, Func<string, DateTime?>? timeFallback = null)
    {
        var result = new List<(string Text, DateTime? Due)>();
        foreach (var line in (raw ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var t = StripListPrefix(line.Trim());
            if (t.Length == 0) continue;

            DateTime? due = null;
            var m = TodoDueRegex.Match(t);
            if (m.Success)
            {
                var date = new DateTime(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["mo"].Value),
                    int.Parse(m.Groups["d"].Value));
                if (m.Groups["hh"].Success)
                    date = date.AddHours(int.Parse(m.Groups["hh"].Value)).AddMinutes(int.Parse(m.Groups["mi"].Value));
                due = date;
                t = t[m.Length..].Trim();
            }
            else
            {
                var ph = TodoPlaceholderRegex.Match(t);
                if (ph.Success) t = t[ph.Length..].Trim();
            }
            if (t.Length == 0) continue;
            if (due == null && timeFallback != null) due = timeFallback(t);
            result.Add((t, due));
        }
        return result;
    }

    /// <summary>模型按指令回的「没有待办」标记 —— 调用方据此显示空态而不是建一条叫"未发现待办"的待办。</summary>
    public static bool IsNoTodoMarker(string? text) =>
        string.Equals((text ?? "").Trim(), "未发现待办", StringComparison.Ordinal);

    /// <summary>行首的 Markdown 序号 / 分点符号（"- "、"1. "、"（1）"……模型不守格式时兜底；循环剥，连着两层也剥干净）。</summary>
    private static string StripListPrefix(string line)
    {
        var t = line;
        for (var i = 0; i < 3; i++)
        {
            var m = ListPrefixRegex.Match(t);
            if (!m.Success) break;
            t = t[m.Length..].Trim();
        }
        return t;
    }

    private static readonly Regex TodoDueRegex =
        new(@"^【\s*(?<y>\d{4})-(?<mo>\d{1,2})-(?<d>\d{1,2})(?:\s+(?<hh>\d{1,2}):(?<mi>\d{2}))?\s*】\s*");

    /// <summary>行首字面量占位「【yyyy-MM-dd( HH:mm)】」—— 不是真实日期，剥掉前缀、日期留空（提示词已禁，这里兜底）。</summary>
    private static readonly Regex TodoPlaceholderRegex =
        new(@"^【\s*y\s*y\s*y\s*y\s*-\s*M\s*M\s*-\s*d\s*d(?:\s+H\s*H\s*:\s*m\s*m)?\s*】\s*", RegexOptions.IgnoreCase);

    private static readonly Regex ListPrefixRegex = new(@"^(?:[-*•·]|（?\d{1,2}[）.、．]|\(\d{1,2}\))\s*");

    /// <summary>
    /// 清洗模型回包：① 剥掉整体包裹的 ``` 代码块围栏；② 剥掉「以下是整理后的内容：」这类开场白。
    ///
    /// <para><b>无条件保留（2026-09-27 拍板）</b>：自定义规则整段替换系统提示词，用户可以要求模型输出代码块 ——
    /// 但清洗是纯机械动作、与提示词无关，砍了它用户随手一条提示词就会让笔记里塞满 ```。
    /// 只剥<b>确实像前言</b>的首行（≤40 字符、以冒号或句号结尾、且含整理类措辞），
    /// 且剥完必须还剩正文 —— 宁可留着丑，也不能把用户正文的第一行当开场白吃掉。</para>
    /// </summary>
    public static string CleanResult(string? raw)
    {
        var t = (raw ?? "").Replace("\r\n", "\n").Trim();
        if (t.Length == 0) return "";

        // ① 整体围栏：^```lang\n ... \n```$
        var fence = Regex.Match(t, @"^```[A-Za-z]*[ \t]*\n(?<body>[\s\S]*?)\n?```$");
        if (fence.Success) t = fence.Groups["body"].Value.Trim();

        // ② 开场白：只剥行首，最多 3 行，且剥完必须还有正文
        var lines = t.Split('\n').ToList();
        var removed = 0;
        while (lines.Count > 1 && removed < 3)
        {
            var head = lines[0].Trim();
            if (head.Length == 0) { lines.RemoveAt(0); removed++; continue; }
            if (!IsPreamble(head)) break;
            lines.RemoveAt(0);
            removed++;
        }

        return string.Join("\n", lines).Trim();
    }

    /// <summary>这一行像不像模型的开场白（而非用户正文）。判据刻意收紧，避免误吃正文首行。</summary>
    public static bool IsPreamble(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0 || s.Length > 40) return false;
        if (!(s.EndsWith("：", StringComparison.Ordinal)
              || s.EndsWith(":", StringComparison.Ordinal)
              || s.EndsWith("。", StringComparison.Ordinal))) return false;

        return s.Contains("以下是", StringComparison.Ordinal)
            || s.Contains("整理后", StringComparison.Ordinal)
            || s.Contains("整理如下", StringComparison.Ordinal)
            || s.Contains("帮你整理", StringComparison.Ordinal)
            || s.Contains("已整理", StringComparison.Ordinal)
            || s.Contains("好的", StringComparison.Ordinal);
    }

    /// <summary>
    /// 预置规则结果的<b>排版机械清洗</b>（2026-09-27 phase2 用户拍板「彻底放弃 * 与 -」）：
    /// ① 去掉 <c>**</c> 加粗记号（结果区是纯 TextBox，** 展示出来就是裸符号）；
    /// ② 行首 <c>- </c>/<c>* </c>/<c>• </c> 分点符号转「· 」（保留缩进；中文排版正常符号，非 Markdown 记号）；
    /// 序号（1. 2.）与正文一律不动。<b>只对预置规则生效</b> —— 自定义规则整段替换提示词（用户拍板：完全交给用户），
    /// 用户主动要的 Markdown 不吃掉；调用方（NoteTidyService）按规则是否预置分流。
    /// 与 <see cref="CleanResult"/> 分开两个方法：那边守「脏数据」（围栏/开场白），这边管「排版」，测试各守各的。
    /// </summary>
    public static string NormalizePlainFormatting(string? raw)
    {
        var t = (raw ?? "").Replace("\r\n", "\n");
        if (t.Length == 0) return "";
        var lines = t.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var bullet = BulletPrefixRegex.Match(line);
            if (bullet.Success)
                line = bullet.Groups["indent"].Value + "· " + bullet.Groups["rest"].Value;
            lines[i] = line.Replace("**", "");
        }
        return string.Join("\n", lines).Trim();
    }

    /// <summary>行首分点符号（缩进保留；要求符号后跟空白，防把「----」分隔线或「*」乘号误伤）。</summary>
    private static readonly Regex BulletPrefixRegex = new(@"^(?<indent>\s*)[*•-]\s+(?<rest>.*)$");
}
