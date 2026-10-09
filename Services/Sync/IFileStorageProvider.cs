using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FocusCapture.Services.Sync;

/// <summary>目录条目：文件名 + 渠道提供的最后修改标记（HTTP Last-Modified / ETag 语义）。
/// Stamp 为 null 表示渠道拿不到（调用方应退化为全量下载）。</summary>
public record CloudFileInfo(string Name, string? LastModified);

/// <summary>
/// 通用文件级存储契约（AI 会话同步专用）：列目录 / 下载 / 上传 / 删除 / 建目录。
/// 由 WebDAVProvider 实现，ChatSyncEngine 依赖本接口。
/// 刻意不塞进 ISyncProvider——那是笔记桶语义的契约，通用文件 PUT/GET 与其语义不通
/// （将来 Server 实现 ISyncProvider 时不受此污染；本接口随渠道各自实现）。
/// </summary>
public interface IFileStorageProvider
{
    /// <summary>列目录（含修改标记）。调用方（ChatSyncEngine）用 LastModified 做增量对账：
    /// 与本地记录一致 → 跳过下载（坚果云 600 请求/30min 红线下的稳态省流核心）。</summary>
    Task<List<CloudFileInfo>> ListFilesAsync(CancellationToken ct);

    /// <summary>下载文件内容；文件不存在（404）返回 null（调用方免 try）</summary>
    Task<string?> DownloadFileAsync(string fileName, CancellationToken ct);

    /// <summary>上传/覆盖文件内容（PUT 幂等）</summary>
    Task UploadFileAsync(string fileName, string content, CancellationToken ct);

    /// <summary>删除文件（幂等语义由渠道决定：WebDAV 对不存在文件 DELETE 一般返回 404，调用方捕获处理）</summary>
    Task DeleteFileAsync(string fileName, CancellationToken ct);

    /// <summary>确保远端目录存在（首配设备/换账号后首次同步前调用）</summary>
    Task EnsureDirectoryAsync(CancellationToken ct);
}
