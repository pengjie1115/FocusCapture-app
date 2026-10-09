using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services.Files;

/// <summary>「与云端核对」的报告（给 UI 用；文件名在此出现是给用户自己看的）。</summary>
public sealed class CloudVerifyReport
{
    /// <summary>原始判定结果（纯函数产出）</summary>
    public CloudVerifyResult Result { get; init; } = new();

    /// <summary>本次核对判定为「云端已不存在」的本地记录（待用户确认后写回标注）</summary>
    public List<FileMetadata> MissingItems { get; init; } = new();

    /// <summary>此前标过缺失、这次核对发现云端又有了的（写回时会自动撤销标注）</summary>
    public List<FileMetadata> RestoredItems { get; init; } = new();

    /// <summary>云端有、本地无记录的路径 —— 只报告，绝不入账（见实施清单 §5.3）</summary>
    public List<string> CloudOnlyPaths { get; init; } = new();

    public DateTime At { get; init; } = DateTime.Now;
}

/// <summary>
/// 「与云端核对」的编排：拉云端清单 → 比对 → 生成报告 → （用户确认后）写回标注。2026-09-27 新增。
///
/// 四条纪律（照 docs/2026-09-27-云端核对方案实施清单.md §9）：
/// 1. <b>手动、低频、带冷却期</b>：绝不自动跑。一轮核对只花 1 次 API 调用，但未上线审核的应用
///    官方配额是「10 次/每小时」，连点几下就可能撞上限，还会连累正常上传。
/// 2. <b>前置检查必须过</b>：未授权不许跑；元数据首轮同步未完成也不许跑 ——
///    否则会把"另一台设备刚传、清单还没同步过来"误报成"云端多出"。
/// 3. <b>只写标注，绝不写文件 / 绝不新增条目</b>：判定交给纯函数 <see cref="CloudVerify"/>，
///    应用阶段只调 <see cref="FileRepository.MarkCloudMissing"/> / <see cref="FileRepository.MarkCloudOk"/>。
///    报告与写回分成两步，正是为了让用户有机会先看再点。
/// 4. <b>结论带时间戳</b>：核对结论会过期（核对之后云端可能又变了），
///    <see cref="AppSettings.CloudVerifyAt"/> 让 UI 能显示"最后核对于 X"。
///
/// <b>为什么不需要与上传队列共享并发闸</b>（实施清单 §9 曾提出，实现时收窄）：
/// 核对与上传的真正冲突点只有"核对期间某文件刚好传成功"，而那一条已被
/// <see cref="FileRepository.RecordUploadResult"/> 重传成功即清除 Missing 覆盖 —— 冲突是自愈的。
/// 为它引入跨模块共享锁，代价（耦合 + 死锁面）大于收益。本服务只用自己的闸防重入。
/// </summary>
public static class CloudVerifyService
{
    /// <summary>两次核对之间的最小间隔。防连点打平台配额。</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>本服务自己的重入闸（见类注释最后一段：不与上传队列耦合）。</summary>
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private static readonly object StampGate = new();
    private static DateTime _lastRunAt = DateTime.MinValue;

    /// <summary>设置（由装配处注入；未注入 = 不记录核对时刻，功能照常可用）。</summary>
    public static AppSettings? Settings { get; set; }

    /// <summary>
    /// 能否开跑。<c>null</c> = 可以；否则返回人话原因（直接拿去显示给用户）。
    /// UI 在按钮点击时先问它，避免"点了没反应"。
    /// </summary>
    public static string? CanRun()
    {
        if (!FileRepository.CloudReady)
            return "尚未完成百度网盘授权，请先在上方完成授权。";

        // 复用既有信号：元数据还在从云端同步时下结论，= 把"还没同步"误报成"云端多出"
        if (!FileStoreSync.InitialPullCompleted)
            return "文件清单正在从云端同步，请稍等片刻再核对。";

        if (RunGate.CurrentCount == 0) return "正在核对中，请稍候。";

        var last = LastRunAt();
        if (last.HasValue)
        {
            var since = DateTime.Now - last.Value;
            if (since >= TimeSpan.Zero && since < Cooldown)
                return $"刚核对过，请等约 {Math.Ceiling((Cooldown - since).TotalMinutes):0} 分钟后再试。";
        }
        return null;
    }

