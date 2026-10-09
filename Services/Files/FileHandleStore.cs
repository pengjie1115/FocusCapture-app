namespace FocusCapture.Services.Files;

/// <summary>句柄的对外视图（界面展示与系统提示注入用）。</summary>
public sealed record FileHandleInfo(string Id, string FileName, string Path, long Size, DateTime CreatedAt);

/// <summary>
/// 文件句柄 —— 本次更新最重要的一条红线。
///
/// <b>问题的本质</b>：如果工具做成 upload_file(path)，path 由模型填，模型就能编造**任意路径**，
/// 等于把整台电脑的文件系统向 AI 敞开。这与 LocalTools「只作用于笔记库」的沙箱设计直接冲突。
///
/// <b>解法</b>：路径从不由模型产生，只能由用户点出来。
/// 用户点「选择文件」→ 本表发一个牌号（file:20260916-1）→ 界面显示成卡片 →
/// 模型只会说「把 file:20260916-1 存到网盘」。
/// <see cref="TryResolve"/> 只认表里已有的牌号，**凭空构造的牌号一律解析失败**。
///
/// 结果：AI 的可达范围 = 用户亲手点过的那些文件。边界掌握在人手里，不在模型手里。
///
/// 句柄放内存、进程级、24 小时过期：它是「刚刚选的那个文件」的短期引用，
/// 跨重启保留只会让模型有机会引用很久以前的路径，没有收益。
/// </summary>
public static class FileHandleStore
{
    private sealed class Handle
    {
        public string Id { get; init; } = "";
        public string Path { get; init; } = "";
        public DateTime CreatedAt { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Handle> Handles = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static string _sequenceDate = "";
    private static int _sequence;

    /// <summary>句柄前缀：file:yyyyMMdd-N</summary>
    public const string Prefix = "file:";

    /// <summary>为用户选中的文件签发一个牌号。</summary>
    public static FileHandleInfo Register(string path)
    {
        var full = Path.GetFullPath(path);
        lock (Gate)
        {
            PurgeExpiredLocked();

            // 同一路径重复选择：复用已有牌号，避免界面堆一长串同名卡片
            var existing = Handles.Values.FirstOrDefault(h =>
                string.Equals(h.Path, full, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return ToInfo(existing);

            var today = DateTime.Now.ToString("yyyyMMdd");
            if (_sequenceDate != today)
            {
                _sequenceDate = today;
                _sequence = 0;
            }
            _sequence++;

            var handle = new Handle
            {
                Id = $"{Prefix}{today}-{_sequence}",
                Path = full,
                CreatedAt = DateTime.Now,
            };
            Handles[handle.Id] = handle;
            AppLog.Info("Files", $"已签发文件句柄 {handle.Id}（{Path.GetFileName(full)}）");
            return ToInfo(handle);
        }
    }

    /// <summary>
    /// 解析牌号 → 本地路径。**只认表里已有的牌号**，这是红线的落点：
    /// 模型编造的 file:20260101-99 解析失败，无法借此读到没被点过的文件。
    /// </summary>
    public static bool TryResolve(string? handleId, out string path, out string error)
    {
        path = "";
        error = "";

        var id = (handleId ?? "").Trim();
        if (id.Length == 0)
        {
            error = "缺少参数 handle。请让用户先在对话框点「选择文件」挑一个文件，再引用系统给出的牌号（形如 file:20260916-1）。";
            return false;
        }
        if (!id.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = $"「{id}」不是合法牌号。牌号必须形如 file:20260916-1，且只能由用户在对话框点「选择文件」产生 —— " +
                    "你无法自行构造。请不要猜测路径或牌号。";
            return false;
        }

        lock (Gate)
        {
            PurgeExpiredLocked();
            if (!Handles.TryGetValue(id, out var handle))
            {
                error = $"牌号「{id}」不存在或已过期（有效期 24 小时）。" +
                        "它可能是编造的，也可能用户还没选过文件。请先让用户在对话框点「选择文件」。";
                return false;
            }
            if (!File.Exists(handle.Path))
            {
                error = $"牌号「{id}」对应的文件已不在本机（{handle.Path}），请让用户重新选择。";
                return false;
            }
            path = handle.Path;
            return true;
        }
    }

    /// <summary>按牌号取单个句柄（不存在或已过期返回 null）。供界面按会话渲染卡片用。</summary>
    public static FileHandleInfo? Get(string handleId)
    {
        lock (Gate)
        {
            PurgeExpiredLocked();
            return Handles.TryGetValue((handleId ?? "").Trim(), out var h) ? ToInfo(h) : null;
        }
    }

    /// <summary>当前有效句柄快照（注入系统提示，让模型知道有哪些牌号可用）。</summary>
    public static List<FileHandleInfo> Snapshot()
    {
        lock (Gate)
        {
            PurgeExpiredLocked();
            return Handles.Values.OrderBy(h => h.CreatedAt).Select(ToInfo).ToList();
        }
    }

    public static void Remove(string handleId)
    {
        lock (Gate) Handles.Remove((handleId ?? "").Trim());
    }

    public static void Clear()
    {
        lock (Gate) Handles.Clear();
    }

    /// <summary>注入系统提示的文本（无句柄时返回空串，不占 token）。
    /// 2026-09-21：支持按会话过滤 —— <paramref name="handleIds"/> 非空时只描述这些牌号
    /// （牌号跨会话隔离：别的会话里选的文件，本会话的模型看不见也操作不了，用户拍板）。
    /// 传 null = 全量（保持旧行为，供诊断用）。</summary>
    public static string DescribeForModel(IEnumerable<string>? handleIds = null)
    {
        var items = Snapshot();
        if (handleIds != null)
        {
            var ids = new HashSet<string>(handleIds, StringComparer.Ordinal);
            items = items.Where(h => ids.Contains(h.Id)).ToList();
        }
        if (items.Count == 0) return "";

        var lines = items.Select(h =>
            $"- {h.Id} → {h.FileName}（{RootMigrationService.FormatSize(h.Size)}）");
        return "用户在对话框里已选择的本地文件（只有这些文件你可以操作，牌号必须原样使用）：\n"
               + string.Join("\n", lines);
    }

    private static void PurgeExpiredLocked()
    {
        var cutoff = DateTime.Now - Lifetime;
        var dead = Handles.Values.Where(h => h.CreatedAt < cutoff).Select(h => h.Id).ToList();
        foreach (var id in dead) Handles.Remove(id);
    }

    private static FileHandleInfo ToInfo(Handle h)
    {
        long size = 0;
        try { size = new FileInfo(h.Path).Length; } catch { /* 拿不到就是 0 */ }
        return new FileHandleInfo(h.Id, Path.GetFileName(h.Path), h.Path, size, h.CreatedAt);
    }
}
