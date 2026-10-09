using System.Threading;
using FocusCapture.Services.AI;

namespace FocusCapture.Services.Agent;

/// <summary>
/// function calling 主循环：发消息（带 tools）→ 执行 tool_calls（写类先过确认闸）→ 结果回填 → 再发。
/// 最多 5 轮工具往返；带 tools 请求 4xx 时自动去 tools 降级为普通问答一次。
/// 写操作确认策略由 WriteConfirmEnabled 控制（默认关，靠系统提示词对话内确认 + 回收站 + 运行日志兜底）。
/// </summary>
public class AgentRunService
{
    private readonly int _maxToolRounds;   // 由构造传入（AppSettings.Agent_maxToolRounds，默认 15）

    private readonly OpenAICompatibleProvider _provider;
    private readonly AgentToolRegistry _registry;
    private readonly ChatSessionService _session;

    /// <summary>确认闸回调（UI 弹窗实现）：返回 true = 用户确认执行。仅 WriteConfirmEnabled=true 时调用。</summary>
    public Func<string, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>写操作是否需要弹窗确认（设置开关，默认 false = 不弹，对话内确认为主）</summary>
    public bool WriteConfirmEnabled { get; set; }

    /// <summary>状态回调（如"正在调用工具: xxx"），UI 用于更新气泡提示</summary>
    public Action<string>? StatusCallback { get; set; }

    /// <summary>
    /// 本轮附加上下文（每轮发送前求值，作为一条额外 system 消息附在消息末尾，**不写入会话历史**）。
    /// 用于「用户当前选中的文件牌号」这类短期状态：它随用户操作随时变化，
    /// 写进会话历史既污染持久化数据，又会让历史会话里残留已经失效的牌号。
    /// </summary>
    public Func<string?>? ExtraSystemContext { get; set; }

    /// <summary>正文流式增量（打字机效果）。事件在后台线程触发，UI 端自行调度到 Dispatcher。</summary>
    public event Action<string>? ContentDelta;

    /// <summary>思考内容流式增量（仅思考型模型产生；工具循环各轮都可能触发）</summary>
    public event Action<string>? ReasoningDelta;

    public AgentRunService(OpenAICompatibleProvider provider, AgentToolRegistry registry, ChatSessionService session, int maxToolRounds = 15)
    {
        _provider = provider;
        _registry = registry;
        _session = session;
        _maxToolRounds = maxToolRounds > 0 ? maxToolRounds : 15;
    }

