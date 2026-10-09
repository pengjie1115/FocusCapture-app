using System.Threading;

namespace FocusCapture.Services.Files;

/// <summary>
/// 对话附件到期清理（2026-09-16 按实机结论重构）。
///
/// 只管 <see cref="FileTypes.Attachment"/> 一类 —— AI 产出与用户主动上传的文件云端永久保留。
/// 这条分级是刻意的：一刀切清理会让网盘从「全量权威仓库」退化成「滚动窗口」，
/// 整个「淘汰只在本地记账、云端是最后安全网」的设计会一起失效。
///
/// <b>2026-09-16 重构的由来</b>：旧顺序是「云端删成功 → 才打墓碑 + 贴淘汰标记」，
/// 而云端删除当时恒失败（errno=2），于是整条链卡死：云端没删、本地没清、墓碑没打，
/// 还每 30 分钟整批重试白烧配额。**根子在于让本地状态推进依赖了一个外部接口的成败。**
///
/// 现在的顺序（两段彻底分开）：
/// <list type="number">
/// <item><b>本地先推进</b>：贴「优先淘汰」标记，本地副本交给淘汰器统一释放（删本地只走一个出口）；</item>
/// <item><b>云端尽力而为</b>：删成功 → 标 <see cref="CloudStates.Expired"/>（记录留着，不打墓碑）；
///   删失败 → 标 <see cref="CloudStates.CleanupPending"/>（设置页可见，用户可去网盘手动清）。</item>
/// </list>
/// 外部接口只影响「云端那份清没清干净」这个**标注**，不再影响本地机制能不能走。
///
/// 重试纪律：失败退避 + 单附件上限 3 次，到顶后只标注、不再自动试。
/// （旧版没有退避也没上限，删除接口一坏就是每 30 分钟整批重试一次 —— 同一个坑在
/// <see cref="UploadQueue"/> 已经踩过一遍，教训写在 REGRESSION B-14 里。）
///
/// 副作用（已知并接受）：云端附件清理后，历史对话里的附件引用会变成死链（取回时会明确提示）。
/// </summary>
public static class AttachmentExpiryService
{
    /// <summary>单个附件连续失败上限：到顶后只标注、不再自动重试（避免无限撞平台）。</summary>
    public const int MaxRetry = 3;

    /// <summary>
    /// 失败退避表（进程内，key = 文件 id）。
    /// **刻意不做持久化**：失败原因多半是"没授权 / 断网 / 配置没填对"，重启后重试一次是合理的
    /// （用户很可能刚好改完配置重启），而跨重启的重复代价上限就是 MaxRetry 次请求 —— 可接受。
    /// 换来的好处是不新增文件、不新增持久化路径，行为完全可测。
    /// </summary>
    private static readonly Dictionary<string, (int Fails, DateTime NextTry)> Backoff = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>下次可再试的时刻（没记录过 = 现在就能试）。设置页与检查点可读。</summary>
    public static DateTime? NextTryAt(string id)
    {
        lock (Gate) return Backoff.TryGetValue(id, out var v) ? v.NextTry : null;
    }

    /// <summary>是否处于「本轮跳过」状态：退避窗口内，或已到失败上限放弃自动重试。</summary>
    private static bool ShouldSkip(string id)
    {
        lock (Gate)
        {
            if (!Backoff.TryGetValue(id, out var v)) return false;
            if (v.Fails >= MaxRetry) return true;   // 到上限：不再自动试，只保留「待清理」标注
            return DateTime.Now < v.NextTry;        // 退避窗口内
        }
    }

    /// <summary>检查点用：直接问"这个 id 现在会被跳过吗"。</summary>
    internal static bool IsSkipping(string id) => ShouldSkip(id);

    private static void RecordFailure(string id)
    {
        lock (Gate)
        {
            var fails = Backoff.TryGetValue(id, out var v) ? v.Fails + 1 : 1;
            // 指数退避，上限 6 小时：既不烧配额，又能在网络/授权恢复后自愈
            var minutes = Math.Min(360, 10 * Math.Pow(2, fails - 1));
            Backoff[id] = (fails, DateTime.Now.AddMinutes(minutes));
        }
    }

    private static void RecordSuccess(string id)
    {
        lock (Gate) Backoff.Remove(id);
    }

