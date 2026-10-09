using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FocusCapture.Services.Baidu;

/// <summary>百度开放平台业务错误。ErrNo 来自官方文档定义，不是猜测。</summary>
public sealed class BaiduApiException : Exception
{
    public int ErrNo { get; }

    public BaiduApiException(int errNo, string message) : base(message) => ErrNo = errNo;

    /// <summary>授权类错误（需要重新走设备码授权或先刷新令牌）。</summary>
    public bool IsAuthError => ErrNo is -6 or 111 or 112;

    /// <summary>频率/配额类错误（可延时重试）。</summary>
    public bool IsThrottled => ErrNo is 20012 or 31034;
}

/// <summary>尚未授权（本机没有可用令牌）。</summary>
public sealed class BaiduAuthRequiredException : Exception
{
    public BaiduAuthRequiredException(string message) : base(message) { }
}

/// <summary>沙箱内单个条目。</summary>
public sealed class BaiduFileEntry
{
    public long FsId { get; init; }
    /// <summary>沙箱内完整路径，形如 /apps/FocusCapture/files/xxx.md</summary>
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool IsDir { get; init; }
    public string Md5 { get; init; } = "";
    public DateTime ModifiedAt { get; init; }
}

/// <summary>设备码授权第一步的返回。</summary>
public sealed class BaiduDeviceCode
{
    public string DeviceCode { get; init; } = "";
    public string UserCode { get; init; } = "";
    public string VerificationUrl { get; init; } = "";
    public string QrCodeUrl { get; init; } = "";
    public int ExpiresInSeconds { get; init; } = 300;
    public int IntervalSeconds { get; init; } = 5;
}

/// <summary>
/// 百度网盘开放平台客户端（2026-09-16）。
///
/// 授权走**设备码模式**（官方推荐路径，见调研报告 2.2）：不需要回调服务器，适合桌面应用。
///
/// 三条硬约束（都来自官方文档，改动前先读调研报告）：
/// 1. <b>只能碰 /apps/{应用名} 沙箱</b> —— 2026-06-03 后创建的应用一律如此，不是配置项。所有路径都过
///    <see cref="EnsureSandboxed"/> 校验，越界直接拒绝（既防手滑，也防将来谁把 AI 给的路径直接传进来）。
/// 2. <b>User-Agent 必须是 pan.baidu.com</b> —— 否则下载直链返回 31326 防盗链错误。
/// 3. <b>refresh_token 一次性</b> —— 每次刷新后旧的立即作废，刷新成功必须马上落盘（见 BaiduCredentialStore）。
///
/// 错误码只做「翻译成人话」，不拿它推断业务状态（夸克那次的教训：错误码不是真相来源）。
/// </summary>
public class BaiduNetdiskClient
{
    private const string OAuthDeviceCodeUrl = "https://openapi.baidu.com/oauth/2.0/device/code";
    private const string OAuthTokenUrl = "https://openapi.baidu.com/oauth/2.0/token";
    private const string XpanFileUrl = "https://pan.baidu.com/rest/2.0/xpan/file";
    private const string XpanMultimediaUrl = "https://pan.baidu.com/rest/2.0/xpan/multimedia";

    /// <summary>取上传域名的接口。官方明文：**传文件数据之前必须先调这个**。</summary>
    private const string LocateUploadUrl = "https://d.pcs.baidu.com/rest/2.0/pcs/file";

    /// <summary>
    /// 分片上传的路径。注意**域名不是固定的** —— 必须先向 <see cref="LocateUploadUrl"/> 要，
    /// 见 <see cref="LocateUploadHostAsync"/>。早先这里写死了 pan.baidu.com，分片全部吃 403。
    /// </summary>
    private const string SuperFile2Path = "/rest/2.0/pcs/superfile2";

    /// <summary>locateupload 固定要求的应用 ID（官方请求示例值，不是我们的 AppKey）。</summary>
    private const string LocateAppId = "250528";

    /// <summary>
    /// 取上传域名失败时的兜底地址。**不是猜的**：官方文档示例、Rust SDK 默认值、
    /// baiduyun_api 的端点表三处独立来源都用它。
    /// </summary>
    private const string FallbackUploadHost = "https://d.pcs.baidu.com";

    /// <summary>第三方应用唯一能访问的顶层目录。</summary>
    public const string SandboxPrefix = "/apps";

    /// <summary>分片大小：官方要求普通用户 4MB，且首片必须 4MB、总片数 ≤1024。</summary>
    public const int SliceBytes = 4 * 1024 * 1024;

    /// <summary>单文件上限（普通用户 4G；会员/超会更高，超限由服务端 errno 拦下并给提示）。</summary>
    public const long MaxSingleFileBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>令牌刷新闸：refresh_token 一次性，并发刷新会让两边都作废，必须串行。</summary>
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private readonly BaiduCredentials _creds;

    /// <summary>沙箱内的工作目录，形如 /apps/FocusCapture/files（由设置面板配置，默认见 DefaultNetRoot）。</summary>
    public string NetRoot { get; }

    public const string DefaultNetRoot = "/apps/FocusCapture";

