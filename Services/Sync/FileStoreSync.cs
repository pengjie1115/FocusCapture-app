using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services.Files;

namespace FocusCapture.Services.Sync;

/// <summary>
/// 文件元数据的跨设备同步。
///
/// 为什么需要它：网盘里的文件本体只有一份，但「有哪些文件、叫什么、什么类型、什么标签」这份清单
/// 必须让每台设备都能立刻看到 —— 否则新设备登录后等于面对一个空仓库。
/// 元数据只有几 KB，秒级即可拉完；文件本体仍然按需取回，一个字节都不自动下载。
///
/// <b>为什么不直接挂到 SyncEngine 的笔记桶上</b>：
/// 笔记桶是「行记录」结构，而文件元数据是另一套结构，合并规则也不同。
/// 这里复用它的机制思路（周期钩子 + 共享并发闸 + E2EE 加密）与存储通道（IFileStorageProvider），
/// 但合并逻辑自己实现，绝不指望「搭上车就跑」。
///
/// 合并之所以能这么简单（按 id 求并集），全靠一条设计纪律：
/// <b>元数据只记客观事实，会变的状态全在本机账本里</b>。
/// </summary>
public class FileStoreSync
{
    /// <summary>云端元数据文件名（与笔记 notes-*、会话 chat-* 前缀互不冲突）。</summary>
    public const string CloudMetaFile = "files_meta.json";

    private readonly AppSettings _settings;
    private readonly IFileStorageProvider _storage;
    private readonly SemaphoreSlim _gate;

    private byte[]? _dek;
    private string _dekSalt = "";

    /// <summary>状态变化（设置页展示）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 首轮拉取是否已完成。**未完成时不能报"文件不存在"** —— 元数据还在路上时下这个结论，
    /// 是把「还没同步」误报成「被删了」的典型事故。
    ///
    /// 默认 true 是刻意的：**没配云同步的用户**根本不存在"还没拉完"这回事，
    /// 若默认 false 会让所有本地检索都顶着"同步中"，反而制造假警报。引擎装配时才置为 false。
    /// </summary>
    public static bool InitialPullCompleted { get; private set; } = true;

    /// <summary>引擎（重）建时调用：清单需要重新拉一轮，这期间别把"还没同步"当成"不存在"。</summary>
    public static void ResetInitialPull() => InitialPullCompleted = false;

    public FileStoreSync(AppSettings settings, IFileStorageProvider storage, SemaphoreSlim sharedGate)
    {
        _settings = settings;
        _storage = storage;
        _gate = sharedGate;
    }

