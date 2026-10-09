namespace FocusCapture.Models;

/// <summary>
/// 一个模型条目（挂在某个供应商下）。
///
/// <para><b>Id 与 DisplayName 分离</b>：Id 是供应商要求的字符串（可能又长又丑，
/// 如 <c>deepseek-ai/DeepSeek-V4-Flash</c>），直接作为请求体的 <c>model</c> 字段发出；
/// DisplayName 是给用户看的名字。两者互不影响。</para>
/// </summary>
public sealed class AiModelEntry
{
    /// <summary>模型 ID —— 请求体 model 字段原样使用。空 = 不可用（请求会被拦下）。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面显示名。留空时界面回退显示 <see cref="Id"/>。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 上下文窗口（token）。<b>0 = 不限制</b>（不裁剪历史消息），与改造前行为一致。
    ///
    /// <para>刻意<b>不预填猜测值</b>：窗口是模型的客观属性，我们无法可靠得知；
    /// 填错不会报错，只会让裁剪逻辑按错误阈值<b>悄悄丢掉消息</b> ——
    /// 表现出来是「AI 怎么把我前面说的忘了」，用户根本查不出原因。宁可留空。</para>
    /// </summary>
    public int ContextWindow { get; set; }

    /// <summary>
    /// 最大输出 Token（写入请求体 <c>max_tokens</c>）。<b>≤0 = 不传该字段</b>，
    /// 由供应商自己的默认值决定 —— 不确定的用户有个安全出口。
    ///
    /// <para>默认 4096 是<b>保守值</b>不是最优值：设太大部分供应商直接 400，
    /// 设太小长回答被 <c>finish_reason=length</c> 截断。4096 是几乎没有供应商会拒绝的位置。</para>
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;
}

/// <summary>
/// 一个模型供应商（一个 API 地址 + 一份密钥 + 若干模型）。
///
/// <para>与旧的扁平字段（<c>AiBaseUrl/AiApiKey/AiModel/AiMaxTokens</c>）的关系：
/// 首次加载时由 <see cref="AppSettings.MigrateLegacyAiConfig"/> 合成第一条供应商，
/// 之后新代码只认本结构。</para>
/// </summary>
public sealed class AiProviderEntry
{
    /// <summary>稳定标识：创建/迁移时生成后不再变，<see cref="AppSettings.ActiveModelKey"/> 靠它定位。</summary>
    public string Id { get; set; } = "";

    /// <summary>供应商显示名（如 Agnes / DeepSeek / siliconflow）。</summary>
    public string Name { get; set; } = "";

    /// <summary>API 地址（BaseUrl），请求时拼 <c>/chat/completions</c> 或 <c>/models</c>。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API 密钥。按用户拍板沿用 settings.json 明文存储（与改造前同级，不新增暴露面）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// 上次状态探测通过的时间（ISO 8601 字符串；空 = 从未测过）。
    /// <b>语义是「上次探测结果」，不是「可用」保证</b> —— Key 被撤销、余额耗尽、供应商宕机，
    /// 这个时间戳照样是旧的。界面文案必须照实说，不能写成「可用」。
    /// </summary>
    public string LastTestedAt { get; set; } = "";

    /// <summary>
    /// 上次探测结果类别：<c>""</c>（未测）/ <c>Ok</c> / <c>Network</c>（本机网络不通）/
    /// <c>Key</c>（401、403）/ <c>Server</c>（429、5xx）。
    /// 分类的意义：断网时全部供应商都失败，若不分类型，用户会以为 Key 全坏了然后去删配置。
    /// </summary>
    public string LastTestStatus { get; set; } = "";

    /// <summary>上次探测的补充说明（失败原因原文，已截断）。仅用于悬停提示，不参与逻辑判断。</summary>
    public string LastTestMessage { get; set; } = "";

    /// <summary>该供应商下已添加的模型。</summary>
    public List<AiModelEntry> Models { get; set; } = new();

    /// <summary>
    /// 深拷贝。编辑页改的是**草稿副本**，点「取消」必须能原样丢弃 ——
    /// 若编辑页直接改真实对象，「取消」就成了一句空话（用户点了取消，配置却已经变了）。
    /// 逐字段手写而不是序列化往返：JSON 往返会悄悄丢掉未知字段，将来加字段时最容易漏。
    /// </summary>
    public AiProviderEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        LastTestedAt = LastTestedAt,
        LastTestStatus = LastTestStatus,
        LastTestMessage = LastTestMessage,
        Models = Models.Select(m => new AiModelEntry
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            ContextWindow = m.ContextWindow,
            MaxOutputTokens = m.MaxOutputTokens,
        }).ToList(),
    };
}
