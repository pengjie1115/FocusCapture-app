using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services.AI;

/// <summary>
/// 供应商状态探测（2026-09-23）：**打开「AI 模型」板块时**并发探一遍全部已配置供应商。
///
/// <para>刻意<b>不做「应用启动时探测」、也不做「后台定时探测」</b>（用户拍板，设计稿 §5.1）：
/// 用户需要状态点准确的时刻，就是他打开面板看的那一刻 —— 启动时探的那一次，等他几分钟后
/// 打开面板可能已经变了，等于花了钱买不到要的东西。后台定时还额外带来常驻活动、耗电，
/// 以及被安全软件拦截的风险（本项目有过真实事故）。</para>
///
/// <para>探测动作 = <c>GET {baseUrl}/models</c>，而非发一次对话请求：
/// 它<b>不需要模型就能测</b>（还没配模型的供应商也该能测出通断），且不消耗生成额度。</para>
///
/// <para><b>永不抛</b>，也<b>绝不修改配置</b> —— 失败只是结论，不是要改用户的东西。</para>
/// </summary>
public static class AiProviderHealthService
{
    /// <summary>单个供应商 5 秒超时：探测是"顺手看一眼"，不该让用户等。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>探测单个供应商。地址为空时**直接给结论、不发请求**。</summary>
    public static async Task<AiHealthResult> ProbeAsync(AiProviderEntry provider, CancellationToken ct = default)
    {
        var url = (provider.BaseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
            return new AiHealthResult(AiHealthStatus.Network, "还没填 Base URL");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/models");
            if (!string.IsNullOrEmpty(provider.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return AiHealthClassifier.Classify((int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            return AiHealthClassifier.Unreachable(ex.Message);
        }
    }

    /// <summary>
    /// 并发探全部，返回「供应商 Id → 结论」。空列表直接返回空字典（不发任何请求）。
    /// <b>永不抛</b>：单个失败不影响其余（<see cref="ProbeAsync"/> 自身就不抛）。
    /// </summary>
    public static async Task<Dictionary<string, AiHealthResult>> ProbeAllAsync(
        IReadOnlyList<AiProviderEntry> providers, CancellationToken ct = default)
    {
        var map = new Dictionary<string, AiHealthResult>(StringComparer.Ordinal);
        if (providers.Count == 0) return map;

        var tasks = providers.Select(async p => (p.Id, Result: await ProbeAsync(p, ct).ConfigureAwait(false))).ToList();
        foreach (var (id, result) in await Task.WhenAll(tasks).ConfigureAwait(false))
            map[id] = result;
        return map;
    }

    /// <summary>把结论写回供应商（时间戳 + 分类）。通过时清掉历史失败说明，避免旧错误留着误导。</summary>
    public static void Apply(AiProviderEntry provider, AiHealthResult result)
    {
        provider.LastTestStatus = result.Status;
        provider.LastTestMessage = result.Status == AiHealthStatus.Ok ? "" : result.Message;
        provider.LastTestedAt = DateTime.Now.ToString("s");
    }
}
