using System.Security.Cryptography;
using System.Text;

namespace FocusCapture.Services;

/// <summary>
/// 激活码 payload 的解析结果。
/// <para>字段刻意留成「版本 / 类型 / 签发日期 / 序号」四段扩展位（用户拍板）：
/// 现在只签发 <see cref="LicenseCode.TypePermanent"/>（永久·通用码），
/// 将来若要出「单机码 / 多设备码 / 限时码」，在同一套体系里加类型值即可，不必换实现、不必逼老用户换码。</para>
/// <para>⚠ 措辞口径（2026-10-08 用户纠正）：面向用户的文案**不得**出现「不绑设备 / 可在任意电脑上使用」——
/// 那等于把「一码走天下」当卖点，是在鼓励白嫖。本注释只描述实现事实，不外传。</para>
/// </summary>
public readonly record struct LicensePayload(string Version, char Type, string IssuedDate, string Serial)
{
    /// <summary>是否永久码。</summary>
    public bool IsPermanent => Type == LicenseCode.TypePermanent;
}

/// <summary>
/// 激活码的编解码与验签（<b>纯函数，零项目依赖</b> —— 只用 BCL，不引 FocusCapturePaths / AppLog / WPF，
/// 所以能进快层秒级测）。
///
/// <para><b>码格式</b>（点分三段，全 ASCII）：</para>
/// <code>FC1.&lt;payload&gt;.&lt;signature&gt;</code>
/// <list type="bullet">
/// <item><c>FC1</c> —— 格式版本前缀（扩展位：未来 <c>FC2</c> 可加限制能力；同时是码泄露后的「换锁」手段）</item>
/// <item><c>&lt;payload&gt;</c> —— base64url（无填充）编码的紧凑串 <c>v1|P|20261008|SN000123</c></item>
/// <item><c>&lt;signature&gt;</c> —— base64url 编码的签名</item>
/// </list>
///
/// <para><b>算法</b>：ECDSA P-256 + SHA-256，签名格式固定为 <b>IEEE P1363</b>（r‖s 定长拼接，P-256 下 64 字节
/// → base64url 约 86 字符，用户可手输）。不选 RSA-2048：签名 256 字节、base64 后约 342 字符，太长了抄不动。</para>
///
/// <para><b>为什么这块必须有检查点</b>：验签写松 = <b>任何人随便输一串都能激活</b>，而且不报错、不留痕。
/// 这是整个方案里唯一真正守得住的地方（用户造不出有效签名），不能靠「看起来对」蒙混。</para>
///
/// <para><b>签名内容</b>：<c>"FC1." + payloadB64</c> 的 ASCII 字节。前缀一并纳入签名，避免前缀被换成别的值后仍能通过。</para>
/// </summary>
public static class LicenseCode
{
    /// <summary>格式版本前缀。</summary>
    public const string Prefix = "FC1";

    /// <summary>payload 版本号（与 <see cref="Prefix"/> 是两个维度：前者管外层格式，后者管内层字段）。</summary>
    /// <remarks>签发时写入的版本。<b>必须</b>同时登记在 <see cref="KnownPayloadVersions"/> 里，
    /// 否则自己签出来的码会被自己拒掉（快层 [23] 有一条断言专门盯这个）。</remarks>
    public const string PayloadVersion = "v1";

    /// <summary>
    /// 本构建认识的 payload 版本<b>清单</b>（向后兼容的扩展点）。
    ///
    /// <para><b>为什么是「清单」而不是「必须等于当前版本」</b>：后者会让<b>每次升版本都静默作废
    /// 全部已签发的码</b> —— 用户手里的码突然失效，而且客户端只会说「激活码无效」，
    /// 不会说「版本不支持」，排查起来毫无线索。改成清单后，将来加 <c>v2</c> 只需往这里加一项，
    /// <c>v1</c> 的老码继续有效；而「老客户端收到 v2 码会拒绝」也是对的 —— 它本来就读不懂新格式。</para>
    /// </summary>
    public static readonly string[] KnownPayloadVersions = { PayloadVersion };

    /// <summary>该 payload 版本是否被本构建接受。</summary>
    public static bool IsKnownPayloadVersion(string? version) =>
        version is not null && Array.IndexOf(KnownPayloadVersions, version) >= 0;

    /// <summary>类型：永久码（当前唯一签发的类型）。</summary>
    public const char TypePermanent = 'P';

    /// <summary>签名格式固定为 IEEE P1363（r‖s 定长拼接），签名与验签两侧必须一致。</summary>
    public static readonly DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>payload 四段的分隔符。</summary>
    public const char FieldSeparator = '|';

    // ── base64url（无填充）──