    public BaiduNetdiskClient(BaiduCredentials credentials, string netRoot = DefaultNetRoot)
    {
        _creds = credentials;
        NetRoot = NormalizeNetRoot(netRoot);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // 所有 pan.baidu.com 请求都要带这个 UA（下载 dlink 不带会吃 31326 防盗链）
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pan.baidu.com");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    // ══════════════════ 授权 ══════════════════

    /// <summary>第一步：取设备码与用户码（用户拿去扫码/输码）。</summary>
    public static async Task<BaiduDeviceCode> RequestDeviceCodeAsync(string appKey, CancellationToken ct)
    {
        var url = $"{OAuthDeviceCodeUrl}?response_type=device_code&client_id={Uri.EscapeDataString(appKey)}" +
                  "&scope=basic,netdisk";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var code = new BaiduDeviceCode
        {
            DeviceCode = Str(json, "device_code"),
            UserCode = Str(json, "user_code"),
            VerificationUrl = Str(json, "verification_url"),
            QrCodeUrl = Str(json, "qrcode_url"),
            ExpiresInSeconds = IntOr(json, "expires_in", 300),
            IntervalSeconds = Math.Max(5, IntOr(json, "interval", 5)),   // 官方要求轮询间隔 ≥5 秒
        };

        if (code.DeviceCode.Length == 0)
            throw new BaiduApiException(Str(json, "error") == "invalid_client" ? -6 : 2,
                $"获取授权码失败：{Str(json, "error_description")}".TrimEnd('：'));
        return code;
    }

    /// <summary>
    /// 第二步：按 interval 轮询换令牌，直到用户授权 / 超时 / 取消。
    /// 官方错误语义：authorization_pending = 用户还没操作（继续等）；其余 = 终止。
    /// </summary>
    public static async Task<BaiduToken> PollDeviceTokenAsync(
        string appKey, string secretKey, BaiduDeviceCode code, Action<string>? status, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddSeconds(code.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(code.IntervalSeconds);

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct).ConfigureAwait(false);

            var url = $"{OAuthTokenUrl}?grant_type=device_token&code={Uri.EscapeDataString(code.DeviceCode)}" +
                      $"&client_id={Uri.EscapeDataString(appKey)}&client_secret={Uri.EscapeDataString(secretKey)}";
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

            var access = Str(json, "access_token");
            if (access.Length > 0)
            {
                status?.Invoke("授权成功");
                return BuildToken(json);
            }

            var error = Str(json, "error");
            if (error is "authorization_pending" or "")
            {
                status?.Invoke($"等待授权…（剩 {Math.Max(0, (int)(deadline - DateTime.Now).TotalSeconds)} 秒）");
                continue;
            }

            throw error switch
            {
                "authorization_declined" => new BaiduApiException(111, "用户在授权页取消了授权。"),
                "expired_token" => new BaiduApiException(112, "授权码已过期，请重新发起授权。"),
                "invalid_client" => new BaiduApiException(-6, "AppKey 或 SecretKey 不正确，请核对设置里的应用凭据。"),
                _ => new BaiduApiException(2, $"授权失败：{Str(json, "error_description")}".TrimEnd('：')),
            };
        }

        throw new BaiduApiException(112, "授权超时（未在有效期内完成扫码/输码），请重新发起。");
    }

    /// <summary>
    /// 刷新令牌。**成功后立刻落盘**（旧 refresh_token 当场作废）。
    /// 串行化：并发刷新会导致两边都失效，只能让用户重新授权。
    /// </summary>
    public async Task<BaiduToken> RefreshTokenAsync(CancellationToken ct)
    {
        await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = BaiduCredentialStore.LoadToken();
            if (current == null || current.RefreshToken.Length == 0)
                throw new BaiduAuthRequiredException("尚无授权记录，请先在设置中完成百度网盘授权。");

            var url = $"{OAuthTokenUrl}?grant_type=refresh_token&refresh_token={Uri.EscapeDataString(current.RefreshToken)}" +
                      $"&client_id={Uri.EscapeDataString(_creds.AppKey)}&client_secret={Uri.EscapeDataString(_creds.SecretKey)}";
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (Str(json, "access_token").Length == 0)
            {
                // refresh_token 一次性：这里失败基本等于必须重新授权，给明确指引而不是静默卡死
                throw new BaiduApiException(-6,
                    $"刷新授权失败（{Str(json, "error_description")}）。refresh_token 只能使用一次，" +
                    "请到「设置 → 文件与网盘」重新授权。");
            }

            var token = BuildToken(json);
            if (token.RefreshToken.Length == 0)
                token.RefreshToken = current.RefreshToken;   // 极少数情况下服务端不回新 refresh_token，保留旧的
            BaiduCredentialStore.SaveToken(token);
            AppLog.Info("Baidu", "授权令牌已刷新并落盘");
            return token;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    /// <summary>取可用令牌；临近到期（<2 天）先主动刷新，避免用到一半失效。</summary>
    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
    {
        var token = BaiduCredentialStore.LoadToken()
                    ?? throw new BaiduAuthRequiredException("尚未授权百度网盘，请到「设置 → 文件与网盘」完成授权。");

        if (!token.IsValid)
        {
            if (token.RefreshToken.Length == 0)
                throw new BaiduAuthRequiredException("百度网盘授权已过期，请重新授权。");
            token = await RefreshTokenAsync(ct).ConfigureAwait(false);
        }
        else if (token.ExpiresAt - DateTime.Now < TimeSpan.FromDays(2))
        {
            try { token = await RefreshTokenAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Baidu", "提前刷新令牌失败，继续用当前令牌：" + ex.Message); }
        }
        return token.AccessToken;
    }

    private static BaiduToken BuildToken(JsonElement json) => new()
    {
        AccessToken = Str(json, "access_token"),
        RefreshToken = Str(json, "refresh_token"),
        // 官方时效 30 天；预留 1 小时缓冲，避免踩在边界上失效
        ExpiresAt = DateTime.Now.AddSeconds(Math.Max(3600, IntOr(json, "expires_in", 2592000)) - 3600),
    };

    // ══════════════════ 文件：列目录 / 建目录 / 查 / 删除 ══════════════════

    /// <summary>列出沙箱内某目录下的条目（只返回直属子项）。</summary>
    public async Task<List<BaiduFileEntry>> ListAsync(string netDir, CancellationToken ct)
    {
        // 列目录是只读，放行 /apps 本身（否则「列应用目录」这种正常请求会被自己的守卫拦下）
        EnsureSandboxed(netDir, allowSandboxRoot: true);
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        var url = $"{XpanFileUrl}?method=list&access_token={Uri.EscapeDataString(token)}" +
                  $"&dir={Uri.EscapeDataString(netDir)}&order=time&desc=1&limit=1000";
        var json = await GetJsonAsync(url, ct, treatMissingAsEmpty: true).ConfigureAwait(false);

        var errno = IntOr(json, "errno", 0);
        // 目录不存在：官方返回 -9；沙箱目录尚未创建时这是常态，按"空目录"处理
        if (errno is -9 or 31066) return new List<BaiduFileEntry>();
        if (errno != 0) throw new BaiduApiException(errno, DescribeErrNo(errno));

        var list = new List<BaiduFileEntry>();
        if (json.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var entry = ParseEntry(item);
                if (entry != null) list.Add(entry);
            }
        }
        return list;
    }

    /// <summary>
    /// 递归列出沙箱内**所有层级**的条目（2026-09-27 新增，「与云端核对」用它一次拉全清单）。
    ///
    /// <b>为什么必须用它、而不是逐目录调 <see cref="ListAsync"/></b>：对话附件按月分子目录，
    /// 逐目录要「列 files 1 次 + 列 attachments 1 次 + 逐个月份各 1 次」，攒几个月就是十几次调用；
    /// 本接口带 <c>recursion=1</c>，**一次调用**就能拿到整个 <c>/apps/{appname}</c> 下的全部条目
    /// （2026-09-27 实测：23 个条目 / 1 次调用 / 737ms）。在"未上线审核的应用只有 10 次/每小时"
    /// 这一配额现实下，这个差别就是「能用」与「一跑就撞限流」的差别。
    ///
    /// 三条实测契约（均与官方文档一致）：
    /// 1. 仅支持 <c>/apps/{appname}</c>，越界返回 42213 —— 与 <see cref="ListAsync"/> 共用同一把守卫；
    /// 2. 翻页必须用响应里的 <c>cursor</c> 作下一次 <c>start</c>，**不能自行累加固定步长**；
    /// 3. <c>list[].md5</c> 官方注明「非文件真实 MD5」（2026-09-27 实测 11 条样本与本地内容 MD5
    ///    相等 0 条）→ **调用方不得拿它做内容比对**，只能按 path 匹配。
    /// </summary>
    public async Task<List<BaiduFileEntry>> ListAllAsync(string netPath, CancellationToken ct)
    {
        // 列目录是只读，放行沙箱根（同 ListAsync 的理由：沙箱根本身就是合法目标）
        EnsureSandboxed(netPath, allowSandboxRoot: true);
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        var all = new List<BaiduFileEntry>();
        var start = 0;

        // 页数上限只是防呆：正常规模远到不了；真撞上说明数据异常，宁可少列也不能无限打平台
        for (var page = 0; page < 50; page++)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{XpanMultimediaUrl}?method=listall&access_token={Uri.EscapeDataString(token)}" +
                      $"&path={Uri.EscapeDataString(netPath)}&recursion=1&order=name&desc=0" +
                      $"&start={start}&limit=1000&web=0";
            var json = await GetJsonAsync(url, ct, treatMissingAsEmpty: true).ConfigureAwait(false);

            var errno = IntOr(json, "errno", 0);
            // 目录不存在：沙箱目录尚未创建时是常态，按"空目录"处理（同 ListAsync）
            if (errno is -9 or 31066) return all;
            if (errno != 0) throw new BaiduApiException(errno, DescribeErrNo(errno));

            if (json.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var entry = ParseEntry(item);
                    if (entry != null) all.Add(entry);
                }
            }

            if (IntOr(json, "has_more") != 1) break;

            // 官方明文：has_more=1 时必须用响应里的 cursor 作下一次 start，不要自行累加固定步长
            var cursor = IntOr(json, "cursor", -1);
            if (cursor <= start) break;   // 游标没前进 = 平台行为异常，停在这里，不死循环
            start = cursor;
        }

