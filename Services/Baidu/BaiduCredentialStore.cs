using System.Text.Json.Serialization;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services.Baidu;

/// <summary>百度网盘「应用身份」：开发者在开放平台创建应用后拿到的 AppKey / SecretKey。</summary>
public sealed class BaiduCredentials
{
    /// <summary>AppKey（即 OAuth 的 client_id）。</summary>
    public string AppKey { get; set; } = "";

    /// <summary>SecretKey（即 client_secret）。桌面端无 PKCE，只能打包进程序，因此本机加密存放。</summary>
    public string SecretKey { get; set; } = "";

    public bool IsComplete => AppKey.Trim().Length > 0 && SecretKey.Trim().Length > 0;
}

/// <summary>百度网盘授权令牌（本机专属，绝不同步到其他设备）。</summary>
public sealed class BaiduToken
{
    public string AccessToken { get; set; } = "";

    /// <summary>refresh_token 是**一次性**的：每次刷新后旧的立即作废，必须立刻覆盖保存。</summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>Access Token 到期时刻（本地时间）。官方时效 30 天。</summary>
    public DateTime ExpiresAt { get; set; }

    public bool IsValid => AccessToken.Length > 0 && DateTime.Now < ExpiresAt;
}

/// <summary>
/// 凭据落盘（2026-09-16）。
///
/// 存放位置刻意**独立于 settings.json**，两条理由：
/// 1. <b>绝不能进云同步</b> —— 授权令牌是本机专属的，跟着 settings.json 跑到另一台设备上只会制造混乱；
///    放独立目录后，同步引擎的白名单扫描天然看不到它。
/// 2. <b>滚动更新不污染主配置</b> —— refresh_token 一次性，每次刷新都要立刻落盘，写一个小文件比反复重写
///    整个 settings.json 更稳（settings.json 同时被设置面板读写）。
///
/// 加密一律走 DPAPI（CurrentUser）：换机器解不开是**期望行为**，提示重新授权即可，不崩。
/// 日志里只允许出现 AppKey 前 4 位，SecretKey / token 任何时候都不打印。
/// </summary>
public static class BaiduCredentialStore
{
    private static string Dir => FocusCapturePaths.Combine("baidu");
    private static string CredentialPath => Path.Combine(Dir, "app_credential.dat");
    private static string TokenPath => Path.Combine(Dir, "token.dat");

    // ── 应用凭据（AppKey / SecretKey） ──

    public static bool HasCredentials
    {
        get { try { return LoadCredentials()?.IsComplete == true; } catch { return false; } }
    }

    public static BaiduCredentials? LoadCredentials()
    {
        try
        {
            var file = CredentialPath;
            if (!File.Exists(file)) return null;
            var plain = Dpapi.Unprotect(File.ReadAllText(file));
            if (string.IsNullOrEmpty(plain)) return null;   // 换机器 / 密文损坏
            return JsonSerializer.Deserialize(plain, BaiduJsonContext.Default.BaiduCredentials);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>保存应用凭据。返回 false = 加密或落盘失败（调用方提示用户，不静默）。</summary>
    public static bool SaveCredentials(string appKey, string secretKey)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var payload = JsonSerializer.Serialize(
                new BaiduCredentials { AppKey = appKey.Trim(), SecretKey = secretKey.Trim() },
                BaiduJsonContext.Default.BaiduCredentials);
            File.WriteAllText(CredentialPath, Dpapi.Protect(payload), new UTF8Encoding(false));
            AppLog.Info("Baidu", $"应用凭据已保存（AppKey {Mask(appKey)}）");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Baidu", "应用凭据保存失败：" + ex.Message);
            return false;
        }
    }

    public static void ClearCredentials()
    {
        TryDelete(CredentialPath);
        ClearToken();   // 换应用身份后旧令牌必然无效
    }

    // ── 授权令牌 ──

    public static BaiduToken? LoadToken()
    {
        try
        {
            var file = TokenPath;
            if (!File.Exists(file)) return null;
            var plain = Dpapi.Unprotect(File.ReadAllText(file));
            if (string.IsNullOrEmpty(plain)) return null;
            return JsonSerializer.Deserialize(plain, BaiduJsonContext.Default.BaiduToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存令牌。**刷新成功后必须立刻调用**：refresh_token 一次性，旧的当场作废，
    /// 晚一步落盘就可能两边都没有可用凭据，只能让用户重新走一遍设备码授权。
    /// </summary>
    public static bool SaveToken(BaiduToken token)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var payload = JsonSerializer.Serialize(token, BaiduJsonContext.Default.BaiduToken);
            File.WriteAllText(TokenPath, Dpapi.Protect(payload), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Baidu", "授权令牌保存失败：" + ex.Message);
            return false;
        }
    }

    public static void ClearToken() => TryDelete(TokenPath);

    /// <summary>是否已授权且令牌未过期。</summary>
    public static bool HasValidToken => LoadToken()?.IsValid == true;

    // ── 辅助 ──

    /// <summary>凭据脱敏：只保留前 4 位，供日志/界面展示。</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(空)";
        return value.Length <= 4 ? "****" : value[..4] + "****";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>重命名请求项（官方 filemanager&amp;opera=rename 的 filelist 元素，字段名必须是小写）。</summary>
public sealed class BaiduRenameItem
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("newname")] public string NewName { get; set; } = "";
}

/// <summary>
/// 百度模块专用序列化上下文（与主 AppJsonContext 隔离，避免互相牵连）。
/// 走编译期源生成而非反射：本项目 Release 关了裁剪，但反射式序列化在开启裁剪时会静默失效，
/// 这类「编译期就能定下来」的东西没必要留隐患。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BaiduCredentials))]
[JsonSerializable(typeof(BaiduToken))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<BaiduRenameItem>))]
internal partial class BaiduJsonContext : JsonSerializerContext
{
}
