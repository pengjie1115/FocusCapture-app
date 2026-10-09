using System;
using System.Collections.Generic;

namespace FocusCapture.Models;

/// <summary>
/// 云同步配置（进 settings.json，随 AppSettings 源生成序列化）。
/// WebDavToken 为 DPAPI 密文（ProtectedData.CurrentUser），RecoveryCodeHash 为加盐哈希；
/// 主密码 / 派生密钥 / 恢复码明文任何位置都不存。
/// </summary>
public class SyncSettings
{
    /// <summary>本机设备 ID（GUID，首次启动生成并持久化）——回声识别依据（§5.0.4）。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>渠道名："WebDAV" / "Server"（引擎不得按名分支逻辑）。</summary>
    public string ProviderName { get; set; } = "";

    public string WebDavUrl { get; set; } = "https://dav.jianguoyun.com/dav/FocusCapture/";
    public string WebDavUser { get; set; } = "";

    /// <summary>坚果云授权码，DPAPI 加密后存（Base64）；解密失败兜底为空并提示重新配置，不崩。</summary>
    public string WebDavToken { get; set; } = "";

    /// <summary>同步游标（云端最新 updatedAt，UTC ISO 字符串）。</summary>
    public string LastCursor { get; set; } = "";

    public bool AutoSyncEnabled { get; set; } = false;

    /// <summary>E2EE 盐（Base64）。首配设备生成、明文存云端 sync_meta.json；本地缓存供离线派生（跨设备一致）。</summary>
    public string E2eeSalt { get; set; } = "";

    /// <summary>盐待上传标记（持久化，2026-09-09 盐分叉修复）。
    /// 背景：原实现用引擎实例字段记"生成了新盐待上传"，引擎重建/应用重启即丢——盐可能永远没上云，
    /// 云端盐为空时其他设备首配各自生成新盐 → 两端密钥分叉（实测事故：A/B 互解不开对方数据）。
    /// 置位点：SetTokenKeyAsync 生成新盐 / MigrateFromLegacyAsync；清除点：SaveSaltAsync 上传成功后。</summary>
    public bool SaltNeedsUpload { get; set; } = false;

    public string RecoveryCodeHash { get; set; } = "";   // 恢复码加盐哈希（Base64）
    public string RecoveryCodeSalt { get; set; } = "";   // 恢复码哈希盐（Base64，同存防暴力枚举）

    /// <summary>待推软删清单（清空回收站时压入 Deleted=true 的笔记，推送成功后清空）。</summary>
    public List<SyncNote> PendingDeletes { get; set; } = new();

    /// <summary>待推恢复清单（回收站恢复时压入的笔记 ID，推送成功后清空）：push 时覆盖云端删除标记，防恢复被旧墓碑误杀。</summary>
    public List<string> PendingRestores { get; set; } = new();

    /// <summary>上次同步结果展示（"成功" / "失败: 原因" / ""）。</summary>
    public string LastSyncResult { get; set; } = "";

    /// <summary>上次同步时间（本地时间字符串，仅展示）。</summary>
    public string LastSyncAt { get; set; } = "";

    // ── AI 会话同步（搭 SyncEngine 周期，ChatSyncEngine 执行；2026-09 迭代） ──

    /// <summary>同步 AI 问答记录开关（默认开）。关 = ChatSyncEngine 完全不跑（不上传不下载），笔记同步不受影响。</summary>
    public bool ChatSyncEnabled { get; set; } = true;

    /// <summary>上传合并间隔秒数：笔记+会话共用的上传打包防抖窗口。下限 30 秒（坚果云限频红线）；
    /// 拉取仍固定每 30 分钟一次，不随此值调节（拉取侧请求量敏感）。</summary>
    public int MergeWindowSeconds { get; set; } = 30;

    /// <summary>会话同步上次结果（独立于笔记的 LastSyncResult，防互相覆盖；设置页云同步区展示）。</summary>
    public string ChatSyncResult { get; set; } = "";

    /// <summary>会话同步上次时间（本地时间字符串，仅展示）。</summary>
    public string ChatSyncAt { get; set; } = "";

    // ── 文件元数据同步（2026-09-16，搭同一个同步周期） ──

    /// <summary>
    /// 同步「网盘文件记录」开关（默认开）。元数据只有几 KB，同步它才能让另一台设备立刻看到完整文件列表
    /// （文件本体仍按需取回，不自动下载）。关掉后本机仍可正常使用文件区，只是列表不跨设备。
    /// </summary>
    public bool FileSyncEnabled { get; set; } = true;

    /// <summary>文件元数据同步上次结果（独立留痕，与会话/笔记互不覆盖）。</summary>
    public string FileSyncResult { get; set; } = "";

    /// <summary>文件元数据同步上次时间（本地时间字符串，仅展示）。</summary>
    public string FileSyncAt { get; set; } = "";

    /// <summary>确保 DeviceId 存在（无则生成 GUID；调用方负责 Save 持久化）。</summary>
    public void EnsureDeviceId()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
            DeviceId = Guid.NewGuid().ToString("N");
    }

    // ── WebDAV 授权码 DPAPI 保护（Windows 原生 crypt32.dll，P/Invoke 封装见 Services/Sync/Dpapi.cs；
    //    环境无外网装不了官方 ProtectedData 包，改用等价原生 API，2026-08-13 审查调整） ──

    public static string ProtectToken(string plainToken)
        => Services.Sync.Dpapi.Protect(plainToken);

    /// <summary>解密失败兜底返回 null（调用方提示重新配置，不崩）。</summary>
    public static string? UnprotectToken(string protectedBase64)
        => Services.Sync.Dpapi.Unprotect(protectedBase64);
}
