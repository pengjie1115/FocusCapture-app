namespace FocusCapture.Services;

/// <summary>
/// 试用期状态快照（纯数据，不含 I/O）。
/// 三个字段直接对应 license.json 的 firstRunDate / usedDays / lastSeenDate。
/// </summary>
public readonly record struct TrialSnapshot(DateOnly FirstRunDate, int UsedDays, DateOnly LastSeenDate);

/// <summary>
/// 试用计日规则（<b>纯函数，零项目依赖</b> —— 不引 FocusCapturePaths / AppLog / WPF，
/// 所以能进快层秒级测）。
///
/// <para><b>为什么这块必须有检查点</b>：天数算错的后果**全是静默的** ——
/// 少给天数 = 用户觉得被坑但说不清；多给天数 = 直接少赚钱。两种都不报错。</para>
///
/// <para><b>计日口径（用户拍板，实现不得擅改）</b>：按「累计实际使用天数」计数，而非固定到期日。
/// 同一天打开 N 次只算 1 天（自然日去重）；系统时钟往回拨**不加天数**。
/// 这样「装了但没打开的天」不消耗额度，符合「先让用户免费体验」的本意。</para>
///
/// <para><b>诚实边界</b>：计数存在本机，删掉状态文件或重装系统即可重置 ——
/// 这是纯本地计时的固有代价，本类不（也无法）防这个。此处只做「同一个文件下」的正确计日
/// 与「时钟回拨」的堵漏。</para>
/// </summary>
public static class TrialClock
{
    /// <summary>免费试用天数（用户拍板 14 天；参考：Typora 15 天 / DEVONthink 30 天）。</summary>
    public const int TrialDays = 14;

    /// <summary>首次运行初始化：当天算第 1 天。</summary>
    public static TrialSnapshot Init(DateOnly today) => new(today, 1, today);

    /// <summary>
    /// 按「今天」推进一天，返回新快照。
    ///
    /// <list type="bullet">
    /// <item><c>today &gt; lastSeenDate</c> → 新的一天：<c>usedDays + 1</c>，lastSeenDate 前移</item>
    /// <item><c>today == lastSeenDate</c> → <b>完全不动</b>（同一天多次打开只算 1 天）</item>
    /// <item><c>today &lt; lastSeenDate</c> → <b>不加天数</b>，原状态原样返回，<paramref name="clockRollback"/> = true</item>
    /// </list>
    ///
    /// <para>回拨分支刻意「原样返回」而不是「把 lastSeen 拉回今天」：拉回等于给作弊者
    /// 「倒拨一天 → 再正拨一天」的空转窗口，且会让天数的单调性失效。</para>
    /// </summary>
    /// <param name="clockRollback">true = 本次判定为系统时钟回拨（调用方据此写运行日志，本轮不推进）。</param>
    public static TrialSnapshot Advance(TrialSnapshot state, DateOnly today, out bool clockRollback)
    {
        clockRollback = false;

        // today < lastSeen：时钟回拨，或 FirstRunDate 被改到了未来 —— 两种都按「不推进」处理
        if (today < state.LastSeenDate || today < state.FirstRunDate)
        {
            clockRollback = true;
            return state;
        }

        // 同一天：一个字段都不动（不是「写回相同值」，是真不动）
        if (today == state.LastSeenDate) return state;

        return new TrialSnapshot(state.FirstRunDate, state.UsedDays + 1, today);
    }

    /// <summary>试用是否已用完（已解锁判定不在这里，见 LicenseGate）。</summary>
    /// <remarks>边界是「用满 14 天仍算试用中」，第 15 天才算过期 —— 所以判据是 <c>&gt;</c> 不是 <c>&gt;=</c>。</remarks>
    public static bool IsExpired(TrialSnapshot state) => state.UsedDays > TrialDays;

    /// <summary>剩余天数（已过期时钳到 0，不返回负数）。</summary>
    public static int RemainingDays(TrialSnapshot state)
    {
        var left = TrialDays - state.UsedDays;
        return left < 0 ? 0 : left;
    }

    /// <summary>
    /// 载入容错：把明显损坏的天数（&lt; 1）拉回 1。
    /// 由 I/O 层（LicenseState）在读取后调用，纯函数本身不做隐式修补 ——
    /// 保持「算多少就是多少」，否则「同一天不动」这类断言会因隐式改写而失真。
    /// </summary>
    public static TrialSnapshot Normalize(TrialSnapshot state) =>
        state.UsedDays < 1 ? state with { UsedDays = 1 } : state;
}
