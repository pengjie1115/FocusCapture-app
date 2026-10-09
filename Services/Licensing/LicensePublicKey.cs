using System.Security.Cryptography;

namespace FocusCapture.Services;

/// <summary>
/// 激活码验签用的<b>公钥</b>（内嵌在程序里，随包分发）。
///
/// <para><b>来源与保管</b>：由 <c>tools/LicenseGen keygen</c> 生成密钥对；私钥保存在
/// <c>%USERPROFILE%\.focuscapture-license\private-key.pem</c>（<b>仓库外</b>，见该目录的 README）。
/// 本文件只放公钥 —— <b>私钥绝不进仓库、不进日志、不进聊天</b>（AGENTS.md 红线 10）。</para>
///
/// <para><b>⚠ 这个常量一旦改动，所有已签发的激活码立即全部失效</b>
///（用户会看到「激活码无效或已被篡改」）。所以：私钥必须离线备份好；
/// <see cref="SpkiBase64"/> 与 <see cref="ExpectedFingerprint"/> 视为<b>冻结值</b>，
/// 只有「决定作废全部老码、重新发码」时才允许一起改。
/// 快层 <c>[23]</c> 组会盯住指纹 —— 手滑换钥匙会让检查点立刻变红，
/// 而不是等用户输码失败才发现。</para>
///
/// <para><b>为什么单独一个文件</b>：它零依赖（只用 BCL），所以能进快层被检查点覆盖；
/// 若塞进 <c>LicenseGate</c>（引 WPF / MessageBox），就进不了快层，指纹也就没人看着了。</para>
/// </summary>
public static class LicensePublicKey
{
    /// <summary>公钥（SubjectPublicKeyInfo 的 base64，124 字符）。</summary>
    public const string SpkiBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExJMMHEwaPESZvdbkoveqxoq2MiHsLDIStTbpsyAJZ2OeaQQdYjJJ6UTH5uwelI8EtHuoWmpWKVVXEgeD53VBIQ==";

    /// <summary>公钥指纹（SPKI 字节的 SHA-256，小写 hex）。与上面常量不符即代表「钥匙被换过」。</summary>
    public const string ExpectedFingerprint = "af2b3652d06d73acbcd922229c7379d687c6ee4cce86da1947ecdf8d619637fd";

    /// <summary>计算当前公钥常量的指纹（供检查点与 <see cref="ExpectedFingerprint"/> 比对）。</summary>
    public static string ComputeFingerprint() =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(SpkiBase64))).ToLowerInvariant();
}
