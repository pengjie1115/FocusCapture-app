using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusCapture.Models;

/// <summary>
/// 云端同步数据模型（第二层契约：与云端桶 JSON 字段完全一致，camelCase）。
/// Content/Tags 在云端为 E2EE 密文（由 CryptoService 负责），本地为明文；
/// Id/SchemaVersion/CreatedAt/UpdatedAt/Deleted/DeviceId 恒为明文（引擎对账需要）。
/// [JsonPropertyName] 显式锁定 camelCase 契约：桶文件（SyncJson.Options）与
/// settings.json 内 PendingDeletes（AppJsonContext 源生成，默认 PascalCase）两条序列化路径命名一致。
/// </summary>
public class SyncNote
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;   // 格式演进预留
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";          // 确定性哈希
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";     // 云端=密文；本地=明文
    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = [];      // 云端=密文数组；本地=明文
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";   // ISO 8601 UTC，明文（不敏感）
    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";   // ISO 8601 UTC，明文（对账需要）；= 笔记行原始时间戳（墓碑=删除时刻），不随上传变化
    [JsonPropertyName("uploadedAt")]
    public string UploadedAt { get; set; } = "";  // ISO 8601 UTC，明文；上传时刻（2026-09-09 新增）。增量拉取游标以此为键——
                                                  // UpdatedAt 是笔记原始时间戳（可能远早于上传时刻），拿它做增量过滤会把
                                                  // "事后才同步的旧笔记"永久漏在他端之外（Bug：A 传 5 条 B 只到 4 条/一条不到）。
                                                  // 存量行（升级前已上云）此字段为空 → 拉取侧视为"始终投递"（幂等，一次性补齐历史漏）
    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }
    [JsonPropertyName("purged")]
    public bool Purged { get; set; }              // true=已彻底删除（回收站已清空）：他端应删除本地行并清除回收站记录，不入回收站
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";    // 最后修改设备
    [JsonPropertyName("prevContent")]
    public string? PrevContent { get; set; }      // 冲突被覆盖方快照（本地留存）

    /// <summary>增量拉取游标键：优先 UploadedAt（上传时刻），存量行为空则退回 UpdatedAt。
    /// ISO 8601 UTC 字符串序 = 时间序。</summary>
    [JsonIgnore]
    public string CursorKey => string.IsNullOrEmpty(UploadedAt) ? UpdatedAt : UploadedAt;

    /// <summary>
    /// 确定性 ID：SHA256(完整行内容) 前 16 字节 hex（小写）—— 同一行在任何设备、任何文件生成相同 ID
    /// → 天然幂等、天然去重、无需索引表。
    /// <para>
    /// v4（2026-09-12 行身份改造）：**相对路径不再参与 ID**。原口径 SHA256(相对路径 + "|" + 行) 的前提是
    /// "同一行在任何设备落在同一个文件"，但实测该前提不成立——同一行的物理归属在系统内有三套口径：
    /// 本地写入按**提醒日**（未到期待办写 `灵感_{DueTime}.md`）、跨端落地按**创建日**（`ResolveRelativePath`）、
    /// 删除查找按时间戳猜文件。三套口径不一致 → 同一条行在不同设备/不同操作路径下落进不同文件 → ID 分裂。
    /// 实测后果（2026-09-12 排查）：①本机删不掉自己的未到期待办（找不到文件）②跨端删除不生效（墓碑 ID 对不上）
    /// ③旧版本行被当作"本机没有的新行"反复投递回本地（已办待办反复复活、副本累积）。
    /// </para>
    /// 行文本自带 `- [yyyy-MM-dd HH:mm] ` 时间戳前缀，唯一性已足够，路径不提供额外区分度。
    /// 代价：同文本行若被人工复制到两个文件，会被视为同一条（同一个 ID）—— 这正是我们要的去重语义。
    /// </summary>
    public static string ComputeId(string lineContent)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lineContent));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public string ToJson() => JsonSerializer.Serialize(this, SyncJson.Options);

    public static SyncNote? FromJson(string json)
        => JsonSerializer.Deserialize<SyncNote>(json, SyncJson.Options);
}

/// <summary>
/// 云端桶文件结构：{ "bucket": "notes-2026-W33-1", "notes": [SyncNote...] }。
/// 打包存储（每桶 ≤200 条），禁止每条笔记一个文件。
/// </summary>
public class SyncBucket
{
    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = "";
    [JsonPropertyName("notes")]
    public List<SyncNote> Notes { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, SyncJson.Options);

    public static SyncBucket? FromJson(string json)
        => JsonSerializer.Deserialize<SyncBucket>(json, SyncJson.Options);
}

/// <summary>同步层统一序列化配置：camelCase，对齐 PRD §5.0.3 契约（WebDAV 与将来 Server 共用同一份）。</summary>
internal static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
