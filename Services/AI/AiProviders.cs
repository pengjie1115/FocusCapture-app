namespace FocusCapture.Services.AI;

/// <summary>
/// AI 供应商预设表（OpenAI 暂不做预置，因其中国大陆无法直连）。
/// 选中预设 → 自动填充 BaseUrl；手改 BaseUrl 且不匹配任何预设 → 归为「自定义」。
/// 实际请求仍走 OpenAICompatibleProvider（BaseUrl/Key/Model 三字段），供应商仅作 UI 辅助与申请页跳转。
/// </summary>
public record AiProviderPreset(string Name, string BaseUrl, string? KeyApplyUrl);

public static class AiProviders
{
    /// <summary>自定义供应商名称（BaseUrl 完全手填、无申请跳转按钮）。</summary>
    public const string Custom = "自定义";

    /// <summary>预设供应商，顺序即下拉显示顺序。</summary>
    public static readonly IReadOnlyList<AiProviderPreset> Presets = new[]
    {
        new AiProviderPreset("Agnes（中国站）", "https://api.agnes-ai.cn/v1",                          "https://agnes-ai.cn/"),
        new AiProviderPreset("DeepSeek",        "https://api.deepseek.com/v1",                         "https://platform.deepseek.com/"),
        new AiProviderPreset("智谱 GLM",        "https://open.bigmodel.cn/api/paas/v4",               "https://open.bigmodel.cn/"),
        new AiProviderPreset("Kimi",            "https://api.moonshot.cn/v1",                         "https://platform.moonshot.cn/"),
        new AiProviderPreset("通义千问",        "https://dashscope.aliyuncs.com/compatible-mode/v1", "https://bailian.console.aliyun.com/"),
        new AiProviderPreset("腾讯混元",        "https://api.hunyuan.cloud.tencent.com/v1",           "https://console.cloud.tencent.com/hunyuan"),
    };

    /// <summary>按 BaseUrl 反推预设；匹配不上返回 null（即应显示「自定义」）。</summary>
    public static AiProviderPreset? MatchByUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var norm = baseUrl.Trim().TrimEnd('/');
        foreach (var p in Presets)
            if (string.Equals(p.BaseUrl.Trim().TrimEnd('/'), norm, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }
}
