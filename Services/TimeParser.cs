using System.Text.RegularExpressions;

namespace FocusCapture.Services;

/// <summary>
/// 本地规则时间解析（v2）：两段式——先提时刻（小时+分钟），再找日期/时段上下文，组合成绝对时间。
/// 相比 v1（7 条正则各自为战）：
/// - 汉字数字：八点 / 八点零三 / 三十号 / 两小时后
/// - 分钟写法：8点03 / 8点零三 / 8点半 / 8点一刻（v1 只认"分"字或"半"）
/// - 全角冒号：8：03（中文输入法默认全角）
/// - 点/时通吃：8点30分 / 8时30分
/// - 凌晨时段：凌晨3点 / 凌晨十二点（v1 会把凌晨十二点认成中午12点）
/// - 合成时段词：今晚8点 / 明早8点 / 明晚9点（规范化拆成 日期词+时段）
/// - 日期表达：本月30号 / 30号 / 8月27号 / 大后天 / 下周三 / 下个月5号
/// - 数字日期：10/8 / 10-8 / 2026/10/8（月在前；月 1-12、日 1-31 合法才识别；日期已过不识别、不顺延）
/// - 相对时长：半小时后 / 一个半小时后 / 一刻钟后（v1 只认纯数字）
/// 解析出的时间若已过当前时刻 → TryParse 返回 false（不设提醒）；
/// 裸时钟（纯"8点"式，无时段无日期）已过 → Parse 的 IsBareClock=true 暴露给 UI 弹确认（用户决策）。
/// </summary>
public static class TimeParser
{
    // ── 规范化：全角冒号 → 半角；合成时段词 → "日期词+时段" ──
    private static readonly (string From, string To)[] NormalizeRules =
    {
        ("：", ":"),
        ("／", "/"),
        ("－", "-"),
        ("今晚", "今天晚上"),
        ("明早", "明天早上"),
        ("明晚", "明天晚上"),
        ("今早", "今天早上"),
    };

    // ── 时刻提取：冒号式 | 点/时式（小时支持汉字数字；分钟支持 半/一刻/数字/汉字数字，"分"字可选）──
    private static readonly Regex TimeOfDayRe = new(
        @"(\d{1,2}):(\d{2})|([0-9零一二三四五六七八九十两]+)[点时](半|一刻|[0-9零一二三四五六七八九十两]+)?分?",
        RegexOptions.Compiled);

