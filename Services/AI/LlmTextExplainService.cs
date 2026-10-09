using System.Threading;

namespace FocusCapture.Services.AI;

/// <summary>
/// LLM 实现的查词/解释服务：按模式组装 system prompt 后走 IChatProvider。
/// </summary>
public class LlmTextExplainService : ITextExplainService
{
    private readonly IChatProvider _provider;

    public LlmTextExplainService(IChatProvider provider)
    {
        _provider = provider;
    }

    public async Task<string> ExplainAsync(string text, ExplainMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var system = mode switch
        {
            ExplainMode.Translate =>
                "你是一个翻译与释义助手。如果输入是单词或短语，输出：词性、释义、常见搭配、例句；如果输入是段落，输出整段中文翻译并简要解释。",
            ExplainMode.Search =>
                "你是一个笔记解释助手。阅读用户提供的笔记内容，解释其中的关键概念、术语或背景，帮助用户理解。",
            _ => "你是一个 AI 助手，回答用户的问题。",
        };

        var messages = new[]
        {
            new ChatMessage(ChatRoles.System, system),
            new ChatMessage(ChatRoles.User, text),
        };
        return await _provider.CompleteAsync(messages, ct).ConfigureAwait(false);
    }
}