    /// <summary>
    /// 处理一条用户消息（可带附件），返回最终答复文本（同时写入会话历史）。
    /// 流式产出经 ContentDelta/ReasoningDelta 事件推送。
    /// 注意：Agent 每次工具往返都会重发完整消息历史，带图消息的图片 token 会按往返次数重复计费。
    /// </summary>
    public async Task<string> RunAsync(string userMessage, List<ChatAttachment>? attachments = null, CancellationToken ct = default)
    {
        AppLog.Info("Agent", $"用户消息：{Trunc(userMessage, 200)}");
        _session.AddUser(userMessage, attachments);

        for (var round = 0; round < _maxToolRounds; round++)
        {
            var sb = new StringBuilder();
            IReadOnlyList<ToolCallItem>? toolCalls = null;
            try
            {
                await foreach (var ev in _provider.StreamChatWithToolsAsync(BuildTurnMessages(), _registry.GetDefinitions(), ct).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case StreamChatEvent.ContentDelta delta:
                            sb.Append(delta.Text);
                            ContentDelta?.Invoke(delta.Text);
                            break;
                        case StreamChatEvent.ReasoningDelta reasoning:
                            ReasoningDelta?.Invoke(reasoning.Text);
                            break;
                        case StreamChatEvent.ToolCalls calls:
                            toolCalls = calls.Calls;
                            break;
                    }
                }
            }
            catch (LlmRequestException ex) when (ex.StatusCode >= 400 && ex.StatusCode < 500)
            {
                AppLog.Warn("Agent", $"带 tools 流式请求 4xx（{ex.StatusCode}），降级普通问答：{Trunc(ex.Message, 200)}");
                return await FallbackPlainChatAsync("当前模型不支持工具调用，已切换普通问答模式。", ct);
            }

            if (toolCalls == null || toolCalls.Count == 0)
            {
                var final = sb.ToString();
                AppLog.Info("Agent", $"第 {round + 1} 轮无工具调用，最终答复 {final.Length} 字");
                _session.AddAssistant(final);
                return final;
            }

            AppLog.Info("Agent", $"第 {round + 1} 轮模型请求 {toolCalls.Count} 个工具：" +
                string.Join("; ", toolCalls.Select(tc => $"{tc.Name}({Trunc(tc.ArgumentsJson, 150)})")));

            // assistant 消息（含 tool_calls 原始 JSON，回传模型必需）
            var toolCallsJson = JsonSerializer.Serialize(toolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.Name, arguments = tc.ArgumentsJson },
            }));
            _session.AddAssistantToolCall(sb.Length > 0 ? sb.ToString() : null, toolCallsJson);

            foreach (var call in toolCalls)
            {
                StatusCallback?.Invoke($"正在调用工具: {call.Name}");
                var toolResult = await ExecuteToolWithGateAsync(call, ct);
                AppLog.Info("Agent", $"工具 {call.Name} 返回：{Trunc(toolResult, 300)}");
                _session.AddToolResult(call.Id, toolResult);
            }
        }

        AppLog.Warn("Agent", $"工具调用达 {_maxToolRounds} 轮上限，强制收尾");
        return await FallbackPlainChatAsync("（本轮工具调用已达上限）请基于以上工具结果直接给出最终回答。", ct);
    }

    private async Task<string> ExecuteToolWithGateAsync(ToolCallItem call, CancellationToken ct)
    {
        if (!_registry.TryGetTool(call.Name, out var tool))
        {
            AppLog.Warn("Agent", $"模型调用了不存在的工具：{call.Name}");
            return $"错误：不存在名为「{call.Name}」的工具。可用工具：{_registry.DescribeAvailable()}";
        }

        if (!tool.IsReadOnly)
        {
            if (WriteConfirmEnabled)
            {
                if (ConfirmHandler == null)
                {
                    AppLog.Warn("Agent", $"工具 {call.Name} 需确认但无确认处理器，已拒绝");
                    return "错误：该操作需要用户确认，但当前环境不支持确认交互，已取消。";
                }
                var confirmed = await ConfirmHandler(tool.DescribeAction(call.ArgumentsJson));
                AppLog.Info("Agent", $"工具 {call.Name} 弹窗确认结果：{(confirmed ? "通过" : "用户取消")}");
                if (!confirmed)
                    return "用户已取消该操作。请停止此动作，不要重复尝试。";
            }
            else
            {
                AppLog.Info("Agent", $"写操作（弹窗关闭）：{tool.DescribeAction(call.ArgumentsJson)}");
            }
        }

        try
        {
            return await tool.ExecuteAsync(call.ArgumentsJson, ct);
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", $"工具 {call.Name} 执行异常，参数：{Trunc(call.ArgumentsJson, 300)}", ex);
            return $"工具 {call.Name} 执行出错：{ex.Message}";
        }
    }

    /// <summary>
    /// 本轮实际发送的消息 = 会话历史 + 一条附加 system 上下文（见 <see cref="ExtraSystemContext"/>）。
    /// 附加消息只存在于本次请求，不落会话文件。
    /// </summary>
    private IReadOnlyList<ChatMessage> BuildTurnMessages()
    {
        var extra = ExtraSystemContext?.Invoke();
        if (string.IsNullOrWhiteSpace(extra)) return _session.Messages;

        var list = new List<ChatMessage>(_session.Messages) { new(ChatRoles.System, extra) };
        return list;
    }

    /// <summary>降级/收尾：去掉 tools 与 tool 消息后普通补全一次（配对消息只存在于本回合上下文，过滤不影响历史）</summary>
    private async Task<string> FallbackPlainChatAsync(string notice, CancellationToken ct)
    {
        var plainMessages = _session.Messages
            .Where(m => m.Role != ChatRoles.Tool && m.ToolCallsJson == null)
            .ToList();

        var extra = ExtraSystemContext?.Invoke();
        if (!string.IsNullOrWhiteSpace(extra))
            plainMessages.Add(new ChatMessage(ChatRoles.System, extra));

        string content;
        try
        {
            content = await _provider.CompleteAsync(plainMessages, ct);
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", $"降级普通问答也失败", ex);
            content = $"（模型请求失败：{ex.Message}）";
        }

        var full = notice + "\n" + content;
        _session.AddAssistant(full);
        return full;
    }

    private static string Trunc(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