    // ── 日期/时段上下文（只在时刻之前查找）──
    private static readonly Regex AbsDateRe = new(@"\d{4}-\d{1,2}-\d{1,2}", RegexOptions.Compiled);
    // 数字斜杠/横杠日期：2026/10/8、10/8、10-8（月在前）。前后禁数字粘连（防"138-1234"误伤）；
    // 全角 ／－ 已在 Normalize 归一为半角。
    private static readonly Regex SlashDateRe = new(@"(?<!\d)(?:(\d{4})[/-])?(\d{1,2})[/-](\d{1,2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex MonthDayRe = new(@"([0-9零一二三四五六七八九十两]+)月([0-9零一二三四五六七八九十两]+)[日号]", RegexOptions.Compiled);
    private static readonly Regex NextMonthRe = new(@"下个月([0-9零一二三四五六七八九十两]+)[日号]", RegexOptions.Compiled);
    private static readonly Regex WeekRe = new(@"(下?)(?:周|星期)([一二三四五六日天])", RegexOptions.Compiled);
    private static readonly Regex RelDayRe = new(@"(大后天|后天|明天|今天)", RegexOptions.Compiled);
    private static readonly Regex DayNumRe = new(@"((?:本月)?([0-9零一二三四五六七八九十两]+))[日号]", RegexOptions.Compiled);
    private static readonly Regex PeriodRe = new(@"(上午|下午|早上|晚上|中午|凌晨)", RegexOptions.Compiled);

    // ── 相对时长：半小时(后) / 半个小时(后) / 一个半小时(后) / 一刻钟(后) / N分钟(后) / N(个)小时(后) / N天(后) ──
    // 注意 alternation 顺序：一个半 必须在 半 之前（"一个半小时"含"半"子串）；捕获组只有 3 个，固定词靠 dm.Value 判断。
    private static readonly Regex DurationRe = new(
        @"一个半\s*个?\s*小时(?:后)?|半个?\s*小时(?:后)?|一刻钟(?:后)?|([0-9零一二三四五六七八九十两]+)\s*个?\s*分钟(?:后)?|([0-9零一二三四五六七八九十两]+)\s*个?\s*小时(?:后)?|([0-9零一二三四五六七八九十两]+)\s*个?\s*天(?:后)?",
        RegexOptions.Compiled);

    /// <summary>解析结果：Matched=是否命中时间表达；Time=绝对时间（可能已过）；IsBareClock=裸时钟（无时段无日期，供已过确认弹窗用）；IsDateOnly=纯日期无时刻（供"问几点"弹窗用）</summary>
    public readonly record struct ParseResult(bool Matched, DateTime Time, bool IsBareClock)
    {
        public bool IsDateOnly { get; init; }
    }

    /// <summary>兼容 v1 语义：命中、非纯日期、且是未来时间 → true。已过/纯日期/未命中 → false。</summary>
    public static bool TryParse(string text, out DateTime time)
    {
        time = default;
        var r = Parse(text);
        if (r.Matched && !r.IsDateOnly && r.Time > DateTime.Now) { time = r.Time; return true; }
        return false;
    }

    /// <summary>完整解析（含已过时间）。规则命中但已过 → Matched=true 且 Time 为过去时刻。</summary>
    public static ParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        text = Normalize(text.Trim());

        // 相对时长（半小时后 → 现在+30分钟；总是未来，不参与已过判定）
        var dm = DurationRe.Match(text);
        if (dm.Success)
        {
            DateTime t;
            if (dm.Groups[1].Success) t = DateTime.Now.AddMinutes(ChineseToInt(dm.Groups[1].Value));       // N分钟
            else if (dm.Groups[2].Success) t = DateTime.Now.AddHours(ChineseToInt(dm.Groups[2].Value));    // N(个)小时
            else if (dm.Groups[3].Success) t = DateTime.Now.AddDays(ChineseToInt(dm.Groups[3].Value));     // N天
            else if (dm.Value.StartsWith("一个半")) t = DateTime.Now.AddMinutes(90);                        // 一个半小时
            else if (dm.Value.StartsWith("半")) t = DateTime.Now.AddMinutes(30);                            // 半小时/半个小时
            else t = DateTime.Now.AddMinutes(15);                                                           // 一刻钟
            return new ParseResult(true, t, false);
        }

        // 时刻提取
        var tm = TimeOfDayRe.Match(text);
        if (!tm.Success)
        {
            // 纯日期（无时刻）：如"30号""本月30号""8月27号""下周三"。识别出日期，时刻由 UI 弹窗问。
            var (date, hasDateWord, _) = ResolveDate(text, 0, 0, isDateOnly: true);
            if (hasDateWord)
                return new ParseResult(true, date, false) { IsDateOnly = true };
            return default;
        }

        int hour, minute;
        if (tm.Groups[1].Success)   // 冒号式
        {
            hour = int.Parse(tm.Groups[1].Value);
            minute = int.Parse(tm.Groups[2].Value);
        }
        else                        // 点/时式
        {
            hour = ChineseToInt(tm.Groups[3].Value);
            minute = tm.Groups[4].Success ? ParseMinute(tm.Groups[4].Value) : 0;
        }
        if (hour > 23 || minute > 59) return default;

        // 时刻之前的文本（上下文只看时刻前面）
        var prefix = text[..tm.Index];

        // 时段（取最靠近时刻的）：有 → 12 小时制偏移
        var periodMatches = PeriodRe.Matches(prefix);
        var period = periodMatches.Count > 0 ? periodMatches[^1].Value : null;
        if (period != null) hour = ApplyPeriod(hour, period);

        // 日期表达（取最靠近时刻的）
        var (date2, hasDateWord2, _) = ResolveDate(prefix, hour, minute, isDateOnly: false);

        var time = date2.AddHours(hour).AddMinutes(minute);
        var bare = period == null && !hasDateWord2;
        return new ParseResult(true, time, bare);
    }

