namespace FocusCapture.Services.AI;

/// <summary>
/// 对话消息。ToolCallId / ToolCallsJson 仅供 Agent 工具循环使用：
/// - ToolCallsJson：assistant 消息携带的 tool_calls 原始 JSON（原样回传模型必需）
/// - ToolCallId：role=tool 的结果消息对应的调用 id
/// 普通问答路径两字段恒为 null，两参构造与历史会话 JSON 完全兼容。
///
/// Attachments（2026-09-14 新增）：用户消息可携带附件（图片走视觉、文档走本地抽文本）。
/// 该字段随会话 JSON 序列化 —— 只存引用（见 <see cref="ChatAttachment"/>），绝不嵌 base64。
/// 旧会话 JSON 无此字段 → 反序列化为 null，行为与改造前完全一致。
/// </summary>
public sealed record ChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolCallsJson = null,
    List<ChatAttachment>? Attachments = null);

public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

public enum ExplainMode { Translate, Search, Ask }

/// <summary>工具定义（发给模型的 tools 数组条目）。ParametersJson 为 JSON Schema 字符串。</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>模型请求的一次工具调用。</summary>
public sealed record ToolCallItem(string Id, string Name, string ArgumentsJson);

/// <summary>ChatWithToolsAsync 的响应：content 与 tool_calls 至少一个非空。</summary>
public sealed record ChatWithToolsResult(string? Content, IReadOnlyList<ToolCallItem> ToolCalls);

/// <summary>
/// 流式响应事件（普通问答与带工具问答共用）。
/// ContentDelta / ReasoningDelta 为增量文本；ToolCalls 在流结束时产出一次（检测到模型请求工具时）。
/// </summary>
public abstract record StreamChatEvent
{
    /// <summary>正文增量</summary>
    public sealed record ContentDelta(string Text) : StreamChatEvent;
    /// <summary>思考内容增量（仅思考型模型返回，如 reasoning_content）</summary>
    public sealed record ReasoningDelta(string Text) : StreamChatEvent;
    /// <summary>流结束：模型请求的工具调用全集（arguments 已拼接完整）</summary>
    public sealed record ToolCalls(IReadOnlyList<ToolCallItem> Calls) : StreamChatEvent;
}

/// <summary>带 StatusCode 的 LLM 请求异常，供 Agent 循环识别 4xx 降级。</summary>
public class LlmRequestException : InvalidOperationException
{
    public int StatusCode { get; }
    public LlmRequestException(string message, int statusCode) : base(message) => StatusCode = statusCode;
}
