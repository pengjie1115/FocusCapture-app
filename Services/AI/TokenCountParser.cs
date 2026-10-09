using System.Globalization;

namespace FocusCapture.Services.AI;

/// <summary>
/// 「上下文窗口 / 最大输出 Token」这类数值输入的解析（2026-09-23 多供应商改造）。
///
/// <para>支持 <c>384K</c> / <c>1M</c> / <c>1.5K</c> 这类带单位写法 —— 用户是照着供应商文档抄的，
/// 文档上写的就是 384K，逼他心算成 393216 是没事找事。</para>
///
/// <para><b>契约</b>：<c>true</c> + 值 = 可接受（含「留空」→ 0；0 表示不限制）；
/// <c>false</c> = 输入的压根不是数字，调用方应<b>保留原值、不要写入配置</b>
/// （不许静默写 0，那等于替用户做了他没做的决定）。</para>
///
/// <para>刻意零项目依赖（不碰配置、日志、WPF），所以能进快层秒级检查点 ——
/// 它错了的后果全是静默的：384K 被当非法值丢掉、K 与 M 差 1024 倍算错一个数量级，
/// 界面上都不会报错。</para>
/// </summary>
public static class TokenCountParser
{
    /// <summary>上下文窗口上限（用户拍板的校验区间上界）。</summary>
    public const int MaxContextWindow = 10_000_000;

    /// <summary>最大输出 Token 上限（用户拍板的校验区间上界）。</summary>
    public const int MaxOutputTokens = 1_000_000;

    /// <summary>
    /// 解析数值。
    /// <para><b>空 / 全空白 → true + 0</b>（留空即不限制，这是用户明确要的出口，不是非法输入）。</para>
    /// <para>非法输入（非数字、负数、只写单位、超出 int 范围）→ false。</para>
    /// </summary>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return true;                    // 留空 = 不限制

        long multiplier = 1;
        var last = char.ToUpperInvariant(s[^1]);
        if (last == 'K') { multiplier = 1024; s = s[..^1].Trim(); }
        else if (last == 'M') { multiplier = 1024 * 1024; s = s[..^1].Trim(); }
        if (s.Length == 0) return false;                   // 只写了单位，如 "K"

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return false;
        if (double.IsNaN(num) || double.IsInfinity(num) || num < 0) return false;

        var scaled = num * multiplier;
        // 溢出判非法而不是钳到 int.MaxValue：用户打错一位数时，
        // 静默存下一个天文数字比明确拒绝危险得多（那个数字会真被写进请求体）。
        if (scaled > int.MaxValue) return false;

        value = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return true;
    }
}