    // ── 时间表达剥离（2026-09-27 phase2，用户拍板：日期时间只进 DueTime，不留在待办正文里）──
    // 「10月1日上午9点12分要坐高铁回家」→「要坐高铁回家」。
    // 不能复用 Parse 的匹配索引：Parse 的 Normalize 会把「今晚」扩成「今天晚上」（长度变了，索引漂移），
    // 所以这里只做一遍 **等长归一**（全角：／－ → 半角，一字换一字），合成词交给 CompositeWordRe 单独认。

    private static readonly Regex CompositeWordRe = new(@"(今晚|今早|明早|明晚)", RegexOptions.Compiled);

    /// <summary>
    /// 把正文里被时间识别命中的那段表达剥掉，返回清理后的正文（收掉剥除处紧邻的顿号 / 逗号 / 空白）。
    /// 没命中时间表达 / 剥完为空 → <b>原样返回</b>（调用方据此保留原文，绝不产生空待办）。
    /// 只在确实设上提醒时调用（SaveNote 侧把关）；句号 / 问号等用户自己的标点不在收尾集合里，不动。
    /// </summary>
    public static string StripTimeExpression(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var t = text.Replace("：", ":").Replace("／", "/").Replace("－", "-");

        var spans = new List<(int Start, int Length)>();
        var dm = DurationRe.Match(t);
        if (dm.Success)
        {
            spans.Add((dm.Index, dm.Length));
        }
        else
        {
            var tm = TimeOfDayRe.Match(t);
            if (tm.Success)
            {
                int hour, minute;
                if (tm.Groups[1].Success) { hour = int.Parse(tm.Groups[1].Value); minute = int.Parse(tm.Groups[2].Value); }
                else { hour = ChineseToInt(tm.Groups[3].Value); minute = tm.Groups[4].Success ? ParseMinute(tm.Groups[4].Value) : 0; }
                if (hour <= 23 && minute <= 59)   // 与 Parse 同口径：非法时刻视同未识别，不剥
                {
                    spans.Add((tm.Index, tm.Length));
                    var pre = t[..tm.Index];
                    var periods = PeriodRe.Matches(pre);
                    if (periods.Count > 0) { var p = periods[^1]; spans.Add((p.Index, p.Length)); }
                    var comp = CompositeWordRe.Matches(pre);
                    if (comp.Count > 0) { var c = comp[^1]; spans.Add((c.Index, c.Length)); }
                    var (_, hasDate, dateMatch) = ResolveDate(pre, hour, minute, isDateOnly: false);
                    if (hasDate && dateMatch != null) spans.Add((dateMatch.Index, dateMatch.Length));
                }
            }
            else
            {
                var (_, hasDate, dateMatch) = ResolveDate(t, 0, 0, isDateOnly: true);
                if (hasDate && dateMatch != null) spans.Add((dateMatch.Index, dateMatch.Length));
            }
        }

        if (spans.Count == 0) return text;

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        foreach (var s in spans)
        {
            if (s.Start > pos) sb.Append(t, pos, s.Start - pos);
            pos = Math.Max(pos, s.Start + s.Length);
        }
        sb.Append(t[pos..]);

        var result = Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ").Trim();
        // 收掉剥除后暴露在首尾的顿号 / 逗号 / 空白（句号问号是用户自己的，不动）
        result = result.Trim('，', '、', '；', '：', '．', ',', ';', ':', ' ', '　');
        return result.Length > 0 ? result : text;
    }

