using System.Text.Json;

namespace FocusCapture.Services.AI;

/// <summary>
/// 供应商状态探测的结果分类（2026-09-23）。
///
/// <para><b>为什么必须分类</b>：断网时**所有**供应商会一起失败。不分类的话用户看到一片红点，
/// 第一反应是「Key 全坏了」，然后去删配置 —— 把好好的东西删掉。这是本功能最大的坑。</para>
/// </summary>
public static class AiHealthStatus
{
    /// <summary>上次探测通过。</summary>
    public const string Ok = "Ok";
    /// <summary>连不上：本机网络问题，或地址填错。与 Key 无关。</summary>
    public const string Network = "Network";
    /// <summary>Key 未通过验证（401 / 403）。</summary>
    public const string Key = "Key";
    /// <summary>余额不足 / 套餐到期：**重试无用**，得去供应商后台充值续订。</summary>
    public const string Account = "Account";
    /// <summary>供应商侧问题（限流、5xx）：稍后重试有意义。</summary>
    public const string Server = "Server";
}

/// <summary>一次探测的结论。<paramref name="Message"/> 是给人看的中文说明。</summary>
public sealed record AiHealthResult(string Status, string Message);

/// <summary>
/// 把 HTTP 状态码 + 响应体翻译成「用户看得懂且不会误导」的分类（2026-09-23）。
///
/// <para><b>为什么单独做成纯函数</b>：分类规则里有调研挖出来的实测陷阱 ——
/// **智谱把「欠费」放在 HTTP 429 里**（业务码 1113），而 DeepSeek 用 402。
/// 若按「429/5xx = 供应商暂时不可用，稍后重试」一刀切，用户会对着欠费反复重试，
/// 而我们还在一遍遍告诉他「稍后重试」—— 那是明确的误导。
/// 这段逻辑不碰网络，所以能进秒级检查点，被机器钉死。</para>
/// </summary>
public static class AiHealthClassifier
{
    /// <summary>
    /// 智谱 429 里表示「钱/套餐」问题的业务码：1113 欠费、1313 套餐到期。
    /// 注意**不能**把 429 里的码全当账户问题 —— 1302/1305/1308/1309/1311 是限流、访问量过大、
    /// 达上限、无权限、公平策略，那些「稍后重试」是有意义的。
    /// </summary>
    private static readonly int[] AccountCodesIn429 = { 1113, 1313 };

    /// <summary>按 HTTP 状态码 + 响应体分类。</summary>
    public static AiHealthResult Classify(int statusCode, string? body)
    {
        var detail = BodyHint(body);

        if (statusCode is >= 200 and < 300)
            return new AiHealthResult(AiHealthStatus.Ok, "");

        if (statusCode is 401 or 403)
            return new AiHealthResult(AiHealthStatus.Key, "API Key 未通过验证" + detail);

        if (statusCode == 402)
            return new AiHealthResult(AiHealthStatus.Account, "账户余额不足（供应商返回 402）" + detail);

        if (statusCode == 429)
        {
            var code = ReadBusinessCode(body);
            if (code != null && Array.IndexOf(AccountCodesIn429, code.Value) >= 0)
                return new AiHealthResult(AiHealthStatus.Account,
                    $"账户余额不足或套餐到期（业务码 {code.Value}）" + detail);
            return new AiHealthResult(AiHealthStatus.Server,
                "请求被限流或额度受限，稍后重试" + (code != null ? $"（业务码 {code.Value}）" : "") + detail);
        }

        if (statusCode >= 500)
            return new AiHealthResult(AiHealthStatus.Server, $"供应商侧错误（HTTP {statusCode}）" + detail);

        return new AiHealthResult(AiHealthStatus.Server, $"未预期的响应（HTTP {statusCode}）" + detail);
    }

    /// <summary>
    /// 连不上时的结论。文案刻意<b>同时给两种可能</b>：
    /// 连不上既可能是本机网络问题，也可能是地址写错了（DNS 也连不上），
    /// 只说「网络问题」会把「地址填错」这种用户自己能修的情况掩盖掉。
    /// </summary>
    public static AiHealthResult Unreachable(string? reason)
        => new(AiHealthStatus.Network,
               "连不上该地址（可能是本机网络问题，也可能是地址填错了）"
               + (string.IsNullOrWhiteSpace(reason) ? "" : "：" + reason.Trim()));

    /// <summary>
    /// 读业务错误码：各家的位置不统一 —— 智谱 <c>error.code</c>、百炼原生顶层 <c>code</c>、
    /// 也有直接放在 <c>code</c> 下的。依次尝试，读不到返回 null（**不猜**）。
    /// </summary>
    private static int? ReadBusinessCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (TryReadCode(root, "code", out var top)) return top;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && TryReadCode(err, "code", out var inner)) return inner;
        }
        catch (JsonException)
        {
            // 错误体不保证是 JSON（DeepSeek 无鉴权时返回的是纯文本）→ 拿不到业务码，交给状态码判定
        }
        return null;
    }

    private static bool TryReadCode(JsonElement obj, string key, out int code)
    {
        code = 0;
        if (!obj.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt32(out code);
        if (v.ValueKind == JsonValueKind.String) return int.TryParse(v.GetString(), out code);
        return false;
    }

    /// <summary>
    /// 把错误体截一段附在提示里。**即使不是 JSON 也要照实展示原文** ——
    /// 吞掉它用户就只剩「失败」两个字，无从下手。
    /// </summary>
    private static string BodyHint(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Length > 160) text = text[..160] + "…";
        return "\n" + text;
    }
}
