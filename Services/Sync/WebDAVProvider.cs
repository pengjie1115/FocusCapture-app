using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FocusCapture.Models;

namespace FocusCapture.Services.Sync;

/// <summary>
/// 坚果云 WebDAV Provider：PROPFIND/PUT/GET/DELETE/MKCOL + sync_meta.json + 桶清单 + 401/503 区分。
/// - 整桶 PUT 覆盖天然幂等（重复推送同一桶结果一致）；
/// - 请求频率由引擎按 Limits 控制（30s 合并窗口天然限流），本类不做 sleep；
/// - 桶规则：ISO 周分桶（updatedAt UTC），≤Limits.MaxBatchSize 条/桶，文件名 notes-{yyyy-Www}-{seq}.json（§5.0.3）。
/// 另实现 IFileStorageProvider（AI 会话同步的通用文件级方法）：只把既有 HTTP 能力公开出去，
/// 笔记桶相关方法（PullAsync/PushAsync/FullAsync/GetMetaAsync/SaveSaltAsync）逻辑一行未动。
/// </summary>
public class WebDAVProvider : ISyncProvider, IFileStorageProvider
{
    private const string DavNs = "{DAV:}";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _user;

    /// <summary>
    /// 503/429 内部重试的退避秒数（默认 {5,15}），数组长度 = 重试次数。
    /// 之所以可注入：本机实测「断网/重试」检查点耗时 25.8 秒，其中约 20 秒是干等这段 5s+15s。
    /// 生产默认值一字未改，测试传 {0,0} 即可跳过等待——重试次数与判定逻辑完全不变。（2026-09-17）
    /// </summary>
    private readonly int[] _throttleRetryBackoffSeconds;

    public string Name => "WebDAV";
    public SyncLimits Limits { get; } = new(30, 200, 500);   // 坚果云实测红线：30min ≤600 请求 / 单次 >200 文件 503

    public WebDAVProvider(string baseUrl, string user, string token, int[]? throttleRetryBackoffSeconds = null)
    {
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _user = user;
        _throttleRetryBackoffSeconds = throttleRetryBackoffSeconds ?? new[] { 5, 15 };
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
    }

    // ── ISyncProvider 实现 ──

    public async Task<SyncPullResult> PullAsync(string? since, CancellationToken ct)
    {
        var notes = await FullAsync(ct).ConfigureAwait(false);
        // since 语义 = 桶内 CursorKey（上传时刻 UploadedAt，存量行退回 UpdatedAt）>= since。
        // 2026-09-09 修复：原按 UpdatedAt（笔记原始时间戳）过滤——A 端事后才同步的旧笔记时间戳小于
        // 他端游标，被永久过滤（"传 5 条对端只到 4 条/一条不到"的根因）。改按上传时刻后，
        // 何时上传与笔记内容时间解耦。存量行 UploadedAt 为空 → 始终投递（幂等，升级后首轮全量补齐历史漏）。
        // 重复投递靠确定性 ID 幂等去重，无害。
        var filtered = string.IsNullOrEmpty(since)
            ? notes
            : notes.Where(n => string.IsNullOrEmpty(n.UploadedAt)
                               || string.CompareOrdinal(n.CursorKey, since) >= 0).ToList();
        // 游标键同样取 CursorKey（含未投递的行，只进不退）
        var newest = notes.Count > 0 ? notes.Max(n => n.CursorKey) : null;
        return new SyncPullResult(filtered, newest);
    }

    public async Task<SyncPushResult> PushAsync(IReadOnlyList<SyncNote> changes, string? lastCursor, CancellationToken ct)
    {
        await EnsureDirectoryAsync(ct).ConfigureAwait(false);
        var meta = await GetMetaAsync(ct).ConfigureAwait(false) ?? new SyncMeta();

        // 分桶：ISO 周 + ≤MaxBatchSize/桶
        var targetBuckets = Bucketize(changes);

        // 整桶 PUT：仅当内容与云端现有桶不同才写（幂等 + 省请求）
        foreach (var pair in targetBuckets)
        {
            string? existing = null;
            if (meta.Buckets.Contains(pair.Key))
                existing = await GetFileAsync(pair.Key, ct).ConfigureAwait(false);
            if (existing == pair.Value) continue;
            await PutFileAsync(pair.Key, pair.Value, ct).ConfigureAwait(false);
        }

        // 孤儿桶清理：目标清单外、云端旧清单里有的 → DELETE（防已删笔记经旧桶复活，§2 桶清单铁律）
        var targetNames = new HashSet<string>(targetBuckets.Keys);
        foreach (var old in meta.Buckets)
        {
            if (!targetNames.Contains(old))
                await DeleteFileAsync(old, ct).ConfigureAwait(false);
        }

        // 更新 meta：桶清单 = 目标桶；游标 = 最新上传时刻 CursorKey（只进不退，与 PullAsync 过滤键一致）
        meta.Buckets = targetBuckets.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (changes.Count > 0)
        {
            var newest = changes.Max(n => n.CursorKey) ?? "";
            if (string.IsNullOrEmpty(meta.Cursor) || string.CompareOrdinal(newest, meta.Cursor) > 0)
                meta.Cursor = newest;
        }
        await PutMetaAsync(meta, ct).ConfigureAwait(false);

        return new SyncPushResult(meta.Cursor);
    }