    /// <summary>
    /// 在时刻前缀中解析基准日期。规则优先级：绝对日期 > 月日 > 下个月 > 星期 > 相对日 > 本月/裸号。
    /// 顺延逻辑：月日今年已过→明年；裸号/本月号本月已过→下月；星期今天已是且时刻已过→下周（纯日期无时刻时不顺延）；下周三强制下周。
    /// 第三个返回值是<b>命中的那个 Match</b>（2026-09-27 phase2）—— <see cref="StripTimeExpression"/> 靠它定位
    /// 要从正文里剥掉的那段日期文字；传入的 prefix 是什么文本，Index 就相对什么文本。
    /// </summary>
    private static (DateTime Date, bool HasDateWord, Match? DateMatch) ResolveDate(string prefix, int hour, int minute, bool isDateOnly)
    {
        var now = DateTime.Now;

        // 1. 绝对日期 yyyy-MM-dd
        var abs = LastMatch(AbsDateRe, prefix);
        if (abs != null && DateTime.TryParse(abs.Value, out var absDate))
            return (absDate.Date, true, abs);

        // 1.5 数字斜杠/横杠 2026/10/8、10/8、10-8（月在前）。月 1-12、日 1-31 合法才认；
        // 日期已过（含带年份的过去日期）→ 视为未识别，继续走后面规则（用户决策：数字式不顺延明年）
        var sd = LastMatch(SlashDateRe, prefix);
        if (sd != null)
        {
            var month = int.Parse(sd.Groups[2].Value);
            var day = int.Parse(sd.Groups[3].Value);
            if (month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                var year = sd.Groups[1].Success ? int.Parse(sd.Groups[1].Value) : now.Year;
                var t = BuildMonthDay(year, month, day, hour, minute);
                if (t.Date >= now.Date) return (t.Date, true, sd);
            }
        }

        // 2. 月日 X月X[日号]（今年已过 → 明年）
        var mm = LastMatch(MonthDayRe, prefix);
        if (mm != null)
        {
            var t = BuildMonthDay(now.Year, ChineseToInt(mm.Groups[1].Value), ChineseToInt(mm.Groups[2].Value), hour, minute);
            if (t < now) t = t.AddYears(1);
            return (t.Date, true, mm);
        }

        // 3. 下个月 N[日号]
        var nm = LastMatch(NextMonthRe, prefix);
        if (nm != null)
        {
            var next = now.AddMonths(1);
            var t = BuildMonthDay(next.Year, next.Month, ChineseToInt(nm.Groups[1].Value), hour, minute);
            return (t.Date, true, nm);
        }

        // 4. 星期（下?周X / 下?星期X）
        var wk = LastMatch(WeekRe, prefix);
        if (wk != null)
        {
            var target = ChineseWeekdayToInt(wk.Groups[2].Value);
            var diff = (target - (int)now.DayOfWeek + 7) % 7;
            if (!string.IsNullOrEmpty(wk.Groups[1].Value)) diff += 7;          // "下周三" 强制下周
            else if (!isDateOnly && diff == 0 && new DateTime(now.Year, now.Month, now.Day, hour, minute, 0) <= now)
                diff += 7;                                                      // 今天已是该星期且时刻已过 → 下周
            return (DateTime.Today.AddDays(diff), true, wk);
        }

        // 5. 相对日（大后天/后天/明天/今天）
        var rd = LastMatch(RelDayRe, prefix);
        if (rd != null)
            return (DateTime.Today.AddDays(DayOffset(rd.Value)), true, rd);

        // 6. 本月/裸号 N[日号]（本月已过 → 下月）。排除属于"X月N号"月日表达的一部分（前缀末尾是"数字+月"）
        var dn = LastMatch(DayNumRe, prefix);
        if (dn != null && !Regex.IsMatch(prefix[..dn.Index], @"[0-9零一二三四五六七八九十两]+月$"))
        {
            var t = BuildMonthDay(now.Year, now.Month, ChineseToInt(dn.Groups[2].Value), hour, minute);
            if (t < now) t = t.AddMonths(1);
            return (t.Date, true, dn);
        }

        // 无日期表达 → 今天
        return (DateTime.Today, false, null);
    }

