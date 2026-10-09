using System.Text.Json;

namespace FocusCapture.Services.AI;

/// <summary>从 <c>/models</c> 响应里解析出来的一条模型（宽容解析的产物）。<see cref="ContextWindow"/> 为 0 表示没拿到。</summary>
public sealed record ParsedModel(string Id, string DisplayName, int ContextWindow);

/// <summary>
/// 解析 <c>GET {baseUrl}/models</c> 的响应（2026-09-23）。
///
/// <para><b>为什么单独做成纯函数</b>：6 家供应商的返回结构差异极大（调研实证）——
/// 顶层容器有的是 <c>data</c>、有的是 <c>models</c>；元素字段从只有 <c>id</c>+<c>object</c>（智谱），
/// 到 <c>id</c>/<c>name</c>/<c>status</c>（腾讯 TokenHub），再到带 <c>context_length</c> 的（仅 Kimi）。
/// 按任何一家写死，换一家就整片漏掉或空引用。**而漏掉不会报错，只是模型列表少几个**，
/// 所以这段必须能被机器反复测 —— 因此它不碰网络、不碰配置，零项目依赖。</para>
/// </summary>
public static class AiModelListParser
{
    /// <summary>
    /// 宽容解析。解析不出结构时返回**已解析到的部分**（可能为空列表），**永不抛** ——
    /// 调用方拿空列表给用户一句「没解析出模型，可手动添加」，比甩一个异常有用。
    /// </summary>
    public static List<ParsedModel> Parse(string? json)
    {
        var result = new List<ParsedModel>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var array = FindModelArray(doc.RootElement);
            if (array == null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in array.Value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                // id 依次退：id → name → model（少数节点只用 name 标识）
                var id = FirstString(el, "id") ?? FirstString(el, "name") ?? FirstString(el, "model");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!seen.Add(id)) continue;   // 同一份列表里重复出现不值得让用户看两遍

                // 显示名优先取 name（腾讯 TokenHub 有该字段），否则回退 id
                var display = FirstString(el, "name");
                result.Add(new ParsedModel(id, string.IsNullOrWhiteSpace(display) ? id : display, ReadContextWindow(el)));
            }
        }
        catch (JsonException)
        {
            // 结构不是预期形态：返回已解析到的部分，由上层给「没解析出模型」的人话提示
        }
        return result;
    }

    /// <summary>找模型数组：顶层容器依次尝试 data → models → 压根就是数组。</summary>
    private static JsonElement? FindModelArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in new[] { "data", "models" })
        {
            if (!root.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Array) return v;
            if (v.ValueKind == JsonValueKind.Object)   // 少数节点会再包一层
                foreach (var inner in new[] { "data", "models" })
                    if (v.TryGetProperty(inner, out var iv) && iv.ValueKind == JsonValueKind.Array) return iv;
        }
        return null;
    }

    private static string? FirstString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// 上下文窗口：字段名各家用得不一样，依次尝试；
    /// **全拿不到就回 0（= 不限制）而不是猜一个值** —— 见 AiModelEntry.ContextWindow 的理由。
    /// </summary>
    private static int ReadContextWindow(JsonElement el)
    {
        foreach (var key in new[] { "context_length", "context_window", "max_context_length", "max_input_tokens" })
        {
            if (!el.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) && s > 0) return s;
        }
        return 0;
    }
}