        AppLog.Info("Baidu", $"listall 递归列目录完成：{netPath} 共 {all.Count} 条");
        return all;
    }

    /// <summary>
    /// 把 <c>list</c> / <c>listall</c> 的一个条目解析成 <see cref="BaiduFileEntry"/>。
    /// 两个接口的条目字段同构，共用一份解析避免将来只改一处（无 path 的条目返回 null，调用方跳过）。
    /// </summary>
    private static BaiduFileEntry? ParseEntry(JsonElement item)
    {
        var path = Str(item, "path");
        if (path.Length == 0) return null;
        return new BaiduFileEntry
        {
            FsId = LongOr(item, "fs_id"),
            Path = path,
            Name = Str(item, "server_filename"),
            Size = LongOr(item, "size"),
            IsDir = IntOr(item, "isdir") == 1,
            Md5 = Str(item, "md5"),
            ModifiedAt = FromUnix(IntOr(item, "server_mtime")),
        };
    }

    /// <summary>
    /// 按名字在父目录里找条目（不缓存 fsid —— 官方文档未承诺 fsid 跨会话稳定，现场查最稳）。
    ///
    /// <b>用途限定</b>：给「已知完整路径、要拿它的 fsid / size / md5」的场景用。
    /// **不要拿它探测目录是否存在** —— 那会列一次父目录，而父目录很可能就是 `/apps`，
    /// 第三方应用能不能列 `/apps` 官方从未承诺。目录一律交给 <see cref="EnsureDirectoryAsync"/>。
    /// </summary>
    public async Task<BaiduFileEntry?> FindAsync(string netPath, CancellationToken ct)
    {
        var parent = ParentOf(netPath);
        var name = netPath[(netPath.LastIndexOf('/') + 1)..];
        var entries = await ListAsync(parent, ct).ConfigureAwait(false);
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }

    /// <summary>本进程内已确保过的目录（建目录幂等，重复建纯属浪费请求与频控额度）。</summary>
    private static readonly HashSet<string> EnsuredDirs = new(StringComparer.Ordinal);
    private static readonly object EnsuredDirsGate = new();

    /// <summary>
    /// 逐级确保目录存在（/apps 已由平台保证存在，从它的下一级开始建）。
    ///
    /// <b>实现要点（2026-09-16 改）</b>：直接 create，把 -8「已存在」当成功 ——
    /// 不再先 list 父目录探测。原因：探测「/apps/FocusCapture 在不在」必须列 /apps，
    /// 而第三方应用能不能列 /apps 官方从未承诺。一旦列不动，整条上传链路就死在这一步
    /// （当时现象：测试连接报错、每个文件 78 毫秒内失败 5 次、网盘永远空）。
    /// 少一次请求、少一个外部依赖，行为还更确定。
    /// </summary>
    public async Task EnsureDirectoryAsync(string netDir, CancellationToken ct)
    {
        EnsureSandboxed(netDir);
        var parts = netDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (current == SandboxPrefix) continue;   // /apps 平台自带，不能也不需要创建

            lock (EnsuredDirsGate)
            {
                if (EnsuredDirs.Contains(current)) continue;
            }

            await CreateDirectoryAsync(current, ct).ConfigureAwait(false);

            lock (EnsuredDirsGate) EnsuredDirs.Add(current);
        }
    }

    private async Task CreateDirectoryAsync(string netDir, CancellationToken ct)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        // rtype=0（不重命名、同名即冲突）**必须显式传**。
        // 2026-09-16 实测：不传时服务端的行为与官方文档不符 —— 用户的网盘里凭空多出
        // `FocusCapture_20260916_104601`、`FocusCapture_20260916_112442` 这类"名字_时间戳"目录，
        // 每重启一次应用就多一个（进程内已建目录缓存清空后第一次上传时触发）。
        // 显式 0 才能拿到"已存在"的信号，由下面的分支按正常情况处理。
        var form = new Dictionary<string, string>
        {
            ["path"] = netDir, ["isdir"] = "1", ["size"] = "0", ["rtype"] = "0",
        };
        var json = await PostFormAsync($"{XpanFileUrl}?method=create&access_token={Uri.EscapeDataString(token)}", form, ct)
            .ConfigureAwait(false);
        var errno = IntOr(json, "errno", 0);
        // -8 / 31061 = 已存在：目录已就绪就是要的结果，属正常情况（并发创建、重启后重复确保）
        if (errno != 0 && errno != -8 && errno != 31061)
            throw new BaiduApiException(errno, $"创建目录失败：{DescribeErrNo(errno)}");

        // 自检：服务端返回的 path 与请求不一致 = 它没报冲突，而是把目录改名后另建了一个。
        // 这不是致命错误（目标目录通常已存在，上传照样能继续），但必须留痕 ——
        // 否则用户网盘里会悄悄堆出一串空目录，而且没人知道是谁建的。
        var actual = Str(json, "path");
        if (actual.Length > 0 &&
            !string.Equals(actual.TrimEnd('/'), netDir.TrimEnd('/'), StringComparison.Ordinal))
        {
            AppLog.Warn("Baidu",
                $"建目录被服务端改名：请求 {netDir} → 实际返回 {actual}。目标目录应当已经存在，" +
                "上传继续；若频繁出现请检查 create 的 rtype 是否被平台忽略。");
        }
    }

    /// <summary>
    /// 删除（沙箱内，可批量）。**走旧式 `method=delete`，不用 `method=filemanager&amp;opera=delete`**。
    ///
    /// 为什么（2026-09-16 实测，见 `tools/baidu-diag` 的 probe-delete）：
    /// `filemanager&amp;opera=delete` 穷举了 12 种参数形态（async 取 0/1/2/不带、filelist 传
    /// path 字符串数组 / 对象数组 / fs_id、form body / json body / 全 query、再叠加参数）
    /// **一律返回 errno=2（请求参数有误）**；而 `method=delete&amp;path=&lt;完整路径&gt;` 一次即成功。
    /// 两次独立验证都以「重新列目录看文件是否还在」为准 —— 返回值不算数，终态才算数。
    ///
    /// 三条必须记住的实测契约：
    /// 1. <b>成功响应里没有 errno</b> —— 只返回 <c>{"request_id":…}</c>。所以**不能靠 errno 判成败**，
    ///    只能判「没有 error_code」。这是本方法不复用上传那套判定的原因。
    /// 2. <b>只接单个 path，不认 filelist</b> —— 传 filelist 得 HTTP 400 + error_code=31023。
    ///    因此批量只能逐个发；删除是轻操作，这点成本换行为确定，值。
    /// 3. <b>失败形态</b>：404 + 31066（文件本就不存在 → 按幂等成功处理）、403 + 31064（越权，
    ///    平台自己也会拦沙箱外的路径）。
    ///
    /// ⚠️ 目录也能删（实测删净了两个本应用建目录 bug 造出的空目录），但返回码是 403/404
    /// 与「确实消失了」互相矛盾 → **目录删除的返回语义未定性**，本方法不依赖它。
    /// </summary>
    public async Task DeleteAsync(IEnumerable<string> netPaths, CancellationToken ct)
    {
        var paths = netPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0) return;

        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSandboxed(p);

            var url = $"{XpanFileUrl}?method=delete&access_token={Uri.EscapeDataString(token)}" +
                      $"&path={Uri.EscapeDataString(p)}";
            var json = await PostEmptyAsync(url, ct).ConfigureAwait(false);

            var errorCode = IntOr(json, "error_code");
            // 31066 = 云端本来就没有这个文件 → 删除的期望状态已达成，不当失败（幂等）
            if (errorCode == 0 || errorCode == 31066) continue;
            throw new BaiduApiException(errorCode, $"删除失败：{DescribeErrNo(errorCode)}");
        }
    }

    /// <summary>重命名（沙箱内）。</summary>
    public async Task RenameAsync(string netPath, string newName, CancellationToken ct)
    {
        EnsureSandboxed(netPath);
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["opera"] = "rename",
            ["async"] = "0",
            ["filelist"] = JsonSerializer.Serialize(
                new List<BaiduRenameItem> { new() { Path = netPath, NewName = newName } },
                BaiduJsonContext.Default.ListBaiduRenameItem),
        };
        var json = await PostFormAsync($"{XpanFileUrl}?method=filemanager&access_token={Uri.EscapeDataString(token)}", form, ct)
            .ConfigureAwait(false);
        var errno = IntOr(json, "errno", 0);
        if (errno != 0) throw new BaiduApiException(errno, $"重命名失败：{DescribeErrNo(errno)}");
    }

    // ══════════════════ 上传（三阶段 + 分片） ══════════════════

    /// <summary>
    /// 上传一个本地文件到沙箱内。返回云端路径。
    /// 三阶段：precreate（登记 + 秒传判定）→ superfile2（逐片上传）→ create（提交）。
    /// </summary>
    public async Task<string> UploadFileAsync(string localPath, string netPath, IProgress<double>? progress, CancellationToken ct)
    {
        EnsureSandboxed(netPath);
        var info = new FileInfo(localPath);
        if (!info.Exists) throw new FileNotFoundException("本地文件不存在", localPath);
        if (info.Length > MaxSingleFileBytes)
            throw new BaiduApiException(-11,
                $"文件 {info.Name} 有 {RootMigrationService.FormatSize(info.Length)}，" +
                "超过普通用户 4GB 的单文件上限，请先压缩或升级网盘会员。");

        // 空文件没有分片：官方要求 block_list 至少一项，用空内容的 md5 占位
        var data = info.Length == 0 ? Array.Empty<byte>() : null;
        var blockList = new List<string>();
        if (info.Length == 0)
        {
            blockList.Add(Md5Hex(Array.Empty<byte>()));
        }
        else
        {
            using var fs = File.OpenRead(localPath);
            var buffer = new byte[SliceBytes];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (read == buffer.Length) blockList.Add(Md5Hex(buffer));
                else
                {
                    var tail = new byte[read];
                    Array.Copy(buffer, tail, read);
                    blockList.Add(Md5Hex(tail));
                }
            }
        }

        // ① precreate
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var preForm = new Dictionary<string, string>
        {
            ["path"] = netPath,
            ["size"] = info.Length.ToString(),
            ["isdir"] = "0",
            ["autoinit"] = "1",
            ["rtype"] = "3",
            ["block_list"] = JsonSerializer.Serialize(blockList, BaiduJsonContext.Default.ListString),
        };
        var pre = await PostFormAsync($"{XpanFileUrl}?method=precreate&access_token={Uri.EscapeDataString(token)}", preForm, ct)
            .ConfigureAwait(false);
        var preErrno = IntOr(pre, "errno", 0);
        if (preErrno != 0) throw new BaiduApiException(preErrno, $"上传登记失败：{DescribeErrNo(preErrno)}");

        var uploadId = Str(pre, "uploadid");
        var returnType = IntOr(pre, "return_type");

        // 服务端告诉我们「还有哪些分片等着上传」。注意：**不是**请求参数里那个"每片 md5 列表"，
        // 同名不同义，唯一权威解释见 ParseMissingSlices 的注释。
        var (hasBlockList, missingSlices) = ParseMissingSlices(pre);
        AppLog.Info("Baidu", hasBlockList
            ? $"precreate：return_type={returnType}，待上传分片 {missingSlices.Count} 个" +
              (missingSlices.Count > 0 ? $"（序号 {string.Join(",", missingSlices.OrderBy(x => x))}）" : "（= 无需上传）")
            : $"precreate：return_type={returnType}，响应里没有可解析的分片列表 → 按「全部上传」处理");

        // 秒传的唯一判据：服务端**显式**给了空的分片列表（= 没有任何片需要上传）。
        //
        // ⛔ 绝不再用 return_type 判断秒传 —— 那是 2026-09-16 第三次事故的根源：
        //    它的常规值被当成"秒传命中"，于是每个文件（含全新文件）都跳过全部分片上传，
        //    create 时服务端发现一片都没收到 → errno 31500（「uploadid 无效或上传未完成」）。
        //    更根本的原因是：本站从未传 content_md5 / slice_md5，服务端无从判断"内容已存在"，
        //    所以 return_type 从来就不是秒传信号。
        var rapid = hasBlockList && missingSlices.Count == 0;
        if (!rapid)
        {
            if (uploadId.Length == 0)
                throw new BaiduApiException(2, "上传登记未返回 uploadid，无法继续。");

            // 传数据之前先要域名（官方硬性要求），否则分片一律 403
            var uploadHost = await LocateUploadHostAsync(netPath, uploadId, ct).ConfigureAwait(false);

            using var fs = File.OpenRead(localPath);
            long sent = 0;
            for (var seq = 0; seq < blockList.Count; seq++)
            {
                ct.ThrowIfCancellationRequested();

                var size = (int)Math.Min(SliceBytes, info.Length - (long)seq * SliceBytes);
                var slice = new byte[size];
                if (size > 0) fs.ReadExactly(slice, 0, size);

                // 一律全量上传（2026-09-16 抉择）：服务端那份"待上传分片"列表**只用于记日志**。
                // 理由：只有本机保证"每一片都传过"，create 提交时带的 block_list 才能与云端完全对上；
                // 若改成"只传列表里的那几片"，等于又把成败押在一个外部字段的完整性上 —— 刚被咬过两轮。
                // 将来若要做「按列表精确补传」（省流量的断点续传），必须先实测确认该列表的完整性再开。
                await UploadSliceAsync(uploadHost, token, netPath, uploadId, seq, slice, ct).ConfigureAwait(false);

                sent += size;
                if (info.Length > 0) progress?.Report((double)sent / info.Length);
            }
        }
        else
        {
            progress?.Report(1.0);
            if (uploadId.Length == 0)
                AppLog.Warn("Baidu", "服务端称无需上传分片，却未返回 uploadid —— 提交可能被拒，留痕备查");
            AppLog.Info("Baidu", $"服务端确认无需上传分片（内容已在云端）：{Path.GetFileName(netPath)}");
        }

        // ③ create 提交
        var createForm = new Dictionary<string, string>
        {
            ["path"] = netPath,
            ["size"] = info.Length.ToString(),
            ["isdir"] = "0",
            ["uploadid"] = uploadId,
            // rtype 必须显式带上，且与上面 precreate 的取值保持一致 —— 官方原文：
            // 「3 为覆盖，**需要与预上传 precreate 接口中的 rtype 保持一致**」。
            // 漏传时服务端按默认 0（不重命名、返回冲突）处理，与 precreate 的 3 对不上（2026-09-16 补）。
            ["rtype"] = "3",
            ["block_list"] = JsonSerializer.Serialize(blockList, BaiduJsonContext.Default.ListString),
        };
        var create = await PostFormAsync($"{XpanFileUrl}?method=create&access_token={Uri.EscapeDataString(token)}", createForm, ct)
            .ConfigureAwait(false);
        var createErrno = IntOr(create, "errno", 0);
        if (createErrno != 0) throw new BaiduApiException(createErrno, $"上传提交失败：{DescribeErrNo(createErrno)}");

        progress?.Report(1.0);
        return netPath;
    }

    /// <summary>
    /// 解析 precreate 响应里的分片列表。**它是「还需要上传」的分片序号，不是「已经传完」的**。
    ///
    /// <b>同名字段、两处语义完全不同 —— 这是 2026-09-16 连续两轮事故的共同根源</b>：
    /// <list type="bullet">
    /// <item><b>请求参数</b> block_list = 本地算的每片 md5 列表（我们自己的数据，可控）；</item>
    /// <item><b>响应字段</b> block_list = <b>还需要上传</b>的缺失分片序号（形如 <c>[0,1,2,3]</c>）。</item>
    /// </list>
    /// 交叉验证：开源 SDK 对该字段的注释原文是
    /// "List of missing block indices that need to be uploaded"；实测也吻合 ——
    /// 15.4MB 的文件（4MB × 4 片）返回 <c>[0,1,2,3]</c>，1.6KB 的文件返回 <c>[0]</c>，
    /// 都是"该文件全部分片"，而不是"已存在分片"。
    ///
    /// <b><paramref name="hasField"/> = false 时，调用方必须按「全部分片都要传」处理</b>
    /// —— 字段缺失或读不懂 = 不知道缺哪些片 = 只能全传。
    ///
    /// <b>本函数的契约比它的返回值更重要</b>：任何读不懂的情况都不抛异常。
    /// 失败方向必须是"多传几片"，不能是"传不上去"（曾在这里抛过一次异常，上传 100% 失败）。
    /// </summary>
    public static (bool HasField, HashSet<int> Slices) ParseMissingSlices(JsonElement response)
    {
        var slices = new HashSet<int>();
        if (response.ValueKind != JsonValueKind.Object) return (false, slices);
        if (!response.TryGetProperty("block_list", out var list)) return (false, slices);
        if (list.ValueKind != JsonValueKind.Array) return (false, slices);

        foreach (var item in list.EnumerateArray())
        {
            // 只认数字与"能当数字读的字符串"，其余（null / 布尔 / 对象）忽略该元素、继续读下一个
            int? parsed = item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(item.GetString(), out var s) => s,
                _ => null,
            };
            if (parsed.HasValue && parsed.Value >= 0) slices.Add(parsed.Value);
        }
        return (true, slices);
    }

    /// <summary>
    /// 取上传域名。官方明文：「**上传文件数据时，需要先通过此接口获取上传域名**。
    /// 可使用返回结果 servers 字段中的 https 协议的任意一个域名。」
    ///
    /// 2026-09-16 第四次事故：分片一直用硬编码的 pan.baidu.com 传，服务端直接回 **403**；
    /// Alist 的相关记录也写明「传统静态域名上传方式已无法满足当前服务架构」。
    ///
    /// 每次上传取一次、同一次上传的所有分片共用 —— **刻意不做跨文件缓存**：
    /// 该域名带有效期，缓存过期反而会引出「大文件传到一半失败、小文件没事」这类难查的问题。
    /// 取不到就走兜底域名（三份独立来源都证明它可用）并留 WARN，不因"要域名"这一步失败而整单失败。
    /// </summary>
    private async Task<string> LocateUploadHostAsync(string netPath, string uploadId, CancellationToken ct)
    {
        try
        {
            var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
            var url = $"{LocateUploadUrl}?method=locateupload&appid={LocateAppId}" +
                      $"&access_token={Uri.EscapeDataString(token)}" +
                      $"&path={Uri.EscapeDataString(netPath)}" +
                      $"&uploadid={Uri.EscapeDataString(uploadId)}" +
                      "&upload_version=2.0";
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

            // 这个接口的错误字段叫 error_code（不是其它接口的 errno）—— 按官方返回结构读
            var errorCode = IntOr(json, "error_code");
            if (errorCode != 0)
            {
                AppLog.Warn("Baidu",
                    $"获取上传域名失败（error_code={errorCode}），改用默认域名 {FallbackUploadHost}");
                return FallbackUploadHost;
            }

            var host = PickUploadHost(json);
            if (host != null) return host;

            // 挑不到就把**原始响应**写进日志 —— 这是"字段真值"唯一可靠的来源。
            // 2026-09-16 实测：日志里只写了"没有可用的 https 域名"，看不到响应到底长什么样，
            // 于是又只能靠猜字段名，白白多绕一轮。以后这一行就是答案。
            AppLog.Warn("Baidu",
                $"获取上传域名：响应里没找到可用域名，改用默认域名 {FallbackUploadHost}。" +
                $"原始响应：{Truncate(json.GetRawText(), 400)}");
            return FallbackUploadHost;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Baidu", "获取上传域名异常，改用默认域名：" + ex.Message);
            return FallbackUploadHost;
        }
    }

    /// <summary>
    /// 从 locateupload 响应里挑一个可用的 https 上传域名；挑不到返回 null（调用方退兜底域名）。
    ///
    /// <b>真实响应形态（2026-09-16 用本机令牌实测拿到，不再是推测）</b>：
    /// <code>
    /// {"error_code":0,"expire":60,"host":"c.pcs.baidu.com","prov":"chongqing","isp":"cnc",
    ///  "servers":[{"server":"https://c5.pcs.baidu.com"},{"server":"https://c6.pcs.baidu.com"},
    ///             {"server":"http://c5.pcs.baidu.com"}, ...]}
    /// </code>
    /// 三个要点：
    /// <list type="number">
    /// <item><c>servers</c> 的元素是**对象**（<c>{"server":"url"}</c>），不是字符串 ——
    ///   早先只认字符串，于是每次都挑不到，只能退兜底域名（日志里刷"没有可用的 https 域名"）；</item>
    /// <item>同一批里 https 与 http 混排，**只取 https**（token 在 query 上）；</item>
    /// <item><c>expire</c> 只有 **60 秒** —— 这也印证了"不跨文件缓存域名"的决定：60 秒太短，
    ///   任何缓存都会引出"大文件传到一半失败、小文件没事"这类难查问题。</item>
    /// </list>
    ///
    /// 抽成纯函数是为了让检查点能直接钉住它 —— 域名挑错在真机上只表现为"分片传不上去"，
    /// 从现象反查回这一行要绕很远。契约同其它解析函数：**任何形状都不抛异常**。
    /// </summary>
    public static string? PickUploadHost(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return null;

        if (response.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in servers.EnumerateArray())
            {
                var host = ExtractHost(item);
                if (host != null) return host;
            }
        }

        if (response.TryGetProperty("host", out var single) && single.ValueKind == JsonValueKind.String)
            return NormalizeHost(single.GetString());

        return null;
    }

    /// <summary>
    /// 从一个 <c>servers</c> 元素里取出域名。**真实形态是对象** <c>{"server":"https://..."}</c>，
    /// 同时兼容直接给字符串的写法（两种都留着，反正代价只是几行）。
    /// </summary>
    private static string? ExtractHost(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return NormalizeHost(item.GetString());
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String)
            return NormalizeHost(s.GetString());
        return null;
    }

    /// <summary>
    /// 把服务端给的域名整成可用的 https 根地址。空值、非 http(s) 协议一律返回 null。
    /// 裸域名（如 <c>c3.pcs.baidu.com</c>）自动补 https:// —— 服务端两种写法都见过。
    /// 明确给 http 的宁可不选：token 在 query 上，明文发出去不如退兜底域名。
    /// </summary>
    private static string? NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().TrimEnd('/');
        if (!v.Contains("://", StringComparison.Ordinal)) return "https://" + v;
        return v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? v : null;
    }

    /// <summary>截断长文本用于日志（留头部即可，别把日志刷爆）。</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"…（共 {text.Length} 字符）";

    /// <summary>
    /// 上传单分片：域名由 <see cref="LocateUploadHostAsync"/> 提供（官方要求），
    /// access_token 在 query 上，文件走 multipart body。
    /// </summary>
    private async Task UploadSliceAsync(string host, string token, string netPath, string uploadId, int partSeq, byte[] slice, CancellationToken ct)
    {
        var url = $"{host}{SuperFile2Path}?method=upload&access_token={Uri.EscapeDataString(token)}" +
                  "&type=tmpfile" +
                  $"&path={Uri.EscapeDataString(netPath)}" +
                  $"&uploadid={Uri.EscapeDataString(uploadId)}" +
                  $"&partseq={partSeq}";

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(slice);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "slice");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        finally
        {
            content.Dispose();
        }

        using (resp)
        {
            var body = await ReadBodyAsync(resp.Content, ct).ConfigureAwait(false);
            using var doc = TryParse(body);
            if (doc == null)
            {
                // HTTP 层失败（403/404/502…）常常返回空体或 HTML —— 必须把状态码和域名一起报出来。
                // 2026-09-16 的 403 根因是"上传域名不对"，当时只报"无法解析的内容"，白绕了一圈。
                if (!resp.IsSuccessStatusCode)
                    throw new BaiduApiException(2,
                        $"分片 {partSeq + 1} 上传被拒：HTTP {(int)resp.StatusCode}（上传域名 {host}）。" +
                        "403 通常表示上传域名不对 —— 需先调 locateupload 取域名。");
                throw new BaiduApiException(2, $"分片 {partSeq + 1} 上传返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");
            }

            var errno = IntOr(doc.RootElement, "errno", 0);
            if (errno != 0)
                throw new BaiduApiException(errno, $"分片 {partSeq + 1} 上传失败：{DescribeErrNo(errno)}");
        }
    }

    // ══════════════════ 下载 ══════════════════

    /// <summary>取下载直链（8 小时有效，且必须拼 access_token + 指定 UA）。</summary>
    public async Task<string?> GetDownloadLinkAsync(string netPath, CancellationToken ct)
    {
        var entry = await FindAsync(netPath, ct).ConfigureAwait(false);
        if (entry == null) return null;
        return await GetDownloadLinkByFsIdAsync(entry.FsId, ct).ConfigureAwait(false);
    }

    private async Task<string?> GetDownloadLinkByFsIdAsync(long fsId, CancellationToken ct)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var url = $"{XpanMultimediaUrl}?method=filemetas&access_token={Uri.EscapeDataString(token)}" +
                  $"&fsids=%5B{fsId}%5D&dlink=1";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var errno = IntOr(json, "errno", 0);
        if (errno != 0) throw new BaiduApiException(errno, DescribeErrNo(errno));

        if (!json.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            var dlink = Str(item, "dlink");
            if (dlink.Length == 0) continue;
            // 官方返回的 dlink 里 & 常被转义成 \u0026，直接拼进 URL 会 400
            dlink = dlink.Replace("\\u0026", "&");
            // 文档要求使用时拼接 access_token
            var sep = dlink.Contains('?') ? "&" : "?";
            return $"{dlink}{sep}access_token={Uri.EscapeDataString(token)}";
        }
        return null;
    }

    /// <summary>
    /// 按需下载到本地。返回 null = 云端没有这个文件（调用方区分「文件不存在」与「下载失败」）。
    /// 下载是实现里真正会撞上限速的一步，因此支持进度回调与取消。
    /// </summary>
    public async Task<string?> DownloadAsync(string netPath, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var dlink = await GetDownloadLinkAsync(netPath, ct).ConfigureAwait(false);
        if (dlink == null) return null;

        var expected = (await FindAsync(netPath, ct).ConfigureAwait(false))?.Size ?? -1;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        // 先写临时文件再改名：中途失败/取消不会在缓存里留半个文件冒充完整副本
        var temp = localPath + ".part";

        try
        {
            using var resp = await Http.GetAsync(dlink, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new BaiduApiException(31326, "下载被拒绝（防盗链校验未通过），请检查网络环境后重试。");
                throw new BaiduApiException(2, $"下载失败：HTTP {(int)resp.StatusCode}。");
            }

            var total = resp.Content.Headers.ContentLength ?? expected;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (File.Exists(localPath)) File.Delete(localPath);
            File.Move(temp, localPath);
            progress?.Report(1.0);
            return localPath;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    // ══════════════════ 沙箱与错误 ══════════════════

    /// <summary>
    /// 沙箱守卫：任何路径都必须落在 /apps 之下。
    /// 这不是"防手滑"，而是把「AI 只能引用用户点过的文件」这条红线的最后一道兜底 ——
    /// 即使上游漏了校验，这里也不会让请求打到用户网盘的其他位置。
    ///
    /// <paramref name="allowSandboxRoot"/>：是否放行 <c>/apps</c> 本身。
    /// <b>只有只读的列目录该传 true</b> —— 写操作的目标永远是具体路径，不会等于 /apps。
    /// </summary>
    /// <remarks>
    /// 2026-09-16 事故：早期版本一律要求 <c>/apps/</c> 前缀，于是「列 /apps」这种合法只读请求
    /// 也被拒，而 <see cref="EnsureDirectoryAsync"/> 逐级建目录时**必然要列一次 /apps** ——
    /// 结果测试连接报「拒绝访问沙箱以外的路径（/apps）」，上传 100% 失败。
    /// 教训：守卫的检查粒度要跟操作的语义对齐，别拿一把尺子量所有请求。
    /// </remarks>
    public static void EnsureSandboxed(string? netPath, bool allowSandboxRoot = false)
    {
        if (string.IsNullOrWhiteSpace(netPath))
            throw new BaiduApiException(2, "网盘路径不能为空。");
        if (allowSandboxRoot && netPath.TrimEnd('/') == SandboxPrefix && netPath.Length > 0) return;
        if (!netPath.StartsWith(SandboxPrefix + "/", StringComparison.Ordinal))
            throw new BaiduApiException(-7,
                $"拒绝访问沙箱以外的路径（{netPath}）。本应用只能读写自己的应用目录 {SandboxPrefix}/{{应用名}}。");
        if (netPath.Contains("..", StringComparison.Ordinal))
            throw new BaiduApiException(-7, "网盘路径中不允许出现「..」。");
    }

    public static string NormalizeNetRoot(string? netRoot)
    {
        var v = (netRoot ?? "").Trim();
        if (v.Length == 0) v = DefaultNetRoot;
        if (!v.StartsWith('/')) v = "/" + v;
        v = v.TrimEnd('/');
        if (!v.StartsWith(SandboxPrefix + "/", StringComparison.Ordinal) && v != SandboxPrefix)
            return DefaultNetRoot;   // 越界配置一律回退默认，不让它变成运行时炸弹
        return v;
    }

    private static string ParentOf(string netPath)
    {
        var idx = netPath.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : netPath[..idx];
    }

    /// <summary>把官方错误码翻成人话（只做翻译，不据此推断业务状态）。</summary>
    public static string DescribeErrNo(int errno) => errno switch
    {
        0 => "成功",
        -6 => "授权失败或已过期，请重新授权",
        -7 => "文件或目录名不合法，或没有访问权限",
        -8 => "文件已存在",
        -9 => "文件或目录不存在",
        -10 => "网盘容量不足",
        -11 => "文件超过大小限制",
        2 => "请求参数有误",
        111 => "当前用户未授权",
        112 => "授权已过期，请重新授权",
        31061 => "同名文件已存在",
        31064 => "没有权限操作该文件或目录（可能不在本应用目录内）",
        31066 => "文件不存在",
        31069 => "文件正在上传中",
        31299 or 31364 or 31365 => "上传分片参数不符合平台要求（分片大小或数量）",
        31355 => "上传参数无效（分片与登记信息不一致）",
        31500 => "创建文件失败：uploadid 无效或分片未传完",
        31023 => "分片数量超过上限",
        31326 => "下载被拒绝（防盗链校验未通过）",
        20012 => "请求超出配额上限，稍后会自动重试",
        31034 => "触发平台频率控制，稍后会自动重试",
        _ => $"平台返回错误（errno={errno}）",
    };

    // ══════════════════ HTTP 基座 ══════════════════

    /// <summary>
    /// 读响应体：**自己按 UTF-8 解码，不看服务端声明的 charset**。
    ///
    /// 2026-09-16 实测：百度分片上传接口的响应头里带了一个非法 charset，
    /// 直接调 <c>ReadAsStringAsync</c> 会抛
    /// "The character set provided in ContentType is invalid. Cannot read content as string" ——
    /// 而那一刻**分片其实已经传出去了**，却因为"读不出响应体"把整单判成失败。
    /// 外部返回的声明永远不能当事实用：这里一律按字节读、自己解码。
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        var bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return bytes.Length == 0 ? "" : Encoding.UTF8.GetString(bytes);
    }

    private static async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct, bool treatMissingAsEmpty = false)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(resp.Content, ct).ConfigureAwait(false);
        using var doc = TryParse(body);
        if (doc == null)
        {
            if (treatMissingAsEmpty) return EmptyObject();
            throw new BaiduApiException(2, $"接口返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");
        }
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(resp.Content, ct).ConfigureAwait(false);
        using var doc = TryParse(body);
        if (doc == null)
            throw new BaiduApiException(2, $"接口返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// POST 空 body（参数全在 query 上）。专供旧式端点使用 —— 例如 `method=delete` 只认
    /// query 里的 path；服务端对 404/403 仍返回合法 JSON（含 error_code），所以这里照常解析，
    /// 由调用方按 error_code 判成败。
    /// </summary>
    private static async Task<JsonElement> PostEmptyAsync(string url, CancellationToken ct)
    {
        using var content = new StringContent("");
        using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(resp.Content, ct).ConfigureAwait(false);
        using var doc = TryParse(body);
        if (doc == null)
            throw new BaiduApiException(2, $"接口返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");
        return doc.RootElement.Clone();
    }

    private static JsonDocument? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body); } catch (JsonException) { return null; }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";

    private static long LongOr(JsonElement e, string name, long fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        return fallback;
    }

    private static int IntOr(JsonElement e, string name, int fallback = 0)
    {
        var v = LongOr(e, name, fallback);
        return v is > int.MinValue and < int.MaxValue ? (int)v : fallback;
    }

    private static DateTime FromUnix(long seconds)
    {
        if (seconds <= 0) return DateTime.MinValue;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>分片与内容校验用的 MD5（小写十六进制，与百度的 block_list 口径一致）。</summary>
    public static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
}
