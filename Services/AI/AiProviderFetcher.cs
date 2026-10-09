using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace FocusCapture.Services.AI;

/// <summary>
/// 拉取某供应商的可用模型列表：<c>GET {baseUrl}/models</c>（2026-09-23）。
///
/// <para>只在用户点「获取可用模型」时调用 —— <b>不做任何后台轮询</b>。
/// 这是本项目新增的网络出口之一，按红线 12 已与用户确认过：只发给用户自己填的地址，
/// Key 走与对话请求同一个 Bearer 头。</para>
///
/// <para><b>永不抛</b>：失败也包装成人话结果返回（沿用本项目通则：本地状态推进不依赖外部接口成败）。</para>
/// </summary>
public static class AiProviderFetcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>拉取结果。<paramref name="Message"/> 永远是给人看的（成功时是「拉到 N 个模型」）。</summary>
    public sealed record FetchResult(bool Ok, List<ParsedModel> Models, string Message);

    public static async Task<FetchResult> FetchAsync(string? baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return new FetchResult(false, new(), "请先填 Base URL");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/models");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 错误归类复用探测那一套（402/429 欠费这些坑只在一处维护）
                var verdict = AiHealthClassifier.Classify((int)response.StatusCode, body);
                return new FetchResult(false, new(), verdict.Message);
            }

            var models = AiModelListParser.Parse(body);
            return models.Count == 0
                ? new FetchResult(false, new(), "供应商返回了内容，但没解析出任何模型 —— 可以在下面手动添加模型 ID")
                : new FetchResult(true, models, $"拉到 {models.Count} 个模型");
        }
        catch (Exception ex)
        {
            return new FetchResult(false, new(), AiHealthClassifier.Unreachable(ex.Message).Message);
        }
    }
}
