using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture.Models;

namespace FocusCapture.Services.Sync;

/// <summary>
/// 渠道频率/容量上报：引擎按 Limits 自动选择合并窗口与批大小（可插拔架构三件套之"频率策略参数化"）。
/// WebDAV 上报 30/200/500（坚果云实测红线）；将来 Server 上报 3/500/10000 —— 引擎零改动。
/// </summary>
public record SyncLimits(int MinIntervalSeconds = 30, int MaxBatchSize = 200, int MaxRequestsPerWindow = 500);

/// <summary>增量拉取结果：Notes = since 之后的变更；NewSince = 当前扫描到的云端最新 updatedAt（UTC ISO 字符串）。</summary>
public record SyncPullResult(List<SyncNote> Notes, string? NewSince);

/// <summary>批量推送结果：NewSince = 推送后云端最新 updatedAt（UTC ISO 字符串）。</summary>
public record SyncPushResult(string? NewSince);

/// <summary>
/// 云端元数据 sync_meta.json：E2EE 盐（明文，跨设备一致的关键）+ 桶清单（孤儿桶清理依据）+ 同步游标。
/// 由 Provider 以渠道方言读写（WebDAV = sync_meta.json 文件；Server = 服务端元数据）。
/// </summary>
public class SyncMeta
{
    /// <summary>E2EE 盐（Base64，明文存储——盐无需保密，跨设备派生同一 DEK 的关键）。</summary>
    public string SaltBase64 { get; set; } = "";

    /// <summary>桶清单（WebDAV 文件名列表，如 notes-2026-W33-1.json）。</summary>
    public List<string> Buckets { get; set; } = new();

    /// <summary>同步游标（云端最新 updatedAt，UTC ISO 字符串）。</summary>
    public string Cursor { get; set; } = "";
}

/// <summary>
/// 渠道错误统一异常：StatusCode=0 网络错误（可重试）；401 授权码错（提示重新生成，配置不改）；
/// 503/429 限流（引擎指数退避）；其他为 HTTP 状态码。
/// </summary>
public class SyncProviderException : Exception
{
    public int StatusCode { get; }

    public SyncProviderException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public bool IsRateLimit => StatusCode == 503 || StatusCode == 429;
    public bool IsAuth => StatusCode == 401;
    public bool IsNetwork => StatusCode == 0;
}

/// <summary>
/// 同步渠道契约（可插拔架构：同步引擎与 UI 只认这层，WebDAV / 将来 Server 只是实现）。
/// since = 上次同步游标（云端最新 updatedAt，UTC ISO 字符串）。
/// </summary>
public interface ISyncProvider
{
    /// <summary>渠道名："WebDAV" / "Server"（仅展示用，引擎不得按名分支逻辑）。</summary>
    string Name { get; }

    /// <summary>频率/容量上报（渠道属性）。</summary>
    SyncLimits Limits { get; }

    /// <summary>
    /// 增量拉取：返回 since 之后的变更 + 最新时间。
    /// WebDAV 无服务端游标：语义 = 拉取全部桶后按 UpdatedAt &gt; since 客户端过滤（含边界），
    /// NewSince = 当前扫描到的最新时间。
    /// </summary>
    Task<SyncPullResult> PullAsync(string? since, CancellationToken ct);

    /// <summary>
    /// 批量推送：changes = 引擎已按"sync_meta 桶清单全量拉取 + 本机变更合并"后的完整目标笔记集
    /// （应存在于云端的全部笔记，含软删标记；Quest-5 §7 第六步 3"push 合并基础 = 全量"）。
    /// 实现负责：按 ISO 周分桶（≤Limits.MaxBatchSize 条/桶）→ 整桶 PUT 覆盖 → 按桶清单 diff 删除孤儿桶
    /// → 更新 sync_meta.json 与游标。整桶覆盖天然幂等——重复推送同一目标集结果一致。
    /// </summary>
    Task<SyncPushResult> PushAsync(IReadOnlyList<SyncNote> changes, string? lastCursor, CancellationToken ct);

    /// <summary>全量导出（新设备首次 / 密钥重置后全量重传）：返回云端全部笔记（密文，未解密）。</summary>
    Task<List<SyncNote>> FullAsync(CancellationToken ct);

    /// <summary>
    /// 读取云端元数据（E2EE 盐 / 桶清单 / 游标）。云端无元数据（首配设备）返回 null。
    /// 审查补充（2026-08-13）：密钥重置后其他设备需比对"云端盐 vs 本地缓存盐"，故契约提供此方法。
    /// </summary>
    Task<SyncMeta?> GetMetaAsync(CancellationToken ct);

    /// <summary>
    /// 保存 E2EE 盐到云端元数据（首配设备生成盐后上传，跨设备派生同一 DEK 的关键；幂等，重复调用结果一致）。
    /// 审查补充（2026-08-13）：首配盐必须随 sync_meta.json 上传，契约提供此方法。
    /// </summary>
    Task SaveSaltAsync(string saltBase64, CancellationToken ct);
}