    /// <summary>字节 → base64url（+ → -、/ → _、去掉尾部 =）。</summary>
    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>base64url → 字节；非法输入返回 null（<b>不抛</b>）。</summary>
    public static byte[]? Base64UrlDecode(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
            case 1: return null;   // 长度 mod 4 == 1 不可能是合法 base64
        }
        try { return Convert.FromBase64String(t); }
        catch { return null; }
    }

    // ── payload 编解码 ──

    /// <summary>payload → 紧凑串 <c>v1|P|20261008|SN000123</c>。</summary>
    public static string EncodePayload(LicensePayload payload) =>
        $"{payload.Version}{FieldSeparator}{payload.Type}{FieldSeparator}{payload.IssuedDate}{FieldSeparator}{payload.Serial}";

    /// <summary>
    /// 紧凑串 → payload。
    /// 段数必须恰好 4；版本、签发日期、序号不得为空；类型必须恰好 1 个字符。
    /// </summary>
    public static bool TryDecodePayload(string? raw, out LicensePayload payload)
    {
        payload = default;
        if (string.IsNullOrEmpty(raw)) return false;

        var parts = raw.Split(FieldSeparator);
        if (parts.Length != 4) return false;
        if (parts[0].Length == 0) return false;   // 版本
        if (parts[1].Length != 1) return false;   // 类型：单字符
        if (parts[2].Length == 0) return false;   // 签发日期
        if (parts[3].Length == 0) return false;   // 序号

        payload = new LicensePayload(parts[0], parts[1][0], parts[2], parts[3]);
        return true;
    }

    /// <summary>当前构建认识的类型集合。未知类型一律拒绝（宁可不解锁，也不要「看不懂就放行」）。</summary>
    public static bool IsKnownType(char type) => type == TypePermanent;

    // ── 签名 / 验签 ──

    /// <summary>待签名/待验签内容：<c>"FC1." + payloadB64</c> 的 ASCII 字节。</summary>
    public static byte[] BuildSigningInput(string payloadB64) =>
        Encoding.ASCII.GetBytes(Prefix + "." + payloadB64);

    /// <summary>
    /// 用私钥签发一个完整激活码。
    /// <para><b>注意</b>：本方法只被 <c>tools/LicenseGen</c>（不随主程序打包）调用；
    /// 主程序侧只做验签，私钥绝不进仓库、不进日志、不进聊天。</para>
    /// </summary>
    public static string Issue(LicensePayload payload, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(EncodePayload(payload)));
        var sig = privateKey.SignData(BuildSigningInput(payloadB64), HashAlgorithmName.SHA256, SignatureFormat);
        return $"{Prefix}.{payloadB64}.{Base64UrlEncode(sig)}";
    }

    /// <summary>
    /// 验签。任何异常（公钥损坏 / 签名长度不对 / base64 非法）一律返回 false —— <b>永不抛</b>（fail-closed）。
    /// </summary>
    /// <param name="publicKeySpkiBase64">SubjectPublicKeyInfo 的 base64（程序内嵌的公钥常量）。</param>
    public static bool VerifySignature(string? payloadB64, string? signatureB64, string publicKeySpkiBase64)
    {
        if (string.IsNullOrEmpty(payloadB64) || string.IsNullOrEmpty(signatureB64)) return false;

        var sig = Base64UrlDecode(signatureB64);
        if (sig is null || sig.Length == 0) return false;

        byte[] spki;
        try { spki = Convert.FromBase64String(publicKeySpkiBase64); }
        catch { return false; }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.VerifyData(BuildSigningInput(payloadB64), sig, HashAlgorithmName.SHA256, SignatureFormat);
        }
        catch
        {
            return false;
        }
    }

    // ── 对外唯一读取入口 ──

    /// <summary>
    /// 拆码 → 验签 → 解 payload → 校验版本与类型。
    /// 任何一步不过都返回 false 并给出中文原因（供界面直接展示）。
    /// </summary>
    public static bool TryRead(string? code, string publicKeySpkiBase64,
                               out LicensePayload payload, out string error)
    {
        payload = default;

        if (!TrySplit(code, out var payloadB64, out var sigB64, out error)) return false;
        if (!VerifySignature(payloadB64, sigB64, publicKeySpkiBase64))
        {
            error = "激活码无效或已被篡改，请核对后重新输入。";
            return false;
        }

        byte[]? raw;
        try { raw = Base64UrlDecode(payloadB64); }
        catch { raw = null; }
        if (raw is null)
        {
            error = "激活码内容无法解码。";
            return false;
        }

        if (!TryDecodePayload(Encoding.UTF8.GetString(raw), out payload))
        {
            error = "激活码内容格式不正确。";
            return false;
        }
        if (!IsKnownPayloadVersion(payload.Version))
        {
            error = "激活码版本不受支持，请升级到最新版后再试。";
            return false;
        }
        if (!IsKnownType(payload.Type))
        {
            error = "激活码类型不受支持，请升级到最新版后再试。";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>只做「拆分」，不验签 —— 供错误提示分级与检查点单独驱动。</summary>
    public static bool TrySplit(string? code, out string payloadB64, out string signatureB64, out string error)
    {
        payloadB64 = "";
        signatureB64 = "";
        error = "";

        if (string.IsNullOrWhiteSpace(code)) { error = "请先粘贴或输入激活码。"; return false; }

        var parts = code.Trim().Split('.');
        if (parts.Length != 3) { error = "激活码格式不正确（应为三段、用 . 分隔）。"; return false; }
        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            error = $"激活码前缀不识别（应为 {Prefix}）。";
            return false;
        }
        if (parts[1].Length == 0 || parts[2].Length == 0) { error = "激活码内容不完整。"; return false; }

        payloadB64 = parts[1];
        signatureB64 = parts[2];
        return true;
    }
}
