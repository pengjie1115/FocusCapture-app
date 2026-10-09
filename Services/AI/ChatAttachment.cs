namespace FocusCapture.Services.AI;

/// <summary>
/// 附件种类：决定发送给模型的方式完全不同。
/// - Image：压缩后转 base64 data URL，以 image_url 部件进消息体（要求模型有视觉能力）
/// - Document：本地抽文本，以普通文字按顺序拼进消息体（不要求模型有特殊能力）
/// </summary>
public enum ChatAttachmentKind
{
    Image = 0,
    Document = 1,
}

/// <summary>
/// 消息附件（轻量引用对象，随会话 JSON 序列化）。
///
/// 存储契约（重要）：二进制本体一律存 <c>chat_history/attachments/</c>，
/// 本对象只是引用 —— 会话 JSON 里绝不嵌 base64。理由：会话文件会被云同步整体加密上传，
/// 且每轮同步要对所有会话做全量扫描对账，一旦嵌入图片（2MB 图 base64 后约 2.7MB）会直接拖垮同步链路。
///
/// 旧会话 JSON 无本字段 → 反序列化为 null，行为与改造前完全一致（向后兼容，无需迁移）。
/// </summary>
public sealed class ChatAttachment
{
    /// <summary>落盘文件名（attachments/ 下，内容 SHA256 前 24 位 + 扩展名；同内容自动去重）</summary>
    public string StoredName { get; set; } = "";

    /// <summary>原始文件名（输入框 chip 与消息气泡展示用）</summary>
    public string FileName { get; set; } = "";

    /// <summary>附件种类</summary>
    public ChatAttachmentKind Kind { get; set; }

    /// <summary>落盘体积（字节）</summary>
    public long SizeBytes { get; set; }

    /// <summary>图片像素宽（仅 Image 类，非 0）</summary>
    public int PixelWidth { get; set; }

    /// <summary>图片像素高（仅 Image 类，非 0）</summary>
    public int PixelHeight { get; set; }

    /// <summary>文档抽取的正文（仅 Document 类；上限 <see cref="ChatAttachmentService.MaxExtractedChars"/>）</summary>
    public string? ExtractedText { get; set; }

    /// <summary>抽取说明（截断提示等；非空时会在消息里附一句说明）</summary>
    public string? ExtractNote { get; set; }

    /// <summary>
    /// 附件在正文中的插入位置（字符偏移）。
    /// 这是"混排"的顺序来源：用户在输入框里"先打字 → 插图 → 再打字"时，
    /// 记录每个附件插在正文第几个字符之后；上行发送时据此把正文切开、
    /// 让 text / image_url 部件按原始顺序交替出现。
    /// <c>int.MaxValue</c> = 追加在正文之后（默认；也兼容旧数据）。
    /// </summary>
    public int InsertOffset { get; set; } = int.MaxValue;

    /// <summary>是否能在本机找到本体文件（跨端拉取的会话其附件不在本机 → false，UI 降级为占位符）</summary>
    public bool IsAvailableLocally() => File.Exists(ChatAttachmentService.ResolvePath(this));
}