    /// <summary>跑一轮元数据同步（搭 SyncEngine 的周期钩子）。</summary>
    public async Task RunOnceAsync()
    {
        if (!_settings.Sync.FileSyncEnabled) return;
        if (!EnsureDekCurrent()) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _storage.EnsureDirectoryAsync(CancellationToken.None).ConfigureAwait(false);

            var local = FileRepository.AllMetadata(includeDeleted: true);
            var remote = await PullAsync().ConfigureAwait(false);   // null = 云端还没有这份清单
            var merged = Merge(local, remote ?? new List<FileMetadata>());

            // 合并结果落地（内存 + 磁盘），并把差异写回云端
            FileRepository.ApplyMergedMetadata(merged);

            if (!MetadataEquals(local, merged) || remote == null)
                await PushAsync(merged).ConfigureAwait(false);

            InitialPullCompleted = true;
            SetStatus(remote == null ? "成功（首次上传清单）" : $"成功（本机 {local.Count} 条 / 合计 {merged.Count} 条）");
        }
        catch (Exception ex)
        {
            SetStatus("失败: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 合并规则（纯函数，检查点直接覆盖） ──

    /// <summary>
    /// 多设备合并：
    /// 1. 按 id 求并集（元数据近似只增不改，所以不存在"冲突"）
    /// 2. 同一 id：删除墓碑优先于普通修改；但**删除之后重新登记的文件**（CreatedAt 晚于墓碑时刻）应当胜出
    /// 3. 都不涉及删除：取 UpdatedAt 较新者（重命名 / 改标签场景）
    /// </summary>
    public static List<FileMetadata> Merge(IEnumerable<FileMetadata> local, IEnumerable<FileMetadata> remote)
    {
        var result = new Dictionary<string, FileMetadata>(StringComparer.Ordinal);

        foreach (var item in local.Concat(remote))
        {
            if (item == null || string.IsNullOrEmpty(item.Id)) continue;
            if (!result.TryGetValue(item.Id, out var existing))
            {
                result[item.Id] = item.Clone();
                continue;
            }
            result[item.Id] = PickWinner(existing, item);
        }

        return result.Values.OrderBy(m => m.CreatedAt).ToList();
    }

    private static FileMetadata PickWinner(FileMetadata a, FileMetadata b)
    {
        if (a.Deleted != b.Deleted)
        {
            var tombstone = a.Deleted ? a : b;
            var live = a.Deleted ? b : a;

            // 墓碑 = 「用户明确不要了」。只有对方是删除之后重新登记的文件才放行 ——
            // 这么判是为了同时防住两件事：
            // ① 一边删、一边只是改了个标签，结果被这点小改动"复活"（CreatedAt 没变，仍判删除）
            // ② 删掉之后用户又主动传了一次，却被旧墓碑永久压住（重传会新建记录，CreatedAt 更新 → 放行）
            return live.CreatedAt > tombstone.UpdatedAt ? live : tombstone;
        }

        return a.UpdatedAt >= b.UpdatedAt ? a : b;
    }

    /// <summary>两份清单是否等价（只比会影响合并结果的字段，忽略顺序）。</summary>
    public static bool MetadataEquals(IReadOnlyList<FileMetadata> a, IReadOnlyList<FileMetadata> b)
    {
        if (a.Count != b.Count) return false;
        var map = b.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
        foreach (var x in a)
        {
            if (!map.TryGetValue(x.Id, out var y)) return false;
            // CloudState 必须参与比较（2026-09-16）：它是"云端到底有没有这份文件"的客观事实，
            // 漏了它就意味着标注永远同步不出去 —— 他端还在以为文件好好躺在网盘里。
            if (x.Name != y.Name || x.UpdatedAt != y.UpdatedAt || x.Deleted != y.Deleted
                || x.NetPath != y.NetPath || x.Size != y.Size || x.Type != y.Type
                || x.CloudState != y.CloudState) return false;
        }
        return true;
    }

    // ── 通道 ──

    private async Task<List<FileMetadata>?> PullAsync()
    {
        var raw = await _storage.DownloadFileAsync(CloudMetaFile, CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var json = Decrypt(raw);
            return JsonSerializer.Deserialize(json, FileJsonContext.Default.ListFileMetadata);
        }
        catch (Exception ex)
        {
            // 密钥不一致 / 云端内容损坏：不抛，按"拉不到"处理（下轮密钥对齐后自然补上）
            AppLog.Warn("Files", "元数据解密失败（可能密钥未对齐），本轮按本地为准：" + ex.Message);
            return null;
        }
    }

    private async Task PushAsync(List<FileMetadata> merged)
    {
        var json = JsonSerializer.Serialize(merged, FileJsonContext.Default.ListFileMetadata);
        // 不重复加密同一份内容（清理器每轮都会调，省得反复算）
        var cipher = Encrypt(json);
        await _storage.UploadFileAsync(CloudMetaFile, cipher, CancellationToken.None).ConfigureAwait(false);
    }

    private string Encrypt(string plain)
    {
        if (_dek == null) throw new InvalidOperationException("E2EE 密钥未就绪");
        return CryptoService.Encrypt(_dek, plain);
    }

    private string Decrypt(string raw)
    {
        if (_dek == null) throw new InvalidOperationException("E2EE 密钥未就绪");
        return CryptoService.Decrypt(_dek, raw);
    }

    /// <summary>E2EE 密钥派生 —— 与 ChatSyncEngine 同算法同来源（笔记端盐 + 授权码），但各持一份实例。</summary>
    private bool EnsureDekCurrent()
    {
        var salt = _settings.Sync.E2eeSalt;
        if (_dek != null && salt == _dekSalt) return true;
        if (string.IsNullOrEmpty(salt))
        {
            SetStatus("等待笔记同步完成首配（E2EE 盐未就绪）");
            return false;
        }

        var token = SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
        if (string.IsNullOrEmpty(token))
        {
            SetStatus("未配置云同步授权码，文件清单同步跳过");
            return false;
        }

        try
        {
            _dek = CryptoService.DeriveKey(token, Convert.FromBase64String(salt));
            _dekSalt = salt;
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("密钥派生失败: " + ex.Message);
            return false;
        }
    }

    private void SetStatus(string status)
    {
        var text = $"{status}（{DateTime.Now:yyyy-MM-dd HH:mm}）";
        if (_settings.Sync.FileSyncResult == text) return;
        _settings.Sync.FileSyncResult = text;
        _settings.Sync.FileSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _settings.Save();
        StatusChanged?.Invoke(text);
    }
}
