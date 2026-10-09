namespace FocusCapture.Services.Files;

/// <summary>一轮淘汰的结果。</summary>
public sealed record EvictionReport(int Removed, long FreedBytes, int Protected, long TotalBytes)
{
    public bool DidWork => Removed > 0;
}

/// <summary>
/// 本地缓存淘汰器。
///
/// 核心语义就一句话：**淘汰 = 设备级事件，只删本地 + 删账本条，元数据与云端一概不动。**
/// 所以「淘汰」在任何情况下都不会造成数据丢失 —— 云端才是权威副本，本地这份本来就是可再生的缓存。
///
/// 触发是**双条件**（时间 + 容量），两个都满足其一即动手：
/// - 时间：<see cref="IdleDays"/> 天内没被使用过
/// - 容量：本地缓存总量超过 <see cref="MaxBytes"/>
///
/// 唯一被保护的是「还没传上去的文件」—— 云端没有副本，删了就真丢了。这条保护会直接压过容量上限：
/// 宁可超容量，也不能丢用户的东西。
/// </summary>
public static class CacheEvictor
{
    /// <summary>本地容量上限默认值（GB → 字节）。</summary>
    public const long DefaultMaxBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>闲置天数默认值（超过则优先淘汰）。</summary>
    public const int DefaultIdleDays = 30;

    /// <summary>总开关（关掉后不再自动淘汰，用户仍可手动清理）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>本地缓存容量上限（字节）。</summary>
    public static long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>闲置多少天后可淘汰。</summary>
    public static int IdleDays { get; set; } = DefaultIdleDays;

    /// <summary>跑一轮淘汰。<paramref name="force"/> = true 时无视「自动清理」开关（用户手动点「立即清理」用）。</summary>
    public static EvictionReport Run(DateTime? nowOverride = null, bool force = false)
    {
        var now = nowOverride ?? DateTime.Now;

        var entries = FileRepository.AllCache();
        if (entries.Count == 0) return new EvictionReport(0, 0, 0, 0);

        var candidates = new List<(CacheEntry Entry, long Size)>();
        var protectedCount = 0;
        long total = 0;

        foreach (var e in entries)
        {
            var size = TryGetSize(e.LocalPath);
            if (size < 0)
            {
                // 账本里记着、磁盘上没了（手工删过 / 迁移丢过）→ 条目本身是垃圾，清掉
                FileRepository.DropLocal(e.Id, "本地文件已不存在");
                continue;
            }

            total += size;
            if (!CanEvict(e))
            {
                protectedCount++;
                continue;
            }
            candidates.Add((e, size));
        }

        if ((!Enabled && !force) || candidates.Count == 0)
            return new EvictionReport(0, 0, protectedCount, total);

        // 排序：被云端贴了「优先淘汰」的最先走，其余按最后使用时间从最旧开始
        var ordered = candidates
            .OrderByDescending(x => x.Entry.PendingEvict)
            .ThenBy(x => x.Entry.LastAccess)
            .ToList();

        var cutoff = now.AddDays(-IdleDays);
        var removed = 0;
        long freed = 0;
        var remaining = total;

        foreach (var (entry, size) in ordered)
        {
            var overCapacity = remaining > MaxBytes;
            var tooOld = entry.LastAccess < cutoff;

            // 「优先淘汰」标记不受双条件约束：它是云端已经清掉的遗留，留着只是缓冲
            if (!entry.PendingEvict && !overCapacity && !tooOld) continue;

            var reason = entry.PendingEvict ? "云端已到期清理"
                : tooOld ? $"超过 {IdleDays} 天未使用"
                : "超出本地容量上限";

            if (FileRepository.DropLocal(entry.Id, reason))
            {
                removed++;
                freed += size;
                remaining -= size;
            }
        }

        if (removed > 0)
            AppLog.Info("Files", $"淘汰完成：清理 {removed} 个本地缓存，释放 {RootMigrationService.FormatSize(freed)}");

        return new EvictionReport(removed, freed, protectedCount, remaining);
    }

    /// <summary>统计当前本地缓存规模（设置面板展示 + 手动清理前的确认）。</summary>
    public static (int Count, long Bytes, int Protected) Measure()
    {
        var entries = FileRepository.AllCache();
        var count = 0;
        long bytes = 0;
        var protectedCount = 0;

        foreach (var e in entries)
        {
            var size = TryGetSize(e.LocalPath);
            if (size < 0) continue;
            count++;
            bytes += size;
            if (!CanEvict(e)) protectedCount++;
        }
        return (count, bytes, protectedCount);
    }

    /// <summary>
    /// 可否淘汰。**未上传完成的文件一律保护** —— 云端还没有副本，删掉就是真丢数据。
    /// 这条保护压过容量上限：宁可超容量，也不能丢用户的东西。
    /// </summary>
    private static bool CanEvict(CacheEntry e) => e.UploadState == UploadStates.Uploaded;

    private static long TryGetSize(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return -1;
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }
}
