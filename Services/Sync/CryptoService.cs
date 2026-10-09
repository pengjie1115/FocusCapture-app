using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FocusCapture.Services.Sync;

/// <summary>
/// E2EE 加密模块：主密码 PBKDF2 派生 DEK + AES-256-GCM 加解密。
/// 密钥永不上传；盐明文存云端 sync_meta.json（跨设备一致的关键，盐无需保密）；
/// 只加密 SyncNote.Content / Tags[]，Id/CreatedAt/UpdatedAt/Deleted/DeviceId 明文（引擎对账需要）。
/// 本地 MD 明文不动（本地明文兜底是密钥重置的前提）。
/// </summary>
public static class CryptoService
{
    public const int Pbkdf2Iterations = 100_000;  // AES-256-GCM 场景安全且 <1s
    public const int SaltSize = 16;               // 字节
    public const int KeySize = 32;                // AES-256
    public const int NonceSize = 12;              // GCM 推荐 96-bit
    public const int TagSize = 16;                // GCM tag 128-bit
    public const int RecoveryCodeLength = 14;

    // 剔除易混淆 0/O/1/I/l（2026-08-13 审查修正：10 位纯数字仅 10^10 空间，加盐哈希仍可被暴力枚举，提升为 14 位混合字符）
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private static readonly int RecoveryAlphabetLen = RecoveryAlphabet.Length;

    // ── 密钥派生 ──

    /// <summary>主密码 → DEK：PBKDF2-SHA256(主密码, 盐, 100_000 次) → 32 字节。首次配置等 ~1s 可接受。</summary>
    public static byte[] DeriveKey(string masterPassword, byte[] salt)
    {
        if (string.IsNullOrEmpty(masterPassword)) throw new ArgumentException("主密码不能为空", nameof(masterPassword));
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != SaltSize) throw new ArgumentException($"盐长度必须为 {SaltSize} 字节", nameof(salt));
        return Rfc2898DeriveBytes.Pbkdf2(masterPassword, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
    }

    /// <summary>主密码强度校验：≥8 位且含字母 + 数字。</summary>
    public static bool IsValidMasterPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return false;
        return password.Any(char.IsLetter) && password.Any(char.IsDigit);
    }

    /// <summary>生成随机盐（16 字节，RandomNumberGenerator）。</summary>
    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    // ── AES-256-GCM ──

    /// <summary>加密：Base64(nonce(12) + ciphertext + tag(16)) 单串；nonce 每次随机。</summary>
    public static string Encrypt(byte[] dek, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(dek);
        if (dek.Length != KeySize) throw new ArgumentException($"DEK 长度必须为 {KeySize} 字节", nameof(dek));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, result, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + cipher.Length, tag.Length);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// 解密：Base64(nonce + ciphertext + tag) → 原文。
    /// 密钥不对 / 数据损坏抛 CryptographicException（调用方捕获后提示"云端数据无法解密，可能主密码不正确"，不崩溃不改本地）。
    /// </summary>
    public static string Decrypt(byte[] dek, string encrypted)
    {
        ArgumentNullException.ThrowIfNull(dek);
        if (dek.Length != KeySize) throw new ArgumentException($"DEK 长度必须为 {KeySize} 字节", nameof(dek));
        var raw = Convert.FromBase64String(encrypted);
        if (raw.Length < NonceSize + TagSize) throw new CryptographicException("密文格式无效（长度不足）");
        var nonce = raw.AsSpan(0, NonceSize);
        var tag = raw.AsSpan(raw.Length - TagSize, TagSize);
        var cipher = raw.AsSpan(NonceSize, raw.Length - NonceSize - TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(dek, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    // ── 恢复码 ──

    /// <summary>生成 14 位恢复码（剔除易混淆字符，拒绝采样保证均匀）。</summary>
    public static string GenerateRecoveryCode()
    {
        var sb = new char[RecoveryCodeLength];
        var max = (int)(byte.MaxValue / RecoveryAlphabetLen) * RecoveryAlphabetLen; // 拒绝采样上界
        var i = 0;
        while (i < RecoveryCodeLength)
        {
            var b = RandomNumberGenerator.GetBytes(1)[0];
            if (b >= max) continue;
            sb[i++] = RecoveryAlphabet[b % RecoveryAlphabetLen];
        }
        return new string(sb);
    }

    /// <summary>恢复码加盐哈希：SHA-256(盐 + 恢复码)，盐 16 字节随机（与哈希同存 SyncSettings，防暴力枚举）。</summary>
    public static (string Hash, string Salt) HashRecoveryCode(string recoveryCode)
    {
        var salt = GenerateSalt();
        return (ComputeRecoveryHash(recoveryCode, salt), Convert.ToBase64String(salt));
    }

    /// <summary>校验恢复码：恒定时间比较，防时序侧信道。</summary>
    public static bool VerifyRecoveryCode(string recoveryCode, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode).Concat(salt).ToArray());
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeRecoveryHash(string recoveryCode, byte[] salt)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode).Concat(salt).ToArray()));
}