    public async Task<List<SyncNote>> FullAsync(CancellationToken ct)
    {
        // 拉取路径同样确保目录存在（修复换账号/云端目录缺失时首次拉取 PROPFIND 404 直接报错，2026-09-05）
        await EnsureDirectoryAsync(ct).ConfigureAwait(false);
        var files = await ListFilesAsync(ct).ConfigureAwait(false);
        var notes = new List<SyncNote>();
        foreach (var f in files.Where(f => f.StartsWith("notes-", StringComparison.Ordinal) &&
                                           f.EndsWith(".json", StringComparison.Ordinal)))
        {
            try
            {
                var json = await GetFileAsync(f, ct).ConfigureAwait(false);
                var bucket = SyncBucket.FromJson(json);
                if (bucket?.Notes != null) notes.AddRange(bucket.Notes);
            }
            catch (SyncProviderException ex) when (ex.StatusCode == 404)
            {
                // 桶被并发删除：跳过（§6 未知处理 5：桶损坏/缺失跳过继续）
            }
            catch (JsonException)
            {
                // 桶 JSON 解析失败：跳过该桶，继续其他桶，不中断全量同步（§6 未知处理 5）
            }
        }
        return notes;
    }

    public async Task<SyncMeta?> GetMetaAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetFileAsync("sync_meta.json", ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SyncMeta>(json, SyncJson.Options);
        }
        catch (SyncProviderException ex) when (ex.StatusCode == 404)
        {
            return null;   // 首配设备：云端无 meta
        }
        catch (JsonException)
        {
            return null;   // meta 损坏：当首配处理（新写覆盖），本地盐缓存仍可用
        }
    }

    public async Task SaveSaltAsync(string saltBase64, CancellationToken ct)
    {
        await EnsureDirectoryAsync(ct).ConfigureAwait(false);
        var meta = await GetMetaAsync(ct).ConfigureAwait(false) ?? new SyncMeta();
        meta.SaltBase64 = saltBase64;
        await PutMetaAsync(meta, ct).ConfigureAwait(false);
    }

    // ── IFileStorageProvider 实现（AI 会话同步用；转发既有 WebDAV 方言方法，笔记桶语义不受影响） ──

    Task<string?> IFileStorageProvider.DownloadFileAsync(string fileName, CancellationToken ct)
        => DownloadFileOrNullAsync(fileName, ct);

    Task IFileStorageProvider.UploadFileAsync(string fileName, string content, CancellationToken ct)
        => PutFileAsync(fileName, content, ct);

    // ── WebDAV 方言 ──

    /// <summary>首次同步前确保 Base URL 目录存在：PROPFIND 400/404/405/409 → MKCOL（坚果云自定义子目录不会自动存在，审查补充；400 为坚果云对不存在目录的实测返回，2026-09-05 新设备验证补充）。</summary>
    public async Task EnsureDirectoryAsync(CancellationToken ct)
    {
        var (exists, status) = await PropFindAsync(ct).ConfigureAwait(false);
        if (exists) return;
        if (status == 400 || status == 404 || status == 405 || status == 409)
            await MkColAsync(ct).ConfigureAwait(false);
        else
            throw new SyncProviderException(status, $"WebDAV 目录探测失败 (HTTP {status})");
    }

    private async Task<(bool Exists, int Status)> PropFindAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
        req.Headers.Add("Depth", "0");
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode);
            return (false, (int)resp.StatusCode);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            throw new SyncProviderException(0, $"网络错误：{ex.Message}");
        }
    }

    private async Task<List<string>> ListFilesAsync(CancellationToken ct)
        => (await ListFilesDetailedAsync(ct).ConfigureAwait(false)).Select(f => f.Name).ToList();

    /// <summary>PROPFIND Depth=1 带修改标记版（增量对账用）：解析 getlastmodified（HTTP 日期），拿不到则 null。</summary>
    private async Task<List<CloudFileInfo>> ListFilesDetailedAsync(CancellationToken ct)
    {
        try
        {
            var body = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
                r.Headers.Add("Depth", "1");
                return r;
            }, "列目录", _baseUrl, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(body);
            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var respEl in doc.Descendants(DavNs + "response"))
            {
                var href = respEl.Element(DavNs + "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                var name = href.TrimEnd('/').Split('/').LastOrDefault();
                if (string.IsNullOrEmpty(name) || name == _baseUrl.TrimEnd('/').Split('/').LastOrDefault()) continue;
                // getlastmodified 在 propstat/prop 下，命名空间各异（DAV: 或坚果云扩展）→ 按本地名兜底匹配
                string? lastModified = respEl.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value;
                names[name] = lastModified;
            }
            return names.Select(kv => new CloudFileInfo(kv.Key, kv.Value)).ToList();
        }
        catch (SyncProviderException) { throw; }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            throw new SyncProviderException(0, $"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            throw new SyncProviderException(0, $"PROPFIND 响应解析失败：{ex.Message}");
        }
    }

    // IFileStorageProvider.ListFilesAsync 显式实现：转发带修改标记版
    Task<List<CloudFileInfo>> IFileStorageProvider.ListFilesAsync(CancellationToken ct) => ListFilesDetailedAsync(ct);

    private Task<string> GetFileAsync(string fileName, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _baseUrl + fileName), "读取", fileName, ct);

    /// <summary>下载文件，404 返回 null（IFileStorageProvider.DownloadFileAsync 实现；笔记桶路径不用它）</summary>
    private async Task<string?> DownloadFileOrNullAsync(string fileName, CancellationToken ct)
    {
        try
        {
            return await GetFileAsync(fileName, ct).ConfigureAwait(false);
        }
        catch (SyncProviderException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    private Task PutFileAsync(string fileName, string content, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Put, _baseUrl + fileName)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        }, "上传", fileName, ct);

    /// <summary>DELETE 文件（公开以实现 IFileStorageProvider.DeleteFileAsync；笔记桶孤儿清理同用此方法）</summary>
    public Task DeleteFileAsync(string fileName, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, _baseUrl + fileName), "删除", fileName, ct);

    private Task MkColAsync(CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(new HttpMethod("MKCOL"), _baseUrl), "创建目录", _baseUrl, ct);

    /// <summary>
    /// 统一发送：401/503/网络错误分类 + 限流自动重试（2026-09-09）。
    /// 503/429 自动退避重试（默认 2 次：5s/15s；退避时长见构造参数，测试可注入 0）——坚果云限流惩罚多为短时突发，
    /// 单发必失败会让整轮同步报废；重试经请求工厂重建（HttpRequestMessage 不可复用）。仍失败才抛给引擎（引擎按 auto/manual 各自处理）。
    /// </summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> reqFactory, string action, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = reqFactory();
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return body;
                }
                var code = (int)resp.StatusCode;
                if (code is 503 or 429 && attempt < _throttleRetryBackoffSeconds.Length)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_throttleRetryBackoffSeconds[attempt]), ct).ConfigureAwait(false);
                    continue;
                }
                var msg = code switch
                {
                    401 => "坚果云授权码无效，请在坚果云客户端（或手机 APP）『设置 → 第三方应用管理』重新生成",
                    503 or 429 => $"坚果云限流 (HTTP {code})",
                    _ => $"{action}失败 (HTTP {code})：{target}"
                };
                throw new SyncProviderException(code, msg);
            }
            catch (SyncProviderException) { throw; }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                throw new SyncProviderException(0, $"网络错误：{ex.Message}");
            }
        }
    }

    private static bool IsNetworkError(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException;

    /// <summary>更新云端 sync_meta.json（保留云端盐——本机不生成盐，盐由首配设备生成；本类只透传）。</summary>
    private Task PutMetaAsync(SyncMeta meta, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(meta, SyncJson.Options);
        return PutFileAsync("sync_meta.json", json, ct);
    }

    // ── 桶拆分（第二层契约：WebDAV 与将来 Server 两端一致的存储格式） ──

    private Dictionary<string, string> Bucketize(IReadOnlyList<SyncNote> notes)
    {
        var result = new Dictionary<string, string>();
        var groups = notes.GroupBy(n => GetIsoWeekKey(n.UpdatedAt));
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            var seq = 1;
            for (int i = 0; i < ordered.Count; i += Limits.MaxBatchSize)
            {
                var chunk = ordered.Skip(i).Take(Limits.MaxBatchSize).ToList();
                var fileName = $"notes-{group.Key}-{seq}.json";
                var bucket = new SyncBucket { Bucket = fileName[..^".json".Length], Notes = chunk };
                result[fileName] = bucket.ToJson();
                seq++;
            }
        }
        return result;
    }

    /// <summary>ISO 周键（yyyy-Www）：确定性分桶依据（同款代码双机一致即可，不要求与严格 ISO 8601 完全对齐）。</summary>
    private static string GetIsoWeekKey(string updatedAtUtc)
    {
        if (!DateTimeOffset.TryParse(updatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            dto = DateTimeOffset.UtcNow;
        var dt = dto.UtcDateTime;
        var cal = CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(dt, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var isoYear = dt.Year;
        if (dt.Month == 1 && week >= 52) isoYear--;
        if (dt.Month == 12 && week == 1) isoYear++;
        return $"{isoYear}-W{week:D2}";
    }
}
