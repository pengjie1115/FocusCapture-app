using System.Threading;

namespace FocusCapture.Services.Files;

/// <summary>
/// 后台上传队列。
///
/// 设计要点：
/// 1. <b>先本地后云端，绝不阻塞用户</b> —— 文件登记完就返回，上传在后台慢慢走
/// 2. <b>只认 origin=local</b> —— 队列来源是 <see cref="FileRepository.PendingUploadIds"/>，
///    取回的文件带 downloaded 标记，永远不会排到这里（防线①）
/// 3. <b>单并发 + 频控退避</b> —— 平台对异常行为有频次限制（官方口径：仅对异常行为限制），
///    串行上传既省得自己撞墙，也让进度显示不打架
/// 4. <b>失败重试有上限</b> —— 重试次数记账本，界面上能看出「有文件一直传不上去」，而不是无声黑洞
/// </summary>
public static class UploadQueue
{
    /// <summary>每批之间最长等待：没有待办时也该定期醒一次（可能有重试项）</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);

    /// <summary>单次失败后的等待（频控时更长）</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>单文件重试上限（超过后不再自动重试，交由设置页提示用户手动处理）。</summary>
    public const int MaxRetry = 5;

    /// <summary>保养节拍：到期清理 + 缓存淘汰的周期（启动时另有一次即时保养，见 MainWindow）。</summary>
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(30);
    private static DateTime _lastMaintenanceUtc = DateTime.UtcNow;

    private static readonly SemaphoreSlim Signal = new(0, 1);
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;
    private static Task? _worker;

    /// <summary>队列状态变化（上传成功/失败/进度），UI 订阅刷新。</summary>
    public static event Action? Changed;

    /// <summary>最近一次失败原因（界面提示用）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>当前正在上传的文件名（无 = null）。</summary>
    public static string? CurrentFileName { get; private set; }

