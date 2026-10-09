using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace FocusCapture.Services.AI;

/// <summary>
/// OpenAI 兼容 Chat Completions 协议实现（Agnes / Hunyuan 等兼容节点通用）。
/// 请求：POST {BaseUrl}/chat/completions，Bearer 认证，stream=true/false。
/// </summary>
public class OpenAICompatibleProvider : IChatProvider
{
    private static readonly HttpClient HttpClient = new();

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly int _contextWindow;

    /// <summary>
    /// 上一次请求发生裁剪时的轻提示（没裁剪或无窗口配置时为 null），供界面照实告诉用户
    /// 「最早的 N 条本轮不再发送」。**裁剪是隐形的**：不提示的话用户只会解读成「AI 变笨了」。
    /// </summary>
    public string? LastTrimHint { get; private set; }

    public string Model => _model;
    public string BaseUrl => _baseUrl;
    public string ApiKey => _apiKey;

    public OpenAICompatibleProvider(string baseUrl, string apiKey, string model, int maxTokens = 4096, int contextWindow = 0)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _apiKey = apiKey ?? "";
        _model = model ?? "";   // 不再 fallback 到固定模型；请求时由 BuildRequest 校验空值
        // ≤0 **原样保留**（2026-09-23 改）：它表示「不传 max_tokens，由供应商默认值决定」。
        // 旧实现把 ≤0 强行回退成 4096 —— 等于用户想表达「别限制我」时无路可走。
        _maxTokens = maxTokens;
        _contextWindow = contextWindow;   // 0 = 不裁剪（用户没填窗口，与改造前行为一致）
    }

    /// <summary>
    /// 发请求前按上下文预算裁剪消息（2026-09-23）。**三条请求路径共用这一处** ——
    /// 分开写必然漏一处（本项目在附件序列化上正是这么丢过附件的）。
    ///
    /// <para>裁剪不发生时原样返回，零开销。</para>
    /// </summary>
    private IReadOnlyList<ChatMessage> ApplyContextBudget(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        LastTrimHint = null;
        if (_contextWindow <= 0 || messages.Count == 0) return messages;

        // 系统提示词与工具定义**永远不会被丢**，所以它们只算进固定开销、不参与裁剪
        var systemTokens = 0;
        var conversation = new List<ChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            if (m.Role == ChatRoles.System) systemTokens += ContextBudget.EstimateTokens(m.Content);
            else conversation.Add(m);
        }

        var items = conversation.Select(ToContextItem).ToList();
        var fixedOverhead = systemTokens + EstimateToolsTokens(tools);
        var plan = ContextBudget.Plan(items, _contextWindow, _maxTokens, fixedOverhead);

        if (plan.SingleMessageTooLarge)
            throw new InvalidOperationException(plan.UserHint ?? "这条内容超过模型能装下的上限。");

        if (!plan.ShouldTrim) return messages;

        LastTrimHint = plan.UserHint;
        var kept = new List<ChatMessage>(messages.Count);
        kept.AddRange(messages.Where(m => m.Role == ChatRoles.System));
        kept.AddRange(conversation.Skip(plan.KeepFromIndex));
        return kept;
    }

    /// <summary>
    /// 映射成裁剪用的视图。**附件必须单独算**：
    /// 图片按固定值（base64 几万字符但供应商按分辨率折算，字符估算对它彻底失效）；
    /// 文档附件同样要算 —— 它的正文只在序列化时才拼进请求（<c>ExtractedText</c>），
    /// 不在这里计入就会「以为还空着」，然后长文档一来就超窗。
    /// </summary>
    private static ContextItem ToContextItem(ChatMessage m)
    {
        var textTokens = ContextBudget.EstimateTokens(m.Content) + ContextBudget.EstimateTokens(m.ToolCallsJson);
        var imageTokens = 0;
        foreach (var a in m.Attachments ?? Enumerable.Empty<ChatAttachment>())
        {
            if (a.Kind == ChatAttachmentKind.Image)
                imageTokens += ContextBudget.ImageTokensPerImage;
            else
                textTokens += ContextBudget.EstimateTokens(a.ExtractedText);
        }
        return new ContextItem(m.Role, !string.IsNullOrEmpty(m.ToolCallsJson), textTokens, imageTokens);
    }

    /// <summary>工具定义的 token 估算（名称 + 描述 + 参数 schema）。拿不到就按 0 —— 少算只是保守度下降，不会算错。</summary>
    private static int EstimateToolsTokens(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools == null || tools.Count == 0) return 0;
        var total = 0;
        foreach (var t in tools)
            total += ContextBudget.EstimateTokens(t.Name)
                   + ContextBudget.EstimateTokens(t.Description)
                   + ContextBudget.EstimateTokens(t.ParametersJson)
                   + 8;   // 每个工具的 JSON 骨架开销
        return total;
    }

    /// <summary>
    /// 按需写入 max_tokens：**≤0 时整个字段都不写**（交给供应商默认值）。
    /// 三条请求路径（流式 / 带工具 / 非流式）共用这一处，避免逻辑分叉。
    ///
    /// <para><c>internal</c> 是为了让慢层检查点能直接断言这条规则 ——
    /// 走网络去验证「字段没发出去」既不现实也不可靠。</para>
    /// </summary>
    internal static void ApplyMaxTokens(JsonObject payload, int maxTokens)
    {
        if (maxTokens > 0) payload["max_tokens"] = maxTokens;
    }

    /// <summary>非流式补全：解析 choices[0].message.content</summary>
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        using var request = BuildRequest(ApplyContextBudget(messages, null), stream: false);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"LLM 请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(body)}{ImageHint(messages)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return content ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"LLM 响应格式异常\n{Truncate(body)}", ex);
        }
    }

    /// <summary>流式补全：SSE 逐行解析，只取正文增量（思考内容等事件由 StreamChatWithToolsAsync/ReadSseAsync 提供）</summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in StreamChatWithToolsAsync(messages, tools: null, ct).ConfigureAwait(false))
        {
            if (ev is StreamChatEvent.ContentDelta delta)
                yield return delta.Text;
        }
    }

    /// <summary>
    /// 流式补全（可选带工具）：SSE 逐行解析 delta.content / delta.reasoning_content / delta.tool_calls。
    /// tools 为 null 时不带 tools 字段（普通流式问答）；带 tools 时模型请求工具会在流结束时以
    /// ToolCalls 事件产出一次（arguments 已按 index 拼接完整）。
    /// HTTP 非 2xx 抛 LlmRequestException（含 StatusCode，供 Agent 循环识别 4xx 降级）。
    /// </summary>
    public async IAsyncEnumerable<StreamChatEvent> StreamChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("未配置模型名称，请在设置 → AI 模型中填写模型名称。");

        messages = ApplyContextBudget(messages, tools);   // 三条路径共用的裁剪入口（2026-09-23）

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = true,
            ["messages"] = BuildMessagesArray(messages),
        };
        ApplyMaxTokens(payload, _maxTokens);
        if (tools != null && tools.Count > 0)
            payload["tools"] = BuildToolsArray(tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            AppLog.Error("AI", $"流式请求失败: HTTP {(int)response.StatusCode}，模型 {_model}，tools={tools?.Count ?? 0}，响应 {Truncate(errorBody, 300)}");
            throw new LlmRequestException(
                $"LLM 流式请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(errorBody)}{ImageHint(messages)}",
                (int)response.StatusCode);
        }

        await foreach (var ev in ReadSseAsync(response, ct).ConfigureAwait(false))
            yield return ev;
    }

    /// <summary>SSE 核心读取循环：逐行解析 data: {...}，产出正文/思考增量；工具调用片段按 index 增量拼接</summary>
    private static async IAsyncEnumerable<StreamChatEvent> ReadSseAsync(
        System.Net.Http.HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // index → (id, name, arguments 增量缓冲)；流结束时统一产出
        var callsByIndex = new SortedDictionary<int, (string? Id, string Name, StringBuilder Args)>();
        bool sawToolCallField = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            string? content = null, reasoning = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                var choice = choices[0];

                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        content = c.GetString();
                    // 思考内容：DeepSeek 系用 reasoning_content，部分节点用 reasoning
                    if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        reasoning = rc.GetString();
                    else if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                        reasoning = r.GetString();

                    if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    {
                        sawToolCallField = true;
                        foreach (var item in tc.EnumerateArray())
                        {
                            var index = item.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                                ? idx.GetInt32() : callsByIndex.Count;
                            if (!callsByIndex.TryGetValue(index, out var slot))
                            {
                                slot = (null, "", new StringBuilder());
                                callsByIndex[index] = slot;
                            }
                            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                                slot.Id = idProp.GetString();
                            if (item.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString()))
                                    slot.Name = n.GetString()!;
                                if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                    slot.Args.Append(a.GetString());
                            }
                            callsByIndex[index] = slot;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // SSE 中偶发的非 JSON 心跳/注释行直接跳过
                continue;
            }

            if (!string.IsNullOrEmpty(reasoning)) yield return new StreamChatEvent.ReasoningDelta(reasoning);
            if (!string.IsNullOrEmpty(content)) yield return new StreamChatEvent.ContentDelta(content);
        }

        if (sawToolCallField && callsByIndex.Count > 0)
        {
            var calls = callsByIndex.Select(kv => new ToolCallItem(
                kv.Value.Id ?? $"call_{kv.Key}",
                kv.Value.Name,
                kv.Value.Args.Length == 0 ? "{}" : kv.Value.Args.ToString())).ToList();
            yield return new StreamChatEvent.ToolCalls(calls);
        }
    }

    /// <summary>连接测试：最小请求（max_tokens=1），HTTP 2xx 且能解析出 content 即成功</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var messages = new[] { new ChatMessage(ChatRoles.User, "hi") };
        using var request = BuildRequest(messages, stream: false, maxTokens: 1);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"连接测试失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content");
            return content.ValueKind == JsonValueKind.String;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"连接测试失败: 响应格式异常\n{Truncate(body)}", ex);
        }
    }

    /// <summary>
    /// 带工具的补全（Agent 循环用，非流式）。
    /// assistant 消息可携带 ToolCallsJson（tool_calls 原样回传），role=tool 消息带 tool_call_id。
    /// HTTP 非 2xx 抛 LlmRequestException（含 StatusCode，供调用方识别 4xx 降级）。
    /// </summary>
    public async Task<ChatWithToolsResult> ChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("未配置模型名称，请在设置 → AI 模型中填写模型名称。");

        messages = ApplyContextBudget(messages, tools);   // 三条路径共用的裁剪入口（2026-09-23）

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["messages"] = BuildMessagesArray(messages),
            ["tools"] = BuildToolsArray(tools),
        };
        ApplyMaxTokens(payload, _maxTokens);

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Error("AI", $"ChatWithTools 请求失败: HTTP {(int)response.StatusCode}，模型 {_model}，响应 {Truncate(body, 300)}");
            throw new LlmRequestException(
                $"LLM 请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(body)}{ImageHint(messages)}",
                (int)response.StatusCode);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            string? content = null;
            if (message.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
                content = contentProp.GetString();

            var toolCalls = new List<ToolCallItem>();
            if (message.TryGetProperty("tool_calls", out var callsProp) && callsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in callsProp.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    toolCalls.Add(new ToolCallItem(
                        call.GetProperty("id").GetString() ?? "",
                        function.GetProperty("name").GetString() ?? "",
                        function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}"));
                }
            }

            return new ChatWithToolsResult(content, toolCalls);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Error("AI", $"ChatWithTools 响应格式异常: {Truncate(body, 300)}");
            throw new InvalidOperationException($"LLM 响应格式异常\n{Truncate(body)}", ex);
        }
    }

    /// <summary>tools 数组序列化（流式/非流式带工具请求共用）</summary>
    private static JsonArray BuildToolsArray(IReadOnlyList<ToolDefinition> tools)
    {
        return new JsonArray(tools.Select(t => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(string.IsNullOrWhiteSpace(t.ParametersJson) ? "{}" : t.ParametersJson)
            }
        }).ToArray<JsonNode?>());
    }

    /// <summary>单次请求最多携带的图片数（「历史图片全量重发」策略下的兜底：防止长会话累积到几十张图）</summary>
    private const int MaxImagesPerRequest = 10;

    /// <summary>
    /// 消息序列化 —— **流式 / 非流式 / 带工具三条路径共用这一处**。
    /// （2026-09-14 改造：原先非流式走匿名对象、流式走 JsonObject，两套逻辑并存；
    ///   加附件时必须同时改两处，漏一处会表现为「连接测试正常但实际发送丢附件」，故合并为一处。）
    ///
    /// 普通消息 {role, content:"文本"}；带附件的消息 content 为部件数组（text / image_url 按原顺序混排）；
    /// assistant 带 tool_calls；tool 消息带 tool_call_id。
    /// </summary>
    private static JsonArray BuildMessagesArray(IReadOnlyList<ChatMessage> messages)
    {
        var imagesToDrop = PickImagesToDrop(messages);

        var array = new JsonArray();
        foreach (var m in messages)
        {
            var node = new JsonObject { ["role"] = m.Role };
            if (m.Role == ChatRoles.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
            {
                node["content"] = m.Content ?? "";
                node["tool_calls"] = JsonNode.Parse(m.ToolCallsJson) ?? new JsonArray();
            }
            else if (m.Role == ChatRoles.Tool)
            {
                node["content"] = m.Content ?? "";
                node["tool_call_id"] = m.ToolCallId ?? "";
            }
            else if (m.Attachments is { Count: > 0 })
            {
                node["content"] = BuildContentParts(m, imagesToDrop);
            }
            else
            {
                node["content"] = m.Content ?? "";
            }
            array.Add(node);
        }
        return array;
    }

    /// <summary>
    /// 选出需要降级为文字占位的图片：全局图片数超过上限时，**从最早的消息开始丢**（保留最近的）。
    /// 用户拍板策略是「历史图片每次都重发」（保上下文，不省 token），本上限只防极端累积。
    /// </summary>
    private static HashSet<ChatAttachment> PickImagesToDrop(IReadOnlyList<ChatMessage> messages)
    {
        var drop = new HashSet<ChatAttachment>();
        var total = messages.Sum(m => m.Attachments?.Count(a => a.Kind == ChatAttachmentKind.Image) ?? 0);
        var excess = total - MaxImagesPerRequest;
        if (excess <= 0) return drop;

        foreach (var m in messages)
        {
            if (m.Attachments == null) continue;
            foreach (var a in m.Attachments)
            {
                if (excess <= 0) return drop;
                if (a.Kind != ChatAttachmentKind.Image) continue;
                drop.Add(a);
                excess--;
            }
        }
        return drop;
    }

    /// <summary>
    /// 组装单条消息的 content 部件数组：按附件的 <see cref="ChatAttachment.InsertOffset"/>
    /// 把正文切开，让文字与图片原样交替（这就是"混排"在上行请求里的落地）。
    /// 文档类不占部件位置，以纯文本拼入（不要求模型有视觉能力）。
    /// </summary>
    private static JsonArray BuildContentParts(ChatMessage m, HashSet<ChatAttachment> imagesToDrop)
    {
        var parts = new JsonArray();
        var body = m.Content ?? "";
        var cursor = 0;
        var buffer = new StringBuilder();

        void FlushText()
        {
            if (buffer.Length == 0) return;
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = buffer.ToString() });
            buffer.Clear();
        }

        // InsertOffset 默认 int.MaxValue → 追加在正文之后；OrderBy 稳定排序保持同位置附件的原始顺序
        foreach (var a in m.Attachments!.OrderBy(a => a.InsertOffset))
        {
            var offset = Math.Clamp(a.InsertOffset, 0, body.Length);
            if (offset > cursor)
            {
                buffer.Append(body, cursor, offset - cursor);
                cursor = offset;
            }
            FlushText();
            AppendAttachmentPart(parts, a, imagesToDrop);
        }

        if (cursor < body.Length) buffer.Append(body, cursor, body.Length - cursor);
        FlushText();

        if (parts.Count == 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
        return parts;
    }

    /// <summary>追加一个附件的部件：图片走 image_url（base64 data URL），文档拼纯文本</summary>
    private static void AppendAttachmentPart(JsonArray parts, ChatAttachment a, HashSet<ChatAttachment> imagesToDrop)
    {
        if (a.Kind == ChatAttachmentKind.Image)
        {
            var url = imagesToDrop.Contains(a) ? null : ChatAttachmentService.BuildImageDataUrl(a);
            if (url != null)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = url },
                });
                return;
            }
            AddTextPart(parts, $"[图片：{a.FileName}（本次未能携带，可能附件不在本机或已超出单次图片上限）]");
            return;
        }

        if (string.IsNullOrEmpty(a.ExtractedText))
        {
            AddTextPart(parts, $"[文件：{a.FileName}（内容未能提取）]");
            return;
        }

        var note = string.IsNullOrEmpty(a.ExtractNote) ? "" : "\n" + a.ExtractNote;
        AddTextPart(parts, $"【文件：{a.FileName}】\n{a.ExtractedText}{note}\n【文件结束】");
    }

    /// <summary>追加文本部件；若上一部件已是文本则合并（避免产出连续多个 text 部件）</summary>
    private static void AddTextPart(JsonArray parts, string text)
    {
        if (parts.Count > 0 && parts[^1] is JsonObject last && last["type"]?.GetValue<string>() == "text")
        {
            last["text"] = (last["text"]?.GetValue<string>() ?? "") + "\n" + text;
            return;
        }
        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
    }

    /// <summary>非流式请求（CompleteAsync / TestConnectionAsync）。与流式共用 BuildMessagesArray，杜绝两套逻辑分叉</summary>
    private HttpRequestMessage BuildRequest(IReadOnlyList<ChatMessage> messages, bool stream, int? maxTokens = null)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("未配置模型名称，请在设置 → AI 模型中填写模型名称。");

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = BuildMessagesArray(messages),
            ["stream"] = stream,
        };
        // CompleteAsync 不传 → 用配置的 _maxTokens；TestConnectionAsync 显式传 1 → 用 1（最小请求测连通）；
        // 两者任一 ≤0 都表示「不写该字段」，由 ApplyMaxTokens 统一决定
        ApplyMaxTokens(payload, maxTokens ?? _maxTokens);

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>请求是否含图片附件：用于 HTTP 400 时给出「模型不支持图片」的中文提示</summary>
    private static bool ContainsImage(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(m => m.Attachments?.Any(a => a.Kind == ChatAttachmentKind.Image) == true);

    /// <summary>面向用户的附件相关报错补充说明（把供应商的英文错误翻译成人话）</summary>
    private static string ImageHint(IReadOnlyList<ChatMessage> messages) =>
        ContainsImage(messages)
            ? "\n\n提示：本次请求包含图片。如果当前模型不支持图片输入，请在「设置 → AI 模型」中换用支持视觉的模型，"
              + "或在「设置 → AI 功能」中关闭图片支持开关后重发纯文字。"
            : "";

    /// <summary>截断响应体，避免异常消息过长</summary>
    private static string Truncate(string text, int maxLength = 200)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
    }
}