    /// <summary>
    /// 跑一轮到期清理。返回**云端成功清理**的条数。
    /// 注意返回值只管云端那一半 —— 本地推进（贴淘汰标记）是无条件的，不体现在这个数字里。
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        List<FileMetadata> expired;
        try
        {
            expired = FileRepository.ExpiredAttachments(DateTime.Now);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", "扫描到期附件失败：" + ex.Message);
            return 0;
        }

        if (expired.Count == 0) return 0;

        // ① 本地先推进（不依赖网盘）：贴「优先淘汰」，本地副本交给淘汰器统一释放。
        //    放在云端之前是刻意的 —— 顺序反过来就又回到"外部接口一坏、本地跟着瘫"的老路。
        foreach (var meta in expired)
            FileRepository.MarkPendingEvict(meta.Id);

        // ② 云端尽力而为。没连网就不试：无法确认云端状态时绝不能标「已清理」（那是谎报）。
        if (FileRepository.Cloud?.IsReady != true)
        {
            foreach (var meta in expired) FileRepository.MarkCleanupPending(meta.Id);
            AppLog.Info("Files",
                $"有 {expired.Count} 个附件已到期，但当前未连接网盘：本地已处理，云端标注为「待清理」，联网后自动补删");
            return 0;
        }

        var done = 0;
        foreach (var meta in expired)
        {
            ct.ThrowIfCancellationRequested();

            if (ShouldSkip(meta.Id))
            {
                // 退避窗口内 / 已放弃自动重试：只保证标注是「待清理」，不再发请求
                FileRepository.MarkCleanupPending(meta.Id);
                continue;
            }

            try
            {
                await FileRepository.Cloud.DeleteAsync(new[] { meta.NetPath }, ct).ConfigureAwait(false);
                FileRepository.MarkCloudExpired(meta.Id);   // 云端确实删掉了，才允许这么标
                RecordSuccess(meta.Id);
                done++;
            }
            catch (Exception ex)
            {
                // 删不掉就如实标注 + 退避。
                // ⛔ 绝不"先标已清理、再去删" —— 顺序颠倒就是死链与谎报的源头（同类事故见 REGRESSION B-14）。
                FileRepository.MarkCleanupPending(meta.Id);
                RecordFailure(meta.Id);
                AppLog.Warn("Files",
                    $"附件「{meta.Name}」云端清理失败，已标注「待清理」并退避重试：{ex.Message}");
            }
        }

        if (done > 0)
            AppLog.Info("Files", $"附件到期清理：云端已清理 {done} 个（本地副本交由淘汰器释放）");
        if (done < expired.Count)
            AppLog.Info("Files", $"附件到期清理：{expired.Count - done} 个未清掉（已标注「云端待清理」）");

        return done;
    }

    /// <summary>清空退避表（用户手动点「重试云端清理」时调用）。返回清掉的记录数。</summary>
    public static int ResetBackoff()
    {
        lock (Gate)
        {
            var n = Backoff.Count;
            Backoff.Clear();
            return n;
        }
    }

    /// <summary>
    /// 用户手动点「重试云端清理」：清空退避表，把当前所有「待清理」的记录再删一轮。
    /// 返回 (成功数, 仍待清理数)。
    ///
    /// <b>为什么必须有这个出口</b>：退避到上限后，记录就永远躺在「待清理」不动了 ——
    /// 用户后来把网络/授权修好，它也不会自己再试。而"永久卡死且没有出口"比"反复重试"更难排查。
    /// 同名的出口在上传那边已经有一个（<see cref="FileRepository.ResetFailedUploads"/>），思路一致：
    /// **把重试挂在用户修好环境的那一刻**。
    /// </summary>
    public static async Task<(int Done, int Left)> RetryPendingAsync(CancellationToken ct = default)
    {
        ResetBackoff();

        if (FileRepository.Cloud?.IsReady != true)
            return (0, FileRepository.CleanupPendingCount());

        var pending = FileRepository.CleanupPendingItems();
        var done = 0;
        foreach (var meta in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await FileRepository.Cloud.DeleteAsync(new[] { meta.NetPath }, ct).ConfigureAwait(false);
                FileRepository.MarkCloudExpired(meta.Id);
                done++;
            }
            catch (Exception ex)
            {
                // 保持「待清理」，让用户看得到还没清掉（不静默、不谎报）
                AppLog.Warn("Files", $"重试云端清理失败：{meta.Name} — {ex.Message}");
            }
        }

        if (done > 0) AppLog.Info("Files", $"重试云端清理：成功 {done} 个");
        return (done, FileRepository.CleanupPendingCount());
    }
}