    public static bool IsRunning
    {
        get { lock (Gate) return _worker is { IsCompleted: false }; }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_worker is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => LoopAsync(token), token);
            AppLog.Info("Files", "上传队列已启动");
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { _cts?.Cancel(); } catch { /* 已释放 */ }
            _cts = null;
            _worker = null;
        }
    }

    /// <summary>有新文件进来 / 用户手动触发时叫醒队列。</summary>
    public static void Kick()
    {
        try { Signal.Release(); } catch (SemaphoreFullException) { /* 已挂起等待，无需重复唤醒 */ }
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await RunOnceAsync(ct).ConfigureAwait(false);

                // 保养节拍：附件到期清理 → 缓存淘汰。
                // 挂在这里而不是另起一个定时器：上传队列本来就是这个模块常驻的那条循环，
                // 再拉一个线程只会多一处需要同步的生命周期。
                await RunMaintenanceIfDueAsync(ct).ConfigureAwait(false);

                if (result.Attempted == 0)
                {
                    // 没活干：睡到自然醒或被 Kick 叫醒
                    await Signal.WaitAsync(IdlePoll, ct).ConfigureAwait(false);
                }
                else if (result.Failed > 0)
                {
                    // 失败必须退避。
                    // 早期版本这里不等待，于是「失败→立刻重跑」在一瞬间循环 —— 实测 78 毫秒内
                    // 把 5 次重试机会全部烧光，日志连刷 5 行「上传失败」，用户看到的是「多次上传失败」，
                    // 实际上一次有意义的等待都没有。更糟的是：那一刻的错误如果是「配置没填对」，
                    // 用户根本来不及去改，配额就耗尽了。
                    var backoff = TimeSpan.FromSeconds(Math.Min(60, 10 * result.Failed));
                    await Signal.WaitAsync(backoff, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("Files", "上传队列异常", ex);
                try { await Task.Delay(IdlePoll, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>到期清理 → 缓存淘汰（每 <see cref="MaintenanceInterval"/> 一次）。单项失败不牵连另一项。</summary>
    private static async Task RunMaintenanceIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastMaintenanceUtc < MaintenanceInterval) return;
        _lastMaintenanceUtc = DateTime.UtcNow;

        try { await AttachmentExpiryService.RunAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Warn("Files", "附件到期清理失败：" + ex.Message); }

        try { CacheEvictor.Run(); }
        catch (Exception ex) { AppLog.Warn("Files", "缓存淘汰失败：" + ex.Message); }
    }

    /// <summary>跑一轮（也供「立即上传」按钮同步调用）。</summary>
    public static async Task<UploadBatchResult> RunOnceAsync(CancellationToken ct)
    {
        if (FileRepository.Cloud?.IsReady != true)
            return new UploadBatchResult(0, 0, 0, "尚未连接网盘");

        var ids = FileRepository.PendingUploadIds();
        if (ids.Count == 0) return new UploadBatchResult(0, 0, 0, null);

        var ok = 0;
        var failed = 0;
        var skipped = 0;
        string? firstError = null;

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;

            // 重试太多次的先放一边（界面能看出问题，不无限骚扰平台）
            var entry = FileRepository.FindCache(id);
            if (entry != null && entry.UploadRetry >= MaxRetry)
            {
                skipped++;
                continue;
            }

            if (!FileRepository.TryBeginUpload(id)) continue;   // 别处已在传

            var meta = FileRepository.FindMetadata(id);
            CurrentFileName = meta?.Name;

            try
            {
                var (success, error) = await UploadOneAsync(id, ct).ConfigureAwait(false);
                FileRepository.RecordUploadResult(id, success, error);
                if (success)
                {
                    ok++;
                    LastError = null;
                }
                else
                {
                    failed++;
                    firstError ??= error;
                    LastError = error;
                }
            }
            catch (OperationCanceledException)
            {
                FileRepository.RecordUploadResult(id, false, "已取消");
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
                LastError = ex.Message;
                FileRepository.RecordUploadResult(id, false, ex.Message);
            }
            finally
            {
                CurrentFileName = null;
            }

            Changed?.Invoke();

            // 频控/配额类错误：整轮退避，别继续撞
            if (firstError != null && (firstError.Contains("配额") || firstError.Contains("频率控制")))
            {
                try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        if (ok > 0 || failed > 0)
            AppLog.Info("Files", $"上传一轮：成功 {ok} / 失败 {failed} / 跳过 {skipped}");

        return new UploadBatchResult(ok, failed, skipped, firstError);
    }

    private static async Task<(bool Ok, string? Error)> UploadOneAsync(string id, CancellationToken ct)
    {
        var meta = FileRepository.FindMetadata(id);
        if (meta == null) return (false, "元数据已不存在");
        if (meta.Deleted) return (true, null);   // 用户已彻底删除：不用传了

        var entry = FileRepository.FindCache(id);
        if (entry == null || string.IsNullOrEmpty(entry.LocalPath) || !File.Exists(entry.LocalPath))
            return (false, "本地文件已不在（可能被清理），无法上传");

        // 防线①复核：账户里标着 downloaded 的绝不上传。
        // 上游已经拦过一道，这里再确认一次 —— 这个环一旦闭合就是「文件越传越多」的事故，值得两道。
        if (entry.Origin == CacheOrigins.Downloaded)
        {
            FileRepository.RecordUploadResult(id, true, null);
            return (true, null);
        }

        var cloud = FileRepository.Cloud;
        if (cloud == null) return (false, "未连接网盘");
        if (!cloud.IsReady) return (false, "百度网盘未授权");

        // 云端目录以元数据里的 NetPath 为准（附件按月分目录后，按类型现拼会建到错的位置）
        await cloud.EnsureDirectoryAsync(FileRepository.NetDirOf(meta.NetPath), ct).ConfigureAwait(false);
        await cloud.UploadAsync(entry.LocalPath, meta.NetPath, null, ct).ConfigureAwait(false);
        return (true, null);
    }
}

public sealed record UploadBatchResult(int Succeeded, int Failed, int Skipped, string? FirstError)
{
    public int Attempted => Succeeded + Failed;
}