    /// <summary>最近一次核对时刻（优先本进程记录，回退到设置里持久化的那个）。</summary>
    public static DateTime? LastRunAt()
    {
        lock (StampGate)
        {
            if (_lastRunAt != DateTime.MinValue) return _lastRunAt;
            var s = Settings?.CloudVerifyAt;
            return !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var t) ? t : null;
        }
    }

    /// <summary>
    /// 跑一轮核对（**只读云端 + 只读本地，不写回任何东西**）。
    /// 返回报告供 UI 展示；用户确认后再调 <see cref="Apply"/>。
    /// </summary>
    public static async Task<(CloudVerifyReport? Report, string? Error)> RunAsync(
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var blocked = CanRun();
        if (blocked != null) return (null, blocked);

        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            status?.Report("正在读取云端清单…");
            var cloud = await FileRepository.Cloud!
                .ListAllAsync(FileRepository.Cloud.NetRoot, ct).ConfigureAwait(false);

            status?.Report("正在比对本地记录…");
            var local = FileRepository.AllMetadata(includeDeleted: true);

            var records = local.Select(m =>
            {
                var entry = FileRepository.FindCache(m.Id);
                return new LocalRecord
                {
                    Id = m.Id,
                    NetPath = m.NetPath,
                    Deleted = m.Deleted,
                    CloudState = m.CloudState,
                    HasLedgerEntry = entry != null,
                    WasUploaded = entry?.UploadState == UploadStates.Uploaded,
                    CreatedAt = m.CreatedAt,
                };
            }).ToList();

            var result = CloudVerify.Compare(records, cloud, new CloudVerifyOptions());

            var byId = local.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
            var report = new CloudVerifyReport
            {
                Result = result,
                MissingItems = result.MarkMissing.Where(byId.ContainsKey).Select(id => byId[id]).ToList(),
                RestoredItems = result.ClearMissing.Where(byId.ContainsKey).Select(id => byId[id]).ToList(),
                CloudOnlyPaths = result.CloudOnly,
            };

            StampNow();

            var summary = $"云端 {result.CloudTotal} 个文件 / 本地 {result.Scanned} 条记录："
                        + $"命中 {result.Hit}，云端已不存在 {result.MarkMissing.Count}，"
                        + $"云端多出 {result.CloudOnly.Count}";
            AppLog.Info("Files", "与云端核对完成：" + summary);
            status?.Report(summary);

            return (report, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "已取消核对。");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", "与云端核对失败：" + ex.Message);
            return (null, "核对失败：" + ex.Message
                        + "\n（可能是网络不通或平台限流，稍后再试即可。上传与取回不受影响。）");
        }
        finally
        {
            RunGate.Release();
        }
    }

    /// <summary>
    /// 把报告里的标注写回元数据（**用户确认后调用**）。
    /// 只改 <see cref="CloudStates"/> 字段，不动文件本体、不新增条目、不删记录。
    /// </summary>
    public static (int Marked, int Restored) Apply(CloudVerifyReport report)
    {
        var marked = 0;
        foreach (var id in report.Result.MarkMissing)
        {
            FileRepository.MarkCloudMissing(id);
            marked++;
        }
        var restored = 0;
        foreach (var id in report.Result.ClearMissing)
        {
            FileRepository.MarkCloudOk(id);
            restored++;
        }
        if (marked > 0 || restored > 0)
            AppLog.Info("Files", $"已应用核对标注：标为云端已不存在 {marked} 条，撤销标注 {restored} 条");
        return (marked, restored);
    }

    private static void StampNow()
    {
        var now = DateTime.Now;
        lock (StampGate) _lastRunAt = now;
        if (Settings == null) return;
        Settings.CloudVerifyAt = now.ToString("yyyy-MM-dd HH:mm");
        Settings.Save();
    }
}