    private static Match? LastMatch(Regex re, string text)
    {
        var ms = re.Matches(text);
        return ms.Count > 0 ? ms[^1] : null;
    }

    private static string Normalize(string text)
    {
        foreach (var (from, to) in NormalizeRules)
            text = text.Replace(from, to);
        return text;
    }

    /// <summary>按年月日时分构造；日钳制到当月最大天数（如 2月30号 → 2月28/29号）</summary>
    private static DateTime BuildMonthDay(int year, int month, int day, int hour, int minute)
    {
        var maxDay = DateTime.DaysInMonth(year, month);
        return new DateTime(year, month, Math.Min(day, maxDay), hour, minute, 0);
    }

    private static int DayOffset(string rel) => rel switch
    {
        "今天" => 0,
        "明天" => 1,
        "后天" => 2,
        "大后天" => 3,
        _ => 0
    };

    private static int ChineseWeekdayToInt(string s) => s switch
    {
        "一" => 1, "二" => 2, "三" => 3, "四" => 4, "五" => 5, "六" => 6,
        "日" or "天" => 0,
        _ => -1
    };

    /// <summary>12 小时制时段 → 24 小时制。上午/早上/凌晨：12→0；下午/晚上/中午：12 保持 12，其余 +12。</summary>
    private static int ApplyPeriod(int hour, string period) => period switch
    {
        "下午" or "晚上" or "中午" => hour == 12 ? 12 : hour + 12,
        "上午" or "早上" or "凌晨" => hour == 12 ? 0 : hour,
        _ => hour
    };

    /// <summary>分钟表达：半=30，一刻=15，其余走汉字数字解析（零三→3、03→3、三十→30）</summary>
    private static int ParseMinute(string s) => s switch
    {
        "半" => 30,
        "一刻" => 15,
        _ => ChineseToInt(s)
    };

    /// <summary>中文数字 → int。支持 零~九、两、以及 十/二十/十三 等组合（十=10，十三=13，二十三=23）。</summary>
    private static int ChineseToInt(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int total = 0, num = 0;
        foreach (var c in s)
        {
            switch (c)
            {
                case '零': num = 0; break;
                case '一': case '幺': num = 1; break;
                case '二': case '两': num = 2; break;
                case '三': num = 3; break;
                case '四': num = 4; break;
                case '五': num = 5; break;
                case '六': num = 6; break;
                case '七': num = 7; break;
                case '八': num = 8; break;
                case '九': num = 9; break;
                case '十':
                    total += (num == 0 ? 1 : num) * 10;
                    num = 0;
                    break;
                default:
                    if (c is >= '0' and <= '9') num = num * 10 + (c - '0');
                    break;
            }
        }
        return total + num;
    }

    /// <summary>DateTime → 自然语言（"今天 09:00" / "明天 上午9点" / "8月27日 20:00"），供建议条/弹窗文案用</summary>
    public static string FormatNaturalTime(DateTime time)
    {
        var now = DateTime.Now;
        var today = now.Date;
        if (time.Date == today)
            return $"今天 {time:HH:mm}";
        if (time.Date == today.AddDays(1))
            return $"明天 {FormatHourCn(time)}";
        return $"{time.Month}月{time.Day}日 {time:HH:mm}";
    }

    private static string FormatHourCn(DateTime t)
    {
        var h = t.Hour;
        var m = t.Minute;
        string period;
        if (h == 12) period = "中午";
        else if (h >= 5 && h < 12) period = "上午";
        else if (h >= 13 && h < 18) period = "下午";
        else if (h >= 18 && h < 24) period = "晚上";
        else period = "凌晨";
        var hh = h > 12 ? h - 12 : h;
        return m == 0 ? $"{period}{hh}点" : $"{period}{hh}点{m}分";
    }
}
